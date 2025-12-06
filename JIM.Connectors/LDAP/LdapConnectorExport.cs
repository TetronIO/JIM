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

    internal LdapConnectorExport(
        LdapConnection connection,
        IList<ConnectedSystemSettingValue> settings,
        ILogger logger)
    {
        _connection = connection;
        _settings = settings;
        _logger = logger;
    }

    internal void Execute(IList<PendingExport> pendingExports)
    {
        _logger.Debug("LdapConnectorExport.Execute: Starting export of {Count} pending exports", pendingExports.Count);

        if (pendingExports.Count == 0)
        {
            _logger.Information("LdapConnectorExport.Execute: No pending exports to process");
            return;
        }

        foreach (var pendingExport in pendingExports)
        {
            try
            {
                ProcessPendingExport(pendingExport);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "LdapConnectorExport.Execute: Failed to process pending export {Id} ({ChangeType})",
                    pendingExport.Id, pendingExport.ChangeType);

                pendingExport.ErrorCount++;
                pendingExport.LastAttemptedAt = DateTime.UtcNow;
                pendingExport.LastErrorMessage = ex.Message;

                // Calculate next retry time using exponential backoff
                var backoffMinutes = Math.Pow(2, pendingExport.ErrorCount);
                pendingExport.NextRetryAt = DateTime.UtcNow.AddMinutes(backoffMinutes);

                if (pendingExport.ErrorCount >= pendingExport.MaxRetries)
                {
                    pendingExport.Status = PendingExportStatus.Failed;
                    _logger.Warning("LdapConnectorExport.Execute: Pending export {Id} has exceeded max retries and is now Failed",
                        pendingExport.Id);
                }
            }
        }

        _logger.Information("LdapConnectorExport.Execute: Completed export processing of {Count} pending exports", pendingExports.Count);
    }

    private void ProcessPendingExport(PendingExport pendingExport)
    {
        pendingExport.Status = PendingExportStatus.Executing;
        pendingExport.LastAttemptedAt = DateTime.UtcNow;

        switch (pendingExport.ChangeType)
        {
            case PendingExportChangeType.Create:
                ProcessCreate(pendingExport);
                break;
            case PendingExportChangeType.Update:
                ProcessUpdate(pendingExport);
                break;
            case PendingExportChangeType.Delete:
                ProcessDelete(pendingExport);
                break;
            default:
                throw new InvalidOperationException($"Unknown change type: {pendingExport.ChangeType}");
        }

        pendingExport.Status = PendingExportStatus.Exported;
        _logger.Debug("LdapConnectorExport.ProcessPendingExport: Successfully processed {ChangeType} for {Id}",
            pendingExport.ChangeType, pendingExport.Id);
    }

    private void ProcessCreate(PendingExport pendingExport)
    {
        // For create, we need to build the DN and all attributes
        var dn = GetDistinguishedNameForCreate(pendingExport);
        if (string.IsNullOrEmpty(dn))
            throw new InvalidOperationException("Cannot create object: Distinguished Name (DN) could not be determined from attribute changes.");

        _logger.Debug("LdapConnectorExport.ProcessCreate: Creating object at DN '{Dn}'", dn);

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
            throw new LdapException((int)response.ResultCode, response.ErrorMessage);
        }

        _logger.Information("LdapConnectorExport.ProcessCreate: Successfully created object at '{Dn}'", dn);
    }

    private void ProcessUpdate(PendingExport pendingExport)
    {
        var dn = GetDistinguishedNameForUpdate(pendingExport);
        if (string.IsNullOrEmpty(dn))
            throw new InvalidOperationException("Cannot update object: Distinguished Name (DN) could not be determined.");

        _logger.Debug("LdapConnectorExport.ProcessUpdate: Updating object at DN '{Dn}'", dn);

        var modifyRequest = new ModifyRequest(dn);

        foreach (var attrChange in pendingExport.AttributeValueChanges)
        {
            if (attrChange.Attribute == null)
                continue;

            var attrName = attrChange.Attribute.Name;

            // Skip distinguished name - it cannot be modified via ModifyRequest
            if (attrName.Equals("distinguishedName", StringComparison.OrdinalIgnoreCase))
                continue;

            var modification = CreateModification(attrChange);
            if (modification != null)
            {
                modifyRequest.Modifications.Add(modification);
            }
        }

        if (modifyRequest.Modifications.Count == 0)
        {
            _logger.Debug("LdapConnectorExport.ProcessUpdate: No modifications to apply for '{Dn}'", dn);
            return;
        }

        var response = (ModifyResponse)_connection.SendRequest(modifyRequest);
        if (response.ResultCode != ResultCode.Success)
        {
            throw new LdapException((int)response.ResultCode, response.ErrorMessage);
        }

        _logger.Information("LdapConnectorExport.ProcessUpdate: Successfully updated object at '{Dn}' with {Count} modifications",
            dn, modifyRequest.Modifications.Count);
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

        if (attrChange.DateTimeValue.HasValue)
            return ConvertDateTimeToLdapFormat(attrChange.DateTimeValue.Value);

        if (attrChange.ByteValue != null)
            return attrChange.ByteValue;

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
        if (pendingExport.ConnectedSystemObject?.SecondaryExternalIdAttributeValue?.StringValue != null)
            return pendingExport.ConnectedSystemObject.SecondaryExternalIdAttributeValue.StringValue;

        // Fallback: check attribute changes for DN
        return GetDistinguishedNameForCreate(pendingExport);
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
}
