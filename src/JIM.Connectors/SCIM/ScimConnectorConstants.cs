// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.SCIM;

/// <summary>
/// Setting names, drop-down values, and defaults for the SCIM 2.0 connector.
/// Centralised so the connector, its collaborators, and tests all reference the same identifiers.
/// </summary>
public static class ScimConnectorConstants
{
    // Connectivity settings
    public const string SettingBaseUrl = "Base URL";
    public const string SettingAuthenticationMethod = "Authentication Method";
    public const string SettingTokenEndpointUrl = "Token Endpoint URL";
    public const string SettingClientId = "Client ID";
    public const string SettingClientSecret = "Client Secret";
    public const string SettingOAuthScope = "OAuth Scope";
    public const string SettingUsername = "Username";
    public const string SettingPassword = "Password";
    public const string SettingBearerToken = "Bearer Token";
    public const string SettingAuthenticationHeaderName = "Authentication Header Name";
    public const string SettingAuthenticationHeaderValue = "Authentication Header Value";
    public const string SettingCertificateValidation = "Certificate Validation";
    public const string SettingMinimumTlsVersion = "Minimum TLS Version";
    public const string SettingConnectionTimeout = "Connection Timeout";

    // Retry settings
    public const string SettingMaxRetries = "Maximum Retries";
    public const string SettingRetryDelay = "Retry Delay (ms)";

    // Authentication Method drop-down values
    public const string AuthMethodOAuthClientCredentials = "OAuth 2.0 Client Credentials";
    public const string AuthMethodHttpBasic = "HTTP Basic";
    public const string AuthMethodStaticBearerToken = "Static Bearer Token";
    public const string AuthMethodCustomHeader = "Custom Header";

    // Certificate Validation drop-down values
    public const string CertValidationFull = "Full Validation";
    public const string CertValidationSkip = "Skip Validation (Insecure)";

    // Minimum TLS Version drop-down values
    public const string TlsVersion12 = "TLS 1.2";
    public const string TlsVersion13 = "TLS 1.3";

    // Defaults
    public const int DefaultConnectionTimeoutSeconds = 30;
    public const int DefaultMaxRetries = 3;
    public const int DefaultRetryDelayMs = 1000;
}
