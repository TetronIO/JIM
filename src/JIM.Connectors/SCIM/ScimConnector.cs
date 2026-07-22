// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;
namespace JIM.Connectors.SCIM;

/// <summary>
/// SCIM 2.0 client connector (RFC 7643/7644). JIM acts as the SCIM client: it initiates connections to external
/// SCIM service providers to discover schemas, import resources, and export provisioning changes.
/// Implementation plan: engineering/plans/doing/SCIM_CLIENT_CONNECTOR_DESIGN.md (issue #545).
/// </summary>
public class ScimConnector : IConnector, IConnectorCapabilities, IConnectorSettings
{
    #region IConnector members
    public string Name => ConnectorConstants.Scim2ConnectorName;

    public string? Description => "Enables bi-directional synchronisation with any system that exposes a SCIM 2.0 service provider interface.";

    public string? Url => "https://github.com/TetronIO/JIM";
    #endregion

    #region IConnectorCapabilities members
    public bool SupportsFullImport => true;
    public bool SupportsDeltaImport => true;
    public bool SupportsExport => true;
    public bool SupportsPartitions => false;
    public bool SupportsPartitionContainers => false;
    public bool SupportsSecondaryExternalId => false;
    public bool SupportsUserSelectedExternalId => false;
    public bool SupportsUserSelectedAttributeTypes => false;
    public bool SupportsAutoConfirmExport => false;
    public bool SupportsParallelExport => true;
    public bool SupportsPaging => true;
    public bool SupportsFilePaths => false;
    #endregion

    #region IConnectorSettings members
    public List<ConnectorSetting> GetSettings()
    {
        return new List<ConnectorSetting>
        {
            new() { Name = "SCIM Service Provider", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
            new() { Name = "SCIM Service Provider Info", Description = "Enter the details of the SCIM 2.0 service provider to connect to.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Label },
            new() { Name = ScimConnectorConstants.SettingBaseUrl, Required = true, Description = "The base URL of the SCIM 2.0 service provider, i.e. https://example.com/scim/v2. HTTPS is required, except for loopback addresses when testing locally.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },

            new() { Name = "Authentication", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
            new()
            {
                Name = ScimConnectorConstants.SettingAuthenticationMethod,
                Required = true,
                Description = "How to authenticate with the SCIM service provider. OAuth 2.0 Client Credentials is most common for cloud providers; Static Bearer Token suits providers that issue long-lived tokens; Custom Header suits providers with non-standard authentication headers.",
                Category = ConnectedSystemSettingCategory.Connectivity,
                Type = ConnectedSystemSettingType.DropDown,
                DropDownValues = new List<string> { ScimConnectorConstants.AuthMethodOAuthClientCredentials, ScimConnectorConstants.AuthMethodHttpBasic, ScimConnectorConstants.AuthMethodStaticBearerToken, ScimConnectorConstants.AuthMethodCustomHeader },
                DefaultStringValue = ScimConnectorConstants.AuthMethodOAuthClientCredentials
            },

            // OAuth 2.0 Client Credentials settings
            new() { Name = ScimConnectorConstants.SettingTokenEndpointUrl, RequiredWhenSetting = ScimConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = ScimConnectorConstants.AuthMethodOAuthClientCredentials, Description = "The OAuth 2.0 token endpoint URL used to acquire access tokens via the Client Credentials flow.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new() { Name = ScimConnectorConstants.SettingClientId, RequiredWhenSetting = ScimConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = ScimConnectorConstants.AuthMethodOAuthClientCredentials, Description = "The OAuth 2.0 client identifier.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new() { Name = ScimConnectorConstants.SettingClientSecret, RequiredWhenSetting = ScimConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = ScimConnectorConstants.AuthMethodOAuthClientCredentials, Description = "The OAuth 2.0 client secret.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },
            new() { Name = ScimConnectorConstants.SettingOAuthScope, Required = false, Description = "Optional OAuth 2.0 scope(s) to request, space-separated. Only used with the OAuth 2.0 Client Credentials authentication method.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },

            // HTTP Basic settings
            new() { Name = ScimConnectorConstants.SettingUsername, RequiredWhenSetting = ScimConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = ScimConnectorConstants.AuthMethodHttpBasic, Description = "The username to authenticate with when using HTTP Basic authentication.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new() { Name = ScimConnectorConstants.SettingPassword, RequiredWhenSetting = ScimConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = ScimConnectorConstants.AuthMethodHttpBasic, Description = "The password to authenticate with when using HTTP Basic authentication.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },

            // Static Bearer Token settings
            new() { Name = ScimConnectorConstants.SettingBearerToken, RequiredWhenSetting = ScimConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = ScimConnectorConstants.AuthMethodStaticBearerToken, Description = "A pre-generated, long-lived bearer token issued by the SCIM service provider.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },

            // Custom Header settings
            new() { Name = ScimConnectorConstants.SettingAuthenticationHeaderName, RequiredWhenSetting = ScimConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = ScimConnectorConstants.AuthMethodCustomHeader, Description = "The name of the HTTP header the SCIM service provider uses for authentication, i.e. X-Api-Key.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new() { Name = ScimConnectorConstants.SettingAuthenticationHeaderValue, RequiredWhenSetting = ScimConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = ScimConnectorConstants.AuthMethodCustomHeader, Description = "The value to send in the authentication header.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },

            new() { Name = "Transport Security", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
            new() { Name = ScimConnectorConstants.SettingCertificateValidation, Required = false, DefaultStringValue = ScimConnectorConstants.CertValidationFull, Description = "How to validate the server's TLS certificate. Full Validation uses the system CA store plus any certificates added in Admin > Certificates.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.DropDown, DropDownValues = new List<string> { ScimConnectorConstants.CertValidationFull, ScimConnectorConstants.CertValidationSkip } },
            new() { Name = ScimConnectorConstants.SettingMinimumTlsVersion, Required = false, DefaultStringValue = ScimConnectorConstants.TlsVersion12, Description = "The minimum TLS protocol version to accept when connecting to the SCIM service provider.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.DropDown, DropDownValues = new List<string> { ScimConnectorConstants.TlsVersion12, ScimConnectorConstants.TlsVersion13 } },
            new() { Name = ScimConnectorConstants.SettingConnectionTimeout, Required = true, DefaultIntValue = ScimConnectorConstants.DefaultConnectionTimeoutSeconds, Description = "How long to wait, in seconds, for a response from the SCIM service provider before giving up.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Integer },

            new() { Name = "Retry Settings", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },
            new() { Name = ScimConnectorConstants.SettingMaxRetries, Required = false, DefaultIntValue = ScimConnectorConstants.DefaultMaxRetries, Description = "Maximum number of retry attempts for transient failures (i.e. HTTP 429, 503, 504). Default is 3.", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Integer },
            new() { Name = ScimConnectorConstants.SettingRetryDelay, Required = false, DefaultIntValue = ScimConnectorConstants.DefaultRetryDelayMs, Description = "Initial delay between retries in milliseconds. Uses exponential backoff with jitter, and honours Retry-After response headers. Default is 1000ms.", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Integer }
        };
    }

    /// <summary>
    /// Validates ScimConnector setting values using custom business logic.
    /// </summary>
    public List<ConnectorSettingValueValidationResult> ValidateSettingValues(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        logger.Verbose($"ValidateSettingValues() called for {Name}");
        var response = new List<ConnectorSettingValueValidationResult>();

        // generic required, required-group and required-when validation is handled centrally by ConnectorSettingValidator
        // (invoked by the application layer before this method); only SCIM-specific rules live here.

        var baseUrlSetting = settingValues.SingleOrDefault(q => q.Setting.Name == ScimConnectorConstants.SettingBaseUrl);
        var baseUrl = baseUrlSetting?.StringValue;

        // Base URL is required, but the generic validator already reports a missing value; the shape checks below
        // cannot run without one.
        if (baseUrlSetting == null || string.IsNullOrEmpty(baseUrl))
            return response;

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            response.Add(new ConnectorSettingValueValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Base URL '{baseUrl}' is not a valid absolute URL. Supply the full SCIM endpoint URL, i.e. https://example.com/scim/v2.",
                SettingValue = baseUrlSetting
            });
            return response;
        }

        if (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp)
        {
            response.Add(new ConnectorSettingValueValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Base URL '{baseUrl}' must use the https scheme (or http for loopback addresses only).",
                SettingValue = baseUrlSetting
            });
            return response;
        }

        // JIM is deployed in high-trust environments; identity data must not travel over cleartext HTTP.
        // Loopback is permitted to support local test service providers.
        if (baseUri.Scheme == Uri.UriSchemeHttp && !baseUri.IsLoopback)
        {
            response.Add(new ConnectorSettingValueValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Base URL '{baseUrl}' uses http against a non-loopback host. HTTPS is required for SCIM service providers; http is only permitted for loopback addresses.",
                SettingValue = baseUrlSetting
            });
        }

        return response;
    }
    #endregion
}
