using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

/// <summary>
/// Handles LDAP export functionality - creating, updating, and deleting objects in LDAP directories.
/// </summary>
internal class LdapConnectorExport
{
    private readonly LdapConnection _connection;
    private readonly IList<ConnectedSystemSettingValue> _settings;
    private readonly ILogger _logger;

    // Setting names
    private const string SettingDeleteBehaviour = "Delete Behaviour";
    private const string SettingDisableAttribute = "Disable Attribute";
    private const string SettingCreateContainersAsNeeded = "Create containers as needed?";

    // Cache of containers we've already created or verified exist during this export session
    private readonly HashSet<string> _verifiedContainers = new(StringComparer.OrdinalIgnoreCase);

    // Track containers created during this export session (for auto-selection in JIM)
    private readonly List<string> _createdContainerExternalIds = new();

    /// <summary>
    /// Gets the list of container external IDs (DNs for LDAP) that were created during this export session.
    /// Used by JIM to auto-select newly created containers in the hierarchy.
    /// </summary>
    internal IReadOnlyList<string> CreatedContainerExternalIds => _createdContainerExternalIds.AsReadOnly();

    internal LdapConnectorExport(
        LdapConnection connection,
        IList<ConnectedSystemSettingValue> settings,
        ILogger logger)
    {
        _connection = connection;
        _settings = settings;
        _logger = logger;
    }

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

        var response = (AddResponse)_connection.SendRequest(addRequest);
        if (response.ResultCode != ResultCode.Success)
        {
            // Build a more descriptive error message that includes the DN and attributes being added
            var attrNames = string.Join(", ", addRequest.Attributes.Cast<DirectoryAttribute>().Select(a => $"'{a.Name}'"));
            var errorDetail = $"LDAP add failed for DN '{dn}'. " +
                $"Attributes: {attrNames}. " +
                $"LDAP error ({(int)response.ResultCode}): {response.ErrorMessage}";

            throw new LdapException((int)response.ResultCode, errorDetail);
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

            var searchResponse = (SearchResponse)_connection.SendRequest(searchRequest);
            if (searchResponse.ResultCode != ResultCode.Success || searchResponse.Entries.Count == 0)
            {
                _logger.Warning("LdapConnectorExport.FetchObjectGuid: Failed to fetch objectGUID for '{Dn}'", dn);
                return null;
            }

            var entry = searchResponse.Entries[0];
            if (entry.Attributes.Contains("objectGUID"))
            {
                var guidBytes = entry.Attributes["objectGUID"][0] as byte[];
                if (guidBytes != null && guidBytes.Length == 16)
                {
                    var guid = new Guid(guidBytes);
                    return guid.ToString();
                }
            }

            return null;
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

        if (modifyRequest.Modifications.Count == 0)
        {
            _logger.Debug("LdapConnectorExport.ProcessUpdate: No attribute modifications to apply for '{Dn}'", workingDn);
            // Return the new DN if renamed, so it can be updated on the CSO
            return wasRenamed ? ExportResult.Succeeded(null, workingDn) : ExportResult.Succeeded();
        }

        var response = (ModifyResponse)_connection.SendRequest(modifyRequest);
        if (response.ResultCode != ResultCode.Success)
        {
            // Handle "attribute or value exists" error gracefully for Add operations.
            // This can happen when trying to add a member that already exists in a group.
            // LDAP error code 20 = LDAP_TYPE_OR_VALUE_EXISTS
            // Since the desired state (member is in group) is already achieved, treat as success.
            if (response.ResultCode == ResultCode.AttributeOrValueExists)
            {
                _logger.Warning("LdapConnectorExport.ProcessUpdate: Some attribute values already exist at '{Dn}'. " +
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

        _logger.Information("LdapConnectorExport.ProcessUpdate: Successfully updated object at '{Dn}' with {Count} modifications",
            workingDn, modifyRequest.Modifications.Count);

        // Return the new DN if renamed, so it can be updated on the CSO
        return wasRenamed ? ExportResult.Succeeded(null, workingDn) : ExportResult.Succeeded();
    }

    /// <summary>
    /// Processes a rename (move) operation using ModifyDNRequest.
    /// Returns the new DN after the rename.
    /// </summary>
    private string ProcessRename(string currentDn, string newDn)
    {
        _logger.Debug("LdapConnectorExport.ProcessRename: Renaming object from '{OldDn}' to '{NewDn}'", currentDn, newDn);

        // Parse the new DN to extract the new RDN and new parent DN
        var (newRdn, newParentDn) = LdapConnectorUtilities.ParseDistinguishedName(newDn);
        var (_, currentParentDn) = LdapConnectorUtilities.ParseDistinguishedName(currentDn);

        if (string.IsNullOrEmpty(newRdn))
        {
            throw new InvalidOperationException($"Cannot rename object: Unable to parse new RDN from DN '{newDn}'");
        }

        // Determine if this is just a rename or also a move to a different container
        var isMove = !string.IsNullOrEmpty(newParentDn) &&
                     !newParentDn.Equals(currentParentDn, StringComparison.OrdinalIgnoreCase);

        var modifyDnRequest = new ModifyDNRequest(
            currentDn,
            newParentDn,  // New parent (null if not moving)
            newRdn        // New RDN (e.g., "CN=New Name")
        );

        // deleteOldRdn should be true to remove the old RDN value
        // This is the default behaviour in most LDAP implementations

        _logger.Debug("LdapConnectorExport.ProcessRename: Executing ModifyDNRequest - NewRdn: '{NewRdn}', NewParent: '{NewParent}', IsMove: {IsMove}",
            newRdn, newParentDn ?? "(same)", isMove);

        var response = (ModifyDNResponse)_connection.SendRequest(modifyDnRequest);
        if (response.ResultCode != ResultCode.Success)
        {
            throw new LdapException((int)response.ResultCode, response.ErrorMessage);
        }

        _logger.Information("LdapConnectorExport.ProcessRename: Successfully renamed object from '{OldDn}' to '{NewDn}'",
            currentDn, newDn);

        return newDn;
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
        var response = (DeleteResponse)_connection.SendRequest(deleteRequest);

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

            var response = (ModifyResponse)_connection.SendRequest(modifyRequest);
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

        var searchResponse = (SearchResponse)_connection.SendRequest(searchRequest);
        if (searchResponse.ResultCode != ResultCode.Success || searchResponse.Entries.Count == 0)
        {
            throw new LdapException((int)searchResponse.ResultCode,
                $"Failed to read current userAccountControl value for '{dn}'");
        }

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

        // Set the ACCOUNTDISABLE bit (0x2)
        var newValue = currentValue | LdapConnectorConstants.UAC_ACCOUNTDISABLE;

        var modifyRequest = new ModifyRequest(dn,
            DirectoryAttributeOperation.Replace,
            "userAccountControl",
            newValue.ToString());

        var modifyResponse = (ModifyResponse)_connection.SendRequest(modifyRequest);
        if (modifyResponse.ResultCode != ResultCode.Success)
        {
            throw new LdapException((int)modifyResponse.ResultCode, modifyResponse.ErrorMessage);
        }
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
        if (attrChange.ChangeType == PendingExportAttributeChangeType.RemoveAll)
        {
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
            return attrChange.GuidValue.Value.ToByteArray();

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
    /// Ensures that all parent containers (OUs) exist in the directory before creating an object.
    /// This method recursively creates any missing parent OUs starting from the root and working down.
    /// </summary>
    /// <param name="objectDn">The DN of the object to be created.</param>
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

    /// <summary>
    /// Checks if a container (OU) exists in the directory.
    /// </summary>
    private bool ContainerExists(string containerDn)
    {
        try
        {
            var searchRequest = new SearchRequest(
                containerDn,
                "(objectClass=*)",
                SearchScope.Base,
                "objectClass");

            var response = (SearchResponse)_connection.SendRequest(searchRequest);
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

    /// <summary>
    /// Creates an Organisational Unit (OU) container in the directory.
    /// </summary>
    private void CreateContainer(string containerDn)
    {
        var (rdn, _) = LdapConnectorUtilities.ParseDistinguishedName(containerDn);

        if (string.IsNullOrEmpty(rdn))
        {
            throw new InvalidOperationException($"Cannot create container: Unable to parse RDN from DN '{containerDn}'");
        }

        _logger.Information("LdapConnectorExport.CreateContainer: Creating missing container '{ContainerDn}'", containerDn);

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

        var response = (AddResponse)_connection.SendRequest(addRequest);
        if (response.ResultCode != ResultCode.Success)
        {
            throw new LdapException((int)response.ResultCode, $"Failed to create container '{containerDn}': {response.ErrorMessage}");
        }

        // Track the created container for auto-selection
        _createdContainerExternalIds.Add(containerDn);

        _logger.Information("LdapConnectorExport.CreateContainer: Successfully created container '{ContainerDn}'", containerDn);
    }
}
