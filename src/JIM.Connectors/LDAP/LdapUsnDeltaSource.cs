// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.Diagnostics;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

/// <summary>
/// The change source for Active Directory and Samba AD. The watermark is the domain controller's
/// HighestCommittedUSN; what changed is every object whose uSNChanged has passed it, searched per selected
/// Container and Object Type; what was deleted is every tombstone in each partition's Deleted Objects container
/// that has done the same. Because a USN is scoped to the domain controller that issued it, the source also
/// records that controller's invocationId and refuses to read from a watermark another one produced.
/// </summary>
internal sealed class LdapUsnDeltaSource : ILdapDeltaSource
{
    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;

    internal LdapUsnDeltaSource(ILdapOperationExecutor executor, ILogger logger)
    {
        _executor = executor;
        _logger = logger;
    }

    /// <summary>
    /// Records the HighestCommittedUSN the rootDSE carries and, from the NTDS Settings object the rootDSE's
    /// dsServiceName names, the domain controller's invocationId, so a later Delta Import can tell whether it has
    /// reached the same domain controller as the one that produced the watermark (see
    /// <see cref="LdapConnectorUtilities.VerifyDomainControllerIdentity"/>). invocationId is not itself a rootDSE
    /// attribute, hence the second read.
    /// </summary>
    public Task CaptureWatermarkAsync(SearchResultEntry rootDseEntry, LdapConnectorRootDse rootDse, TimeSpan searchTimeout)
    {
        rootDse.HighestCommittedUsn = LdapConnectorUtilities.GetEntryAttributeLongValue(rootDseEntry, "HighestCommittedUSN");

        var dsServiceName = LdapConnectorUtilities.GetEntryAttributeStringValue(rootDseEntry, "dsServiceName");
        if (string.IsNullOrEmpty(dsServiceName))
        {
            _logger.Warning("GetRootDseInformation: rootDSE did not return dsServiceName. " +
                "Domain controller identity cannot be verified for this import.");
        }
        else
        {
            rootDse.InvocationId = QueryInvocationId(dsServiceName, searchTimeout);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Guards against a USN-based Delta Import silently connecting to a different domain controller than the one
    /// that produced the persisted watermark (see #230).
    /// </summary>
    public void VerifyContinuity(LdapConnectorRootDse previous, LdapConnectorRootDse current) =>
        LdapConnectorUtilities.VerifyDomainControllerIdentity(previous, current, _logger);

    /// <summary>
    /// Evaluates, for every partition given, whether the account JIM connects as may list its Deleted Objects
    /// container, where <see cref="ReadChangesAsync"/> is about to look for tombstones (#1723). An account without
    /// rights over that container is not refused there: its tombstone search succeeds with no rows, so without
    /// this an import would import every change and no deletion and provably leave deleted objects in JIM.
    /// </summary>
    public async Task<IReadOnlyList<LdapDeltaSourceFinding>> VerifyReadinessAsync(LdapConnectorRootDse rootDse, IReadOnlyCollection<string> namingContexts, CancellationToken cancellationToken)
    {
        if (namingContexts.Count == 0)
        {
            _logger.Debug("LdapUsnDeltaSource: No naming context was given, so access to the Deleted Objects container was not checked.");
            return [];
        }

        var access = new LdapConnectorDeletedObjectsAccess(_executor, _logger);
        var findings = new List<LdapDeltaSourceFinding>();
        foreach (var namingContext in namingContexts)
            findings.Add(ToFinding(await access.CheckAsync(namingContext, cancellationToken)));

        return findings;
    }

    /// <summary>
    /// Restates one partition's access check as a finding about this source: a proven denial is an unavailability
    /// that stops a Delta Import, an unknown is carried as its warning, and a grant has nothing to say.
    /// </summary>
    internal static LdapDeltaSourceFinding ToFinding(DeletedObjectsAccessFinding finding) => finding.Outcome switch
    {
        DeletedObjectsAccessOutcome.Granted => new LdapDeltaSourceFinding
        {
            Subject = finding.ContainerDn,
            Outcome = LdapDeltaSourceOutcome.Available,
            Detail = finding.Detail
        },
        DeletedObjectsAccessOutcome.Denied => new LdapDeltaSourceFinding
        {
            Subject = finding.ContainerDn,
            Outcome = LdapDeltaSourceOutcome.Unavailable,
            Detail = finding.Detail,
            DeltaImportText = LdapConnectorDeletedObjectsAccess.DescribeForDeltaImport(finding),
            SchemaDiscoveryText = LdapConnectorDeletedObjectsAccess.DescribeForSchemaDiscovery(finding)
        },
        _ => new LdapDeltaSourceFinding
        {
            Subject = finding.ContainerDn,
            Outcome = LdapDeltaSourceOutcome.CouldNotDetermine,
            Detail = finding.Detail,
            DeltaImportText = LdapConnectorDeletedObjectsAccess.DescribeForDeltaImport(finding),
            SchemaDiscoveryText = LdapConnectorDeletedObjectsAccess.DescribeForSchemaDiscovery(finding)
        }
    };

    public bool HasBaseline(LdapConnectorRootDse previous) => previous.HighestCommittedUsn.HasValue;

    /// <summary>
    /// Reads one page of changes: for each target partition, the objects of each selected Object Type changed in
    /// each top-level selected Container since the watermark, then the partition's tombstones. Each search pages
    /// under a token of its own, so on a later page only the searches whose token was carried forward run again.
    /// </summary>
    public async Task ReadChangesAsync(LdapDeltaReadContext context, ConnectedSystemImportResult result, CancellationToken cancellationToken)
    {
        // The shell established HasBaseline before calling, so the watermark is there to read.
        var previousUsn = context.PreviousRootDse.HighestCommittedUsn!.Value;

        _logger.Debug("GetDeltaImportObjects: Using AD USN-based delta import. Previous USN: {PreviousUsn}", previousUsn);

        await context.Host.EnterPhaseAsync(LdapConnectorPhases.QueryChanges, $"Querying changes since USN {previousUsn:N0}...");

        // For AD, query objects where uSNChanged > previous HighestCommittedUSN
        foreach (var selectedPartition in context.TargetPartitions)
        {
            // Use GetTopLevelSelectedContainers to avoid duplicates when both parent and child containers are selected
            foreach (var selectedContainer in ConnectedSystemUtilities.GetTopLevelSelectedContainers(selectedPartition))
            {
                foreach (var selectedObjectType in context.ObjectTypes.Where(ot => ot.Selected))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.Debug("GetDeltaImportObjects: Cancellation requested. Stopping");
                        return;
                    }

                    var paginationTokenName = LdapConnectorUtilities.GetPaginationTokenName(selectedContainer, selectedObjectType);
                    var paginationToken = context.PaginationTokens.SingleOrDefault(pt => pt.Name == paginationTokenName);
                    var lastRunsCookie = paginationToken?.ByteValue;

                    // On subsequent pages, skip combos with no pagination token (see full import comment)
                    if (context.PaginationTokens.Count > 0 && paginationToken == null)
                        continue;

                    await context.Host.EnterPhaseAsync(LdapConnectorPhases.Fetch, $"Fetching changed {selectedObjectType.Name} objects from {selectedContainer.Name}...");
                    var readBefore = result.ImportObjects.Count;
                    ReadChangedObjects(context, result, selectedContainer, selectedObjectType, previousUsn, lastRunsCookie, cancellationToken);
                    await context.Host.ReportObjectsReadAsync(result.ImportObjects.Count - readBefore);
                }
            }

            // Query deleted objects (tombstones) for this partition
            // AD moves deleted objects to CN=Deleted Objects,<partition DN>
            // We query this container separately with the Show Deleted Objects control
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("GetDeltaImportObjects: Cancellation requested before querying deleted objects. Stopping");
                return;
            }

            var deletedObjectsTokenName = LdapConnectorUtilities.GetDeletedObjectsPaginationTokenName(selectedPartition);
            var deletedObjectsToken = context.PaginationTokens.SingleOrDefault(pt => pt.Name == deletedObjectsTokenName);

            // On subsequent pages, skip the tombstone search when it has no pagination token (see full import comment)
            if (context.PaginationTokens.Count > 0 && deletedObjectsToken == null)
                continue;

            await context.Host.EnterPhaseAsync(LdapConnectorPhases.QueryDeletions, $"Querying deleted objects in {selectedPartition.Name}...");
            var deletionsBefore = result.ImportObjects.Count;
            ReadDeletedObjects(context, result, selectedPartition, previousUsn, deletedObjectsToken?.ByteValue, cancellationToken);
            await context.Host.ReportObjectsReadAsync(result.ImportObjects.Count - deletionsBefore);
        }
    }

    /// <summary>
    /// Queries the DC's NTDS Settings object (identified by the rootDSE's dsServiceName) for its
    /// invocationId, a binary GUID attribute that is not itself exposed on the rootDSE. Used by
    /// <see cref="LdapConnectorUtilities.VerifyDomainControllerIdentity"/> to detect a delta import
    /// connecting to a different DC than the one that produced the persisted USN watermark.
    /// Never throws: a missing attribute or an LDAP-level failure (permissions, a non-AD server that
    /// still claims AD capabilities) is logged as a warning and returns null, since this is a
    /// defence-in-depth guard, not itself required for the import to proceed.
    /// </summary>
    private Guid? QueryInvocationId(string dsServiceNameDn, TimeSpan searchTimeout)
    {
        var request = new SearchRequest(dsServiceNameDn, "(objectClass=*)", SearchScope.Base, "invocationId");

        try
        {
            var response = (SearchResponse)_executor.SendRequest(request, searchTimeout);
            if (response == null || response.Entries.Count == 0)
            {
                _logger.Warning("QueryInvocationId: No entries returned when querying the NTDS Settings object at {Dn}. " +
                    "Domain controller identity cannot be verified for this import.", LogSanitiser.Sanitise(dsServiceNameDn));
                return null;
            }

            var invocationId = LdapConnectorUtilities.GetEntryAttributeGuidValue(response.Entries[0], "invocationId");
            if (invocationId == null)
            {
                _logger.Warning("QueryInvocationId: The NTDS Settings object at {Dn} did not return an invocationId value. " +
                    "Domain controller identity cannot be verified for this import.", LogSanitiser.Sanitise(dsServiceNameDn));
            }

            return invocationId;
        }
        catch (DirectoryOperationException ex)
        {
            LogInvocationIdQueryFailure(dsServiceNameDn, ex);
            return null;
        }
        catch (LdapException ex)
        {
            LogInvocationIdQueryFailure(dsServiceNameDn, ex);
            return null;
        }
    }

    /// <summary>
    /// Shared warning log for <see cref="QueryInvocationId"/>'s two identical-shaped catch clauses
    /// (permissions, or a non-AD server that still claims AD capabilities).
    /// </summary>
    private void LogInvocationIdQueryFailure(string dsServiceNameDn, Exception ex)
    {
        _logger.Warning("QueryInvocationId: Failed to query the NTDS Settings object at {Dn}. " +
            "Domain controller identity cannot be verified for this import. Error: {Message}",
            LogSanitiser.Sanitise(dsServiceNameDn), LogSanitiser.Sanitise(ex.Message));
    }

    /// <summary>
    /// Gets delta results for Active Directory using USN-based change tracking.
    /// Queries for objects where uSNChanged is greater than the previous watermark.
    /// </summary>
    private void ReadChangedObjects(LdapDeltaReadContext context, ConnectedSystemImportResult result, ConnectedSystemContainer container, ConnectedSystemObjectType objectType, long previousUsn, byte[]? lastRunsCookie, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("GetDeltaResultsUsingUsn: Cancellation requested. Stopping");
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        // Build filter for objects changed since last USN, of the specified object type
        // uSNChanged is a 64-bit integer stored as a string
        var ldapFilter = $"(&(objectClass={objectType.Name})(uSNChanged>={previousUsn + 1}))";

        // Build attribute list
        var attributes = objectType.Attributes.Where(a => a.Selected).Select(a => a.Name).ToList();
        attributes.AddRange(objectType.Attributes.Where(a => a.IsExternalId).Select(a => a.Name));
        attributes.AddRange(objectType.Attributes.Where(a => a.IsSecondaryExternalId).Select(a => a.Name));
        attributes.Add("objectClass");
        attributes.Add("isDeleted"); // To detect deleted objects (when searching deleted objects container)
        var queryAttributes = attributes.Distinct().ToArray();

        var searchRequest = new SearchRequest(container.ExternalId, ldapFilter,
            LdapConnectorUtilities.GetSearchScope(container), queryAttributes);

        // Only add paging control if the directory supports it
        var supportsPaging = context.CurrentRootDse?.SupportsPaging ?? true;
        if (supportsPaging)
        {
            var pageResultRequestControl = new PageResultRequestControl(context.PageSize)
            {
                // Make paging non-critical so servers that don't support paging can ignore it
                IsCritical = false
            };
            if (lastRunsCookie is { Length: > 0 })
                pageResultRequestControl.Cookie = lastRunsCookie;

            searchRequest.Controls.Add(pageResultRequestControl);
        }

        SearchResponse searchResponse;
        try
        {
            searchResponse = (SearchResponse)_executor.SendRequest(searchRequest, context.SearchTimeout);
        }
        catch (DirectoryOperationException ex) when (lastRunsCookie is { Length: > 0 } &&
            ex.Message.Contains("does not support the control", StringComparison.OrdinalIgnoreCase))
        {
            // Server returned a cookie on first page but doesn't actually support paging (e.g., Samba AD)
            // Retry without paging control - results should have already been returned on first page
            _logger.Warning("GetDeltaResultsUsingUsn: Server rejected paging cookie, assuming all results were returned on first page. Error: {Message}", LogSanitiser.Sanitise(ex.Message));
            return;
        }

        // Handle pagination - only if paging is supported
        if (supportsPaging && searchResponse.Controls != null &&
            searchResponse.Controls.SingleOrDefault(c => c is PageResultResponseControl) is PageResultResponseControl pageResultResponseControl &&
            pageResultResponseControl.Cookie.Length > 0)
        {
            var tokenName = LdapConnectorUtilities.GetPaginationTokenName(container, objectType);
            result.PaginationTokens.Add(new ConnectedSystemPaginationToken(tokenName, pageResultResponseControl.Cookie));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("GetDeltaResultsUsingUsn: Cancellation requested after search. Stopping");
            return;
        }

        // USN-based delta imports cannot distinguish Create vs Update, only that something changed.
        // Use NotSet so JIM determines the actual change type based on CSO existence.
        result.ImportObjects.AddRange(context.Host.ConvertEntries(searchResponse.Entries, ObjectChangeType.NotSet, objectType));

        stopwatch.Stop();
        _logger.Debug("GetDeltaResultsUsingUsn: Found {Count} changed objects for type '{ObjectType}' in container '{Container}' (USN > {Usn}) in {Elapsed}",
            searchResponse.Entries.Count, objectType.Name, container.Name, previousUsn, stopwatch.Elapsed);
    }

    /// <summary>
    /// Gets deleted objects (tombstones) from the AD Deleted Objects container using USN-based change tracking.
    /// This enables delta imports to detect deletions that occurred since the last import.
    ///
    /// When AD deletes an object, it:
    /// 1. Moves the object to CN=Deleted Objects,&lt;partition DN&gt;
    /// 2. Sets isDeleted=TRUE
    /// 3. Strips most attributes, keeping only objectGUID, objectSid, distinguishedName (mangled), lastKnownParent
    /// 4. Updates uSNChanged
    ///
    /// The search is paged, exactly as <see cref="ReadChangedObjects"/> pages a container's changes, under a
    /// pagination token of the partition's own, so it runs once per Delta Import rather than once per page and a
    /// clean-up larger than the directory's page size is imported in full rather than refused (#1724). The search
    /// itself lives in <see cref="LdapConnectorDeletedObjectsSearch"/>, where it can be tested; what becomes of
    /// each tombstone is decided here.
    /// </summary>
    private void ReadDeletedObjects(LdapDeltaReadContext context, ConnectedSystemImportResult result, ConnectedSystemPartition partition, long previousUsn, byte[]? lastRunsCookie, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("GetDeletedObjectsUsingUsn: Cancellation requested. Stopping");
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        // The same construction the up-front access check used, so a note recorded here about this container
        // replaces the check's note about it rather than sitting beside it.
        var deletedObjectsDn = LdapConnectorDeletedObjectsAccess.ContainerDnFor(partition.ExternalId);

        var page = new LdapConnectorDeletedObjectsSearch(_executor, _logger)
            .Search(deletedObjectsDn, previousUsn, context.PageSize, context.CurrentRootDse?.SupportsPaging ?? true, lastRunsCookie, context.SearchTimeout);

        switch (page.Outcome)
        {
            case DeletedObjectsSearchOutcome.Refused:
                context.Notes.Record(deletedObjectsDn, LdapConnectorDeletedObjectsAccess.DescribeRefusedSearch(deletedObjectsDn, page.Detail));
                return;
            case DeletedObjectsSearchOutcome.ContainerMissing:
                context.Notes.Record(deletedObjectsDn, LdapConnectorDeletedObjectsAccess.DescribeMissingContainer(deletedObjectsDn));
                return;
            case DeletedObjectsSearchOutcome.LimitExceeded:
                context.Notes.Record(deletedObjectsDn, LdapConnectorDeletedObjectsAccess.DescribeLimitExceeded(deletedObjectsDn));
                return;
        }

        if (page.NextCookie != null)
        {
            var tokenName = LdapConnectorUtilities.GetDeletedObjectsPaginationTokenName(partition);
            result.PaginationTokens.Add(new ConnectedSystemPaginationToken(tokenName, page.NextCookie));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("GetDeletedObjectsUsingUsn: Cancellation requested after search. Stopping");
            return;
        }

        // Process each deleted object (tombstone)
        var deletedCount = 0;
        foreach (var entry in page.Entries)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("GetDeletedObjectsUsingUsn: Cancellation requested during processing. Stopping");
                return;
            }

            // Get the objectGUID - this is the stable identifier for matching to existing CSOs
            var objectGuid = LdapConnectorUtilities.GetEntryAttributeGuidValues(entry, "objectGUID")?.FirstOrDefault();
            if (objectGuid == null || objectGuid == Guid.Empty)
            {
                _logger.Warning("GetDeletedObjectsUsingUsn: Deleted object has no objectGUID. DN: {Dn}", LogSanitiser.Sanitise(entry.DistinguishedName));
                continue;
            }

            // Determine the object type from objectClass
            // Tombstones retain their objectClass hierarchy
            var objectClasses = LdapConnectorUtilities.GetEntryAttributeStringValues(entry, "objectClass");
            if (objectClasses == null || objectClasses.Count == 0)
            {
                _logger.Warning("GetDeletedObjectsUsingUsn: Deleted object has no objectClass. DN: {Dn}", LogSanitiser.Sanitise(entry.DistinguishedName));
                continue;
            }

            // Find the matching object type from our schema
            var objectType = LdapObjectTypeMatcher.Match(objectClasses, context.ObjectTypes);
            if (objectType == null)
            {
                // This tombstone is for an object type we're not importing - skip it
                _logger.Verbose("GetDeletedObjectsUsingUsn: Skipping deleted object with unselected object type. Classes: {Classes}", string.Join(",", objectClasses));
                continue;
            }

            // Create an import object with Delete change type
            var importObject = new ConnectedSystemImportObject
            {
                ObjectType = objectType.Name,
                ChangeType = ObjectChangeType.Deleted
            };

            // Add the objectGUID as an attribute so JIM can match this to the existing CSO
            // The external ID attribute for the LDAP connector is typically objectGUID
            var guidAttribute = objectType.Attributes.FirstOrDefault(a => a.IsExternalId && a.Type == AttributeDataType.Guid);
            if (guidAttribute != null)
            {
                importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute
                {
                    Name = guidAttribute.Name,
                    Type = AttributeDataType.Guid,
                    GuidValues = new List<Guid> { objectGuid.Value }
                });
            }
            else
            {
                // Fallback: try to add objectGUID directly if it's a selected attribute
                var objectGuidAttr = objectType.Attributes.FirstOrDefault(a =>
                    a.Name.Equals("objectGUID", StringComparison.OrdinalIgnoreCase));
                if (objectGuidAttr != null)
                {
                    importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute
                    {
                        Name = "objectGUID",
                        Type = AttributeDataType.Guid,
                        GuidValues = new List<Guid> { objectGuid.Value }
                    });
                }
            }

            result.ImportObjects.Add(importObject);
            deletedCount++;

            _logger.Debug("GetDeletedObjectsUsingUsn: Detected deleted {ObjectType} with objectGUID {Guid}",
                objectType.Name, objectGuid);
        }

        stopwatch.Stop();
        _logger.Information("GetDeletedObjectsUsingUsn: Found {Count} deleted objects in partition '{Partition}' (USN > {Usn}) in {Elapsed}",
            deletedCount, partition.Name, previousUsn, stopwatch.Elapsed);
    }
}
