// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Staging;

namespace JIM.Connectors.SCIM.Authentication;

/// <summary>
/// Builds the authentication strategy a Connected System's settings describe.
/// <para>
/// This is the only place that decrypts stored secrets, so the strategies themselves never handle
/// encrypted material. Validation failures name the setting an administrator must correct, and never
/// echo a secret value.
/// </para>
/// </summary>
public static class ScimAuthenticationStrategyFactory
{
    /// <param name="settingValues">The Connected System's setting values.</param>
    /// <param name="credentialProtection">
    /// Decrypts stored secrets. When null, stored values are used as-is, matching the LDAP connector so
    /// that a value saved before credential protection existed still works.
    /// </param>
    /// <param name="tokenClient">Client used for OAuth token acquisition, carrying the connector's TLS configuration.</param>
    /// <exception cref="ScimAuthenticationException">A required setting is missing, malformed, or names an unknown method.</exception>
    public static IScimAuthenticationStrategy Create(
        List<ConnectedSystemSettingValue> settingValues,
        ICredentialProtection? credentialProtection,
        HttpClient tokenClient)
    {
        ArgumentNullException.ThrowIfNull(settingValues);
        ArgumentNullException.ThrowIfNull(tokenClient);

        var method = ReadString(settingValues, ScimConnectorConstants.SettingAuthenticationMethod);
        if (string.IsNullOrWhiteSpace(method))
            throw new ScimAuthenticationException($"The '{ScimConnectorConstants.SettingAuthenticationMethod}' setting has no value.");

        return method switch
        {
            ScimConnectorConstants.AuthMethodOAuthClientCredentials => CreateOAuth(settingValues, credentialProtection, tokenClient),
            ScimConnectorConstants.AuthMethodHttpBasic => CreateBasic(settingValues, credentialProtection),
            ScimConnectorConstants.AuthMethodStaticBearerToken => CreateStaticBearer(settingValues, credentialProtection),
            ScimConnectorConstants.AuthMethodCustomHeader => CreateCustomHeader(settingValues, credentialProtection),
            _ => throw new ScimAuthenticationException(
                $"'{method}' is not a supported value for the '{ScimConnectorConstants.SettingAuthenticationMethod}' setting.")
        };
    }

    private static IScimAuthenticationStrategy CreateOAuth(
        List<ConnectedSystemSettingValue> settingValues,
        ICredentialProtection? credentialProtection,
        HttpClient tokenClient)
    {
        var tokenEndpoint = RequireString(settingValues, ScimConnectorConstants.SettingTokenEndpointUrl);
        if (!Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out var tokenEndpointUri))
            throw new ScimAuthenticationException(
                $"The '{ScimConnectorConstants.SettingTokenEndpointUrl}' setting is not a valid absolute URL.");

        return new ScimOAuthClientCredentialsAuthentication(
            tokenClient,
            tokenEndpointUri,
            RequireString(settingValues, ScimConnectorConstants.SettingClientId),
            RequireSecret(settingValues, ScimConnectorConstants.SettingClientSecret, credentialProtection),
            ReadString(settingValues, ScimConnectorConstants.SettingOAuthScope));
    }

    private static IScimAuthenticationStrategy CreateBasic(
        List<ConnectedSystemSettingValue> settingValues,
        ICredentialProtection? credentialProtection)
    {
        return new ScimBasicAuthentication(
            RequireString(settingValues, ScimConnectorConstants.SettingUsername),
            RequireSecret(settingValues, ScimConnectorConstants.SettingPassword, credentialProtection));
    }

    private static IScimAuthenticationStrategy CreateStaticBearer(
        List<ConnectedSystemSettingValue> settingValues,
        ICredentialProtection? credentialProtection)
    {
        return new ScimStaticBearerTokenAuthentication(
            RequireSecret(settingValues, ScimConnectorConstants.SettingBearerToken, credentialProtection));
    }

    private static IScimAuthenticationStrategy CreateCustomHeader(
        List<ConnectedSystemSettingValue> settingValues,
        ICredentialProtection? credentialProtection)
    {
        return new ScimCustomHeaderAuthentication(
            RequireString(settingValues, ScimConnectorConstants.SettingAuthenticationHeaderName),
            RequireSecret(settingValues, ScimConnectorConstants.SettingAuthenticationHeaderValue, credentialProtection));
    }

    private static string? ReadString(List<ConnectedSystemSettingValue> settingValues, string settingName)
    {
        return settingValues.SingleOrDefault(s => s.Setting.Name == settingName)?.StringValue;
    }

    private static string RequireString(List<ConnectedSystemSettingValue> settingValues, string settingName)
    {
        var value = ReadString(settingValues, settingName);
        if (string.IsNullOrWhiteSpace(value))
            throw new ScimAuthenticationException($"The '{settingName}' setting is required for the chosen Authentication Method, but has no value.");

        return value;
    }

    private static string RequireSecret(
        List<ConnectedSystemSettingValue> settingValues,
        string settingName,
        ICredentialProtection? credentialProtection)
    {
        var stored = settingValues.SingleOrDefault(s => s.Setting.Name == settingName)?.StringEncryptedValue;
        if (string.IsNullOrWhiteSpace(stored))
            throw new ScimAuthenticationException($"The '{settingName}' setting is required for the chosen Authentication Method, but has no value.");

        var decrypted = credentialProtection?.Unprotect(stored) ?? stored;
        if (string.IsNullOrWhiteSpace(decrypted))
            throw new ScimAuthenticationException($"The '{settingName}' setting could not be decrypted.");

        return decrypted;
    }
}
