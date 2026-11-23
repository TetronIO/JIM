// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Graph;

/// <summary>
/// Setting names, drop-down values and defaults for the Microsoft Graph Connector.
/// Centralised so the Connector, its collaborators and tests all reference the same identifiers.
/// </summary>
public static class GraphConnectorConstants
{
    // Connectivity settings
    public const string SettingTenantId = "Tenant ID";
    public const string SettingCloud = "Cloud";
    public const string SettingClientId = "Client ID";
    public const string SettingAuthenticationMethod = "Authentication Method";
    public const string SettingClientSecret = "Client Secret";
    public const string SettingCertificate = "Certificate";
    public const string SettingCertificatePassword = "Certificate Password";
    public const string SettingConnectionTimeout = "Connection Timeout";

    // Rate limiting and retry settings
    public const string SettingMaximumWritesPerSecond = "Maximum Writes Per Second";
    public const string SettingMaxRetries = "Maximum Retries";

    // Authentication Method drop-down values
    public const string AuthMethodClientSecret = "Client Secret";
    public const string AuthMethodCertificate = "Certificate";
    public const string AuthMethodFederatedCredential = "Federated Credential";

    public const string SettingManagedIdentityClientId = "Managed Identity Client ID";

    // Cloud drop-down values, named as Microsoft names the national cloud deployments
    public const string CloudGlobal = "Global";
    public const string CloudUsGovernmentL4 = "US Government L4 (GCC High)";
    public const string CloudUsGovernmentL5 = "US Government L5 (DoD)";
    public const string CloudChina = "China (operated by 21Vianet)";

    // Defaults and bounds
    public const int DefaultConnectionTimeoutSeconds = 100;
    public const int MaximumConnectionTimeoutSeconds = 600;

    /// <summary>
    /// Half the application and tenant write quota (3,000 requests per 150 seconds), so an export at full speed leaves
    /// the other half, and the tenant-wide quota every application shares, for everyone else.
    /// </summary>
    public const int DefaultMaximumWritesPerSecond = 10;

    public const int DefaultMaxRetries = 5;
    public const int MaximumMaxRetries = 10;

    /// <summary>The first retry's wait; later retries back off exponentially with jitter, unless Graph says how long.</summary>
    public const int RetryBaseDelayMilliseconds = 1000;

    /// <summary>
    /// The longest wait JIM accepts from a Retry-After before giving up on the request. Graph's write quota is measured
    /// over 150 seconds, so a throttled write can legitimately be asked to wait that long; anything past this bound
    /// means Graph will not be ready within a period worth stalling a run for.
    /// </summary>
    public const int MaximumRetryDelaySeconds = 300;

    /// <summary>
    /// The smallest RSA key Microsoft Entra ID accepts for a certificate credential, and the floor JIM enforces so a
    /// weaker key is refused when the setting is saved rather than at the first token request.
    /// </summary>
    public const int MinimumRsaKeySizeBits = 2048;

    /// <summary>
    /// How close to expiry a certificate may be before saving the settings warns that it needs renewing. A certificate
    /// that expires mid-schedule stops every import and export, so the warning gives an administrator time to act.
    /// </summary>
    public const int CertificateExpiryWarningDays = 30;

    /// <summary>
    /// Whether the Connector reads which synchronised objects have had their Source of Authority switched between Active
    /// Directory and Entra ID. Off by default: the only permission that reads it also allows changing it.
    /// </summary>
    public const string SettingTrackSourceOfAuthority = "Track Source of Authority Changes";
}
