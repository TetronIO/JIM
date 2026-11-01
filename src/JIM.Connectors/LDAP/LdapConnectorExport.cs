// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;
using Serilog;
namespace JIM.Connectors.LDAP;

/// <summary>
/// Handles LDAP export functionality - creating, updating, and deleting objects in LDAP directories.
/// Supports both sequential and concurrent LDAP operations within a batch.
/// </summary>
internal class LdapConnectorExport
{
    private readonly ILdapOperationExecutor _executor;
    private readonly IList<ConnectedSystemSettingValue> _settings;
    private readonly ILogger _logger;
    private readonly int _exportConcurrency;
    private readonly int _modifyBatchSize;
    private readonly LdapDirectoryType _directoryType;
    private readonly string _placeholderMemberDn;
    private readonly bool _directoryAdvertisesPermissiveModify;

    // Value changes found already in the state an export asked for, across this export run. Written from concurrent
    // exports on the async path, so only through Interlocked.
    private int _valuesAlreadyInDesiredState;

    // Setting names
    private const string SettingDeleteBehaviour = "Delete Behaviour";
    private const string SettingDisableAttribute = "Disable Attribute";
    private const string SettingCreateContainersAsNeeded = "Create containers as needed?";

    /// <summary>
    /// Active Directory protected attributes that cannot be deleted via LDAP.
    /// When JIM attempts to clear these attributes (set to null), we must instead
    /// replace them with their default "unset" values.
    ///
    /// This list is based on Samba AD's objectclass_attrs.c del_prot_attributes[] array:
    /// https://github.com/samba-team/samba/blob/master/source4/dsdb/samdb/ldb_modules/objectclass_attrs.c
    ///
    /// The full list of protected attributes is:
    /// nTSecurityDescriptor, objectSid, sAMAccountType, sAMAccountName, groupType,
    /// primaryGroupID, userAccountControl, accountExpires, badPasswordTime, badPwdCount,
    /// codePage, countryCode, lastLogoff, lastLogon, logonCount, pwdLastSet
    ///
    /// We only include attributes that JIM might legitimately try to clear via Synchronisation Rules.
    /// System-managed attributes (objectSid, sAMAccountType, etc.) are not included as
    /// JIM would never attempt to modify them.
    /// </summary>
    internal static readonly Dictionary<string, string> ProtectedAttributeDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        // accountExpires: 9223372036854775807 (Int64.MaxValue) means "never expires"
        // A value of 0 also means "never expires" but MaxValue is the default for new accounts
        ["accountExpires"] = "9223372036854775807"
    };

    /// <summary>
    /// Gets the default value for a protected AD attribute, or null if the attribute is not protected.
    /// Protected attributes cannot be deleted in AD - they must be replaced with a default value instead.
    /// </summary>
    /// <param name="attributeName">The attribute name to check (case-insensitive)</param>
    /// <returns>The default value to use when clearing the attribute, or null if the attribute is not protected</returns>
    internal static string? GetProtectedAttributeDefault(string attributeName)
    {
        return ProtectedAttributeDefaults.TryGetValue(attributeName, out var defaultValue)
            ? defaultValue
            : null;
    }

    // Cache of containers we've already created or verified exist during this export session
    private readonly HashSet<string> _verifiedContainers = new(StringComparer.OrdinalIgnoreCase);

    // Track containers created during this export session (for auto-selection in JIM)
    private readonly List<string> _createdContainerExternalIds = new();

    // Serialises container creation to prevent race conditions when concurrent exports
    // try to create the same parent OU simultaneously
    private readonly SemaphoreSlim _containerSemaphore = new(1, 1);

    /// <summary>
    /// Gets the list of container external IDs (DNs for LDAP) that were created during this export session.
    /// Used by JIM to auto-select newly created containers in the hierarchy.
    /// </summary>
    internal IReadOnlyList<string> CreatedContainerExternalIds => _createdContainerExternalIds.AsReadOnly();

    /// <summary>
    /// How many value changes this export run found already in the state their export asked for (a value to add that
    /// was already present, one to remove that was already absent), counted where the directory refused a modify for
    /// that reason and the connector applied it in parts instead. A directory that honours Permissive Modify
    /// accepts such changes without saying so, and they are not counted.
    /// </summary>
    internal int ValuesAlreadyInDesiredState => Volatile.Read(ref _valuesAlreadyInDesiredState);

    internal LdapConnectorExport(
        ILdapOperationExecutor executor,
        IList<ConnectedSystemSettingValue> settings,
        ILogger logger,
        int exportConcurrency = LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY,
        int modifyBatchSize = LdapConnectorConstants.DEFAULT_MODIFY_BATCH_SIZE,
        LdapDirectoryType directoryType = LdapDirectoryType.ActiveDirectory,
        string? placeholderMemberDn = null,
        bool directoryAdvertisesPermissiveModify = false)
    {
        _executor = executor;
        _settings = settings;
        _logger = logger;
        _directoryType = directoryType;
        _exportConcurrency = Math.Clamp(exportConcurrency, 1, LdapConnectorConstants.MAX_EXPORT_CONCURRENCY);
        _modifyBatchSize = Math.Clamp(modifyBatchSize, LdapConnectorConstants.MIN_MODIFY_BATCH_SIZE, LdapConnectorConstants.MAX_MODIFY_BATCH_SIZE);
        _placeholderMemberDn = placeholderMemberDn ?? LdapConnectorConstants.DEFAULT_GROUP_PLACEHOLDER_MEMBER_DN;
        _directoryAdvertisesPermissiveModify = directoryAdvertisesPermissiveModify;

        if (exportConcurrency > LdapConnectorConstants.MAX_EXPORT_CONCURRENCY)
        {
            _logger.Warning("LdapConnectorExport: Requested export concurrency {Requested} exceeds maximum {Maximum}. Clamped to {Maximum}",
                exportConcurrency, LdapConnectorConstants.MAX_EXPORT_CONCURRENCY);
        }

        if (modifyBatchSize != _modifyBatchSize)
        {
            _logger.Warning("LdapConnectorExport: Requested modify batch size {Requested} was clamped to {Actual} (valid range: {Min}-{Max})",
                modifyBatchSize, _modifyBatchSize, LdapConnectorConstants.MIN_MODIFY_BATCH_SIZE, LdapConnectorConstants.MAX_MODIFY_BATCH_SIZE);
        }
    }

    #region Sequential execution (sync path)

    /// <summary>
    /// Executes Pending Exports one after another, returning a result for every one of them.
    /// </summary>
    /// <remarks>
    /// A batch is never abandoned partway, cancelled or not. JIM records a batch's results only once the Connector
    /// returns them, so a batch stopped after some of its exports had been written would lose those results: an object
    /// created without its External ID being recorded, created again by the next run and refused as already existing.
    /// JIM observes a cancelled run between batches instead.
    /// </remarks>
    internal List<ConnectedSystemExportResult> Execute(IList<PendingExport> pendingExports)
    {
        _logger.Debug("LdapConnectorExport.Execute: Starting export of {Count} Pending Exports", pendingExports.Count);

        var results = new List<ConnectedSystemExportResult>();

        if (pendingExports.Count == 0)
        {
            _logger.Information("LdapConnectorExport.Execute: No Pending Exports to process");
            return results;
        }

        foreach (var pendingExport in pendingExports)
        {
            try
            {
                var result = ProcessPendingExport(pendingExport);
                results.Add(result);
            }
            catch (Exception ex)
            {
                LogExportFailure(ex, "LdapConnectorExport.Execute", pendingExport);

                // Return failure result - ExportExecutionServer is responsible for updating
                // ErrorCount, Status, and retry timing. The connector should
                // only report success or failure via ConnectedSystemExportResult.
                results.Add(FailureFor(ex, pendingExport));
            }
        }

        _logger.Information("LdapConnectorExport.Execute: Completed export processing of {Count} Pending Exports", pendingExports.Count);
        LogValuesAlreadyInDesiredStateSummary("LdapConnectorExport.Execute");
        return results;
    }

    // Every container stating where the administrator manages on this Connected System, selections and exclusions
    // alike, or empty when JIM has not stated a scope (a Connected System with no container selections, or a
    // caller that does not supply one). Each carries its own Container Scope, so a One Level container speaks for
    // writes directly within it and for nothing beneath it.
    private IReadOnlyList<ConnectedSystemContainer> _managedScope = [];

    /// <summary>
    /// Tells the Connector which containers the administrator manages, so it can refuse to write outside them.
    /// </summary>
    /// <remarks>
    /// An empty or unset scope means "not stated" and permits everything, so a Connected System with no container
    /// selections behaves exactly as it always has.
    /// </remarks>
    internal void SetManagedScope(IReadOnlyList<ConnectedSystemContainer> scopeDecidingContainers)
    {
        _managedScope = scopeDecidingContainers;
    }

    /// <summary>
    /// Returns an error result when an export would write outside the managed scope, or null when it is permitted.
    /// </summary>
    /// <remarks>
    /// Container selection is the administrator's statement of what JIM manages, and export used to ignore it: an
    /// Export Attribute Flow that moves an account into an unselected container wrote it somewhere JIM cannot read
    /// back, so the change was never confirmed, the next Full Import treated the object as deleted, and the
    /// following synchronisation disconnected it, then deleted or re-provisioned it. Refusing the write per object
    /// leaves everything else in the run to proceed, and puts the configuration error where somebody will see it.
    /// A container the Connector created during this run counts as in scope: JIM selects it as soon as the run ends.
    ///
    /// Each container's Container Scope decides what "inside" means. Beneath a One Level container the import
    /// returns nothing, so writing there is exactly the unreadable write this guard exists to refuse, even though
    /// the Distinguished Name looks like it sits within a selected container.
    /// </remarks>
    private ConnectedSystemExportResult? CheckTargetIsWithinManagedScope(string? targetDn)
    {
        if (_managedScope.Count == 0 || string.IsNullOrEmpty(targetDn))
            return null;

        if (IsWithinManagedScope(targetDn))
            return null;

        var message = $"Cannot write to '{targetDn}': it is outside the containers selected for this Connected System, " +
                      "so JIM would not be able to import the object back and would treat it as deleted on the next Full Import. " +
                      "Select the container on the Scope tab, or change the Attribute Flow that produced this " +
                      "Distinguished Name.";

        _logger.Warning("LdapConnectorExport: Refused an export to '{Dn}' because it is outside the selected containers", LogSanitiser.Sanitise(targetDn));
        return ConnectedSystemExportResult.Failed(message, ConnectedSystemExportErrorType.OutsideManagedScope);
    }

    private bool IsWithinManagedScope(string targetDn)
    {
        if (LdapConnectorUtilities.IsDnInScope(targetDn, _managedScope))
            return true;

        // A container created during this run is selected by JIM the moment the run finishes, so an object being
        // provisioned into one it has just created is in scope even though the stored selection has yet to catch
        // up. It is selected as a subtree, which is what JIM's own auto-selection records.
        var withinAContainerCreatedThisRun = _createdContainerExternalIds.Any(createdDn =>
            LdapConnectorUtilities.IsDnWithinContainerScope(targetDn, new ConnectedSystemContainer { ExternalId = createdDn }));

        // ...unless an exclusion carves that branch out, in which case the container JIM created sits inside a
        // part of the directory the administrator has said it does not manage, and the selection about to catch up
        // will not cover it either (#1255).
        return withinAContainerCreatedThisRun && !IsCarvedOutByAnExclusion(targetDn);
    }

    /// <summary>
    /// Whether an excluded Container is the one with the final say over a Distinguished Name. Distinct from
    /// "not in scope": a name nothing covers is merely unmanaged, where this one has been deliberately carved out.
    /// </summary>
    private bool IsCarvedOutByAnExclusion(string targetDn) =>
        LdapConnectorUtilities.ResolveMostSpecificContainerScope(targetDn, _managedScope) is { Excluded: true };

    private ConnectedSystemExportResult ProcessPendingExport(PendingExport pendingExport)
    {
        pendingExport.Status = PendingExportStatus.Executing;
        pendingExport.LastAttemptedAt = DateTime.UtcNow;

        var scopeFailure = CheckTargetIsWithinManagedScope(GetExportDestinationDistinguishedName(pendingExport));
        if (scopeFailure != null)
            return scopeFailure;

        ConnectedSystemExportResult result;
        switch (pendingExport.ChangeType)
        {
            case PendingExportChangeType.Create:
                result = ProcessCreate(pendingExport);
                break;
            case PendingExportChangeType.Update:
                result = ProcessUpdate(pendingExport);
                break;
            case PendingExportChangeType.Delete:
                result = ProcessDelete(pendingExport);
                break;
            default:
                throw new InvalidOperationException($"Unknown change type: {pendingExport.ChangeType}");
        }

        if (result.Success)
        {
            pendingExport.Status = PendingExportStatus.Exported;
            _logger.Debug("LdapConnectorExport.ProcessPendingExport: Successfully processed {ChangeType} for {Id}",
                pendingExport.ChangeType, pendingExport.Id);
        }
        else
        {
            _logger.Warning("LdapConnectorExport.ProcessPendingExport: Failed to process {ChangeType} for {Id}: {Error}",
                pendingExport.ChangeType, pendingExport.Id, result.ErrorMessage);
        }

        return result;
    }

    private ConnectedSystemExportResult ProcessCreate(PendingExport pendingExport)
    {
        // For create, we need to build the DN and all attributes
        var dn = GetDistinguishedNameForCreate(pendingExport);
        if (string.IsNullOrEmpty(dn))
            throw new InvalidOperationException("Cannot create object: Distinguished Name (DN) could not be determined from attribute changes.");

        // Validate that the DN doesn't contain empty RDN components (e.g., "OU=,OU=Users,...").
        if (!LdapConnectorUtilities.HasValidRdnValues(dn))
            return ConnectedSystemExportResult.Failed(
                $"Cannot create object: Distinguished Name '{dn}' contains empty RDN components.",
                ConnectedSystemExportErrorType.InvalidGeneratedExternalId);

        _logger.Debug("LdapConnectorExport.ProcessCreate: Creating object at DN '{Dn}'", LogSanitiser.Sanitise(dn));

        // Ensure parent containers exist if the setting is enabled
        var createContainersAsNeeded = GetSettingBoolValue(SettingCreateContainersAsNeeded) ?? false;
        if (createContainersAsNeeded)
        {
            EnsureParentContainersExist(dn);
        }

        // Track whether we injected a placeholder so we can provide a specific error if it's rejected
        var hasPlaceholder = RequiresPlaceholderMember(pendingExport);

        var (addRequest, overflowModifyRequests, attributesAwaitingInitialPassword) = BuildAddRequestWithOverflow(pendingExport, dn);

        try
        {
            var response = (AddResponse)_executor.SendRequest(addRequest);
            if (response.ResultCode != ResultCode.Success)
            {
                ThrowAddFailure(addRequest, dn, response);
            }
        }
        catch (DirectoryOperationException ex) when (hasPlaceholder && IsPlaceholderConstraintViolation(ex))
        {
            return HandlePlaceholderRejection(dn, ex);
        }

        // Send any overflow ModifyRequests for multi-valued attributes that exceeded the batch size
        var singleValuedAttributes = GetSingleValuedAttributeNames(pendingExport);
        foreach (var modifyRequest in overflowModifyRequests)
            SendModify(modifyRequest, dn, singleValuedAttributes);

        _logger.Debug("LdapConnectorExport.ProcessCreate: Successfully created object at '{Dn}'", LogSanitiser.Sanitise(dn));

        // After successful create, fetch the system-assigned external ID (objectGUID for AD, entryUUID for OpenLDAP)
        var rootDse = new LdapConnectorRootDse { DirectoryType = _directoryType };
        var externalId = FetchExternalId(dn, rootDse);
        if (externalId != null)
        {
            _logger.Debug("LdapConnectorExport.ProcessCreate: Retrieved external ID {ExternalId} for '{Dn}'", LogSanitiser.Sanitise(externalId), LogSanitiser.Sanitise(dn));
            return CreateSucceeded(externalId, dn, attributesAwaitingInitialPassword, addRequest);
        }

        // External ID not available, return success without it
        return CreateSucceeded(null, dn, attributesAwaitingInitialPassword, addRequest);
    }

    /// <summary>
    /// Fetches the external ID attribute for a newly created object.
    /// For AD this is objectGUID (binary GUID); for OpenLDAP this is entryUUID (string UUID).
    /// </summary>
    private string? FetchExternalId(string dn, LdapConnectorRootDse rootDse)
    {
        try
        {
            var externalIdAttr = rootDse.ExternalIdAttributeName;
            var searchRequest = new SearchRequest(
                dn,
                "(objectClass=*)",
                SearchScope.Base,
                externalIdAttr);

            var searchResponse = (SearchResponse)_executor.SendRequest(searchRequest);
            return ParseExternalIdFromResponse(searchResponse, dn, rootDse);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "LdapConnectorExport.FetchExternalId: Error fetching external ID for '{Dn}'", LogSanitiser.Sanitise(dn));
            return null;
        }
    }

    private ConnectedSystemExportResult ProcessUpdate(PendingExport pendingExport)
    {
        var currentDn = GetDistinguishedNameForUpdate(pendingExport);
        if (string.IsNullOrEmpty(currentDn))
            throw new InvalidOperationException("Cannot update object: Distinguished Name (DN) could not be determined.");

        _logger.Debug("LdapConnectorExport.ProcessUpdate: Updating object at DN '{Dn}'", LogSanitiser.Sanitise(currentDn));

        // Check if a rename is needed (DN has changed)
        var newDn = GetNewDistinguishedName(pendingExport);
        var workingDn = currentDn;
        var wasRenamed = false;

        if (!string.IsNullOrEmpty(newDn) && !newDn.Equals(currentDn, StringComparison.OrdinalIgnoreCase))
        {
            // Validate that the new DN doesn't contain empty RDN components (e.g., "OU=,OU=Users,...").
            if (!LdapConnectorUtilities.HasValidRdnValues(newDn))
            {
                return ConnectedSystemExportResult.Failed(
                    $"Cannot rename object: new Distinguished Name '{newDn}' contains empty RDN components.",
                    ConnectedSystemExportErrorType.InvalidGeneratedExternalId);
            }

            // DN has changed - perform rename first
            workingDn = ProcessRename(currentDn, newDn);
            wasRenamed = true;
        }
        else if (GetCaseOnlyRenameDn(pendingExport, currentDn, newDn) is { } caseOnlyRenameDn)
        {
            workingDn = ProcessRename(currentDn, caseOnlyRenameDn);
            wasRenamed = true;
        }

        // Track whether placeholder modifications were injected for error handling
        var hasPlaceholderModifications = RequiresPlaceholderMember(pendingExport);

        var modifyRequests = BuildModifyRequests(pendingExport, workingDn);

        if (modifyRequests.Count == 0)
        {
            _logger.Debug("LdapConnectorExport.ProcessUpdate: No attribute modifications to apply for '{Dn}'", LogSanitiser.Sanitise(workingDn));
            return wasRenamed ? ConnectedSystemExportResult.Succeeded(null, workingDn) : ConnectedSystemExportResult.Succeeded();
        }

        // Execute each chunked modify request sequentially
        var singleValuedAttributes = GetSingleValuedAttributeNames(pendingExport);
        IReadOnlyList<string>? attributesAwaitingInitialPassword = null;
        for (var i = 0; i < modifyRequests.Count; i++)
        {
            var request = modifyRequests[i];
            if (modifyRequests.Count > 1)
            {
                _logger.Debug("LdapConnectorExport.ProcessUpdate: Sending modify request chunk {Chunk}/{Total} with {Count} modifications for '{Dn}'",
                    i + 1, modifyRequests.Count, request.Modifications.Count, LogSanitiser.Sanitise(workingDn));
            }

            try
            {
                SendModify(request, workingDn, singleValuedAttributes);
            }
            catch (DirectoryOperationException ex) when (CanWithholdEnablementAfterRefusal(pendingExport, request, ex))
            {
                // A refusal of the resend propagates and fails the export exactly as this refusal would have.
                var (withheld, attributeName) = WithEnablementWithheld(request, workingDn);
                SendModify(withheld, workingDn, singleValuedAttributes);
                attributesAwaitingInitialPassword = [attributeName];
            }
            catch (DirectoryOperationException ex) when (hasPlaceholderModifications && IsPlaceholderConstraintViolation(ex))
            {
                return HandlePlaceholderRejection(workingDn, ex);
            }
        }

        return UpdateSucceeded(workingDn, wasRenamed, modifyRequests, attributesAwaitingInitialPassword);
    }

    /// <summary>
    /// Processes a rename (move) operation using ModifyDNRequest.
    /// Returns the new DN after the rename.
    /// </summary>
    private string ProcessRename(string currentDn, string newDn)
    {
        _logger.Debug("LdapConnectorExport.ProcessRename: Renaming object from '{OldDn}' to '{NewDn}'", LogSanitiser.Sanitise(currentDn), LogSanitiser.Sanitise(newDn));

        var modifyDnRequest = BuildModifyDnRequest(currentDn, newDn);

        var response = (ModifyDNResponse)_executor.SendRequest(modifyDnRequest);
        if (response.ResultCode != ResultCode.Success)
        {
            throw new LdapException((int)response.ResultCode, response.ErrorMessage);
        }

        _logger.Information("LdapConnectorExport.ProcessRename: Successfully renamed object from '{OldDn}' to '{NewDn}'",
            LogSanitiser.Sanitise(currentDn), LogSanitiser.Sanitise(newDn));

        return newDn;
    }

    private ConnectedSystemExportResult ProcessDelete(PendingExport pendingExport)
    {
        var dn = GetDistinguishedNameForUpdate(pendingExport);
        if (string.IsNullOrEmpty(dn))
            throw new InvalidOperationException("Cannot delete object: Distinguished Name (DN) could not be determined.");

        // Validate that the DN doesn't contain empty RDN components before attempting delete/disable.
        if (!LdapConnectorUtilities.HasValidRdnValues(dn))
            return ConnectedSystemExportResult.Failed(
                $"Cannot delete object: Distinguished Name '{dn}' contains empty RDN components.",
                ConnectedSystemExportErrorType.InvalidGeneratedExternalId);

        var deleteBehaviour = GetSettingValue(SettingDeleteBehaviour) ?? LdapConnectorConstants.DELETE_BEHAVIOUR_DELETE;

        if (deleteBehaviour != LdapConnectorConstants.DELETE_BEHAVIOUR_DISABLE)
        {
            ProcessHardDelete(dn);
            return ConnectedSystemExportResult.Succeeded();
        }

        var disabledThrough = ProcessDisable(pendingExport, dn);
        return DisableSucceeded(disabledThrough);
    }

    private void ProcessHardDelete(string dn)
    {
        _logger.Debug("LdapConnectorExport.ProcessHardDelete: Deleting object at DN '{Dn}'", LogSanitiser.Sanitise(dn));

        try
        {
            var deleteRequest = new DeleteRequest(dn);
            var response = (DeleteResponse)_executor.SendRequest(deleteRequest);

            if (IsNoSuchObjectResult(response.ResultCode, response.ErrorMessage))
            {
                _logger.Information("LdapConnectorExport.ProcessHardDelete: Object at '{Dn}' does not exist (already deleted), treating as success", LogSanitiser.Sanitise(dn));
                return;
            }

            if (response.ResultCode != ResultCode.Success)
            {
                throw new LdapException((int)response.ResultCode, response.ErrorMessage);
            }

            _logger.Information("LdapConnectorExport.ProcessHardDelete: Successfully deleted object at '{Dn}'", LogSanitiser.Sanitise(dn));
        }
        catch (DirectoryOperationException ex) when (IsNoSuchObjectResult(ex.Response?.ResultCode, ex.Message))
        {
            // Samba AD and some directory implementations throw DirectoryOperationException
            // instead of returning the error code in the response. Treat as idempotent success.
            _logger.Information("LdapConnectorExport.ProcessHardDelete: Object at '{Dn}' does not exist (already deleted), treating as success", LogSanitiser.Sanitise(dn));
        }
    }

    /// <summary>
    /// Disables the account in place of deleting it, and returns the attribute the disable was written through.
    /// </summary>
    private string ProcessDisable(PendingExport pendingExport, string dn)
    {
        _logger.Debug("LdapConnectorExport.ProcessDisable: Disabling object at DN '{Dn}'", LogSanitiser.Sanitise(dn));

        var disableAttribute = GetSettingValue(SettingDisableAttribute) ?? "userAccountControl";

        // For Active Directory userAccountControl, we need to read the current value and set the disable bit
        if (disableAttribute.Equals("userAccountControl", StringComparison.OrdinalIgnoreCase))
        {
            DisableUsingUserAccountControl(dn);
        }
        else
        {
            // For other directories/attributes, just set a value indicating disabled
            // This is a simplified approach - real-world implementations may need more complex logic
            var modifyRequest = new ModifyRequest(dn,
                DirectoryAttributeOperation.Replace,
                disableAttribute,
                "TRUE");

            var response = (ModifyResponse)_executor.SendRequest(modifyRequest);
            if (response.ResultCode != ResultCode.Success)
            {
                throw new LdapException((int)response.ResultCode, response.ErrorMessage);
            }
        }

        _logger.Information("LdapConnectorExport.ProcessDisable: Successfully disabled object at '{Dn}'", LogSanitiser.Sanitise(dn));
        return disableAttribute;
    }

    private void DisableUsingUserAccountControl(string dn)
    {
        // Read current userAccountControl value
        var searchRequest = new SearchRequest(
            dn,
            "(objectClass=*)",
            SearchScope.Base,
            "userAccountControl");

        var searchResponse = (SearchResponse)_executor.SendRequest(searchRequest);
        if (searchResponse.ResultCode != ResultCode.Success || searchResponse.Entries.Count == 0)
        {
            throw new LdapException((int)searchResponse.ResultCode,
                $"Failed to read current userAccountControl value for '{dn}'");
        }

        var newValue = ParseUacAndSetDisableBit(searchResponse);

        var modifyRequest = new ModifyRequest(dn,
            DirectoryAttributeOperation.Replace,
            "userAccountControl",
            newValue.ToString());

        var modifyResponse = (ModifyResponse)_executor.SendRequest(modifyRequest);
        if (modifyResponse.ResultCode != ResultCode.Success)
        {
            throw new LdapException((int)modifyResponse.ResultCode, modifyResponse.ErrorMessage);
        }
    }

    private void EnsureParentContainersExist(string objectDn)
    {
        var (_, parentDn) = LdapConnectorUtilities.ParseDistinguishedName(objectDn);

        if (string.IsNullOrEmpty(parentDn))
        {
            // No parent DN - this is a root-level object, nothing to create
            return;
        }

        // Check if we've already verified this container exists in this session
        if (_verifiedContainers.Contains(parentDn))
        {
            return;
        }

        // Build the chain of parent containers from root to immediate parent
        var containerChain = BuildContainerChain(parentDn);

        // Process from root downwards, creating any missing containers
        foreach (var containerDn in containerChain)
        {
            if (_verifiedContainers.Contains(containerDn))
            {
                continue;
            }

            if (!ContainerExists(containerDn))
            {
                CreateContainer(containerDn);
            }

            _verifiedContainers.Add(containerDn);
        }
    }

    private bool ContainerExists(string containerDn)
    {
        try
        {
            var searchRequest = new SearchRequest(
                containerDn,
                "(objectClass=*)",
                SearchScope.Base,
                "objectClass");

            var response = (SearchResponse)_executor.SendRequest(searchRequest);
            return response.ResultCode == ResultCode.Success && response.Entries.Count > 0;
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return false;
        }
        catch (LdapException ex) when (ex.ErrorCode == 32) // LDAP_NO_SUCH_OBJECT
        {
            return false;
        }
    }

    private void CreateContainer(string containerDn)
    {
        var addRequest = BuildContainerAddRequest(containerDn);

        try
        {
            var response = (AddResponse)_executor.SendRequest(addRequest);
            if (response.ResultCode == ResultCode.EntryAlreadyExists)
            {
                LogContainerCreatedElsewhere(containerDn);
                return;
            }

            if (response.ResultCode != ResultCode.Success)
            {
                throw new LdapException((int)response.ResultCode, $"Failed to create container '{containerDn}': {response.ErrorMessage}");
            }
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.EntryAlreadyExists)
        {
            LogContainerCreatedElsewhere(containerDn);
            return;
        }

        // Track the created container for auto-selection
        _createdContainerExternalIds.Add(containerDn);

        _logger.Information("LdapConnectorExport.CreateContainer: Successfully created container '{ContainerDn}'", LogSanitiser.Sanitise(containerDn));
    }

    #endregion

    #region Concurrent execution (async path)

    /// <summary>
    /// Executes Pending Exports, returning a result for every one of them in the order they were given.
    /// When concurrency is 1, delegates to the sequential <see cref="Execute"/> method.
    /// When concurrency > 1, processes multiple exports concurrently using SemaphoreSlim
    /// while maintaining positional ordering of results.
    /// </summary>
    /// <remarks>
    /// As on the sequential path, a batch is never abandoned partway, cancelled or not: every export scheduled runs and
    /// returns its result, those still waiting for a slot included. JIM records a batch's results only once the
    /// Connector returns them, so abandoning the waiting exports would return no results at all, losing those of the
    /// exports already written (a created object's External ID among them), and would leave the batch's Pending
    /// Exports Executing until the Worker restarts. JIM observes a cancelled run between batches instead.
    /// </remarks>
    /// <param name="pendingExports">The batch of Pending Exports to execute.</param>
    /// <param name="cancellationToken">The run's cancellation, deliberately not observed within the batch (see the
    /// remarks); it only notes in the log that the batch was finished after the run was cancelled.</param>
    internal async Task<List<ConnectedSystemExportResult>> ExecuteAsync(
        IList<PendingExport> pendingExports,
        CancellationToken cancellationToken)
    {
        _logger.Debug("LdapConnectorExport.ExecuteAsync: Starting export of {Count} Pending Exports (concurrency: {Concurrency})",
            pendingExports.Count, _exportConcurrency);

        if (pendingExports.Count == 0)
        {
            _logger.Information("LdapConnectorExport.ExecuteAsync: No Pending Exports to process");
            return new List<ConnectedSystemExportResult>();
        }

        // If concurrency is 1, fall back to synchronous sequential processing
        // for maximum compatibility and simplicity
        var results = _exportConcurrency <= 1
            ? Execute(pendingExports)
            : await ExecuteConcurrentlyAsync(pendingExports);

        if (cancellationToken.IsCancellationRequested)
        {
            _logger.Information("LdapConnectorExport.ExecuteAsync: The export run was cancelled while this batch of {Count} Pending Exports " +
                "was under way; the batch was finished so that every result reaches JIM, which stops before the next batch",
                pendingExports.Count);
        }

        return results;
    }

    private async Task<List<ConnectedSystemExportResult>> ExecuteConcurrentlyAsync(IList<PendingExport> pendingExports)
    {
        // Pre-allocate results array to maintain positional ordering.
        // Each task writes to its own unique index - no shared mutable state between tasks.
        var results = new ConnectedSystemExportResult[pendingExports.Count];
        using var semaphore = new SemaphoreSlim(_exportConcurrency);

        // No cancellation token on the wait or the task: an export waiting for a slot when the run is cancelled still
        // runs, so the batch returns every result (see the remarks on ExecuteAsync).
        var tasks = new Task[pendingExports.Count];
        for (var i = 0; i < pendingExports.Count; i++)
        {
            var index = i; // Capture for closure
            var pendingExport = pendingExports[i];

            tasks[i] = Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    results[index] = await ProcessPendingExportAsync(pendingExport);
                }
                catch (Exception ex)
                {
                    LogExportFailure(ex, "LdapConnectorExport.ExecuteAsync", pendingExport);
                    results[index] = FailureFor(ex, pendingExport);
                }
                finally
                {
                    semaphore.Release();
                }
            });
        }

        await Task.WhenAll(tasks);

        _logger.Information("LdapConnectorExport.ExecuteAsync: Completed export processing of {Count} Pending Exports",
            pendingExports.Count);
        LogValuesAlreadyInDesiredStateSummary("LdapConnectorExport.ExecuteAsync");
        return results.ToList();
    }

    private async Task<ConnectedSystemExportResult> ProcessPendingExportAsync(PendingExport pendingExport)
    {
        pendingExport.Status = PendingExportStatus.Executing;
        pendingExport.LastAttemptedAt = DateTime.UtcNow;

        var scopeFailure = CheckTargetIsWithinManagedScope(GetExportDestinationDistinguishedName(pendingExport));
        if (scopeFailure != null)
            return scopeFailure;

        var result = pendingExport.ChangeType switch
        {
            PendingExportChangeType.Create => await ProcessCreateAsync(pendingExport),
            PendingExportChangeType.Update => await ProcessUpdateAsync(pendingExport),
            PendingExportChangeType.Delete => await ProcessDeleteAsync(pendingExport),
            _ => throw new InvalidOperationException($"Unknown change type: {pendingExport.ChangeType}")
        };

        if (result.Success)
        {
            pendingExport.Status = PendingExportStatus.Exported;
            _logger.Debug("LdapConnectorExport.ProcessPendingExportAsync: Successfully processed {ChangeType} for {Id}",
                pendingExport.ChangeType, pendingExport.Id);
        }
        else
        {
            _logger.Warning("LdapConnectorExport.ProcessPendingExportAsync: Failed to process {ChangeType} for {Id}: {Error}",
                pendingExport.ChangeType, pendingExport.Id, result.ErrorMessage);
        }

        return result;
    }

    private async Task<ConnectedSystemExportResult> ProcessCreateAsync(PendingExport pendingExport)
    {
        var dn = GetDistinguishedNameForCreate(pendingExport);
        if (string.IsNullOrEmpty(dn))
            throw new InvalidOperationException("Cannot create object: Distinguished Name (DN) could not be determined from attribute changes.");

        // Validate that the DN doesn't contain empty RDN components (e.g., "OU=,OU=Users,...").
        if (!LdapConnectorUtilities.HasValidRdnValues(dn))
            return ConnectedSystemExportResult.Failed(
                $"Cannot create object: Distinguished Name '{dn}' contains empty RDN components.",
                ConnectedSystemExportErrorType.InvalidGeneratedExternalId);

        _logger.Debug("LdapConnectorExport.ProcessCreateAsync: Creating object at DN '{Dn}'", LogSanitiser.Sanitise(dn));

        var createContainersAsNeeded = GetSettingBoolValue(SettingCreateContainersAsNeeded) ?? false;
        if (createContainersAsNeeded)
        {
            await EnsureParentContainersExistAsync(dn);
        }

        var (addRequest, overflowModifyRequests, attributesAwaitingInitialPassword) = BuildAddRequestWithOverflow(pendingExport, dn);

        // Sequential within this export: create must succeed before GUID fetch
        var response = (AddResponse)await _executor.SendRequestAsync(addRequest);
        if (response.ResultCode != ResultCode.Success)
        {
            ThrowAddFailure(addRequest, dn, response);
        }

        // Send any overflow ModifyRequests for multi-valued attributes that exceeded the batch size
        var singleValuedAttributes = GetSingleValuedAttributeNames(pendingExport);
        foreach (var modifyRequest in overflowModifyRequests)
            await SendModifyAsync(modifyRequest, dn, singleValuedAttributes);

        _logger.Debug("LdapConnectorExport.ProcessCreateAsync: Successfully created object at '{Dn}'", LogSanitiser.Sanitise(dn));

        var rootDse = new LdapConnectorRootDse { DirectoryType = _directoryType };
        var externalId = await FetchExternalIdAsync(dn, rootDse);
        if (externalId != null)
        {
            _logger.Debug("LdapConnectorExport.ProcessCreateAsync: Retrieved external ID {ExternalId} for '{Dn}'", LogSanitiser.Sanitise(externalId), LogSanitiser.Sanitise(dn));
            return CreateSucceeded(externalId, dn, attributesAwaitingInitialPassword, addRequest);
        }

        return CreateSucceeded(null, dn, attributesAwaitingInitialPassword, addRequest);
    }

    private async Task<string?> FetchExternalIdAsync(string dn, LdapConnectorRootDse rootDse)
    {
        try
        {
            var externalIdAttr = rootDse.ExternalIdAttributeName;
            var searchRequest = new SearchRequest(
                dn,
                "(objectClass=*)",
                SearchScope.Base,
                externalIdAttr);

            var searchResponse = (SearchResponse)await _executor.SendRequestAsync(searchRequest);
            return ParseExternalIdFromResponse(searchResponse, dn, rootDse);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "LdapConnectorExport.FetchExternalIdAsync: Error fetching external ID for '{Dn}'", LogSanitiser.Sanitise(dn));
            return null;
        }
    }

    private async Task<ConnectedSystemExportResult> ProcessUpdateAsync(PendingExport pendingExport)
    {
        var currentDn = GetDistinguishedNameForUpdate(pendingExport);
        if (string.IsNullOrEmpty(currentDn))
            throw new InvalidOperationException("Cannot update object: Distinguished Name (DN) could not be determined.");

        _logger.Debug("LdapConnectorExport.ProcessUpdateAsync: Updating object at DN '{Dn}'", LogSanitiser.Sanitise(currentDn));

        var newDn = GetNewDistinguishedName(pendingExport);
        var workingDn = currentDn;
        var wasRenamed = false;

        if (!string.IsNullOrEmpty(newDn) && !newDn.Equals(currentDn, StringComparison.OrdinalIgnoreCase))
        {
            // Validate that the new DN doesn't contain empty RDN components (e.g., "OU=,OU=Users,...").
            if (!LdapConnectorUtilities.HasValidRdnValues(newDn))
            {
                return ConnectedSystemExportResult.Failed(
                    $"Cannot rename object: new Distinguished Name '{newDn}' contains empty RDN components.",
                    ConnectedSystemExportErrorType.InvalidGeneratedExternalId);
            }

            // Sequential within this export: rename must complete before modify
            workingDn = await ProcessRenameAsync(currentDn, newDn);
            wasRenamed = true;
        }
        else if (GetCaseOnlyRenameDn(pendingExport, currentDn, newDn) is { } caseOnlyRenameDn)
        {
            workingDn = await ProcessRenameAsync(currentDn, caseOnlyRenameDn);
            wasRenamed = true;
        }

        var modifyRequests = BuildModifyRequests(pendingExport, workingDn);

        if (modifyRequests.Count == 0)
        {
            _logger.Debug("LdapConnectorExport.ProcessUpdateAsync: No attribute modifications to apply for '{Dn}'", LogSanitiser.Sanitise(workingDn));
            return wasRenamed ? ConnectedSystemExportResult.Succeeded(null, workingDn) : ConnectedSystemExportResult.Succeeded();
        }

        // Execute each chunked modify request sequentially
        var singleValuedAttributes = GetSingleValuedAttributeNames(pendingExport);
        IReadOnlyList<string>? attributesAwaitingInitialPassword = null;
        for (var i = 0; i < modifyRequests.Count; i++)
        {
            var request = modifyRequests[i];
            if (modifyRequests.Count > 1)
            {
                _logger.Debug("LdapConnectorExport.ProcessUpdateAsync: Sending modify request chunk {Chunk}/{Total} with {Count} modifications for '{Dn}'",
                    i + 1, modifyRequests.Count, request.Modifications.Count, LogSanitiser.Sanitise(workingDn));
            }

            try
            {
                await SendModifyAsync(request, workingDn, singleValuedAttributes);
            }
            catch (DirectoryOperationException ex) when (CanWithholdEnablementAfterRefusal(pendingExport, request, ex))
            {
                // A refusal of the resend propagates and fails the export exactly as this refusal would have.
                var (withheld, attributeName) = WithEnablementWithheld(request, workingDn);
                await SendModifyAsync(withheld, workingDn, singleValuedAttributes);
                attributesAwaitingInitialPassword = [attributeName];
            }
        }

        return UpdateSucceeded(workingDn, wasRenamed, modifyRequests, attributesAwaitingInitialPassword);
    }

    private async Task<string> ProcessRenameAsync(string currentDn, string newDn)
    {
        _logger.Debug("LdapConnectorExport.ProcessRenameAsync: Renaming object from '{OldDn}' to '{NewDn}'", LogSanitiser.Sanitise(currentDn), LogSanitiser.Sanitise(newDn));

        var modifyDnRequest = BuildModifyDnRequest(currentDn, newDn);

        var response = (ModifyDNResponse)await _executor.SendRequestAsync(modifyDnRequest);
        if (response.ResultCode != ResultCode.Success)
        {
            throw new LdapException((int)response.ResultCode, response.ErrorMessage);
        }

        _logger.Information("LdapConnectorExport.ProcessRenameAsync: Successfully renamed object from '{OldDn}' to '{NewDn}'",
            LogSanitiser.Sanitise(currentDn), LogSanitiser.Sanitise(newDn));

        return newDn;
    }

    private async Task<ConnectedSystemExportResult> ProcessDeleteAsync(PendingExport pendingExport)
    {
        var dn = GetDistinguishedNameForUpdate(pendingExport);
        if (string.IsNullOrEmpty(dn))
            throw new InvalidOperationException("Cannot delete object: Distinguished Name (DN) could not be determined.");

        // Validate that the DN doesn't contain empty RDN components before attempting delete/disable.
        if (!LdapConnectorUtilities.HasValidRdnValues(dn))
            return ConnectedSystemExportResult.Failed(
                $"Cannot delete object: Distinguished Name '{dn}' contains empty RDN components.",
                ConnectedSystemExportErrorType.InvalidGeneratedExternalId);

        var deleteBehaviour = GetSettingValue(SettingDeleteBehaviour) ?? LdapConnectorConstants.DELETE_BEHAVIOUR_DELETE;

        if (deleteBehaviour != LdapConnectorConstants.DELETE_BEHAVIOUR_DISABLE)
        {
            await ProcessHardDeleteAsync(dn);
            return ConnectedSystemExportResult.Succeeded();
        }

        var disabledThrough = await ProcessDisableAsync(pendingExport, dn);
        return DisableSucceeded(disabledThrough);
    }

    private async Task ProcessHardDeleteAsync(string dn)
    {
        _logger.Debug("LdapConnectorExport.ProcessHardDeleteAsync: Deleting object at DN '{Dn}'", LogSanitiser.Sanitise(dn));

        try
        {
            var deleteRequest = new DeleteRequest(dn);
            var response = (DeleteResponse)await _executor.SendRequestAsync(deleteRequest);

            if (IsNoSuchObjectResult(response.ResultCode, response.ErrorMessage))
            {
                _logger.Information("LdapConnectorExport.ProcessHardDeleteAsync: Object at '{Dn}' does not exist (already deleted), treating as success", LogSanitiser.Sanitise(dn));
                return;
            }

            if (response.ResultCode != ResultCode.Success)
            {
                throw new LdapException((int)response.ResultCode, response.ErrorMessage);
            }

            _logger.Information("LdapConnectorExport.ProcessHardDeleteAsync: Successfully deleted object at '{Dn}'", LogSanitiser.Sanitise(dn));
        }
        catch (DirectoryOperationException ex) when (IsNoSuchObjectResult(ex.Response?.ResultCode, ex.Message))
        {
            _logger.Information("LdapConnectorExport.ProcessHardDeleteAsync: Object at '{Dn}' does not exist (already deleted), treating as success", LogSanitiser.Sanitise(dn));
        }
    }

    /// <summary>
    /// Disables the account in place of deleting it, and returns the attribute the disable was written through.
    /// </summary>
    private async Task<string> ProcessDisableAsync(PendingExport pendingExport, string dn)
    {
        _logger.Debug("LdapConnectorExport.ProcessDisableAsync: Disabling object at DN '{Dn}'", LogSanitiser.Sanitise(dn));

        var disableAttribute = GetSettingValue(SettingDisableAttribute) ?? "userAccountControl";

        if (disableAttribute.Equals("userAccountControl", StringComparison.OrdinalIgnoreCase))
        {
            await DisableUsingUserAccountControlAsync(dn);
        }
        else
        {
            var modifyRequest = new ModifyRequest(dn,
                DirectoryAttributeOperation.Replace,
                disableAttribute,
                "TRUE");

            var response = (ModifyResponse)await _executor.SendRequestAsync(modifyRequest);
            if (response.ResultCode != ResultCode.Success)
            {
                throw new LdapException((int)response.ResultCode, response.ErrorMessage);
            }
        }

        _logger.Information("LdapConnectorExport.ProcessDisableAsync: Successfully disabled object at '{Dn}'", LogSanitiser.Sanitise(dn));
        return disableAttribute;
    }

    private async Task DisableUsingUserAccountControlAsync(string dn)
    {
        // Sequential within this operation: must read current value before writing
        var searchRequest = new SearchRequest(
            dn,
            "(objectClass=*)",
            SearchScope.Base,
            "userAccountControl");

        var searchResponse = (SearchResponse)await _executor.SendRequestAsync(searchRequest);
        if (searchResponse.ResultCode != ResultCode.Success || searchResponse.Entries.Count == 0)
        {
            throw new LdapException((int)searchResponse.ResultCode,
                $"Failed to read current userAccountControl value for '{dn}'");
        }

        var newValue = ParseUacAndSetDisableBit(searchResponse);

        var modifyRequest = new ModifyRequest(dn,
            DirectoryAttributeOperation.Replace,
            "userAccountControl",
            newValue.ToString());

        var modifyResponse = (ModifyResponse)await _executor.SendRequestAsync(modifyRequest);
        if (modifyResponse.ResultCode != ResultCode.Success)
        {
            throw new LdapException((int)modifyResponse.ResultCode, modifyResponse.ErrorMessage);
        }
    }

    /// <summary>
    /// Ensures parent containers exist, serialised via semaphore to prevent race conditions
    /// when concurrent exports try to create the same parent OU simultaneously.
    /// </summary>
    private async Task EnsureParentContainersExistAsync(string objectDn)
    {
        var (_, parentDn) = LdapConnectorUtilities.ParseDistinguishedName(objectDn);

        if (string.IsNullOrEmpty(parentDn) || _verifiedContainers.Contains(parentDn))
            return;

        // Serialise container creation to prevent race conditions
        await _containerSemaphore.WaitAsync();
        try
        {
            // Double-check after acquiring semaphore
            if (_verifiedContainers.Contains(parentDn))
                return;

            var containerChain = BuildContainerChain(parentDn);

            foreach (var containerDn in containerChain)
            {
                if (_verifiedContainers.Contains(containerDn))
                    continue;

                if (!await ContainerExistsAsync(containerDn))
                {
                    await CreateContainerAsync(containerDn);
                }

                _verifiedContainers.Add(containerDn);
            }
        }
        finally
        {
            _containerSemaphore.Release();
        }
    }

    private async Task<bool> ContainerExistsAsync(string containerDn)
    {
        try
        {
            var searchRequest = new SearchRequest(
                containerDn,
                "(objectClass=*)",
                SearchScope.Base,
                "objectClass");

            var response = (SearchResponse)await _executor.SendRequestAsync(searchRequest);
            return response.ResultCode == ResultCode.Success && response.Entries.Count > 0;
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return false;
        }
        catch (LdapException ex) when (ex.ErrorCode == 32) // LDAP_NO_SUCH_OBJECT
        {
            return false;
        }
    }

    private async Task CreateContainerAsync(string containerDn)
    {
        var addRequest = BuildContainerAddRequest(containerDn);

        try
        {
            var response = (AddResponse)await _executor.SendRequestAsync(addRequest);
            if (response.ResultCode == ResultCode.EntryAlreadyExists)
            {
                LogContainerCreatedElsewhere(containerDn);
                return;
            }

            if (response.ResultCode != ResultCode.Success)
            {
                throw new LdapException((int)response.ResultCode, $"Failed to create container '{containerDn}': {response.ErrorMessage}");
            }
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.EntryAlreadyExists)
        {
            LogContainerCreatedElsewhere(containerDn);
            return;
        }

        _createdContainerExternalIds.Add(containerDn);

        _logger.Information("LdapConnectorExport.CreateContainerAsync: Successfully created container '{ContainerDn}'", LogSanitiser.Sanitise(containerDn));
    }

    /// <summary>
    /// A container add answered with entryAlreadyExists means another writer created it between this connector finding
    /// it missing and adding it: typically a parallel export batch, which has a connector (and so a container
    /// semaphore) of its own. The container is there, which is all the object's create needs, but this connector did
    /// not create it, so it is not reported as created.
    /// </summary>
    private void LogContainerCreatedElsewhere(string containerDn)
    {
        _logger.Information("LdapConnectorExport: Container '{ContainerDn}' was created by another writer before this connector could create it; continuing",
            LogSanitiser.Sanitise(containerDn));
    }

    #endregion

    #region Shared helpers (used by both sync and async paths)

    /// <summary>
    /// Builds an AddRequest for creating a new LDAP object, plus any overflow ModifyRequests
    /// needed when a multi-valued attribute (e.g. member) exceeds the batch size limit.
    /// The AddRequest carries the first batch of values; each overflow ModifyRequest carries
    /// the next batch as an Add operation against the already-created object.
    /// Also returns the attributes the add carries with an interim value because an Initial Password will follow
    /// (see <see cref="WithholdEnablementUntilInitialPassword"/>), or null when nothing was rewritten.
    /// </summary>
    internal (AddRequest AddRequest, List<ModifyRequest> OverflowModifyRequests, IReadOnlyList<string>? AttributesAwaitingInitialPassword) BuildAddRequestWithOverflow(PendingExport pendingExport, string dn)
    {
        var addRequest = new AddRequest(dn);

        // The classes JIM worked out this entry belongs to, stated in one multi-valued attribute: its structural
        // class, and every auxiliary class whose attributes this add carries. See ClassMembershipPlanner.
        var objectClasses = GetObjectClasses(pendingExport);
        if (objectClasses.Count > 0)
            addRequest.Attributes.Add(new DirectoryAttribute("objectClass", [.. objectClasses]));

        // The structural class comes first, and is what the entry fundamentally is; the checks below are about it.
        var objectClass = objectClasses.FirstOrDefault();

        // Consolidate attribute changes by name so multi-valued attributes (e.g. member) are
        // grouped into a single list per attribute name before applying batch size limits.
        var attributeGroups = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);

        foreach (var attrChange in pendingExport.AttributeValueChanges)
        {
            // A connector-defined attribute (Exchange's, #2021) is not in the directory: the Exchange step acts on it
            if (attrChange.Attribute == null || attrChange.Attribute.IsConnectorDefined)
                continue;

            var attrName = attrChange.Attribute.Name;

            // Skip distinguished name as it's already handled
            if (attrName.Equals("distinguishedName", StringComparison.OrdinalIgnoreCase))
                continue;

            // Skip objectClass if we already added it
            if (attrName.Equals("objectClass", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = GetAttributeValue(attrChange);
            if (value != null)
            {
                if (!attributeGroups.TryGetValue(attrName, out var values))
                {
                    values = [];
                    attributeGroups[attrName] = values;
                }
                values.Add(value);
            }
        }

        // Active Directory refuses an enabled account with no password, so one an Initial Password will follow is
        // created disabled (#2039). Rewrites this request's copy of the value only, never the Pending Export.
        var attributesAwaitingInitialPassword = WithholdEnablementUntilInitialPassword(pendingExport, attributeGroups, dn);

        // Placeholder member injection for groupOfNames/groupOfUniqueNames on non-AD directories.
        // If this is a MUST-member object class and no member values are present, inject the placeholder.
        if (RequiresPlaceholderMember(pendingExport) && !string.IsNullOrEmpty(objectClass))
        {
            var memberAttrName = GetMemberAttributeName(objectClass);
            if (!attributeGroups.ContainsKey(memberAttrName) || attributeGroups[memberAttrName].Count == 0)
            {
                _logger.Information(
                    "LdapConnectorExport.BuildAddRequestWithOverflow: Object class '{ObjectClass}' requires at least one member. " +
                    "Injecting placeholder member '{Placeholder}' for '{Dn}'.",
                    objectClass, LogSanitiser.Sanitise(_placeholderMemberDn), LogSanitiser.Sanitise(dn));
                attributeGroups[memberAttrName] = new List<object> { _placeholderMemberDn };
            }
        }

        var overflowModifyRequests = new List<ModifyRequest>();

        foreach (var (attrName, values) in attributeGroups)
        {
            // For attributes within the batch size, add all values to the AddRequest directly
            if (values.Count <= _modifyBatchSize)
            {
                var attr = new DirectoryAttribute(attrName);
                foreach (var value in values)
                {
                    if (value is byte[] bytes)
                        attr.Add(bytes);
                    else
                        attr.Add(value.ToString());
                }
                addRequest.Attributes.Add(attr);
                continue;
            }

            // Attribute exceeds batch size: put first batch in the AddRequest, remainder in ModifyRequests
            _logger.Information("LdapConnectorExport.BuildAddRequestWithOverflow: Attribute '{AttrName}' has {Count} values " +
                "exceeding batch size {BatchSize} for '{Dn}'. First {BatchSize} values in AddRequest; remainder in overflow ModifyRequests.",
                attrName, values.Count, _modifyBatchSize, LogSanitiser.Sanitise(dn));

            var firstBatch = new DirectoryAttribute(attrName);
            for (var i = 0; i < _modifyBatchSize; i++)
            {
                var value = values[i];
                if (value is byte[] bytes)
                    firstBatch.Add(bytes);
                else
                    firstBatch.Add(value.ToString());
            }
            addRequest.Attributes.Add(firstBatch);

            // Build overflow ModifyRequests for remaining values in batches
            for (var i = _modifyBatchSize; i < values.Count; i += _modifyBatchSize)
            {
                var chunk = values.Skip(i).Take(_modifyBatchSize).ToList();
                var mod = new DirectoryAttributeModification
                {
                    Name = attrName,
                    Operation = DirectoryAttributeOperation.Add
                };
                foreach (var value in chunk)
                {
                    if (value is byte[] bytes)
                        mod.Add(bytes);
                    else
                        mod.Add(value.ToString());
                }

                var modifyRequest = new ModifyRequest(dn);
                modifyRequest.Modifications.Add(mod);
                overflowModifyRequests.Add(modifyRequest);
            }
        }

        return (addRequest, overflowModifyRequests, attributesAwaitingInitialPassword);
    }

    /// <summary>
    /// Creates an Active Directory account disabled when an Initial Password will follow it (#2039), by adding
    /// ACCOUNTDISABLE (0x2) to the <c>userAccountControl</c> value the add carries, and returns the attribute so the
    /// result can say so. Returns null, leaving the add exactly as the Attribute Flows built it, unless every one of
    /// these holds:
    /// <list type="bullet">
    /// <item>The directory is real Active Directory. Not the Active Directory family: Samba AD accepts an enabled
    /// account with no password, so it keeps the value it was given.</item>
    /// <item>The change is a Create, and JIM said an Initial Password follows it
    /// (<see cref="PendingExport.InitialPasswordFollows"/>).</item>
    /// <item><c>userAccountControl</c> is present as a single integer value without ACCOUNTDISABLE
    /// (<see cref="TryWithholdEnablement"/>). An account the Attribute Flow already creates disabled needs nothing, and
    /// JIM never invents a value it was not given: absent, Active Directory creates the account disabled on its own.</item>
    /// </list>
    /// <para>
    /// This rewrite is proactive, and for a Create only, because an account being created never has a password yet. An
    /// Update writes to an account that may well have one, so it is sent as the Attribute Flow asks, and withheld only
    /// once Active Directory has refused it for want of a password (<see cref="CanWithholdEnablementAfterRefusal"/>, #2158).
    /// </para>
    /// <para>
    /// Only <paramref name="attributeGroups"/>, the values built for this request, is changed. The Pending Export and its
    /// changes keep the Synchronisation Rule's value, which is what the confirming import is judged against, and
    /// Connectors never mutate an export.
    /// </para>
    /// </summary>
    private IReadOnlyList<string>? WithholdEnablementUntilInitialPassword(PendingExport pendingExport, Dictionary<string, List<object>> attributeGroups, string dn)
    {
        if (_directoryType != LdapDirectoryType.ActiveDirectory
            || pendingExport.ChangeType != PendingExportChangeType.Create
            || !pendingExport.InitialPasswordFollows)
            return null;

        if (!attributeGroups.TryGetValue(LdapConnectorConstants.UserAccountControlAttributeName, out var values) || values.Count != 1)
            return null;

        if (!TryWithholdEnablement(values[0], out var flowValue, out var interimValue))
            return null;

        values[0] = interimValue.ToString(CultureInfo.InvariantCulture);

        // Named as the Pending Export spells it; the dictionary matches the name whatever its case.
        var attributeName = attributeGroups.Keys.First(key =>
            key.Equals(LdapConnectorConstants.UserAccountControlAttributeName, StringComparison.OrdinalIgnoreCase));

        _logger.Debug("LdapConnectorExport.BuildAddRequestWithOverflow: Creating '{Dn}' disabled ({Attribute} {FlowValue} sent as {InterimValue}) " +
            "because an Initial Password follows; the account is enabled once the password is set",
            LogSanitiser.Sanitise(dn), LogSanitiser.Sanitise(attributeName), flowValue, interimValue);

        return [attributeName];
    }

    /// <summary>
    /// Whether an Update the directory refused can be sent again with its enable withheld until a password is delivered
    /// (#2158). Active Directory will not enable an account that has no password, so an Attribute Flow enabling one whose
    /// password is still queued (a pre-hire whose start date arrives before the Initial Password is delivered, say) is
    /// refused. Where JIM said a password follows the Update, the account is held disabled instead, exactly as a Create
    /// is (#2039), and the delivery enables it once the password is set. Every one of these must hold:
    /// <list type="bullet">
    /// <item>The change is an Update, and JIM said a password follows it (<see cref="PendingExport.InitialPasswordFollows"/>):
    /// one is still owed to the account's person on this Connected System.</item>
    /// <item>The directory refused it as one that will not enable an account with no password
    /// (<see cref="LdapAccountEnableRefusalClassifier.IsAccountEnableRefusal"/>, confined to the Active Directory family).
    /// Reactive rather than proactive, so an account that already has a password is enabled at once, and a directory
    /// that never refuses (Samba AD, today) is never held.</item>
    /// <item>The refused request carries exactly one <c>userAccountControl</c> modification, a Replace of one integer
    /// value without ACCOUNTDISABLE (<see cref="TryWithholdEnablement"/>). Anything else is not an enable JIM can hold,
    /// and is reported as the refusal it was.</item>
    /// </list>
    /// <para>
    /// The refused modify applied nothing (an LDAP modify is atomic), so every change it carried is sent again with it.
    /// Should the refusal have come from one step of the fallback in parts (<see cref="SendModify"/>), sending the whole
    /// request again replays the steps that were applied; that is safe, because every step is a Replace, or a value
    /// change the directory accepts as already in place under Permissive Modify or answers with the same fallback.
    /// </para>
    /// </summary>
    private bool CanWithholdEnablementAfterRefusal(PendingExport pendingExport, ModifyRequest request, DirectoryOperationException refusal) =>
        pendingExport.ChangeType == PendingExportChangeType.Update
        && pendingExport.InitialPasswordFollows
        && FindEnablingUserAccountControl(request) != null
        && LdapAccountEnableRefusalClassifier.IsAccountEnableRefusal(refusal, pendingExport, _directoryType);

    /// <summary>
    /// The same modifications for the same entry, with ACCOUNTDISABLE added to the enabling <c>userAccountControl</c>
    /// value, and the attribute named as the request spells it. Only the modifications are copied: the request is sent
    /// as any other, so Permissive Modify and the fallback in parts apply to it as they did to the one refused.
    /// </summary>
    private (ModifyRequest Request, string AttributeName) WithEnablementWithheld(ModifyRequest refused, string dn)
    {
        var (enabling, flowValue, interimValue) = FindEnablingUserAccountControl(refused)
            ?? throw new InvalidOperationException("The refused modify carries no enabling userAccountControl to withhold.");

        var withheld = new DirectoryAttributeModification { Name = enabling.Name, Operation = DirectoryAttributeOperation.Replace };
        withheld.Add(interimValue.ToString(CultureInfo.InvariantCulture));

        var request = new ModifyRequest(refused.DistinguishedName);
        request.Modifications.AddRange(refused.Modifications.Cast<DirectoryAttributeModification>()
            .Select(modification => ReferenceEquals(modification, enabling) ? withheld : modification)
            .ToArray());

        _logger.Information("LdapConnectorExport: Active Directory will not enable '{Dn}' until it has a password, and one is owed to it, " +
            "so {Attribute} {FlowValue} is sent as {InterimValue}; the account is enabled once the password is set",
            LogSanitiser.Sanitise(dn), LogSanitiser.Sanitise(enabling.Name), flowValue, interimValue);

        return (request, enabling.Name);
    }

    /// <summary>
    /// The request's one <c>userAccountControl</c> modification, when it is a Replace of a single value that enables
    /// the account, with that value and the value disabling it; null otherwise, including when the attribute is modified
    /// more than once.
    /// </summary>
    private static (DirectoryAttributeModification Modification, int FlowValue, int InterimValue)? FindEnablingUserAccountControl(ModifyRequest request)
    {
        var userAccountControl = request.Modifications.Cast<DirectoryAttributeModification>()
            .Where(modification => modification.Name.Equals(LdapConnectorConstants.UserAccountControlAttributeName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (userAccountControl is not [{ Operation: DirectoryAttributeOperation.Replace, Count: 1 } modification])
            return null;

        return TryWithholdEnablement(modification[0], out var flowValue, out var interimValue)
            ? (modification, flowValue, interimValue)
            : null;
    }

    /// <summary>
    /// Reads a <c>userAccountControl</c> value an Attribute Flow sent as one that enables the account: an integer
    /// without ACCOUNTDISABLE (0x2), with the same value disabling it, which is what Active Directory holds until the
    /// account has a password. False for anything else: a value that already disables the account needs nothing, and
    /// one that is not an integer is never guessed at. Shared by the Create rewrite (#2039) and the Update resend (#2158).
    /// </summary>
    private static bool TryWithholdEnablement(object? value, out int flowValue, out int interimValue)
    {
        interimValue = 0;
        if (value is not string text || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out flowValue))
        {
            flowValue = 0;
            return false;
        }

        if ((flowValue & LdapConnectorConstants.UAC_ACCOUNTDISABLE) != 0)
            return false;

        interimValue = flowValue | LdapConnectorConstants.UAC_ACCOUNTDISABLE;
        return true;
    }

    /// <summary>
    /// Throws an LdapException with detailed error information for a failed AddRequest.
    /// </summary>
    private static void ThrowAddFailure(AddRequest addRequest, string dn, AddResponse response)
    {
        var attrNames = string.Join(", ", addRequest.Attributes.Cast<DirectoryAttribute>().Select(a => $"'{a.Name}'"));
        var errorDetail = $"LDAP add failed for DN '{dn}'. " +
            $"Attributes: {attrNames}. " +
            $"LDAP error ({(int)response.ResultCode}): {response.ErrorMessage}";

        throw new LdapException((int)response.ResultCode, errorDetail);
    }

    /// <summary>
    /// Parses the objectGUID from a SearchResponse.
    /// </summary>
    private string? ParseExternalIdFromResponse(SearchResponse searchResponse, string dn, LdapConnectorRootDse rootDse)
    {
        if (searchResponse.ResultCode != ResultCode.Success || searchResponse.Entries.Count == 0)
        {
            _logger.Warning("LdapConnectorExport.ParseExternalIdFromResponse: Failed to fetch external ID for '{Dn}'", LogSanitiser.Sanitise(dn));
            return null;
        }

        var entry = searchResponse.Entries[0];
        var externalIdAttr = rootDse.ExternalIdAttributeName;

        if (!entry.Attributes.Contains(externalIdAttr))
            return null;

        switch (rootDse.DirectoryType)
        {
            case LdapDirectoryType.ActiveDirectory:
            case LdapDirectoryType.SambaAD:
            {
                // AD/Samba AD objectGUID is a 16-byte binary value in Microsoft GUID byte order (little-endian first 3 components)
                var guidBytes = entry.Attributes[externalIdAttr][0] as byte[];
                if (guidBytes is { Length: 16 })
                {
                    var guid = IdentifierParser.FromMicrosoftBytes(guidBytes);
                    return guid.ToString();
                }
                break;
            }
            case LdapDirectoryType.OpenLDAP:
            case LdapDirectoryType.Generic:
            case LdapDirectoryType.DirectoryServer389:
            {

                // OpenLDAP entryUUID is a string-formatted UUID (RFC 4530)
                var uuidString = entry.Attributes[externalIdAttr][0] as string;
                return uuidString?.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Builds one or more ModifyRequests for updating attributes on an existing LDAP object.
    /// Consolidates multiple changes to the same attribute with the same operation into a single
    /// DirectoryAttributeModification with multiple values (per RFC 4511). When a consolidated
    /// modification exceeds the configured batch size, it is split into multiple ModifyRequests.
    /// </summary>
    internal List<ModifyRequest> BuildModifyRequests(PendingExport pendingExport, string workingDn)
    {
        // Step 1: Collect all non-RDN attribute changes, grouped by (attribute name, operation)
        // This consolidates e.g. 200 individual "member Add" changes into a single modification with 200 values
        var consolidatedModifications = ConsolidateModifications(pendingExport, workingDn);

        if (consolidatedModifications.Count == 0)
            return [];

        // Step 2: Placeholder member handling for groupOfNames/groupOfUniqueNames on non-AD directories.
        if (RequiresPlaceholderMember(pendingExport))
        {
            InjectPlaceholderModificationsIfNeeded(pendingExport, consolidatedModifications);
        }

        // Step 3: Split consolidated modifications into chunks if any exceed the batch size
        // Single-valued attributes and small multi-valued changes go into the first request.
        // Large multi-valued changes (e.g., 200 member adds) are split across multiple requests.
        return ChunkModifyRequests(workingDn, consolidatedModifications);
    }

    /// <summary>
    /// Consolidates individual attribute value changes into grouped modifications per RFC 4511.
    /// Multiple changes to the same attribute with the same operation type are merged into a single
    /// DirectoryAttributeModification containing all values. This is more efficient and correct
    /// than sending separate modifications for each value.
    /// </summary>
    internal static List<ConsolidatedModification> ConsolidateModifications(PendingExport pendingExport, string? workingDn = null)
    {
        // Group changes by (attribute name, operation type), preserving the original attribute changes
        // for protected attribute handling
        var groups = new Dictionary<(string Name, DirectoryAttributeOperation Operation), ConsolidatedModification>(
            EqualityComparer<(string, DirectoryAttributeOperation)>.Default);

        foreach (var attrChange in pendingExport.AttributeValueChanges)
        {
            // A connector-defined attribute (Exchange's, #2021) is not in the directory: the Exchange step acts on it
            if (attrChange.Attribute == null || attrChange.Attribute.IsConnectorDefined)
                continue;

            var attrName = attrChange.Attribute.Name;

            // Skip RDN (Relative Distinguished Name) attributes — they cannot be modified via LDAP ModifyRequest.
            // These require a ModifyDNRequest (rename operation) instead, which is handled separately.
            // The actual RDN attribute is determined from the object's DN (e.g., CN for AD, uid for OpenLDAP).
            if (IsRdnAttribute(attrName, workingDn))
                continue;

            var operation = attrChange.ChangeType switch
            {
                PendingExportAttributeChangeType.Add => DirectoryAttributeOperation.Add,
                PendingExportAttributeChangeType.Update => DirectoryAttributeOperation.Replace,
                PendingExportAttributeChangeType.Remove => DirectoryAttributeOperation.Delete,
                PendingExportAttributeChangeType.RemoveAll => DirectoryAttributeOperation.Replace,
                _ => throw new InvalidOperationException($"Unknown attribute change type: {attrChange.ChangeType}")
            };

            var key = (attrName.ToLowerInvariant(), operation);

            if (!groups.TryGetValue(key, out var consolidated))
            {
                consolidated = new ConsolidatedModification
                {
                    AttributeName = attrName,
                    Operation = operation
                };
                groups[key] = consolidated;
            }

            consolidated.AttributeChanges.Add(attrChange);
        }

        return groups.Values.ToList();
    }

    /// <summary>
    /// The attributes this export changes that the Connected System's schema says hold one value at most, compared
    /// without regard to case. An attribute counts only when every change to it says so, and objectClass, which is
    /// multi-valued in every directory, never does.
    /// </summary>
    internal static HashSet<string> GetSingleValuedAttributeNames(PendingExport pendingExport) =>
        pendingExport.AttributeValueChanges
            .Where(change => change.Attribute != null)
            .GroupBy(change => change.Attribute!.Name, StringComparer.OrdinalIgnoreCase)
            .Where(changes => !changes.Key.Equals(LdapConnectorConstants.ObjectClassAttributeName, StringComparison.OrdinalIgnoreCase) &&
                              changes.All(change => change.Attribute!.AttributePlurality == AttributePlurality.SingleValued))
            .Select(changes => changes.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Splits consolidated modifications into one or more ModifyRequests, chunking any multi-valued modification that
    /// exceeds the configured batch size.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every attribute's first chunk goes into one base request, so small changes to several attributes still land
    /// together; each later chunk of an attribute is a request of its own, carrying that attribute's next batch of
    /// deletes and next batch of adds.
    /// </para>
    /// <para>
    /// Within any request, an attribute's delete comes before its add. RFC 4511 applies a modify's changes in order and
    /// checks the schema once, at the end, so this is always safe within one request, and it is what keeps a value the
    /// export removes under one case and adds under another (proxyAddresses compares without regard to case): added
    /// first, the add is refused or, under Permissive Modify, skipped as already present, and the delete then takes the
    /// value away while the export reports success. The same order lets a single-valued attribute change from its old
    /// value to its new one in one request.
    /// </para>
    /// <para>
    /// Batching must not separate such a pair either, or the delete could land in a later request than the add. So each
    /// value deleted and added again under a spelling that compares equal travels with its counterpart: the pairs fill
    /// the first requests, a pair is never split, and the attribute's other deletes and adds fill the room left, from the
    /// base request on. A later request therefore only ever deletes values no earlier request added, and the base request
    /// carries adds whenever the export has any, so an attribute an entry must hold (groupOfNames' member) is never left
    /// empty between requests. Every modification still respects the batch size, unless one value is deleted or added
    /// more often under different spellings than the batch size allows, which no directory holds.
    /// </para>
    /// </remarks>
    private List<ModifyRequest> ChunkModifyRequests(string workingDn, List<ConsolidatedModification> consolidatedModifications)
    {
        var baseModifications = new List<DirectoryAttributeModification>();
        var overflowModifications = new List<List<DirectoryAttributeModification>>();

        foreach (var chunks in consolidatedModifications
                     .GroupBy(consolidated => consolidated.AttributeName, StringComparer.OrdinalIgnoreCase)
                     .Select(attributeChanges => ChunkAttribute([.. attributeChanges]))
                     .Where(chunks => chunks.Count > 0))
        {
            baseModifications.AddRange(chunks[0]);
            overflowModifications.AddRange(chunks.Skip(1));
        }

        var requests = new List<ModifyRequest>();
        if (baseModifications.Count > 0)
            requests.Add(NewModifyRequest(workingDn, baseModifications));

        requests.AddRange(overflowModifications.Select(modifications => NewModifyRequest(workingDn, modifications)));

        if (requests.Count > 1)
        {
            _logger.Information("LdapConnectorExport.ChunkModifyRequests: Split modifications for '{Dn}' into {RequestCount} " +
                "LDAP requests (batch size: {BatchSize})", LogSanitiser.Sanitise(workingDn), requests.Count, _modifyBatchSize);
        }

        return requests;
    }

    private static ModifyRequest NewModifyRequest(string dn, List<DirectoryAttributeModification> modifications)
    {
        var request = new ModifyRequest(dn);
        request.Modifications.AddRange(modifications.ToArray());
        return request;
    }

    /// <summary>
    /// One attribute's modifications, by request: the first list joins the base request, and each later one is a
    /// request of its own. A replacement keeps its place among the attribute's changes; the deletes and adds are placed
    /// together, deletes first, where the first of them stood.
    /// </summary>
    private List<List<DirectoryAttributeModification>> ChunkAttribute(List<ConsolidatedModification> attributeChanges)
    {
        var chunks = new List<List<DirectoryAttributeModification>>();
        var valueChangesPlaced = false;

        foreach (var consolidated in attributeChanges)
        {
            if (consolidated.Operation == DirectoryAttributeOperation.Replace)
            {
                PlaceInChunks(chunks, BuildConsolidatedModification(consolidated).Select(modification => new List<DirectoryAttributeModification> { modification }));
            }
            else if (!valueChangesPlaced)
            {
                valueChangesPlaced = true;
                PlaceInChunks(chunks, ChunkDeletesAndAdds(
                    consolidated.AttributeName,
                    CollectValues(attributeChanges.Where(change => change.Operation == DirectoryAttributeOperation.Delete), consolidated.AttributeName),
                    CollectValues(attributeChanges.Where(change => change.Operation == DirectoryAttributeOperation.Add), consolidated.AttributeName)));
            }
        }

        return chunks;
    }

    private static void PlaceInChunks(List<List<DirectoryAttributeModification>> chunks, IEnumerable<List<DirectoryAttributeModification>> placed)
    {
        var index = 0;
        foreach (var modifications in placed)
        {
            if (index == chunks.Count)
                chunks.Add([]);
            chunks[index++].AddRange(modifications);
        }
    }

    /// <summary>
    /// The values one attribute's deletes or adds carry, in order. A remove with no value is skipped, as it always has
    /// been; an add with none keeps its place in the batch and contributes no value.
    /// </summary>
    private List<object?> CollectValues(IEnumerable<ConsolidatedModification> consolidated, string attributeName)
    {
        var values = new List<object?>();
        foreach (var attrChange in consolidated.SelectMany(modification => modification.AttributeChanges))
        {
            var value = GetAttributeValue(attrChange);
            if (value == null && attrChange.ChangeType == PendingExportAttributeChangeType.Remove)
            {
                _logger.Warning("LdapConnectorExport.CollectValues: Cannot remove value for '{AttrName}' - no value specified", attributeName);
                continue;
            }

            values.Add(value);
        }

        return values;
    }

    /// <summary>
    /// One attribute's deletes and adds in batches, each batch a delete (if any) followed by an add (if any); see the
    /// remarks on <see cref="ChunkModifyRequests"/> for why pairs travel together.
    /// </summary>
    private List<List<DirectoryAttributeModification>> ChunkDeletesAndAdds(string attributeName, List<object?> deletes, List<object?> adds)
    {
        var batches = new List<(List<object?> Deletes, List<object?> Adds)>();
        var pairedKeys = new HashSet<string>(LdapValueCollisionKey.Comparer);

        // Only an attribute with both deletes and adds can have pairs; a large group gaining or losing members has one.
        if (deletes.Count > 0 && adds.Count > 0)
        {
            var deletesByKey = LdapValueCollisionKey.Group(deletes);
            var addsByKey = LdapValueCollisionKey.Group(adds);
            pairedKeys.UnionWith(deletesByKey.Keys.Where(addsByKey.ContainsKey));

            foreach (var key in deletesByKey.Keys.Where(pairedKeys.Contains))
            {
                var pairDeletes = deletesByKey[key];
                var pairAdds = addsByKey[key];
                if (batches.Count == 0 ||
                    batches[^1].Deletes.Count + pairDeletes.Count > _modifyBatchSize ||
                    batches[^1].Adds.Count + pairAdds.Count > _modifyBatchSize)
                {
                    batches.Add(([], []));
                }

                batches[^1].Deletes.AddRange(pairDeletes);
                batches[^1].Adds.AddRange(pairAdds);
            }
        }

        FillBatches(batches, pairedKeys.Count == 0 ? deletes : deletes.Where(value => !IsPaired(value, pairedKeys)), batch => batch.Deletes);
        FillBatches(batches, pairedKeys.Count == 0 ? adds : adds.Where(value => !IsPaired(value, pairedKeys)), batch => batch.Adds);

        if (batches.Count > 1)
        {
            _logger.Information("LdapConnectorExport.ChunkDeletesAndAdds: Chunking {DeleteCount} deletes and {AddCount} adds for attribute " +
                "'{AttrName}' into {BatchCount} batches of up to {BatchSize}",
                deletes.Count, adds.Count, attributeName, batches.Count, _modifyBatchSize);
        }

        if (pairedKeys.Count > 0)
        {
            _logger.Debug("LdapConnectorExport.ChunkDeletesAndAdds: {PairCount} value(s) of '{AttrName}' are deleted and added again under a " +
                "spelling the directory may treat as the same; each is deleted before it is added, in the same request",
                pairedKeys.Count, attributeName);
        }

        return [.. batches.Select(batch => ToModifications(attributeName, batch.Deletes, batch.Adds))];
    }

    private void FillBatches(
        List<(List<object?> Deletes, List<object?> Adds)> batches,
        IEnumerable<object?> values,
        Func<(List<object?> Deletes, List<object?> Adds), List<object?>> side)
    {
        var index = 0;
        foreach (var value in values)
        {
            while (index < batches.Count && side(batches[index]).Count >= _modifyBatchSize)
                index++;

            if (index == batches.Count)
                batches.Add(([], []));

            side(batches[index]).Add(value);
        }
    }

    private static List<DirectoryAttributeModification> ToModifications(string attributeName, List<object?> deletes, List<object?> adds)
    {
        var modifications = new List<DirectoryAttributeModification>();
        if (deletes.Count > 0)
            modifications.Add(NewModification(attributeName, DirectoryAttributeOperation.Delete, deletes));
        if (adds.Count > 0)
            modifications.Add(NewModification(attributeName, DirectoryAttributeOperation.Add, adds));
        return modifications;
    }

    private static DirectoryAttributeModification NewModification(string attributeName, DirectoryAttributeOperation operation, IEnumerable<object?> values)
    {
        var modification = new DirectoryAttributeModification { Name = attributeName, Operation = operation };
        foreach (var value in values)
        {
            if (value is byte[] bytes)
                modification.Add(bytes);
            else if (value != null)
                modification.Add(value.ToString());
        }

        return modification;
    }

    private static bool IsPaired(object? value, HashSet<string> pairedKeys) =>
        LdapValueCollisionKey.Of(value) is { } key && pairedKeys.Contains(key);

    /// <summary>
    /// Builds one or more DirectoryAttributeModification objects from a consolidated replacement.
    /// If the number of values exceeds the batch size, returns multiple modifications (one per chunk).
    /// </summary>
    private List<DirectoryAttributeModification> BuildConsolidatedModification(ConsolidatedModification consolidated)
    {
        var results = new List<DirectoryAttributeModification>();

        // Collect all values from the attribute changes
        var values = new List<object?>();

        foreach (var attrChange in consolidated.AttributeChanges)
        {
            // Handle RemoveAll (clear attribute) - uses Replace with no values or a protected default
            if (attrChange.ChangeType == PendingExportAttributeChangeType.RemoveAll)
            {
                var mod = CreateModification(attrChange);
                if (mod != null)
                    results.Add(mod);
                return results; // RemoveAll is a standalone operation
            }

            var value = GetAttributeValue(attrChange);

            // Update with no value = clear attribute - handle as standalone
            if (value == null && attrChange.ChangeType == PendingExportAttributeChangeType.Update)
            {
                var mod = CreateModification(attrChange);
                if (mod != null)
                    results.Add(mod);
                return results;
            }

            values.Add(value);
        }

        if (values.Count == 0)
            return results;

        // If within batch size, create a single modification with all values
        if (values.Count <= _modifyBatchSize)
        {
            results.Add(NewModification(consolidated.AttributeName, consolidated.Operation, values));
            return results;
        }

        // Chunk into multiple modifications
        _logger.Information("LdapConnectorExport.BuildConsolidatedModification: Chunking {Count} values for attribute '{AttrName}' " +
            "(operation: {Operation}) into batches of {BatchSize}",
            values.Count, consolidated.AttributeName, consolidated.Operation, _modifyBatchSize);

        for (var i = 0; i < values.Count; i += _modifyBatchSize)
            results.Add(NewModification(consolidated.AttributeName, consolidated.Operation, values.Skip(i).Take(_modifyBatchSize)));

        return results;
    }

    /// <summary>
    /// The distinguished name a change of case to the object's own name gives it (#2051), or null when the export makes
    /// no such change. A distinguished name is compared ignoring case, because the directory owns the case of its parent
    /// components, so a case-only change is read from the object's relative name: the RDN attribute's new value
    /// (<c>cn</c>, which compares exactly), else the leaf of a staged distinguished name. The parent is kept as the
    /// directory holds it. Only a single-valued RDN whose value differs from the current one in case alone qualifies; any
    /// other change of name comes with a staged distinguished name, and is a rename of its own.
    /// </summary>
    internal static string? GetCaseOnlyRenameDn(PendingExport pendingExport, string currentDn, string? newDn)
    {
        if (!LdapDistinguishedName.TryParse(currentDn, out var current) || current.LeafRdn.Components.Count != 1)
            return null;

        var leaf = current.LeafRdn.Components[0];
        var newValue = pendingExport.AttributeValueChanges
            .LastOrDefault(change => change.ChangeType == PendingExportAttributeChangeType.Update &&
                                     change.StringValue != null &&
                                     change.Attribute?.Name.Equals(leaf.Type, StringComparison.OrdinalIgnoreCase) == true)?
            .StringValue;

        if (newValue == null &&
            LdapDistinguishedName.TryParse(newDn, out var staged) &&
            staged.LeafRdn.Components.Count == 1 &&
            staged.LeafRdn.Components[0].Type.Equals(leaf.Type, StringComparison.OrdinalIgnoreCase))
            newValue = staged.LeafRdn.Components[0].Value;

        if (newValue == null ||
            string.Equals(newValue, leaf.Value, StringComparison.Ordinal) ||
            !string.Equals(newValue, leaf.Value, StringComparison.OrdinalIgnoreCase))
            return null;

        var newRdn = $"{leaf.Type}={LdapConnectorUtilities.EscapeRdnValue(newValue)}";
        return current.Parent == null ? newRdn : $"{newRdn},{current.Parent}";
    }

    /// <summary>
    /// Checks if an attribute name is an RDN (Relative Distinguished Name) attribute.
    /// RDN attributes cannot be modified via LDAP ModifyRequest — they require ModifyDNRequest.
    /// The actual RDN attribute varies by directory and object type (e.g., CN for AD users, uid for OpenLDAP).
    /// We determine the RDN by parsing it from the object's current DN.
    /// </summary>
    /// <param name="attrName">The attribute name to check.</param>
    /// <param name="dn">The current DN of the object, used to determine the actual RDN attribute.</param>
    private static bool IsRdnAttribute(string attrName, string? dn = null)
    {
        // distinguishedName is always skipped — it's the full DN, not a modifiable attribute
        if (attrName.Equals("distinguishedName", StringComparison.OrdinalIgnoreCase))
            return true;

        // If we have the DN, the RDN attribute is the type of the leaf RDN's first component.
        if (!string.IsNullOrEmpty(dn) &&
            LdapDistinguishedName.TryParse(dn, out var parsedDn) &&
            parsedDn.LeafRdn.Components.Count > 0)
        {
            return attrName.Equals(parsedDn.LeafRdn.Components[0].Type, StringComparison.OrdinalIgnoreCase);
        }

        // Fallback: common RDN attributes (when the DN is unavailable or unparseable)
        return attrName.Equals("cn", StringComparison.OrdinalIgnoreCase) ||
               attrName.Equals("ou", StringComparison.OrdinalIgnoreCase) ||
               attrName.Equals("dc", StringComparison.OrdinalIgnoreCase) ||
               attrName.Equals("uid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether an LDAP result code or error message indicates the target object does not exist.
    /// Handles both standard LDAP NoSuchObject (32) and Samba AD's Windows DS error code 0x2030 (8240),
    /// which is ERROR_DS_NO_SUCH_OBJECT. Samba AD sometimes returns this as ResultCode.Other with the
    /// Windows error code embedded in the error message rather than using the standard LDAP result code.
    /// </summary>
    internal static bool IsNoSuchObjectResult(ResultCode? resultCode, string? errorMessage)
    {
        if (resultCode == ResultCode.NoSuchObject)
            return true;

        // Samba AD returns ERROR_DS_NO_SUCH_OBJECT (0x2030 / 8240) embedded in the error message
        // when the target object does not exist, often with ResultCode.Other instead of NoSuchObject.
        if (!string.IsNullOrEmpty(errorMessage) &&
            (errorMessage.Contains("00002030", StringComparison.OrdinalIgnoreCase) ||
             errorMessage.Contains("DSID-0C090CE2", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    /// <summary>
    /// Represents a group of attribute changes consolidated by attribute name and operation type.
    /// </summary>
    internal class ConsolidatedModification
    {
        public required string AttributeName { get; init; }
        public required DirectoryAttributeOperation Operation { get; init; }
        public List<PendingExportAttributeValueChange> AttributeChanges { get; } = [];
    }

    /// <summary>
    /// The result of an update whose modify requests have all reached the state they asked for. A renamed object
    /// reports its new Distinguished Name, so JIM records where the object now lives; an enable withheld until a password
    /// is delivered (#2158) is reported as awaiting it. Whether the update wrote the enabled state is judged from the
    /// requests as built, so an attribute reported awaiting a password is always one that wrote it.
    /// </summary>
    private ConnectedSystemExportResult UpdateSucceeded(string workingDn, bool wasRenamed, List<ModifyRequest> modifyRequests,
        IReadOnlyList<string>? attributesAwaitingInitialPassword)
    {
        _logger.Debug("LdapConnectorExport: Successfully updated object at '{Dn}' with {Count} modify requests",
            LogSanitiser.Sanitise(workingDn), modifyRequests.Count);

        var result = ConnectedSystemExportResult.Succeeded(null, wasRenamed ? workingDn : null, attributesAwaitingInitialPassword);
        result.SetsAccountEnabledState = WritesAccountEnabledState(modifyRequests
            .SelectMany(request => request.Modifications.Cast<DirectoryAttributeModification>())
            .Select(modification => modification.Name));
        return result;
    }

    /// <summary>
    /// The result of a create the directory accepted, saying which attributes carry an interim value until an Initial
    /// Password is delivered (#2039) and whether the add wrote the account's enabled state (#2064).
    /// </summary>
    private ConnectedSystemExportResult CreateSucceeded(string? externalId, string dn, IReadOnlyList<string>? attributesAwaitingInitialPassword, AddRequest addRequest)
    {
        var result = ConnectedSystemExportResult.Succeeded(externalId, dn, attributesAwaitingInitialPassword);
        result.SetsAccountEnabledState = WritesAccountEnabledState(addRequest.Attributes.Cast<DirectoryAttribute>().Select(attribute => attribute.Name));
        return result;
    }

    /// <summary>
    /// The result of a delete performed as a disable, saying whether the disable wrote the account's enabled state
    /// (#2064), so an Initial Password still owed to the account never re-enables it.
    /// </summary>
    private ConnectedSystemExportResult DisableSucceeded(string disabledThrough)
    {
        var result = ConnectedSystemExportResult.Succeeded();
        result.SetsAccountEnabledState = WritesAccountEnabledState([disabledThrough]);
        return result;
    }

    /// <summary>
    /// Whether a request writing these attributes writes the account's enabled state (#2064): <c>userAccountControl</c>,
    /// on the Active Directory family only. Elsewhere a password delivery's Enable Account means nothing, so there is no
    /// enabled state for an Attribute Flow to take over from it.
    /// </summary>
    private bool WritesAccountEnabledState(IEnumerable<string> attributeNames) =>
        _directoryType is LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD
        && attributeNames.Any(name => name.Equals(LdapConnectorConstants.UserAccountControlAttributeName, StringComparison.OrdinalIgnoreCase));

    #region Modify requests: Permissive Modify and the fallback in parts

    // Active Directory's ERROR_MEMBER_NOT_IN_GROUP (0x561), which it reports with unwillingToPerform (53) when asked
    // to remove a member a group does not hold.
    private const string ActiveDirectoryMemberNotInGroupCode = "00000561";

    // Samba AD's words for the same refusal, also under unwillingToPerform (53): "Attribute member doesn't exist for
    // target GUID <guid>". Samba sends it even when the modify carries Permissive Modify, and likewise answers
    // entryAlreadyExists for the add of a member the group already holds.
    private const string SambaMemberNotPresentWording = "doesn't exist for target GUID";

    // Samba AD's words when the group held the member once and has since lost it, under the same code: it keeps the
    // removed link and answers "Attribute member already deleted for target GUID <guid>" from it.
    private const string SambaMemberAlreadyRemovedWording = "already deleted for target GUID";

    // The attribute Active Directory and Samba AD hold group membership in, and the only one whose entryAlreadyExists
    // and unwillingToPerform answers are read as "already there" or "already gone": entryAlreadyExists is also how
    // they report an account name in use, which is a genuine failure.
    private const string ActiveDirectoryMemberAttributeName = "member";

    /// <summary>
    /// Sends one ModifyRequest of an export and sees it through to the state it asks for, or fails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An LDAP modify is atomic: a directory that finds one value to add already present, or one value to remove
    /// already absent, refuses the whole request and applies none of the others. An export asks for a state, and that
    /// state holds for such a value, so the refusal must not fail the export; but nor may the request be treated as a
    /// success, because every other change it carried was dropped with it.
    /// </para>
    /// <para>
    /// Where the directory advertises Permissive Modify the request carries it, and the directory itself accepts such
    /// values. Where it does not, or where a directory refuses regardless (Samba AD still refuses the removal of an
    /// absent member, and OpenLDAP the delete of any absent value), the request is applied again in parts, halving any
    /// part the directory refuses again; see <see cref="LdapModifyBisection"/>. Any other refusal propagates and fails
    /// the export, as it always has.
    /// </para>
    /// </remarks>
    /// <param name="request">The request to send.</param>
    /// <param name="dn">The entry it modifies, for the log.</param>
    /// <param name="singleValuedAttributes">The attributes the Connected System's schema says are single-valued.</param>
    private void SendModify(ModifyRequest request, string dn, IReadOnlySet<string> singleValuedAttributes)
    {
        AttachPermissiveModifyIfOffered(request);
        DirectoryOperationException? refusedForAValueInPlace = null;
        try
        {
            SendModifyOrThrow(request);
        }
        catch (DirectoryOperationException rejection) when (IsRefusedForAValueAlreadyInDesiredState(rejection, request))
        {
            refusedForAValueInPlace = rejection;
        }

        if (refusedForAValueInPlace == null)
            return;

        var bisection = BeginBisection(request, dn, singleValuedAttributes, refusedForAValueInPlace);
        while (bisection.TryTakeNext(out var step))
        {
            AttachPermissiveModifyIfOffered(step);
            try
            {
                SendModifyOrThrow(step);
                bisection.Applied();
            }
            catch (DirectoryOperationException stepRejection) when (bisection.IsAlreadyInDesiredState(stepRejection))
            {
                bisection.RefusedAsAlreadyInDesiredState();
            }
        }

        CompleteBisection(dn, bisection);
    }

    /// <summary>
    /// The async form of <see cref="SendModify"/>, for the concurrent export path.
    /// </summary>
    private async Task SendModifyAsync(ModifyRequest request, string dn, IReadOnlySet<string> singleValuedAttributes)
    {
        AttachPermissiveModifyIfOffered(request);
        DirectoryOperationException? refusedForAValueInPlace = null;
        try
        {
            await SendModifyOrThrowAsync(request);
        }
        catch (DirectoryOperationException rejection) when (IsRefusedForAValueAlreadyInDesiredState(rejection, request))
        {
            refusedForAValueInPlace = rejection;
        }

        if (refusedForAValueInPlace == null)
            return;

        var bisection = BeginBisection(request, dn, singleValuedAttributes, refusedForAValueInPlace);
        while (bisection.TryTakeNext(out var step))
        {
            AttachPermissiveModifyIfOffered(step);
            try
            {
                await SendModifyOrThrowAsync(step);
                bisection.Applied();
            }
            catch (DirectoryOperationException stepRejection) when (bisection.IsAlreadyInDesiredState(stepRejection))
            {
                bisection.RefusedAsAlreadyInDesiredState();
            }
        }

        CompleteBisection(dn, bisection);
    }

    private void SendModifyOrThrow(ModifyRequest request) =>
        ThrowUnlessSuccessful((ModifyResponse)_executor.SendRequest(request));

    private async Task SendModifyOrThrowAsync(ModifyRequest request) =>
        ThrowUnlessSuccessful((ModifyResponse)await _executor.SendRequestAsync(request));

    /// <summary>
    /// Raises a returned failure the way the platform raises one. LdapConnection throws DirectoryOperationException,
    /// carrying the response, for every result other than success, so a failure is only ever returned by an executor
    /// that does not; raising it identically means one path decides what every refusal means.
    /// </summary>
    private static void ThrowUnlessSuccessful(ModifyResponse response)
    {
        if (response.ResultCode != ResultCode.Success)
            throw new DirectoryOperationException(response, response.ErrorMessage);
    }

    /// <summary>
    /// Attaches the Permissive Modify control to a request that adds or deletes values, where the directory advertised
    /// it on this connection. A request that only replaces cannot meet a value already present or already absent, and
    /// is left as it is.
    /// </summary>
    private void AttachPermissiveModifyIfOffered(ModifyRequest request)
    {
        if (!_directoryAdvertisesPermissiveModify || !CarriesValueChanges(request))
            return;

        var alreadyAttached = request.Controls.Cast<DirectoryControl>()
            .Any(control => control.Type == LdapConnectorConstants.LDAP_SERVER_PERMISSIVE_MODIFY_OID);
        if (!alreadyAttached)
            request.Controls.Add(new PermissiveModifyControl());
    }

    private static bool CarriesValueChanges(ModifyRequest request) =>
        request.Modifications.Cast<DirectoryAttributeModification>()
            .Any(modification => modification.Operation is DirectoryAttributeOperation.Add or DirectoryAttributeOperation.Delete);

    /// <summary>
    /// Whether the directory refused this request because a value it adds is already present, or a value it deletes is
    /// already absent: the only refusal that applying the request in parts can resolve.
    /// </summary>
    private bool IsRefusedForAValueAlreadyInDesiredState(DirectoryOperationException rejection, ModifyRequest request) =>
        request.Modifications.Cast<DirectoryAttributeModification>()
            .Any(modification => MeansAlreadyInDesiredState(rejection, modification));

    /// <summary>
    /// Whether a refusal says this modification's value is already where the export wants it.
    /// </summary>
    /// <remarks>
    /// RFC directories (OpenLDAP, 389 Directory Server) answer attributeOrValueExists (20) for a value already present
    /// and noSuchAttribute (16) for a value already absent, on any attribute, and Active Directory does the same for
    /// most attributes. For group membership, Active Directory and Samba AD answer entryAlreadyExists (68) for a member
    /// already present, and unwillingToPerform (53) for one already absent, naming the reason (Active Directory's
    /// ERROR_MEMBER_NOT_IN_GROUP code, Samba AD's "doesn't exist for target GUID", or "already deleted for target GUID"
    /// for a member the group held once). Both codes mean something else
    /// elsewhere (entryAlreadyExists is also an account name in use; unwillingToPerform also refuses to take a member
    /// out of its primary group), so they are read this way only for the AD family's member attribute, and
    /// unwillingToPerform only when the directory gave that reason.
    /// </remarks>
    private bool MeansAlreadyInDesiredState(DirectoryOperationException rejection, DirectoryAttributeModification modification)
    {
        if (rejection.Response is not { } response)
            return false;

        var isActiveDirectoryMembership = (_directoryType is LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD) &&
                                          modification.Name.Equals(ActiveDirectoryMemberAttributeName, StringComparison.OrdinalIgnoreCase);

        return modification.Operation switch
        {
            DirectoryAttributeOperation.Add =>
                response.ResultCode == ResultCode.AttributeOrValueExists ||
                (isActiveDirectoryMembership && response.ResultCode == ResultCode.EntryAlreadyExists),
            DirectoryAttributeOperation.Delete =>
                response.ResultCode == ResultCode.NoSuchAttribute ||
                (isActiveDirectoryMembership && response.ResultCode == ResultCode.UnwillingToPerform &&
                 SaysMemberNotInGroup(rejection, response)),
            _ => false
        };
    }

    private static bool SaysMemberNotInGroup(DirectoryOperationException rejection, DirectoryResponse response)
    {
        var message = $"{response.ErrorMessage} {rejection.Message}";
        return message.Contains(ActiveDirectoryMemberNotInGroupCode, StringComparison.OrdinalIgnoreCase) ||
               message.Contains(SambaMemberNotPresentWording, StringComparison.OrdinalIgnoreCase) ||
               message.Contains(SambaMemberAlreadyRemovedWording, StringComparison.OrdinalIgnoreCase);
    }

    private LdapModifyBisection BeginBisection(ModifyRequest rejected, string dn, IReadOnlySet<string> singleValuedAttributes, DirectoryOperationException rejection)
    {
        var bisection = new LdapModifyBisection(rejected, singleValuedAttributes, _directoryType, MeansAlreadyInDesiredState);

        _logger.Information("LdapConnectorExport: The directory refused the whole modify of '{Dn}' ({ResultCode}: {Message}) because a value " +
            "it carries is already in the state the export asks for, so none of its changes were applied. Applying its {ValueChanges} value changes " +
            "in parts, halving any part the directory refuses again.",
            LogSanitiser.Sanitise(dn), rejection.Response?.ResultCode,
            LogSanitiser.Sanitise(string.IsNullOrEmpty(rejection.Response?.ErrorMessage) ? rejection.Message : rejection.Response.ErrorMessage),
            bisection.ValueChanges);

        return bisection;
    }

    private void CompleteBisection(string dn, LdapModifyBisection bisection)
    {
        Interlocked.Add(ref _valuesAlreadyInDesiredState, bisection.ValuesAlreadyInDesiredState);

        _logger.Information("LdapConnectorExport: Applied the modify of '{Dn}' in parts ({RequestCount} requests). {AlreadyInDesiredState} of " +
            "{ValueChanges} value changes were already in the desired state; the rest were applied.",
            LogSanitiser.Sanitise(dn), bisection.RequestsSent, bisection.ValuesAlreadyInDesiredState, bisection.ValueChanges);
    }

    private void LogValuesAlreadyInDesiredStateSummary(string caller)
    {
        var total = ValuesAlreadyInDesiredState;
        if (total == 0)
            return;

        _logger.Information("{Caller}: {Count} value changes were already in the desired state (a value to add already present, or one to " +
            "remove already absent) and were treated as achieved", caller, total);
    }

    #endregion

    /// <summary>
    /// Builds a ModifyDNRequest for renaming/moving an LDAP object.
    /// </summary>
    private ModifyDNRequest BuildModifyDnRequest(string currentDn, string newDn)
    {
        var (newRdn, newParentDn) = LdapConnectorUtilities.ParseDistinguishedName(newDn);
        var (_, currentParentDn) = LdapConnectorUtilities.ParseDistinguishedName(currentDn);

        if (string.IsNullOrEmpty(newRdn))
        {
            throw new InvalidOperationException($"Cannot rename object: Unable to parse new RDN from DN '{newDn}'");
        }

        var isMove = !string.IsNullOrEmpty(newParentDn) &&
                     !newParentDn.Equals(currentParentDn, StringComparison.OrdinalIgnoreCase);

        _logger.Debug("LdapConnectorExport.BuildModifyDnRequest: NewRdn: '{NewRdn}', NewParent: '{NewParent}', IsMove: {IsMove}",
            LogSanitiser.Sanitise(newRdn), LogSanitiser.Sanitise(newParentDn) ?? "(same)", isMove);

        return new ModifyDNRequest(
            currentDn,
            newParentDn,  // New parent (null if not moving)
            newRdn        // New RDN (e.g., "CN=New Name")
        );
    }

    /// <summary>
    /// Parses the current userAccountControl value from a SearchResponse and sets the ACCOUNTDISABLE bit.
    /// </summary>
    private static int ParseUacAndSetDisableBit(SearchResponse searchResponse)
    {
        var currentValue = 0;
        var entry = searchResponse.Entries[0];
        if (entry.Attributes.Contains("userAccountControl"))
        {
            var valueStr = entry.Attributes["userAccountControl"][0]?.ToString();
            if (!string.IsNullOrEmpty(valueStr))
            {
                int.TryParse(valueStr, out currentValue);
            }
        }

        return currentValue | LdapConnectorConstants.UAC_ACCOUNTDISABLE;
    }

    /// <summary>
    /// Builds an AddRequest for creating a container (OU or CN).
    /// </summary>
    private AddRequest BuildContainerAddRequest(string containerDn)
    {
        var (rdn, _) = LdapConnectorUtilities.ParseDistinguishedName(containerDn);

        if (string.IsNullOrEmpty(rdn))
        {
            throw new InvalidOperationException($"Cannot create container: Unable to parse RDN from DN '{containerDn}'");
        }

        _logger.Information("LdapConnectorExport.BuildContainerAddRequest: Creating missing container '{ContainerDn}'", LogSanitiser.Sanitise(containerDn));

        var addRequest = new AddRequest(containerDn);

        // Determine object class based on RDN type
        if (rdn.StartsWith("OU=", StringComparison.OrdinalIgnoreCase))
        {
            // Organisational Unit
            addRequest.Attributes.Add(new DirectoryAttribute("objectClass", "organizationalUnit"));

            // Extract the OU name for the 'ou' attribute (some directories require this)
            var ouName = rdn.Substring(3); // Remove "OU="
            addRequest.Attributes.Add(new DirectoryAttribute("ou", ouName));
        }
        else if (rdn.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
        {
            // Container - use the generic container objectClass
            addRequest.Attributes.Add(new DirectoryAttribute("objectClass", "container"));

            // Extract the CN name
            var cnName = rdn.Substring(3); // Remove "CN="
            addRequest.Attributes.Add(new DirectoryAttribute("cn", cnName));
        }
        else
        {
            throw new InvalidOperationException($"Cannot create container with RDN type '{rdn.Split('=')[0]}'. Only OU and CN containers are supported.");
        }

        return addRequest;
    }

    /// <summary>
    /// Gets the new distinguished name from the Pending Export's attribute changes.
    /// This is used to detect if a rename operation is needed.
    /// </summary>
    private static string? GetNewDistinguishedName(PendingExport pendingExport)
    {
        var dnAttrChange = pendingExport.AttributeValueChanges
            .FirstOrDefault(a => a.Attribute?.Name.Equals("distinguishedName", StringComparison.OrdinalIgnoreCase) == true);

        return dnAttrChange?.StringValue;
    }

    private DirectoryAttributeModification? CreateModification(PendingExportAttributeValueChange attrChange)
    {
        var operation = attrChange.ChangeType switch
        {
            PendingExportAttributeChangeType.Add => DirectoryAttributeOperation.Add,
            PendingExportAttributeChangeType.Update => DirectoryAttributeOperation.Replace,
            PendingExportAttributeChangeType.Remove => DirectoryAttributeOperation.Delete,
            PendingExportAttributeChangeType.RemoveAll => DirectoryAttributeOperation.Replace,
            _ => throw new InvalidOperationException($"Unknown attribute change type: {attrChange.ChangeType}")
        };

        var modification = new DirectoryAttributeModification
        {
            Name = attrChange.Attribute!.Name,
            Operation = operation
        };

        // For RemoveAll, we replace with no values (clears the attribute)
        // However, some AD attributes are protected and cannot be cleared - use default value instead
        if (attrChange.ChangeType == PendingExportAttributeChangeType.RemoveAll)
        {
            var attrName = attrChange.Attribute!.Name;
            if (ProtectedAttributeDefaults.TryGetValue(attrName, out var defaultValue))
            {
                _logger.Debug("LdapConnectorExport.CreateModification: Attribute '{AttrName}' is protected and cannot be cleared via RemoveAll. " +
                    "Substituting default value '{DefaultValue}' instead.",
                    attrName, defaultValue);
                modification.Add(defaultValue);

                // Update the attribute change so reconciliation knows what value to expect
                UpdateAttributeChangeWithSubstitutedValue(attrChange, defaultValue);
            }
            return modification;
        }

        // For Remove, we need to specify the value to remove
        // For Add/Update, we need to specify the value to add/set
        var value = GetAttributeValue(attrChange);
        if (value != null)
        {
            if (value is byte[] bytes)
                modification.Add(bytes);
            else
                modification.Add(value.ToString());
        }
        else if (attrChange.ChangeType == PendingExportAttributeChangeType.Remove)
        {
            // If removing and no value specified, we can't proceed
            _logger.Warning("LdapConnectorExport.CreateModification: Cannot remove value for '{AttrName}' - no value specified",
                attrChange.Attribute.Name);
            return null;
        }
        else if (attrChange.ChangeType == PendingExportAttributeChangeType.Update)
        {
            // Update with no value means "clear this attribute".
            // Some AD attributes are protected and cannot be deleted - they must be replaced
            // with a default value instead.
            var attrName = attrChange.Attribute.Name;
            if (ProtectedAttributeDefaults.TryGetValue(attrName, out var defaultValue))
            {
                _logger.Debug("LdapConnectorExport.CreateModification: Attribute '{AttrName}' is protected and cannot be cleared. " +
                    "Substituting default value '{DefaultValue}' instead.",
                    attrName, defaultValue);
                modification.Add(defaultValue);

                // Update the attribute change so reconciliation knows what value to expect
                UpdateAttributeChangeWithSubstitutedValue(attrChange, defaultValue);
            }
            // else: no value and not protected - modification will have no values, which clears the attribute
        }

        return modification;
    }

    private static object? GetAttributeValue(PendingExportAttributeValueChange attrChange)
    {
        if (!string.IsNullOrEmpty(attrChange.StringValue))
            return attrChange.StringValue;

        if (attrChange.IntValue.HasValue)
            return attrChange.IntValue.Value.ToString();

        if (attrChange.LongValue.HasValue)
            return attrChange.LongValue.Value.ToString();

        if (attrChange.DecimalValue.HasValue)
            // Defensive probe: no LDAP syntax maps to the Decimal data type, so a Decimal-bearing
            // change should never reach this connector. Returning the canonical invariant string
            // keeps an unexpected carrier from falling through to the null return below, which the
            // caller treats as an instruction to clear the attribute (silent data loss).
            return DecimalAttributeValue.ToCanonicalString(attrChange.DecimalValue.Value);

        if (attrChange.DateTimeValue.HasValue)
            return ConvertDateTimeToLdapFormat(attrChange.DateTimeValue.Value);

        if (attrChange.ByteValue != null)
            return attrChange.ByteValue;

        if (attrChange.GuidValue.HasValue)
            // ToMicrosoftBytes() produces Microsoft GUID byte order (little-endian first 3 components).
            // This is correct for AD/Samba AD targets. For RFC 4122 targets (OpenLDAP binary UUIDs),
            // use IdentifierParser.ToRfc4122Bytes() instead when that connector path is implemented.
            return IdentifierParser.ToMicrosoftBytes(attrChange.GuidValue.Value);

        if (attrChange.BoolValue.HasValue)
            return attrChange.BoolValue.Value.ToString().ToUpperInvariant();

        if (!string.IsNullOrEmpty(attrChange.UnresolvedReferenceValue))
            return attrChange.UnresolvedReferenceValue;

        return null;
    }

    /// <summary>
    /// A date as GeneralizedTime (#2153, S6): the canonical value, in UTC, with the invariant culture. A whole second
    /// keeps the <c>.0Z</c> form every directory has always been sent, which is all Active Directory holds; a fraction is
    /// written with up to six digits, trailing zeros trimmed, so a microsecond reaches an RFC directory exactly. Export
    /// staging has already floored the value to the attribute's resolution, so a directory that holds whole seconds is
    /// never sent a fraction.
    /// </summary>
    internal static string ConvertDateTimeToLdapFormat(DateTime dateTime)
    {
        var canonical = DateTimeAttributeValue.ToCanonical(dateTime);
        return canonical.Ticks % TimeSpan.TicksPerSecond == 0
            ? canonical.ToString("yyyyMMddHHmmss'.0Z'", CultureInfo.InvariantCulture)
            : canonical.ToString("yyyyMMddHHmmss.FFFFFF'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Updates a PendingExportAttributeValueChange with the substituted value for a protected attribute.
    /// This ensures that reconciliation knows what value to expect on the CSO after export.
    /// </summary>
    private static void UpdateAttributeChangeWithSubstitutedValue(PendingExportAttributeValueChange attrChange, string substitutedValue)
    {
        // Determine the correct property to set based on the attribute type
        var attrType = attrChange.Attribute?.Type ?? AttributeDataType.NotSet;

        switch (attrType)
        {
            case AttributeDataType.LongNumber:
                if (long.TryParse(substitutedValue, out var longVal))
                    attrChange.LongValue = longVal;
                break;

            case AttributeDataType.Number:
                if (int.TryParse(substitutedValue, out var intVal))
                    attrChange.IntValue = intVal;
                break;

            case AttributeDataType.Text:
            default:
                attrChange.StringValue = substitutedValue;
                break;
        }
    }

    /// <summary>
    /// Returns the Distinguished Name this export would write to: the destination of a rename where one is being
    /// performed, the new object's own Distinguished Name on a create, otherwise the object's current one.
    /// </summary>
    /// <remarks>
    /// It is the destination that matters for scope, not the origin. An object being moved back INTO managed scope
    /// is exactly the fix an administrator would apply, and must not be refused; an object being moved out of it is
    /// the failure this check exists for.
    /// </remarks>
    private static string? GetExportDestinationDistinguishedName(PendingExport pendingExport)
    {
        if (pendingExport.ChangeType == PendingExportChangeType.Create)
            return GetDistinguishedNameForCreate(pendingExport);

        var newDn = GetNewDistinguishedName(pendingExport);
        return !string.IsNullOrEmpty(newDn) ? newDn : GetDistinguishedNameForUpdate(pendingExport);
    }

    private static string? GetDistinguishedNameForCreate(PendingExport pendingExport)
    {
        // For create operations, the DN should be in the attribute changes
        var dnAttrChange = pendingExport.AttributeValueChanges
            .FirstOrDefault(a => a.Attribute?.Name.Equals("distinguishedName", StringComparison.OrdinalIgnoreCase) == true);

        return dnAttrChange?.StringValue;
    }

    private static string? GetDistinguishedNameForUpdate(PendingExport pendingExport)
    {
        // For update/delete operations, use the CSO's secondary external ID (which is the DN for LDAP)
        var cso = pendingExport.ConnectedSystemObject;
        if (cso != null)
        {
            Log.Verbose("GetDistinguishedNameForUpdate: CSO {CsoId} - SecondaryExternalIdAttributeId={SecondaryAttrId}, AttributeValues.Count={AttrCount}",
                cso.Id, cso.SecondaryExternalIdAttributeId, cso.AttributeValues?.Count ?? 0);

            if (cso.SecondaryExternalIdAttributeId.HasValue && cso.AttributeValues != null)
            {
                var matchingAttrValues = cso.AttributeValues
                    .Where(av => av.AttributeId == cso.SecondaryExternalIdAttributeId.Value)
                    .ToList();

                Log.Verbose("GetDistinguishedNameForUpdate: Found {Count} attribute value(s) matching SecondaryExternalIdAttributeId {AttrId}",
                    matchingAttrValues.Count, cso.SecondaryExternalIdAttributeId.Value);

                foreach (var av in matchingAttrValues)
                {
                    Log.Verbose("GetDistinguishedNameForUpdate: AttrValue Id={Id}, AttributeId={AttrId}, StringValue='{StringValue}'",
                        av.Id, av.AttributeId, LogSanitiser.Sanitise(av.StringValue));
                }
            }

            if (cso.SecondaryExternalIdAttributeValue?.StringValue != null)
            {
                Log.Debug("GetDistinguishedNameForUpdate: Using CSO SecondaryExternalIdAttributeValue: '{DN}'",
                    LogSanitiser.Sanitise(cso.SecondaryExternalIdAttributeValue.StringValue));
                return cso.SecondaryExternalIdAttributeValue.StringValue;
            }

            Log.Debug("GetDistinguishedNameForUpdate: CSO {CsoId} has no SecondaryExternalIdAttributeValue, falling back to attribute changes",
                cso.Id);
        }
        else
        {
            Log.Debug("GetDistinguishedNameForUpdate: PendingExport {ExportId} has no ConnectedSystemObject", pendingExport.Id);
        }

        // Fallback: check attribute changes for DN
        var dnFromAttrChanges = GetDistinguishedNameForCreate(pendingExport);
        if (dnFromAttrChanges != null)
        {
            Log.Debug("GetDistinguishedNameForUpdate: Using DN from attribute changes: '{DN}'", LogSanitiser.Sanitise(dnFromAttrChanges));
        }
        else
        {
            Log.Warning("GetDistinguishedNameForUpdate: No DN found for Pending Export {ExportId} - neither CSO secondary external ID nor attribute changes contain DN",
                pendingExport.Id);
        }

        return dnFromAttrChanges;
    }

    /// <summary>
    /// The object classes this entry is to be created with, in the order JIM stated them: the structural class
    /// first, then every auxiliary class the entry belongs to.
    /// </summary>
    /// <remarks>
    /// JIM computes these and puts them on the Pending Export, because which auxiliary classes an entry belongs to
    /// follows from the values that entry actually has, not from the configuration; see ClassMembershipPlanner. The
    /// Object Type's name is the fallback for a Connected System that predates this, or one whose schema has not
    /// been refreshed since: it is what this connector always used, and it remains correct for an entry with no
    /// auxiliary classes.
    /// </remarks>
    private static List<string> GetObjectClasses(PendingExport pendingExport)
    {
        var stated = pendingExport.AttributeValueChanges
            .Where(change => change.Attribute?.Name.Equals(LdapConnectorConstants.ObjectClassAttributeName, StringComparison.OrdinalIgnoreCase) == true)
            .Select(change => change.StringValue)
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (stated.Count > 0)
            return stated;

        var typeName = pendingExport.ConnectedSystemObject?.Type?.Name
                       ?? pendingExport.AttributeValueChanges.FirstOrDefault()?.Attribute?.ConnectedSystemObjectType?.Name;

        return string.IsNullOrEmpty(typeName) ? [] : [typeName];
    }

    /// <summary>
    /// Checks whether a DirectoryOperationException represents a constraint violation that could be
    /// caused by the directory rejecting the placeholder member DN (e.g. referential integrity enabled).
    /// </summary>
    private static bool IsPlaceholderConstraintViolation(DirectoryOperationException ex)
    {
        var response = ex.Response;
        if (response == null)
            return false;

        // A value already in use (a group's mail under OpenLDAP's slapo-unique, say) is a constraint violation too,
        // but the directory said what it was, and it was not the placeholder.
        if (LdapUniquenessRejectionClassifier.TryClassify(ex, out _))
            return false;

        return response.ResultCode is ResultCode.ConstraintViolation or ResultCode.NoSuchObject
                                      or ResultCode.UnwillingToPerform;
    }

    /// <summary>
    /// Logs an export the directory refused. A value already in use is a per-object data conflict the export run reports
    /// on the object (and may correct, Unique Value Generation's Collision Remediation), and an account Active Directory
    /// will not enable without a password (#2039) is configuration the export run reports on the object with its
    /// remedies, so both are logged at Warning; every other failure stays an Error, as it always was.
    /// </summary>
    private void LogExportFailure(Exception ex, string caller, PendingExport pendingExport)
    {
        if (LdapUniquenessRejectionClassifier.TryClassify(ex, out _))
            _logger.Warning(ex, "{Caller}: Pending Export {Id} ({ChangeType}) was rejected because a value it carries is already in use",
                caller, pendingExport.Id, pendingExport.ChangeType);
        else if (LdapAccountEnableRefusalClassifier.IsAccountEnableRefusal(ex, pendingExport, _directoryType))
            _logger.Warning(ex, "{Caller}: Pending Export {Id} ({ChangeType}) was refused because Active Directory will not enable an account that has no password",
                caller, pendingExport.Id, pendingExport.ChangeType);
        else
            _logger.Error(ex, "{Caller}: Failed to process Pending Export {Id} ({ChangeType})", caller, pendingExport.Id, pendingExport.ChangeType);
    }

    /// <summary>
    /// The result for an export the directory refused. A rejection because a value is already in use is classified
    /// as such, naming the attribute where the directory named it (Unique Value Generation, decision 9), and Active
    /// Directory refusing to enable an account that has no password is classified with the remedies named (#2039);
    /// every other failure is reported as it always was. The directory's own message is kept whole either way.
    /// </summary>
    private ConnectedSystemExportResult FailureFor(Exception ex, PendingExport pendingExport)
    {
        if (LdapUniquenessRejectionClassifier.TryClassify(ex, out var rejectedAttributeName))
            return ConnectedSystemExportResult.ValueAlreadyInUse(ex.Message, rejectedAttributeName);

        if (LdapAccountEnableRefusalClassifier.IsAccountEnableRefusal(ex, pendingExport, _directoryType))
            return LdapAccountEnableRefusalClassifier.FailureFor(ex);

        // Any other result the directory returned is a refusal, not a JIM fault (#2118).
        return LdapDirectoryRefusalClassifier.FailureFor(ex) ?? ConnectedSystemExportResult.Failed(ex.Message);
    }

    /// <summary>
    /// Returns a structured export error when the placeholder member DN is rejected by the directory.
    /// This typically means the directory has referential integrity enabled and the placeholder DN
    /// does not reference an existing entry.
    /// </summary>
    private ConnectedSystemExportResult HandlePlaceholderRejection(string dn, DirectoryOperationException ex)
    {
        _logger.Warning(ex,
            "LdapConnectorExport: Placeholder member '{Placeholder}' was rejected by the directory for '{Dn}'. " +
            "The directory may have referential integrity enabled. " +
            "Update the '{Setting}' connector setting to point to an existing entry.",
            LogSanitiser.Sanitise(_placeholderMemberDn), LogSanitiser.Sanitise(dn), LdapConnectorConstants.SETTING_GROUP_PLACEHOLDER_MEMBER_DN);

        return ConnectedSystemExportResult.Failed(
            $"Failed to add placeholder member '{_placeholderMemberDn}' to group '{dn}'; " +
            $"the directory may have referential integrity enabled. " +
            $"Update the '{LdapConnectorConstants.SETTING_GROUP_PLACEHOLDER_MEMBER_DN}' connector setting to point to an existing entry in the directory.",
            ConnectedSystemExportErrorType.PlaceholderMemberConstraintViolation);
    }

    /// <summary>
    /// Inspects the consolidated modifications for a groupOfNames/groupOfUniqueNames export and injects
    /// placeholder member add/remove operations as needed:
    /// - If member removals would leave the group empty, adds the placeholder DN (to satisfy the MUST constraint).
    /// - If members are being added to a group that currently only has the placeholder, removes the placeholder.
    /// </summary>
    private void InjectPlaceholderModificationsIfNeeded(
        PendingExport pendingExport,
        List<ConsolidatedModification> consolidatedModifications)
    {
        // The class that demands members, whichever position it sits in: an entry may carry auxiliary classes
        // alongside its structural one.
        var objectClass = GetObjectClasses(pendingExport)
            .FirstOrDefault(className => LdapConnectorConstants.MUST_MEMBER_OBJECT_CLASSES.Contains(className));

        if (string.IsNullOrEmpty(objectClass))
            return;

        var memberAttrName = GetMemberAttributeName(objectClass);

        // Count current members on the CSO (the directory's current state as JIM knows it)
        var currentMemberCount = GetCurrentMemberCount(pendingExport, memberAttrName);

        // Count how many members are being added and removed in this export
        var addCount = 0;
        var removeCount = 0;
        foreach (var mod in consolidatedModifications)
        {
            if (!mod.AttributeName.Equals(memberAttrName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (mod.Operation == DirectoryAttributeOperation.Add)
                addCount += mod.AttributeChanges.Count;
            else if (mod.Operation == DirectoryAttributeOperation.Delete)
                removeCount += mod.AttributeChanges.Count;
        }

        // Check if the group currently only has the placeholder member
        var hasOnlyPlaceholder = currentMemberCount == 1 && GroupHasOnlyPlaceholderMember(pendingExport, memberAttrName);

        // Scenario 1: Removing members would leave the group empty — inject placeholder
        if (removeCount > 0 && !hasOnlyPlaceholder)
        {
            var effectiveMemberCount = currentMemberCount - removeCount + addCount;
            if (effectiveMemberCount <= 0)
            {
                _logger.Information(
                    "LdapConnectorExport.InjectPlaceholderModificationsIfNeeded: Removing all members from '{ObjectClass}' group. " +
                    "Injecting placeholder member '{Placeholder}' to satisfy MUST constraint.",
                    objectClass, LogSanitiser.Sanitise(_placeholderMemberDn));

                var placeholderAttr = new ConnectedSystemObjectTypeAttribute
                {
                    Name = memberAttrName,
                    ConnectedSystemObjectType = pendingExport.ConnectedSystemObject!.Type
                };

                consolidatedModifications.Add(new ConsolidatedModification
                {
                    AttributeName = memberAttrName,
                    Operation = DirectoryAttributeOperation.Add,
                    AttributeChanges = { new PendingExportAttributeValueChange
                    {
                        Attribute = placeholderAttr,
                        ChangeType = PendingExportAttributeChangeType.Add,
                        StringValue = _placeholderMemberDn
                    }}
                });
            }
        }

        // Scenario 2: Adding members to a placeholder-only group — remove the placeholder
        if (addCount > 0 && hasOnlyPlaceholder)
        {
            _logger.Information(
                "LdapConnectorExport.InjectPlaceholderModificationsIfNeeded: Adding real members to placeholder-only '{ObjectClass}' group. " +
                "Removing placeholder member '{Placeholder}'.",
                objectClass, LogSanitiser.Sanitise(_placeholderMemberDn));

            var placeholderAttr = new ConnectedSystemObjectTypeAttribute
            {
                Name = memberAttrName,
                ConnectedSystemObjectType = pendingExport.ConnectedSystemObject!.Type
            };

            consolidatedModifications.Add(new ConsolidatedModification
            {
                AttributeName = memberAttrName,
                Operation = DirectoryAttributeOperation.Delete,
                AttributeChanges = { new PendingExportAttributeValueChange
                {
                    Attribute = placeholderAttr,
                    ChangeType = PendingExportAttributeChangeType.Remove,
                    StringValue = _placeholderMemberDn
                }}
            });
        }
    }

    /// <summary>
    /// Gets the number of current member values on the CSO for the specified member attribute.
    /// </summary>
    private static int GetCurrentMemberCount(PendingExport pendingExport, string memberAttrName)
    {
        var cso = pendingExport.ConnectedSystemObject;
        if (cso?.AttributeValues == null)
            return 0;

        return cso.AttributeValues.Count(av =>
            av.Attribute?.Name.Equals(memberAttrName, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// Checks whether the group's only current member is the placeholder DN.
    /// </summary>
    private bool GroupHasOnlyPlaceholderMember(PendingExport pendingExport, string memberAttrName)
    {
        var cso = pendingExport.ConnectedSystemObject;
        if (cso?.AttributeValues == null)
            return false;

        var memberValues = cso.AttributeValues
            .Where(av => av.Attribute?.Name.Equals(memberAttrName, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        if (memberValues.Count != 1)
            return false;

        var memberDn = memberValues[0].UnresolvedReferenceValue ?? memberValues[0].StringValue;
        return _placeholderMemberDn.Equals(memberDn, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether placeholder member handling is required for the given Pending Export.
    /// Placeholder handling is needed when:
    /// 1. The directory is not Active Directory or Samba AD (which use 'group' class with no MUST member constraint)
    /// 2. The object class is one that requires at least one member value (e.g. groupOfNames, groupOfUniqueNames)
    /// </summary>
    internal bool RequiresPlaceholderMember(PendingExport pendingExport)
    {
        // AD and Samba AD use the 'group' class which allows empty groups — no placeholder needed
        if (_directoryType is LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD)
            return false;

        return GetObjectClasses(pendingExport)
            .Any(className => LdapConnectorConstants.MUST_MEMBER_OBJECT_CLASSES.Contains(className));
    }

    /// <summary>
    /// Gets the member attribute name for the object class. Returns "member" for groupOfNames
    /// and "uniqueMember" for groupOfUniqueNames.
    /// </summary>
    internal static string GetMemberAttributeName(string objectClass)
    {
        return objectClass.Equals("groupOfUniqueNames", StringComparison.OrdinalIgnoreCase)
            ? "uniqueMember"
            : "member";
    }

    private string? GetSettingValue(string settingName)
    {
        return _settings.SingleOrDefault(s => s.Setting.Name == settingName)?.StringValue;
    }

    private bool? GetSettingBoolValue(string settingName)
    {
        return _settings.SingleOrDefault(s => s.Setting.Name == settingName)?.CheckboxValue;
    }

    /// <summary>
    /// Builds a list of container DNs from root to the specified container.
    /// For example, for "OU=Engineering,OU=Users,DC=panoply,DC=local", returns:
    /// ["OU=Users,DC=panoply,DC=local", "OU=Engineering,OU=Users,DC=panoply,DC=local"]
    /// </summary>
    internal static List<string> BuildContainerChain(string containerDn)
    {
        var chain = new List<string>();
        var currentDn = containerDn;

        while (!string.IsNullOrEmpty(currentDn))
        {
            var (rdn, parentDn) = LdapConnectorUtilities.ParseDistinguishedName(currentDn);

            if (string.IsNullOrEmpty(rdn))
                break;

            // Check if this is an OU or CN container (not DC - domain components are not created)
            if (rdn.StartsWith("OU=", StringComparison.OrdinalIgnoreCase) ||
                rdn.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                chain.Add(currentDn);
            }
            else if (rdn.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
            {
                // We've reached the domain level - stop here as DCs already exist
                break;
            }

            currentDn = parentDn ?? string.Empty;
        }

        // Reverse so we process from root to leaf
        chain.Reverse();
        return chain;
    }

    #endregion
}
