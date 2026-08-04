// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using JIM.Connectors.SCIM.Authentication;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Connectors.SCIM;

/// <summary>
/// Builds the HTTP stack a SCIM connection needs from a Connected System's connectivity settings:
/// minimum TLS version, certificate trust policy, timeout, retry policy and authentication.
/// <para>
/// Kept separate from <see cref="ScimHttpClient"/> so the client stays about protocol behaviour while
/// the security-relevant configuration is assembled (and tested) in one place.
/// </para>
/// </summary>
public static class ScimHttpClientFactory
{
    /// <summary>
    /// Builds the message handler, applying the minimum TLS version and certificate trust policy.
    /// </summary>
    /// <param name="settingValues">The Connected System's setting values.</param>
    /// <param name="trustedCertificates">Enabled certificates from the JIM store; may be empty.</param>
    public static SocketsHttpHandler CreateHandler(
        List<ConnectedSystemSettingValue> settingValues,
        IReadOnlyList<X509Certificate2> trustedCertificates,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settingValues);
        ArgumentNullException.ThrowIfNull(trustedCertificates);
        ArgumentNullException.ThrowIfNull(logger);

        var handler = new SocketsHttpHandler
        {
            SslOptions =
            {
                EnabledSslProtocols = ResolveSslProtocols(settingValues)
            }
        };

        if (ShouldSkipCertificateValidation(settingValues))
        {
            logger.Warning("Certificate validation is disabled for this SCIM Connected System. The connection is not protected against interception and this must not be used in production.");
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        else
        {
            var validator = new ScimCertificateValidator(trustedCertificates, logger);
            handler.SslOptions.RemoteCertificateValidationCallback =
                (_, certificate, chain, errors) => validator.Validate(certificate as X509Certificate2, chain, errors);
        }

        return handler;
    }

    /// <summary>
    /// Builds the <see cref="HttpClient"/> over a handler, applying the configured timeout.
    /// </summary>
    public static HttpClient CreateHttpClient(SocketsHttpHandler handler, List<ConnectedSystemSettingValue> settingValues)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(settingValues);

        var timeoutSeconds = ReadInt(settingValues, ScimConnectorConstants.SettingConnectionTimeout)
                             ?? ScimConnectorConstants.DefaultConnectionTimeoutSeconds;

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    }

    /// <summary>
    /// Builds the retry policy from the connector's retry settings.
    /// </summary>
    public static ScimRetryPolicy CreateRetryPolicy(List<ConnectedSystemSettingValue> settingValues)
    {
        ArgumentNullException.ThrowIfNull(settingValues);

        var maxRetries = ReadInt(settingValues, ScimConnectorConstants.SettingMaxRetries)
                         ?? ScimConnectorConstants.DefaultMaxRetries;
        var retryDelayMs = ReadInt(settingValues, ScimConnectorConstants.SettingRetryDelay)
                           ?? ScimConnectorConstants.DefaultRetryDelayMs;

        return new ScimRetryPolicy(
            maxRetries,
            TimeSpan.FromMilliseconds(retryDelayMs),
            TimeSpan.FromSeconds(ScimConnectorConstants.MaximumRetryDelaySeconds));
    }

    /// <summary>
    /// Builds a ready-to-use client: configured transport, authentication strategy and retry policy.
    /// </summary>
    /// <param name="settingValues">The Connected System's setting values.</param>
    /// <param name="trustedCertificates">Enabled certificates from the JIM store; may be empty.</param>
    /// <param name="credentialProtection">Decrypts stored secrets; null falls back to the stored value.</param>
    /// <exception cref="ScimAuthenticationException">Authentication settings are missing or malformed.</exception>
    public static ScimHttpClient Create(
        List<ConnectedSystemSettingValue> settingValues,
        IReadOnlyList<X509Certificate2> trustedCertificates,
        ICredentialProtection? credentialProtection,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settingValues);

        var baseUrl = settingValues.SingleOrDefault(s => s.Setting.Name == ScimConnectorConstants.SettingBaseUrl)?.StringValue;
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            throw new ScimAuthenticationException($"The '{ScimConnectorConstants.SettingBaseUrl}' setting is not a valid absolute URL.");

        var handler = CreateHandler(settingValues, trustedCertificates, logger);
        var httpClient = CreateHttpClient(handler, settingValues);

        // The token client shares the transport configuration, so an internal authorisation server behind
        // the same private CA is trusted without separate certificate settings.
        var authentication = ScimAuthenticationStrategyFactory.Create(settingValues, credentialProtection, httpClient);

        return new ScimHttpClient(httpClient, baseUri, authentication, CreateRetryPolicy(settingValues), logger);
    }

    /// <summary>
    /// Resolves the minimum TLS version into the set of protocols to enable. Anything below TLS 1.2 is
    /// never enabled, whatever the setting says.
    /// </summary>
    private static SslProtocols ResolveSslProtocols(List<ConnectedSystemSettingValue> settingValues)
    {
        var minimumTls = settingValues
            .SingleOrDefault(s => s.Setting.Name == ScimConnectorConstants.SettingMinimumTlsVersion)?.StringValue;

        return minimumTls == ScimConnectorConstants.TlsVersion13
            ? SslProtocols.Tls13
            : SslProtocols.Tls12 | SslProtocols.Tls13;
    }

    /// <summary>
    /// Certificate validation is skipped only on an exact match for the insecure option, so a
    /// misconfigured or unrecognised value fails safe into full validation.
    /// </summary>
    private static bool ShouldSkipCertificateValidation(List<ConnectedSystemSettingValue> settingValues)
    {
        var value = settingValues
            .SingleOrDefault(s => s.Setting.Name == ScimConnectorConstants.SettingCertificateValidation)?.StringValue;

        return value == ScimConnectorConstants.CertValidationSkip;
    }

    private static int? ReadInt(List<ConnectedSystemSettingValue> settingValues, string settingName)
    {
        return settingValues.SingleOrDefault(s => s.Setting.Name == settingName)?.IntValue;
    }
}
