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

    // Import settings
    public const string SettingPaginationMode = "Pagination Mode";
    public const string SettingExcludedAttributes = "Excluded Attributes";
    public const string SettingChangeDetection = "Change Detection";

    // Pagination Mode drop-down values
    public const string PaginationModeAuto = "Auto-detect";
    public const string PaginationModeIndex = "Index-based";
    public const string PaginationModeCursor = "Cursor-based";

    // Change Detection drop-down values
    public const string ChangeDetectionAuto = "Auto-detect";
    public const string ChangeDetectionLastModified = "Last Modified Filter";
    public const string ChangeDetectionFullScan = "Full Scan";

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

    /// <summary>
    /// Ceiling on any single retry wait. A provider asking for longer than this via Retry-After has said
    /// it will not be ready within a period JIM is willing to stall a run for, so the throttle is
    /// surfaced instead of waited out. Not administrator-configurable: it bounds run duration rather
    /// than expressing a preference.
    /// </summary>
    public const int MaximumRetryDelaySeconds = 300;
}
