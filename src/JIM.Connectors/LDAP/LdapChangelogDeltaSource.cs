// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;
using JIM.Models.Staging;
using Serilog;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

/// <summary>
/// The change source for directories that publish a draft-good-ldap-changelog (389 Directory Server, and the
/// generic fallback): the last changeNumber under cn=changelog is the watermark, and a Delta Import reads the
/// entries numbered after it, fetching each target's current state by DN. Extracted verbatim from
/// <c>LdapConnectorImport</c>; the path's known defects are marked below for the layer that fixes them (#1725).
/// </summary>
internal sealed class LdapChangelogDeltaSource : ILdapDeltaSource
{
    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;

    internal LdapChangelogDeltaSource(ILdapOperationExecutor executor, ILogger logger)
    {
        _executor = executor;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task CaptureWatermarkAsync(SearchResultEntry rootDseEntry, LdapConnectorRootDse rootDse, TimeSpan searchTimeout)
    {
        // Generic/Oracle: query cn=changelog for the latest changeNumber
        rootDse.LastChangeNumber = QueryDirectoryForLastChangeNumber(0);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void VerifyContinuity(LdapConnectorRootDse previous, LdapConnectorRootDse current)
    {
        // A changelog watermark is not scoped to a server the way a USN is, and nothing checks yet that the
        // changelog has not been trimmed past the baseline; this source has nothing to verify in this layer.
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LdapDeltaSourceFinding>> VerifyReadinessAsync(LdapConnectorRootDse rootDse, IReadOnlyCollection<string> namingContexts, CancellationToken cancellationToken)
    {
        // A later layer adds the probe of cn=changelog; the import shell checked nothing for this source before.
        return Task.FromResult<IReadOnlyList<LdapDeltaSourceFinding>>([]);
    }

    /// <inheritdoc />
    public bool HasBaseline(LdapConnectorRootDse previous) => previous.LastChangeNumber.HasValue;

    /// <inheritdoc />
    public async Task ReadChangesAsync(LdapDeltaReadContext context, ConnectedSystemImportResult result, CancellationToken cancellationToken)
    {
        // The shell has already established HasBaseline before calling.
        var previousChangeNumber = context.PreviousRootDse.LastChangeNumber!.Value;

        await context.Host.EnterPhaseAsync(LdapConnectorPhases.QueryChanges, $"Querying changelog since change number {previousChangeNumber:N0}...");

        var readBefore = result.ImportObjects.Count;
        GetDeltaResultsUsingChangelog(result, previousChangeNumber, context.ScopeDecidingContainers, context.SearchTimeout, context.Host, cancellationToken);
        await context.Host.ReportObjectsReadAsync(result.ImportObjects.Count - readBefore);
    }

    /// <summary>
    /// For directories that support changelog.
    /// </summary>
    private int? QueryDirectoryForLastChangeNumber(int lastChangeNumber)
    {
        // TODO (#878): this needs optimising. If we pass in zero, do we really want to have to enumerate all changes to get the last change number?
        // TODO (#878): make sure this works with a range of directory implementations.

        var ldapFilter = $"(&(!(cn=changelog))(changeNumber>={lastChangeNumber}))";
        var ldapRequest = new SearchRequest("cn=changelog", ldapFilter, SearchScope.Subtree);

        try
        {
            var ldapResponse = (SearchResponse)_executor.SendRequest(ldapRequest);
            if (ldapResponse == null)
            {
                _logger.Warning("QueryDirectoryForLastChangeNumber: ldapResponse is null");
                return 0;
            }

            if (ldapResponse.ResultCode != ResultCode.Success || ldapResponse.Entries.Count == 0)
            {
                _logger.Warning($"QueryDirectoryForLastChangeNumber: Didn't get an expected result. Result code: {ldapResponse.ResultCode}, entries: {ldapResponse.Entries.Count}");
                // TODO (#1725): a failed or empty watermark query returns 0 here, which the next Delta Import reads as a
                // real baseline ("every change since number zero") rather than as "no watermark was captured".
                return 0;
            }

            // this is a valid result
            var index = 0;
            if (ldapResponse.Entries.Count > 1)
                index = ldapResponse.Entries.Count - 1;

            var lastChangeEntry = ldapResponse.Entries[index];
            return LdapConnectorUtilities.GetEntryAttributeIntValue(lastChangeEntry, "changenumber");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "QueryDirectoryForLastChangeNumber: Unhandled exception");
            // TODO (#1725): as above; an exception is swallowed and recorded as change number 0.
            return 0;
        }
    }

    /// <summary>
    /// Gets delta results for changelog-based directories (e.g., OpenLDAP, Oracle Directory).
    /// Queries the cn=changelog container for changes since the last change number.
    /// </summary>
    private void GetDeltaResultsUsingChangelog(ConnectedSystemImportResult result, int previousChangeNumber, IReadOnlyList<ConnectedSystemContainer> targetContainers,
        TimeSpan searchTimeout, ILdapDeltaImportHost host, CancellationToken cancellationToken)
    {
        _logger.Debug("GetDeltaResultsUsingChangelog: Querying for changes since changeNumber {PreviousChange}", previousChangeNumber);
        var skippedOutOfScope = 0;

        // TODO (#1725): LDAP (RFC 4515) defines ">=" but not ">", so this filter is malformed; the search must ask for
        // changeNumber>={previousChangeNumber + 1}.
        var ldapFilter = $"(&(!(cn=changelog))(changeNumber>{previousChangeNumber}))";
        var ldapRequest = new SearchRequest("cn=changelog", ldapFilter, SearchScope.Subtree,
            "changeNumber", "changeType", "targetDN", "changes");

        try
        {
            var ldapResponse = (SearchResponse)_executor.SendRequest(ldapRequest, searchTimeout);

            if (ldapResponse == null || ldapResponse.ResultCode != ResultCode.Success)
            {
                _logger.Warning("GetDeltaResultsUsingChangelog: Failed to query changelog. ResultCode: {ResultCode}",
                    ldapResponse?.ResultCode);
                // TODO (#1725): a refused search returns silently, so the Delta Import completes having imported nothing
                // and reports no error; it must fail the run instead.
                return;
            }

            _logger.Debug("GetDeltaResultsUsingChangelog: Found {Count} changelog entries", ldapResponse.Entries.Count);

            foreach (SearchResultEntry changeEntry in ldapResponse.Entries)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Debug("GetDeltaResultsUsingChangelog: Cancellation requested. Stopping");
                    return;
                }

                var changeType = LdapConnectorUtilities.GetEntryAttributeStringValue(changeEntry, "changeType");
                var targetDn = LdapConnectorUtilities.GetEntryAttributeStringValue(changeEntry, "targetDN");

                if (string.IsNullOrEmpty(targetDn))
                    continue;

                // Filter by Container scope: the changelog is directory-wide, so it reports changes to objects
                // this Connected System does not import. A full import only reads the selected Containers, each
                // to its own scope; without this, a delta import would bring in objects a full import never would.
                if (!LdapConnectorUtilities.IsDnInScope(targetDn, targetContainers))
                {
                    skippedOutOfScope++;
                    continue;
                }

                // Map changelog changeType to ObjectChangeType
                // Changelog provides explicit change types, so we use them directly.
                // Unknown types fall back to NotSet so JIM determines based on CSO existence.
                var objectChangeType = changeType?.ToLowerInvariant() switch
                {
                    "add" => ObjectChangeType.Added,
                    "modify" => ObjectChangeType.Updated,
                    "delete" => ObjectChangeType.Deleted,
                    "modrdn" or "moddn" => ObjectChangeType.Updated,
                    _ => ObjectChangeType.NotSet
                };

                // For deletes, we can create a minimal import object
                if (objectChangeType == ObjectChangeType.Deleted)
                {
                    var deleteObject = new ConnectedSystemImportObject
                    {
                        ChangeType = ObjectChangeType.Deleted,
                        // Note: For deletes, we need the DN as the identifier
                        // The synchronisation engine will need to match this to existing objects
                    };
                    result.ImportObjects.Add(deleteObject);
                }
                else
                {
                    // For adds/modifies, we need to fetch the current state of the object
                    var currentObject = host.GetObjectByDn(targetDn, objectChangeType);
                    if (currentObject != null)
                    {
                        result.ImportObjects.Add(currentObject);
                    }
                }
            }

            if (skippedOutOfScope > 0)
                _logger.Information("GetDeltaResultsUsingChangelog: Skipped {SkippedCount} changelog entries for objects outside the selected Container scope",
                    skippedOutOfScope);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GetDeltaResultsUsingChangelog: Error querying changelog");
            // TODO (#1725): as above; the exception is swallowed and the run completes as though nothing had changed.
        }
    }
}
