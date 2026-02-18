using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
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
    /// We only include attributes that JIM might legitimately try to clear via sync rules.
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

    internal LdapConnectorExport(
        ILdapOperationExecutor executor,
        IList<ConnectedSystemSettingValue> settings,
        ILogger logger,
        int exportConcurrency = LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY)
    {
        _executor = executor;
        _settings = settings;
        _logger = logger;
        _exportConcurrency = Math.Clamp(exportConcurrency, 1, LdapConnectorConstants.MAX_EXPORT_CONCURRENCY);
    }

    #region Sequential execution (sync path)

    internal List<ExportResult> Execute(IList<PendingExport> pendingExports)
    {
        _logger.Debug("LdapConnectorExport.Execute: Starting export of {Count} pending exports", pendingExports.Count);

        var results = new List<ExportResult>();

        if (pendingExports.Count == 0)
        {
            _logger.Information("LdapConnectorExport.Execute: No pending exports to process");
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
                _logger.Error(ex, "LdapConnectorExport.Execute: Failed to process pending export {Id} ({ChangeType})",
                    pendingExport.Id, pendingExport.ChangeType);

                // Return failure result - ExportExecutionServer is responsible for updating
                // ErrorCount, Status, and retry timing (Q6 decision). The connector should
                // only report success or failure via ExportResult.
                results.Add(ExportResult.Failed(ex.Message));
            }
        }

        _logger.Information("LdapConnectorExport.Execute: Completed export processing of {Count} pending exports", pendingExports.Count);
        return results;
    }

    private ExportResult ProcessPendingExport(PendingExport pendingExport)
    {
        pendingExport.Status = PendingExportStatus.Executing;
        pendingExport.LastAttemptedAt = DateTime.UtcNow;

        ExportResult result;
        switch (pendingExport.ChangeType)
        {
            case PendingExportChangeType.Create:
                result = ProcessCreate(pendingExport);
                break;
            case PendingExportChangeType.Update:
                result = ProcessUpdate(pendingExport);
                break;
            case PendingExportChangeType.Delete:
                ProcessDelete(pendingExport);
                result = ExportResult.Succeeded();
                break;
            default:
                throw new InvalidOperationException($"Unknown change type: {pendingExport.ChangeType}");
        }

        pendingExport.Status = PendingExportStatus.Exported;
        _logger.Debug("LdapConnectorExport.ProcessPendingExport: Successfully processed {ChangeType} for {Id}",
            pendingExport.ChangeType, pendingExport.Id);
        return result;
    }

    private ExportResult ProcessCreate(PendingExport pendingExport)
    {
        // For create, we need to build the DN and all attributes
        var dn = GetDistinguishedNameForCreate(pendingExport);
        if (string.IsNullOrEmpty(dn))
            throw new InvalidOperationException("Cannot create object: Distinguished Name (DN) could not be determined from attribute changes.");

        _logger.Debug("LdapConnectorExport.ProcessCreate: Creating object at DN '{Dn}'", dn);

        // Ensure parent containers exist if the setting is enabled
        var createContainersAsNeeded = GetSettingBoolValue(SettingCreateContainersAsNeeded) ?? false;
        if (createContainersAsNeeded)
        {
            EnsureParentContainersExist(dn);
        }

        var addRequest = BuildAddRequest(pendingExport, dn);

        var response = (AddResponse)_executor.SendRequest(addRequest);
        if (response.ResultCode != ResultCode.Success)
        {
            ThrowAddFailure(addRequest, dn, response);
        }

        _logger.Information("LdapConnectorExport.ProcessCreate: Successfully created object at '{Dn}'", dn);

        // After successful create, fetch the system-assigned objectGUID
        var objectGuid = FetchObjectGuid(dn);
        if (objectGuid != null)
        {
            _logger.Debug("LdapConnectorExport.ProcessCreate: Retrieved objectGUID {ObjectGuid} for '{Dn}'", objectGuid, dn);
            return ExportResult.Succeeded(objectGuid, dn);
        }

        // objectGUID not available, return success without external ID
        return ExportResult.Succeeded(null, dn);
    }

    /// <summary>
    /// Fetches the objectGUID for a newly created object.
    /// </summary>
    private string? FetchObjectGuid(string dn)
    {
        try
        {
            var searchRequest = new SearchRequest(
                dn,
                "(objectClass=*)",
                SearchScope.Base,
                "objectGUID");

            var searchResponse = (SearchResponse)_executor.SendRequest(searchRequest);
            return ParseObjectGuidFromResponse(searchResponse, dn);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "LdapConnectorExport.FetchObjectGuid: Error fetching objectGUID for '{Dn}'", dn);
            return null;
        }
    }

    private ExportResult ProcessUpdate(PendingExport pendingExport)
    {
        var currentDn = GetDistinguishedNameForUpdate(pendingExport);
        if (string.IsNullOrEmpty(currentDn))
            throw new InvalidOperationException("Cannot update object: Distinguished Name (DN) could not be determined.");

        _logger.Debug("LdapConnectorExport.ProcessUpdate: Updating object at DN '{Dn}'", currentDn);

        // Check if a rename is needed (DN has changed)
        var newDn = GetNewDistinguishedName(pendingExport);
        var workingDn = currentDn;
        var wasRenamed = false;

        if (!string.IsNullOrEmpty(newDn) && !newDn.Equals(currentDn, StringComparison.OrdinalIgnoreCase))
        {
            // DN has changed - perform rename first
            workingDn = ProcessRename(currentDn, newDn);
            wasRenamed = true;
        }

        var modifyRequest = BuildModifyRequest(pendingExport, workingDn);

        if (modifyRequest.Modifications.Count == 0)
        {
            _logger.Debug("LdapConnectorExport.ProcessUpdate: No attribute modifications to apply for '{Dn}'", workingDn);
            // Return the new DN if renamed, so it can be updated on the CSO
            return wasRenamed ? ExportResult.Succeeded(null, workingDn) : ExportResult.Succeeded();
        }

        var response = (ModifyResponse)_executor.SendRequest(modifyRequest);
        return HandleModifyResponse(response, modifyRequest, workingDn, wasRenamed);
    }

    /// <summary>
    /// Processes a rename (move) operation using ModifyDNRequest.
    /// Returns the new DN after the rename.
    /// </summary>
    private string ProcessRename(string currentDn, string newDn)
    {
        _logger.Debug("LdapConnectorExport.ProcessRename: Renaming object from '{OldDn}' to '{NewDn}'", currentDn, newDn);

        var modifyDnRequest = BuildModifyDnRequest(currentDn, newDn);

        var response = (ModifyDNResponse)_executor.SendRequest(modifyDnRequest);
        if (response.ResultCode != ResultCode.Success)
        {
            throw new LdapException((int)response.ResultCode, response.ErrorMessage);
        }

        _logger.Information("LdapConnectorExport.ProcessRename: Successfully renamed object from '{OldDn}' to '{NewDn}'",
            currentDn, newDn);

        return newDn;
    }

    private void ProcessDelete(PendingExport pendingExport)
    {
        var dn = GetDistinguishedNameForUpdate(pendingExport);
        if (string.IsNullOrEmpty(dn))
            throw new InvalidOperationException("Cannot delete object: Distinguished Name (DN) could not be determined.");

        var deleteBehaviour = GetSettingValue(SettingDeleteBehaviour) ?? LdapConnectorConstants.DELETE_BEHAVIOUR_DELETE;

        if (deleteBehaviour == LdapConnectorConstants.DELETE_BEHAVIOUR_DISABLE)
        {
            ProcessDisable(pendingExport, dn);
        }
        else
        {
            ProcessHardDelete(dn);
        }
    }

    private void ProcessHardDelete(string dn)
    {
        _logger.Debug("LdapConnectorExport.ProcessHardDelete: Deleting object at DN '{Dn}'", dn);

        var deleteRequest = new DeleteRequest(dn);
        var response = (DeleteResponse)_executor.SendRequest(deleteRequest);

        if (response.ResultCode == ResultCode.NoSuchObject)
        {
            // Object already deleted - treat as idempotent success.
            // The desired state (object gone) is already achieved.
            _logger.Information("LdapConnectorExport.ProcessHardDelete: Object at '{Dn}' does not exist (already deleted), treating as success", dn);
            return;
        }

        if (response.ResultCode != ResultCode.Success)
        {
            throw new LdapException((int)response.ResultCode, response.ErrorMessage);
        }

        _logger.Information("LdapConnectorExport.ProcessHardDelete: Successfully deleted object at '{Dn}'", dn);
    }

    private void ProcessDisable(PendingExport pendingExport, string dn)
    {
        _logger.Debug("LdapConnectorExport.ProcessDisable: Disabling object at DN '{Dn}'", dn);

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

        _logger.Information("LdapConnectorExport.ProcessDisable: Successfully disabled object at '{Dn}'", dn);
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

        var response = (AddResponse)_executor.SendRequest(addRequest);
        if (response.ResultCode != ResultCode.Success)
        {
            throw new LdapException((int)response.ResultCode, $"Failed to create container '{containerDn}': {response.ErrorMessage}");
        }

        // Track the created container for auto-selection
        _createdContainerExternalIds.Add(containerDn);

        _logger.Information("LdapConnectorExport.CreateContainer: Successfully created container '{ContainerDn}'", containerDn);
    }

    #endregion

    #region Concurrent execution (async path)

    /// <summary>
    /// Executes pending exports asynchronously with configurable concurrency.
    /// When concurrency is 1, delegates to the sequential <see cref="Execute"/> method.
    /// When concurrency > 1, processes multiple exports concurrently using SemaphoreSlim
    /// while maintaining positional ordering of results.
    /// </summary>
    internal async Task<List<ExportResult>> ExecuteAsync(
        IList<PendingExport> pendingExports,
        CancellationToken cancellationToken)
    {
        _logger.Debug("LdapConnectorExport.ExecuteAsync: Starting export of {Count} pending exports (concurrency: {Concurrency})",
            pendingExports.Count, _exportConcurrency);

        if (pendingExports.Count == 0)
        {
            _logger.Information("LdapConnectorExport.ExecuteAsync: No pending exports to process");
            return new List<ExportResult>();
        }

        // If concurrency is 1, fall back to synchronous sequential processing
        // for maximum compatibility and simplicity
        if (_exportConcurrency <= 1)
            return Execute(pendingExports);

        // Pre-allocate results array to maintain positional ordering.
        // Each task writes to its own unique index - no shared mutable state between tasks.
        var results = new ExportResult[pendingExports.Count];
        using var semaphore = new SemaphoreSlim(_exportConcurrency);

        var tasks = new Task[pendingExports.Count];
        for (var i = 0; i < pendingExports.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = i; // Capture for closure
            var pendingExport = pendingExports[i];

            tasks[i] = Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    results[index] = await ProcessPendingExportAsync(pendingExport);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "LdapConnectorExport.ExecuteAsync: Failed to process pending export {Id} ({ChangeType})",
                        pendingExport.Id, pendingExport.ChangeType);
                    results[index] = ExportResult.Failed(ex.Message);
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken);
        }

        await Task.WhenAll(tasks);

        _logger.Information("LdapConnectorExport.ExecuteAsync: Completed export processing of {Count} pending exports",
            pendingExports.Count);
        return results.ToList();
    }

    private async Task<ExportResult> ProcessPendingExportAsync(PendingExport pendingExport)
    {
        pendingExport.Status = PendingExportStatus.Executing;
        pendingExport.LastAttemptedAt = DateTime.UtcNow;

        var result = pendingExport.ChangeType switch
        {
            PendingExportChangeType.Create => await ProcessCreateAsync(pendingExport),
            PendingExportChangeType.Update => await ProcessUpdateAsync(pendingExport),
            PendingExportChangeType.Delete => await ProcessDeleteAsync(pendingExport),
            _ => throw new InvalidOperationException($"Unknown change type: {pendingExport.ChangeType}")
        };

        pendingExport.Status = PendingExportStatus.Exported;
        _logger.Debug("LdapConnectorExport.ProcessPendingExportAsync: Successfully processed {ChangeType} for {Id}",
            pendingExport.ChangeType, pendingExport.Id);
        return result;
    }

    private async Task<ExportResult> ProcessCreateAsync(PendingExport pendingExport)
    {
        var dn = GetDistinguishedNameForCreate(pendingExport);
        if (string.IsNullOrEmpty(dn))
            throw new InvalidOperationException("Cannot create object: Distinguished Name (DN) could not be determined from attribute changes.");

        _logger.Debug("LdapConnectorExport.ProcessCreateAsync: Creating object at DN '{Dn}'", dn);

        var createContainersAsNeeded = GetSettingBoolValue(SettingCreateContainersAsNeeded) ?? false;
        if (createContainersAsNeeded)
        {
            await EnsureParentContainersExistAsync(dn);
        }

        var addRequest = BuildAddRequest(pendingExport, dn);

        // Sequential within this export: create must succeed before GUID fetch
        var response = (AddResponse)await _executor.SendRequestAsync(addRequest);
        if (response.ResultCode != ResultCode.Success)
        {
            ThrowAddFailure(addRequest, dn, response);
        }

        _logger.Information("LdapConnectorExport.ProcessCreateAsync: Successfully created object at '{Dn}'", dn);

        var objectGuid = await FetchObjectGuidAsync(dn);
        if (objectGuid != null)
        {
            _logger.Debug("LdapConnectorExport.ProcessCreateAsync: Retrieved objectGUID {ObjectGuid} for '{Dn}'", objectGuid, dn);
            return ExportResult.Succeeded(objectGuid, dn);
        }

        return ExportResult.Succeeded(null, dn);
    }

    private async Task<string?> FetchObjectGuidAsync(string dn)
    {
        try
        {
            var searchRequest = new SearchRequest(
                dn,
                "(objectClass=*)",
                SearchScope.Base,
                "objectGUID");

            var searchResponse = (SearchResponse)await _executor.SendRequestAsync(searchRequest);
            return ParseObjectGuidFromResponse(searchResponse, dn);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "LdapConnectorExport.FetchObjectGuidAsync: Error fetching objectGUID for '{Dn}'", dn);
            return null;
        }
    }

    private async Task<ExportResult> ProcessUpdateAsync(PendingExport pendingExport)
    {
        var currentDn = GetDistinguishedNameForUpdate(pendingExport);
        if (string.IsNullOrEmpty(currentDn))
            throw new InvalidOperationException("Cannot update object: Distinguished Name (DN) could not be determined.");

        _logger.Debug("LdapConnectorExport.ProcessUpdateAsync: Updating object at DN '{Dn}'", currentDn);

        var newDn = GetNewDistinguishedName(pendingExport);
        var workingDn = currentDn;
        var wasRenamed = false;

        if (!string.IsNullOrEmpty(newDn) && !newDn.Equals(currentDn, StringComparison.OrdinalIgnoreCase))
        {
            // Sequential within this export: rename must complete before modify
            workingDn = await ProcessRenameAsync(currentDn, newDn);
            wasRenamed = true;
        }

        var modifyRequest = BuildModifyRequest(pendingExport, workingDn);

        if (modifyRequest.Modifications.Count == 0)
        {
            _logger.Debug("LdapConnectorExport.ProcessUpdateAsync: No attribute modifications to apply for '{Dn}'", workingDn);
            return wasRenamed ? ExportResult.Succeeded(null, workingDn) : ExportResult.Succeeded();
        }

        var response = (ModifyResponse)await _executor.SendRequestAsync(modifyRequest);
        return HandleModifyResponse(response, modifyRequest, workingDn, wasRenamed);
    }

    private async Task<string> ProcessRenameAsync(string currentDn, string newDn)
    {
        _logger.Debug("LdapConnectorExport.ProcessRenameAsync: Renaming object from '{OldDn}' to '{NewDn}'", currentDn, newDn);

        var modifyDnRequest = BuildModifyDnRequest(currentDn, newDn);

        var response = (ModifyDNResponse)await _executor.SendRequestAsync(modifyDnRequest);
        if (response.ResultCode != ResultCode.Success)
        {
            throw new LdapException((int)response.ResultCode, response.ErrorMessage);
        }

        _logger.Information("LdapConnectorExport.ProcessRenameAsync: Successfully renamed object from '{OldDn}' to '{NewDn}'",
            currentDn, newDn);

        return newDn;
    }

    private async Task<ExportResult> ProcessDeleteAsync(PendingExport pendingExport)
    {
        var dn = GetDistinguishedNameForUpdate(pendingExport);
        if (string.IsNullOrEmpty(dn))
            throw new InvalidOperationException("Cannot delete object: Distinguished Name (DN) could not be determined.");

        var deleteBehaviour = GetSettingValue(SettingDeleteBehaviour) ?? LdapConnectorConstants.DELETE_BEHAVIOUR_DELETE;

        if (deleteBehaviour == LdapConnectorConstants.DELETE_BEHAVIOUR_DISABLE)
        {
            await ProcessDisableAsync(pendingExport, dn);
        }
        else
        {
            await ProcessHardDeleteAsync(dn);
        }

        return ExportResult.Succeeded();
    }

    private async Task ProcessHardDeleteAsync(string dn)
    {
        _logger.Debug("LdapConnectorExport.ProcessHardDeleteAsync: Deleting object at DN '{Dn}'", dn);

        var deleteRequest = new DeleteRequest(dn);
        var response = (DeleteResponse)await _executor.SendRequestAsync(deleteRequest);

        if (response.ResultCode == ResultCode.NoSuchObject)
        {
            _logger.Information("LdapConnectorExport.ProcessHardDeleteAsync: Object at '{Dn}' does not exist (already deleted), treating as success", dn);
            return;
        }

        if (response.ResultCode != ResultCode.Success)
        {
            throw new LdapException((int)response.ResultCode, response.ErrorMessage);
        }

        _logger.Information("LdapConnectorExport.ProcessHardDeleteAsync: Successfully deleted object at '{Dn}'", dn);
    }

    private async Task ProcessDisableAsync(PendingExport pendingExport, string dn)
    {
        _logger.Debug("LdapConnectorExport.ProcessDisableAsync: Disabling object at DN '{Dn}'", dn);

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

        _logger.Information("LdapConnectorExport.ProcessDisableAsync: Successfully disabled object at '{Dn}'", dn);
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

        var response = (AddResponse)await _executor.SendRequestAsync(addRequest);
        if (response.ResultCode != ResultCode.Success)
        {
            throw new LdapException((int)response.ResultCode, $"Failed to create container '{containerDn}': {response.ErrorMessage}");
        }

        _createdContainerExternalIds.Add(containerDn);

        _logger.Information("LdapConnectorExport.CreateContainerAsync: Successfully created container '{ContainerDn}'", containerDn);
    }

    #endregion

    #region Shared helpers (used by both sync and async paths)

    /// <summary>
    /// Builds an AddRequest for creating a new LDAP object.
    /// </summary>
    private AddRequest BuildAddRequest(PendingExport pendingExport, string dn)
    {
        var addRequest = new AddRequest(dn);

        // Get the object class from the pending export
        var objectClass = GetObjectClass(pendingExport);
        if (!string.IsNullOrEmpty(objectClass))
        {
            addRequest.Attributes.Add(new DirectoryAttribute("objectClass", objectClass));
        }

        // Add all attributes from the pending export
        foreach (var attrChange in pendingExport.AttributeValueChanges)
        {
            if (attrChange.Attribute == null)
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
                addRequest.Attributes.Add(new DirectoryAttribute(attrName, value));
            }
        }

        return addRequest;
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
    private string? ParseObjectGuidFromResponse(SearchResponse searchResponse, string dn)
    {
        if (searchResponse.ResultCode != ResultCode.Success || searchResponse.Entries.Count == 0)
        {
            _logger.Warning("LdapConnectorExport.ParseObjectGuidFromResponse: Failed to fetch objectGUID for '{Dn}'", dn);
            return null;
        }

        var entry = searchResponse.Entries[0];
        if (entry.Attributes.Contains("objectGUID"))
        {
            var guidBytes = entry.Attributes["objectGUID"][0] as byte[];
            if (guidBytes != null && guidBytes.Length == 16)
            {
                // AD objectGUID uses Microsoft GUID byte order (little-endian first 3 components)
                var guid = IdentifierParser.FromMicrosoftBytes(guidBytes);
                return guid.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// Builds a ModifyRequest for updating attributes on an existing LDAP object.
    /// </summary>
    private ModifyRequest BuildModifyRequest(PendingExport pendingExport, string workingDn)
    {
        var modifyRequest = new ModifyRequest(workingDn);

        foreach (var attrChange in pendingExport.AttributeValueChanges)
        {
            if (attrChange.Attribute == null)
                continue;

            var attrName = attrChange.Attribute.Name;

            // Skip RDN (Relative Distinguished Name) attributes - they cannot be modified via LDAP ModifyRequest
            // These require a ModifyDNRequest (rename operation) instead, which is handled above.
            // - distinguishedName: The full DN, immutable via MODIFY
            // - cn: Common Name, the RDN for most object types (users, groups, etc.)
            // - ou: Organisational Unit name, RDN for OUs
            // - dc: Domain Component, RDN for domain objects
            if (attrName.Equals("distinguishedName", StringComparison.OrdinalIgnoreCase) ||
                attrName.Equals("cn", StringComparison.OrdinalIgnoreCase) ||
                attrName.Equals("ou", StringComparison.OrdinalIgnoreCase) ||
                attrName.Equals("dc", StringComparison.OrdinalIgnoreCase))
                continue;

            var modification = CreateModification(attrChange);
            if (modification != null)
            {
                modifyRequest.Modifications.Add(modification);
            }
        }

        return modifyRequest;
    }

    /// <summary>
    /// Handles the response from a ModifyRequest, including the special case for AttributeOrValueExists.
    /// </summary>
    private ExportResult HandleModifyResponse(ModifyResponse response, ModifyRequest modifyRequest, string workingDn, bool wasRenamed)
    {
        if (response.ResultCode != ResultCode.Success)
        {
            // Handle "attribute or value exists" error gracefully for Add operations.
            // This can happen when trying to add a member that already exists in a group.
            // LDAP error code 20 = LDAP_TYPE_OR_VALUE_EXISTS
            // Since the desired state (member is in group) is already achieved, treat as success.
            if (response.ResultCode == ResultCode.AttributeOrValueExists)
            {
                _logger.Warning("LdapConnectorExport.HandleModifyResponse: Some attribute values already exist at '{Dn}'. " +
                    "This typically means a group member was already present. Treating as success. Error: {Error}",
                    workingDn, response.ErrorMessage);
                return wasRenamed ? ExportResult.Succeeded(null, workingDn) : ExportResult.Succeeded();
            }

            // Build a more descriptive error message that includes the attributes being modified
            var modifiedAttrs = string.Join(", ", modifyRequest.Modifications
                .Cast<DirectoryAttributeModification>()
                .Select(m => $"'{m.Name}' ({m.Operation})"));
            var errorDetail = $"LDAP modify failed for DN '{workingDn}'. " +
                $"Modified attributes: {modifiedAttrs}. " +
                $"LDAP error ({(int)response.ResultCode}): {response.ErrorMessage}";

            throw new LdapException((int)response.ResultCode, errorDetail);
        }

        _logger.Information("LdapConnectorExport.HandleModifyResponse: Successfully updated object at '{Dn}' with {Count} modifications",
            workingDn, modifyRequest.Modifications.Count);

        return wasRenamed ? ExportResult.Succeeded(null, workingDn) : ExportResult.Succeeded();
    }

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
            newRdn, newParentDn ?? "(same)", isMove);

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

        _logger.Information("LdapConnectorExport.BuildContainerAddRequest: Creating missing container '{ContainerDn}'", containerDn);

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
    /// Gets the new distinguished name from the pending export's attribute changes.
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

    private static string ConvertDateTimeToLdapFormat(DateTime dateTime)
    {
        // LDAP uses generalizedTime format: YYYYMMDDHHMMSS.0Z
        return dateTime.ToUniversalTime().ToString("yyyyMMddHHmmss.0Z");
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
                        av.Id, av.AttributeId, av.StringValue);
                }
            }

            if (cso.SecondaryExternalIdAttributeValue?.StringValue != null)
            {
                Log.Debug("GetDistinguishedNameForUpdate: Using CSO SecondaryExternalIdAttributeValue: '{DN}'",
                    cso.SecondaryExternalIdAttributeValue.StringValue);
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
            Log.Debug("GetDistinguishedNameForUpdate: Using DN from attribute changes: '{DN}'", dnFromAttrChanges);
        }
        else
        {
            Log.Warning("GetDistinguishedNameForUpdate: No DN found for pending export {ExportId} - neither CSO secondary external ID nor attribute changes contain DN",
                pendingExport.Id);
        }

        return dnFromAttrChanges;
    }

    private static string? GetObjectClass(PendingExport pendingExport)
    {
        // Try to get object class from attribute changes
        var objectClassAttr = pendingExport.AttributeValueChanges
            .FirstOrDefault(a => a.Attribute?.Name.Equals("objectClass", StringComparison.OrdinalIgnoreCase) == true);

        if (objectClassAttr?.StringValue != null)
            return objectClassAttr.StringValue;

        // Try to get from CSO type
        if (pendingExport.ConnectedSystemObject?.Type?.Name != null)
            return pendingExport.ConnectedSystemObject.Type.Name;

        // Try to derive from attribute metadata
        var firstAttr = pendingExport.AttributeValueChanges.FirstOrDefault();
        return firstAttr?.Attribute?.ConnectedSystemObjectType?.Name;
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
    /// For example, for "OU=Engineering,OU=Users,DC=subatomic,DC=local", returns:
    /// ["OU=Users,DC=subatomic,DC=local", "OU=Engineering,OU=Users,DC=subatomic,DC=local"]
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
