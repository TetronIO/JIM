// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Connectors.Graph;

/// <summary>
/// Microsoft Graph Connector: synchronises users and groups with a Microsoft Entra ID tenant through the Microsoft
/// Graph API, authenticating as the customer's own app registration (client credentials, app-only).
/// Implementation plan: engineering/plans/doing/MICROSOFT_GRAPH_CONNECTOR_DESIGN.md (issue #361).
/// </summary>
/// <remarks>
/// Registered with the Connector factory but not seeded until the plan's final phase, so it is not offered to
/// administrators while it is being built. The capability flags declare what the finished Connector does.
/// </remarks>
public class GraphConnector : IConnector, IConnectorCapabilities, IConnectorSettings, IConnectorCredentialAware
{
    private static readonly string[] MultiTenantAliases = ["common", "organizations", "consumers"];

    private ICredentialProtection? _credentialProtection;

    #region IConnector members
    public string Name => ConnectorConstants.GraphConnectorName;

    public string? Description => "Enables bi-directional synchronisation of users and groups with Microsoft Entra ID, through the Microsoft Graph API.";

    public string? Url => "https://github.com/TetronIO/JIM";
    #endregion

    #region IConnectorCapabilities members
    public bool SupportsFullImport => true;

    /// <summary>Graph delta queries; the delta link is the watermark.</summary>
    public bool SupportsDeltaImport => true;

    public bool SupportsExport => true;

    /// <summary>A tenant has no partitions.</summary>
    public bool SupportsPartitions => false;

    public bool SupportsPartitionContainers => false;
    public bool SupportsSecondaryExternalId => false;

    /// <summary>The External ID is the object's <c>id</c>, which Entra ID assigns and never changes.</summary>
    public bool SupportsUserSelectedExternalId => false;

    public bool SupportsUserSelectedAttributeTypes => false;
    public bool SupportsUserSelectedAttributePlurality => false;

    /// <summary>Graph is eventually consistent, so only the confirming import proves a write landed.</summary>
    public bool SupportsAutoConfirmExport => false;

    /// <summary>Throughput comes from <c>$batch</c> and pacing; parallel export would only race the write quota.</summary>
    public bool SupportsParallelExport => false;

    public bool SupportsPaging => true;
    public bool SupportsFilePaths => false;
    public bool SupportsPasswordSet => false;
    public bool SupportsPasswordPolicyDiscovery => false;
    public bool SupportsUniquenessProbe => false;
    public bool SupportsUniquenessRejectionClassification => false;

    /// <summary>A directory object's <c>id</c> is unique across the tenant, whether the object is a user or a group.</summary>
    public bool ExternalIdsUniqueAcrossObjectTypes => true;
    #endregion

    #region IConnectorCredentialAware members
    public void SetCredentialProtection(ICredentialProtection? credentialProtection) => _credentialProtection = credentialProtection;
    #endregion

    #region IConnectorSettings members
    public List<ConnectorSetting> GetSettings()
    {
        return
        [
            new() { Name = "Microsoft Entra ID Tenant", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
            new() { Name = "Microsoft Entra ID Tenant Info", Description = "Enter the details of the tenant to synchronise with, and of the app registration JIM authenticates as. The app registration needs Microsoft Graph application permissions, granted with admin consent.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Label },
            new() { Name = GraphConnectorConstants.SettingTenantId, Required = true, Description = "The tenant's ID (a GUID), or one of its verified domain names, i.e. contoso.onmicrosoft.com. Shown as Directory (tenant) ID on the app registration's Overview page.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new()
            {
                Name = GraphConnectorConstants.SettingCloud,
                Required = false,
                Description = "The Microsoft cloud the tenant is in. Choose Global for Microsoft 365 GCC (Moderate) tenants too, which use the global endpoints.",
                Category = ConnectedSystemSettingCategory.Connectivity,
                Type = ConnectedSystemSettingType.DropDown,
                DropDownValues = GraphCloud.All.Select(c => c.Name).ToList(),
                DefaultStringValue = GraphConnectorConstants.CloudGlobal
            },

            new() { Name = "Authentication", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
            new() { Name = GraphConnectorConstants.SettingClientId, Required = true, Description = "The app registration's Application (client) ID, a GUID shown on its Overview page.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new()
            {
                Name = GraphConnectorConstants.SettingAuthenticationMethod,
                Required = true,
                Description = "How JIM proves it is the app registration. A certificate is recommended for production: its private key never leaves JIM, whereas a client secret is sent with every token request.",
                Category = ConnectedSystemSettingCategory.Connectivity,
                Type = ConnectedSystemSettingType.DropDown,
                DropDownValues = [GraphConnectorConstants.AuthMethodClientSecret, GraphConnectorConstants.AuthMethodCertificate],
                DefaultStringValue = GraphConnectorConstants.AuthMethodClientSecret
            },
            new() { Name = GraphConnectorConstants.SettingClientSecret, RequiredWhenSetting = GraphConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = GraphConnectorConstants.AuthMethodClientSecret, Description = "A client secret created under Certificates & secrets on the app registration. Supply the secret's Value, not its Secret ID.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },
            new() { Name = GraphConnectorConstants.SettingCertificate, RequiredWhenSetting = GraphConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = GraphConnectorConstants.AuthMethodCertificate, Description = $"The certificate and its private key, as a PFX file's contents Base64-encoded, i.e. the output of [Convert]::ToBase64String([IO.File]::ReadAllBytes('app.pfx')) in PowerShell. Upload the same certificate (without its private key) under Certificates & secrets on the app registration. It needs an RSA key of at least {GraphConnectorConstants.MinimumRsaKeySizeBits} bits.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },
            new() { Name = GraphConnectorConstants.SettingCertificatePassword, Required = false, Description = "The password protecting the PFX, if it has one. Only used with the Certificate authentication method.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted }
        ];
    }

    /// <summary>
    /// Validates the Microsoft Graph Connector's setting values. Missing required values are reported centrally by
    /// ConnectorSettingValidator before this runs, so only the shape of supplied values is checked here.
    /// </summary>
    public List<ConnectorSettingValueValidationResult> ValidateSettingValues(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        logger.Verbose($"ValidateSettingValues() called for {Name}");
        var response = new List<ConnectorSettingValueValidationResult>();

        var tenantId = Find(settingValues, GraphConnectorConstants.SettingTenantId);
        if (!string.IsNullOrWhiteSpace(tenantId?.StringValue))
        {
            var tenantError = CheckTenantId(tenantId.StringValue.Trim());
            if (tenantError != null)
                response.Add(Error(tenantId, tenantError));
        }

        var clientId = Find(settingValues, GraphConnectorConstants.SettingClientId);
        if (!string.IsNullOrWhiteSpace(clientId?.StringValue) && !Guid.TryParse(clientId.StringValue.Trim(), out _))
        {
            response.Add(Error(clientId,
                $"The {GraphConnectorConstants.SettingClientId} must be the app registration's Application (client) ID, a GUID such as " +
                "00000000-0000-0000-0000-000000000000, shown on its Overview page."));
        }

        var cloud = Find(settingValues, GraphConnectorConstants.SettingCloud);
        if (cloud != null && !GraphCloud.TryResolve(cloud.StringValue, out _))
        {
            response.Add(Error(cloud,
                $"'{cloud.StringValue}' is not a cloud this Connector knows. Choose one of: {string.Join(", ", GraphCloud.All.Select(c => c.Name))}."));
        }

        var authenticationMethod = Find(settingValues, GraphConnectorConstants.SettingAuthenticationMethod)?.StringValue;
        var certificate = Find(settingValues, GraphConnectorConstants.SettingCertificate);
        if (authenticationMethod == GraphConnectorConstants.AuthMethodCertificate && !string.IsNullOrWhiteSpace(certificate?.StringEncryptedValue))
        {
            var certificateResult = CheckCertificate(settingValues, certificate);
            if (certificateResult != null)
                response.Add(certificateResult);
        }

        return response;
    }
    #endregion

    /// <summary>
    /// A tenant ID is a GUID or a verified domain name. The multi-tenant aliases that sign-in pages accept are refused:
    /// app-only authentication acts in one specific tenant, and Entra ID rejects client credentials requests to them.
    /// </summary>
    private static string? CheckTenantId(string tenantId)
    {
        if (Guid.TryParse(tenantId, out _))
            return null;

        if (MultiTenantAliases.Contains(tenantId, StringComparer.OrdinalIgnoreCase))
        {
            return $"'{tenantId}' is a multi-tenant sign-in alias, not a tenant. Supply the tenant's ID (a GUID) or one of its " +
                   "verified domain names, shown as Directory (tenant) ID on the app registration's Overview page.";
        }

        if (Uri.CheckHostName(tenantId) == UriHostNameType.Dns && tenantId.Contains('.'))
            return null;

        return $"The {GraphConnectorConstants.SettingTenantId} must be the tenant's ID (a GUID) or one of its verified domain names, " +
               "i.e. contoso.onmicrosoft.com. Supply the value on its own, not a sign-in URL.";
    }

    private ConnectorSettingValueValidationResult? CheckCertificate(List<ConnectedSystemSettingValue> settingValues, ConnectedSystemSettingValue certificate)
    {
        var pfx = Decrypt(certificate.StringEncryptedValue);
        if (string.IsNullOrWhiteSpace(pfx))
            return Error(certificate, $"The {GraphConnectorConstants.SettingCertificate} setting could not be decrypted.");

        var password = Decrypt(Find(settingValues, GraphConnectorConstants.SettingCertificatePassword)?.StringEncryptedValue);

        using var result = GraphClientCertificate.Load(pfx, password, DateTime.UtcNow);
        if (result.Error != null)
            return Error(certificate, result.Error);

        return result.Warning != null
            ? ConnectorSettingValueValidationResult.Warning(certificate, result.Warning)
            : null;
    }

    /// <summary>The stored value as entered, decrypting it where JIM has encrypted it at rest.</summary>
    private string? Decrypt(string? stored) =>
        string.IsNullOrEmpty(stored) ? stored : _credentialProtection?.Unprotect(stored) ?? stored;

    private static ConnectedSystemSettingValue? Find(List<ConnectedSystemSettingValue> settingValues, string name) =>
        settingValues.SingleOrDefault(s => s.Setting.Name == name);

    private static ConnectorSettingValueValidationResult Error(ConnectedSystemSettingValue settingValue, string message) =>
        new() { IsValid = false, ErrorMessage = message, SettingValue = settingValue };
}
