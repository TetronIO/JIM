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

    // Authentication Method drop-down values
    public const string AuthMethodClientSecret = "Client Secret";
    public const string AuthMethodCertificate = "Certificate";

    // Cloud drop-down values, named as Microsoft names the national cloud deployments
    public const string CloudGlobal = "Global";
    public const string CloudUsGovernmentL4 = "US Government L4 (GCC High)";
    public const string CloudUsGovernmentL5 = "US Government L5 (DoD)";
    public const string CloudChina = "China (operated by 21Vianet)";

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
}
