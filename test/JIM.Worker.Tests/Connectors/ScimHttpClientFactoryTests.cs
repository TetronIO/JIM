// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using JIM.Connectors.SCIM;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Turns a Connected System's connectivity settings into a configured HTTP stack: minimum TLS version,
/// certificate trust policy and timeout. These are security-relevant defaults, so each is pinned.
/// </summary>
[TestFixture]
public class ScimHttpClientFactoryTests
{
    private ILogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();
    }

    private static ConnectedSystemSettingValue Setting(string name, string? stringValue = null, int? intValue = null)
    {
        return new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = name },
            StringValue = stringValue,
            IntValue = intValue
        };
    }

    private static List<ConnectedSystemSettingValue> CreateSettings(
        string certificateValidation = ScimConnectorConstants.CertValidationFull,
        string minimumTls = ScimConnectorConstants.TlsVersion12,
        int? timeoutSeconds = null)
    {
        return
        [
            Setting(ScimConnectorConstants.SettingBaseUrl, "https://provider.example.com/scim/v2"),
            Setting(ScimConnectorConstants.SettingCertificateValidation, certificateValidation),
            Setting(ScimConnectorConstants.SettingMinimumTlsVersion, minimumTls),
            Setting(ScimConnectorConstants.SettingConnectionTimeout, intValue: timeoutSeconds)
        ];
    }

    private static X509Certificate2 CreateUntrustedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=rogue.example.com", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    [Test]
    public void CreateHandler_MinimumTls12_EnablesTls12AndAbove()
    {
        using var handler = ScimHttpClientFactory.CreateHandler(CreateSettings(minimumTls: ScimConnectorConstants.TlsVersion12), [], _logger);

        // Asserted as an exact set: anything below TLS 1.2 is prohibited outright, not merely
        // deprioritised, and an exact match proves no older protocol crept in.
        Assert.That(handler.SslOptions.EnabledSslProtocols, Is.EqualTo(SslProtocols.Tls12 | SslProtocols.Tls13));
    }

    [Test]
    public void CreateHandler_MinimumTls13_ExcludesTls12()
    {
        using var handler = ScimHttpClientFactory.CreateHandler(CreateSettings(minimumTls: ScimConnectorConstants.TlsVersion13), [], _logger);

        Assert.Multiple(() =>
        {
            Assert.That(handler.SslOptions.EnabledSslProtocols.HasFlag(SslProtocols.Tls13), Is.True);
            Assert.That(handler.SslOptions.EnabledSslProtocols.HasFlag(SslProtocols.Tls12), Is.False);
        });
    }

    [Test]
    public void CreateHandler_NoTlsSettingValue_DefaultsToTls12()
    {
        var settings = new List<ConnectedSystemSettingValue> { Setting(ScimConnectorConstants.SettingBaseUrl, "https://provider.example.com") };

        using var handler = ScimHttpClientFactory.CreateHandler(settings, [], _logger);

        Assert.That(handler.SslOptions.EnabledSslProtocols, Is.EqualTo(SslProtocols.Tls12 | SslProtocols.Tls13));
    }

    [Test]
    public void CreateHandler_FullValidation_RejectsAnUntrustedCertificate()
    {
        using var handler = ScimHttpClientFactory.CreateHandler(CreateSettings(), [], _logger);
        using var certificate = CreateUntrustedCertificate();

        var accepted = handler.SslOptions.RemoteCertificateValidationCallback!
            .Invoke(this, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.That(accepted, Is.False);
    }

    [Test]
    public void CreateHandler_FullValidation_AcceptsACertificateTrustedByJimStore()
    {
        using var certificate = CreateUntrustedCertificate();
        using var handler = ScimHttpClientFactory.CreateHandler(CreateSettings(), [certificate], _logger);

        var accepted = handler.SslOptions.RemoteCertificateValidationCallback!
            .Invoke(this, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.That(accepted, Is.True);
    }

    [Test]
    public void CreateHandler_SkipValidation_AcceptsAnythingIncludingHostnameMismatch()
    {
        // An explicit, clearly labelled insecure escape hatch for lab use. It must be total: a
        // partially-validating "skip" would be worse than either honest option.
        using var handler = ScimHttpClientFactory.CreateHandler(
            CreateSettings(certificateValidation: ScimConnectorConstants.CertValidationSkip), [], _logger);
        using var certificate = CreateUntrustedCertificate();

        var accepted = handler.SslOptions.RemoteCertificateValidationCallback!
            .Invoke(this, certificate, null,
                SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch);

        Assert.That(accepted, Is.True);
    }

    [Test]
    public void CreateHandler_UnrecognisedCertificateValidationValue_FallsBackToFullValidation()
    {
        // Failing safe matters here: a typo in the setting must not silently disable certificate checks.
        using var handler = ScimHttpClientFactory.CreateHandler(
            CreateSettings(certificateValidation: "Sure, whatever"), [], _logger);
        using var certificate = CreateUntrustedCertificate();

        var accepted = handler.SslOptions.RemoteCertificateValidationCallback!
            .Invoke(this, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.That(accepted, Is.False);
    }

    [Test]
    public void CreateHttpClient_ConfiguredTimeout_IsApplied()
    {
        using var handler = ScimHttpClientFactory.CreateHandler(CreateSettings(timeoutSeconds: 45), [], _logger);

        using var httpClient = ScimHttpClientFactory.CreateHttpClient(handler, CreateSettings(timeoutSeconds: 45));

        Assert.That(httpClient.Timeout, Is.EqualTo(TimeSpan.FromSeconds(45)));
    }

    [Test]
    public void CreateHttpClient_NoTimeoutConfigured_UsesTheConnectorDefault()
    {
        using var handler = ScimHttpClientFactory.CreateHandler(CreateSettings(), [], _logger);

        using var httpClient = ScimHttpClientFactory.CreateHttpClient(handler, CreateSettings());

        Assert.That(httpClient.Timeout, Is.EqualTo(TimeSpan.FromSeconds(ScimConnectorConstants.DefaultConnectionTimeoutSeconds)));
    }

    [Test]
    public void CreateRetryPolicy_UsesConfiguredRetryCountAndDelay()
    {
        var settings = new List<ConnectedSystemSettingValue>
        {
            Setting(ScimConnectorConstants.SettingMaxRetries, intValue: 2),
            Setting(ScimConnectorConstants.SettingRetryDelay, intValue: 250)
        };

        var policy = ScimHttpClientFactory.CreateRetryPolicy(settings);
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);

        var firstAttempt = policy.EvaluateResponse(response, attempt: 1, DateTimeOffset.UnixEpoch);
        var exhausted = policy.EvaluateResponse(response, attempt: 2, DateTimeOffset.UnixEpoch);

        Assert.Multiple(() =>
        {
            Assert.That(firstAttempt.ShouldRetry, Is.True);
            // The configured delay plus the policy's default jitter band of up to 20%.
            Assert.That(firstAttempt.Delay, Is.InRange(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(300)));
            Assert.That(exhausted.ShouldRetry, Is.False, "the configured maximum of two retries is respected.");
        });
    }

    [Test]
    public void CreateRetryPolicy_NoRetrySettingsConfigured_UsesConnectorDefaults()
    {
        var policy = ScimHttpClientFactory.CreateRetryPolicy([]);
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);

        var firstAttempt = policy.EvaluateResponse(response, attempt: 1, DateTimeOffset.UnixEpoch);

        Assert.Multiple(() =>
        {
            Assert.That(firstAttempt.ShouldRetry, Is.True);
            Assert.That(firstAttempt.Delay, Is.InRange(
                TimeSpan.FromMilliseconds(ScimConnectorConstants.DefaultRetryDelayMs),
                TimeSpan.FromMilliseconds(ScimConnectorConstants.DefaultRetryDelayMs * 1.2)));
        });
    }
}
