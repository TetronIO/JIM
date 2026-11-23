// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using JIM.Connectors.Http;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Connectors.Graph;

/// <summary>
/// Builds the HTTP stack a Microsoft Graph connection needs from a Connected System's settings: TLS and certificate
/// trust, timeout, the cloud's endpoints, authentication, retry and write pacing. Kept apart from
/// <see cref="GraphHttpClient"/> so the security-relevant configuration is assembled, and tested, in one place.
/// </summary>
internal static class GraphHttpClientFactory
{
    /// <param name="trustedCertificates">Enabled certificates from JIM's store, which a TLS-inspecting proxy's CA can be added to; may be empty.</param>
    /// <param name="credentialProtection">Decrypts stored secrets; null falls back to the stored value.</param>
    /// <exception cref="HttpAuthenticationException">The settings cannot authenticate: a value is missing, the Cloud is unknown, or the certificate is unusable.</exception>
    public static GraphHttpClient Create(
        List<ConnectedSystemSettingValue> settingValues,
        IReadOnlyList<X509Certificate2> trustedCertificates,
        ICredentialProtection? credentialProtection,
        ILogger logger,
        GraphClientPurpose purpose = GraphClientPurpose.Run)
    {
        ArgumentNullException.ThrowIfNull(settingValues);
        ArgumentNullException.ThrowIfNull(trustedCertificates);
        ArgumentNullException.ThrowIfNull(logger);

        var cloud = ResolveCloud(settingValues);
        var validator = new TrustedCertificateValidator(trustedCertificates, logger);
        var handler = new SocketsHttpHandler
        {
            // Graph supports TLS 1.2 and 1.3; nothing older is ever offered.
            SslOptions =
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) => validator.Validate(certificate as X509Certificate2, chain, errors)
            }
        };

        var timeoutSeconds = ReadInt(settingValues, GraphConnectorConstants.SettingConnectionTimeout) ?? GraphConnectorConstants.DefaultConnectionTimeoutSeconds;
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, GraphConnectorConstants.MaximumConnectionTimeoutSeconds)) };

        IHttpAuthenticationStrategy authentication;
        IDisposable? ownedCredential;
        try
        {
            // The token client shares the transport, so a TLS-inspecting proxy's CA trusted for Graph is trusted for
            // the token service too.
            authentication = CreateAuthentication(settingValues, credentialProtection, httpClient, out ownedCredential, purpose: purpose, logger: logger);
        }
        catch (HttpAuthenticationException)
        {
            httpClient.Dispose();
            throw;
        }

        return new GraphHttpClient(httpClient, cloud, authentication, CreateRetryPolicy(settingValues), CreatePacer(settingValues), logger, ownedCredential);
    }

    /// <param name="ownedCredential">
    /// What the credential holds open, for the caller to dispose with the client: the loaded certificate, or the client the
    /// managed identity's token is fetched with; otherwise null.
    /// </param>
    /// <param name="identityHandler">Reaches the instance metadata service; replaced in tests. By default a direct connection.</param>
    /// <param name="purpose">Decides how long to wait for Entra ID to apply a newly uploaded certificate.</param>
    /// <param name="logger">Where those waits are logged.</param>
    /// <param name="delay">Waiting seam, so the waits are testable without waiting.</param>
    /// <exception cref="HttpAuthenticationException">A value is missing, the Cloud is unknown, or the certificate is unusable.</exception>
    public static IHttpAuthenticationStrategy CreateAuthentication(
        List<ConnectedSystemSettingValue> settingValues,
        ICredentialProtection? credentialProtection,
        HttpClient tokenClient,
        out IDisposable? ownedCredential,
        HttpMessageHandler? identityHandler = null,
        GraphClientPurpose purpose = GraphClientPurpose.Run,
        ILogger? logger = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(settingValues);
        ArgumentNullException.ThrowIfNull(tokenClient);

        var cloud = ResolveCloud(settingValues);
        var tenantId = RequireString(settingValues, GraphConnectorConstants.SettingTenantId);
        var clientId = RequireString(settingValues, GraphConnectorConstants.SettingClientId);
        var tokenEndpoint = cloud.TokenEndpoint(tenantId);

        IOAuthClientCredential credential;
        var method = ReadString(settingValues, GraphConnectorConstants.SettingAuthenticationMethod);
        if (method == GraphConnectorConstants.AuthMethodFederatedCredential)
        {
            credential = new GraphFederatedCredential(CreateManagedIdentitySource(settingValues, cloud, identityHandler, out ownedCredential));
        }
        // Unset means the setting's default, a certificate.
        else if (method != GraphConnectorConstants.AuthMethodClientSecret)
        {
            var pfx = RequireSecret(settingValues, GraphConnectorConstants.SettingCertificate, credentialProtection);
            var password = ReadSecret(settingValues, GraphConnectorConstants.SettingCertificatePassword, credentialProtection);
            var loaded = GraphClientCertificate.Load(pfx, password, DateTime.UtcNow);
            if (loaded.Error != null)
            {
                loaded.Dispose();
                throw new HttpAuthenticationException(loaded.Error);
            }

            credential = new GraphClientAssertion(loaded.Certificate!, clientId);
            ownedCredential = loaded;
        }
        else
        {
            credential = new OAuthClientSecretCredential(RequireSecret(settingValues, GraphConnectorConstants.SettingClientSecret, credentialProtection));
            ownedCredential = null;
        }

        return new OAuthClientCredentialsAuthentication(tokenClient, tokenEndpoint, clientId, credential, cloud.Scope, describeFailure: EntraTokenFailure.Describe,
            stillBeingApplied: EntraTokenFailure.StillBeingAppliedRetryFor(purpose), delay: delay, logger: logger);
    }

    private static GraphManagedIdentityTokenSource CreateManagedIdentitySource(List<ConnectedSystemSettingValue> settingValues, GraphCloud cloud,
        HttpMessageHandler? identityHandler, out IDisposable? ownedCredential)
    {
        var clientId = RequireString(settingValues, GraphConnectorConstants.SettingManagedIdentityClientId);

        // Straight to the instance metadata service: it is link-local, and a proxy must never see its token.
        var identityClient = identityHandler != null
            ? new HttpClient(identityHandler, disposeHandler: false)
            : new HttpClient(new SocketsHttpHandler { UseProxy = false });
        identityClient.Timeout = TimeSpan.FromSeconds(30);
        ownedCredential = identityClient;
        return new GraphManagedIdentityTokenSource(identityClient, clientId, cloud.FederatedTokenAudience);
    }

    public static HttpRetryPolicy CreateRetryPolicy(List<ConnectedSystemSettingValue> settingValues)
    {
        var maxRetries = ReadInt(settingValues, GraphConnectorConstants.SettingMaxRetries) ?? GraphConnectorConstants.DefaultMaxRetries;
        return new HttpRetryPolicy(
            Math.Clamp(maxRetries, 0, GraphConnectorConstants.MaximumMaxRetries),
            TimeSpan.FromMilliseconds(GraphConnectorConstants.RetryBaseDelayMilliseconds),
            TimeSpan.FromSeconds(GraphConnectorConstants.MaximumRetryDelaySeconds));
    }

    /// <summary>
    /// The pacer for the configured write rate. Validation refuses a rate outside 1 to 20 when the settings are saved;
    /// a value stored before that is kept within the quota here rather than failing the run.
    /// </summary>
    public static GraphRequestPacer CreatePacer(List<ConnectedSystemSettingValue> settingValues)
    {
        var writesPerSecond = ReadInt(settingValues, GraphConnectorConstants.SettingMaximumWritesPerSecond) ?? GraphConnectorConstants.DefaultMaximumWritesPerSecond;
        return new GraphRequestPacer(Math.Clamp(writesPerSecond, 1, (int)GraphRequestPacer.WriteQuotaPerSecond));
    }

    private static GraphCloud ResolveCloud(List<ConnectedSystemSettingValue> settingValues)
    {
        var name = ReadString(settingValues, GraphConnectorConstants.SettingCloud);
        return GraphCloud.TryResolve(name, out var cloud)
            ? cloud
            : throw new HttpAuthenticationException($"The {GraphConnectorConstants.SettingCloud} setting names a cloud this Connector does not know.");
    }

    private static string RequireString(List<ConnectedSystemSettingValue> settingValues, string name)
    {
        var value = ReadString(settingValues, name)?.Trim();
        return string.IsNullOrEmpty(value)
            ? throw new HttpAuthenticationException($"The '{name}' setting is required, but has no value.")
            : value;
    }

    private static string RequireSecret(List<ConnectedSystemSettingValue> settingValues, string name, ICredentialProtection? credentialProtection)
    {
        var stored = settingValues.SingleOrDefault(s => s.Setting.Name == name)?.StringEncryptedValue;
        if (string.IsNullOrWhiteSpace(stored))
            throw new HttpAuthenticationException($"The '{name}' setting is required for the chosen Authentication Method, but has no value.");

        var decrypted = credentialProtection?.Unprotect(stored) ?? stored;
        return string.IsNullOrWhiteSpace(decrypted)
            ? throw new HttpAuthenticationException($"The '{name}' setting could not be decrypted.")
            : decrypted;
    }

    private static string? ReadSecret(List<ConnectedSystemSettingValue> settingValues, string name, ICredentialProtection? credentialProtection)
    {
        var stored = settingValues.SingleOrDefault(s => s.Setting.Name == name)?.StringEncryptedValue;
        return string.IsNullOrEmpty(stored) ? null : credentialProtection?.Unprotect(stored) ?? stored;
    }

    private static string? ReadString(List<ConnectedSystemSettingValue> settingValues, string name) =>
        settingValues.SingleOrDefault(s => s.Setting.Name == name)?.StringValue;

    private static int? ReadInt(List<ConnectedSystemSettingValue> settingValues, string name) =>
        settingValues.SingleOrDefault(s => s.Setting.Name == name)?.IntValue;
}
