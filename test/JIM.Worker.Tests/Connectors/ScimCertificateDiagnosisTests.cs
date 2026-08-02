// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using JIM.Connectors.SCIM;
using JIM.Models.Connectors;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// What an administrator is told when a SCIM service provider's TLS certificate is refused.
/// <para>
/// The transport reports a refused certificate as "The SSL connection could not be established", which
/// is indistinguishable from a firewall. Showing the certificate instead is what lets an administrator
/// trust that specific certificate, a decision made at a point in time, rather than reaching for the
/// setting that accepts whatever is presented from then on.
/// </para>
/// </summary>
[TestFixture]
public class ScimCertificateDiagnosisTests
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

    /// <summary>
    /// A connector whose transport always fails the TLS handshake and whose certificate examination
    /// returns a scripted diagnostic, so the path from failure to explanation is testable without
    /// standing up a TLS server.
    /// </summary>
    private sealed class CertificateRefusingScimConnector : ScimConnector
    {
        private readonly ServerCertificateDiagnostic? _diagnostic;

        public CertificateRefusingScimConnector(ServerCertificateDiagnostic? diagnostic)
        {
            _diagnostic = diagnostic;
        }

        public (string Host, int Port)? Probed { get; private set; }

        internal override Task<ScimHttpClient> CreateClientAsync(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
        {
            var handler = new StubHttpMessageHandler(_ =>
                throw new HttpRequestException("The SSL connection could not be established.",
                    new AuthenticationException("The remote certificate is invalid.")));

            return Task.FromResult(new ScimHttpClient(
                new HttpClient(handler),
                new Uri("https://provider.example.com/scim/v2"),
                new JIM.Connectors.SCIM.Authentication.ScimStaticBearerTokenAuthentication("token"),
                new ScimRetryPolicy(maxRetries: 0, baseDelay: TimeSpan.Zero, maxDelay: TimeSpan.Zero),
                logger,
                delay: (_, _) => Task.CompletedTask));
        }

        internal override ServerCertificateDiagnostic? ProbeCertificate(
            string host, int port, IReadOnlyCollection<X509Certificate2> trustedCertificates, TimeSpan timeout, ILogger logger)
        {
            Probed = (host, port);
            return _diagnostic;
        }
    }

    private static ConnectedSystemSettingValue Setting(string name, string? stringValue = null)
    {
        return new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = name },
            StringValue = stringValue
        };
    }

    private static List<ConnectedSystemSettingValue> Settings(
        string baseUrl = "https://provider.example.com/scim/v2",
        string certificateValidation = ScimConnectorConstants.CertValidationFull)
    {
        return
        [
            Setting(ScimConnectorConstants.SettingBaseUrl, baseUrl),
            Setting(ScimConnectorConstants.SettingAuthenticationMethod, ScimConnectorConstants.AuthMethodStaticBearerToken),
            Setting(ScimConnectorConstants.SettingCertificateValidation, certificateValidation)
        ];
    }

    private static ServerCertificateDiagnostic UntrustedIssuer()
    {
        return new ServerCertificateDiagnostic
        {
            Host = "provider.example.com",
            Port = 443,
            Subject = "CN=provider.example.com",
            Issuer = "CN=Example Internal CA",
            Thumbprint = "AABBCCDDEEFF00112233445566778899AABBCCDD",
            FailureReason = ServerCertificateFailureReason.UntrustedIssuer,
            Remediation = "The issuing certificate authority is not trusted. Add it, and any intermediates, to the JIM certificate store (Admin > Certificates)."
        };
    }

    #region recognising a certificate failure
    [Test]
    public void LooksLikeACertificateFailure_HandshakeFailureNestedInATransportError_IsRecognised()
    {
        // The handshake failure arrives inside HttpRequestException, and the connector wraps that again.
        var exception = new ScimRequestException("The SCIM request failed.", HttpStatusCode.ServiceUnavailable,
            new HttpRequestException("The SSL connection could not be established.", new AuthenticationException("bad certificate")));

        Assert.That(ScimCertificateDiagnosis.LooksLikeACertificateFailure(exception), Is.True);
    }

    [Test]
    public void LooksLikeACertificateFailure_RefusedConnection_IsNotACertificateProblem()
    {
        // A firewall, a wrong port and a timeout must keep their own message rather than being blamed on
        // a certificate the administrator would then go looking for.
        var exception = new HttpRequestException("Connection refused.", new SocketException(111));

        Assert.That(ScimCertificateDiagnosis.LooksLikeACertificateFailure(exception), Is.False);
    }

    [Test]
    public void ResolveEndpoint_HttpsBaseUrlWithNoPort_UsesTheDefaultHttpsPort()
    {
        Assert.That(ScimCertificateDiagnosis.ResolveEndpoint("https://provider.example.com/scim/v2"), Is.EqualTo(("provider.example.com", 443)));
    }

    [Test]
    public void ResolveEndpoint_ExplicitPort_IsHonoured()
    {
        Assert.That(ScimCertificateDiagnosis.ResolveEndpoint("https://provider.example.com:8443/scim/v2"), Is.EqualTo(("provider.example.com", 8443)));
    }

    [Test]
    public void ResolveEndpoint_PlainHttpBaseUrl_HasNoCertificateToExplain()
    {
        Assert.That(ScimCertificateDiagnosis.ResolveEndpoint("http://localhost:5300"), Is.Null);
    }
    #endregion

    #region what the administrator is shown
    /// <summary>
    /// Reported as a failed validation result carrying the rejection, exactly as the LDAP connector does, because
    /// that is what the portal's settings tab and the REST settings endpoint both read. Throwing out of validation
    /// instead escapes both: the portal never renders the certificate card, and the API answers 500.
    /// </summary>
    [Test]
    public void ValidateSettingValues_ProviderCertificateRefused_ReportsTheCertificateRatherThanAnOpaqueFailure()
    {
        var connector = new CertificateRefusingScimConnector(UntrustedIssuer());

        var results = connector.ValidateSettingValues(Settings(), _logger);

        var failure = results.Single(r => !r.IsValid);
        var rejection = failure.Exception as ServerCertificateRejectedException;
        Assert.That(rejection, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(rejection!.Diagnostic.Thumbprint, Is.EqualTo("AABBCCDDEEFF00112233445566778899AABBCCDD"));
            Assert.That(rejection!.Diagnostic.FailureReason, Is.EqualTo(ServerCertificateFailureReason.UntrustedIssuer));
            // The remediation points at the store, which is the decision JIM wants an administrator to make.
            Assert.That(failure.ErrorMessage, Does.Contain("Admin > Certificates"));
            Assert.That(connector.Probed, Is.EqualTo(("provider.example.com", 443)));
        });
    }

    [Test]
    public void ValidateSettingValues_CertificateThatTurnsOutToBeFine_LeavesTheOriginalFailureAlone()
    {
        // The connection failed for some other reason; blaming the certificate would send the
        // administrator after a problem that is not there.
        var connector = new CertificateRefusingScimConnector(new ServerCertificateDiagnostic
        {
            Host = "provider.example.com",
            Port = 443,
            FailureReason = ServerCertificateFailureReason.None
        });

        var results = connector.ValidateSettingValues(Settings(), _logger);

        Assert.That(results.Single().ErrorMessage, Does.Contain("Could not connect to the SCIM service provider"));
    }

    /// <summary>
    /// The gap that made this shareable worth doing. The certificate was only ever examined when settings were
    /// validated, so a Run Profile that failed on TLS reported an opaque transport error and the failed Activity
    /// never carried the certificate, which is exactly where an administrator looks after a run fails. The LDAP
    /// connector has always diagnosed at the connection site; these prove SCIM now does too.
    /// </summary>
    [Test]
    public void GetSchemaAsync_ProviderCertificateRefused_ReportsTheCertificate()
    {
        var connector = new CertificateRefusingScimConnector(UntrustedIssuer());

        var exception = Assert.ThrowsAsync<ServerCertificateRejectedException>(async () =>
            await connector.GetSchemaAsync(Settings(), _logger));

        Assert.That(exception!.Diagnostic.Thumbprint, Is.EqualTo("AABBCCDDEEFF00112233445566778899AABBCCDD"));
    }

    [Test]
    public void ImportAsync_ProviderCertificateRefused_ReportsTheCertificateRatherThanAnOpaqueTransportFailure()
    {
        var connector = new CertificateRefusingScimConnector(UntrustedIssuer());
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "HR Cloud", SettingValues = Settings() };
        connector.OpenImportConnection(Settings(), _logger);

        var exception = Assert.ThrowsAsync<ServerCertificateRejectedException>(async () =>
            await connector.ImportAsync(connectedSystem, new ConnectedSystemRunProfile { RunType = ConnectedSystemRunType.FullImport },
                [], null, _logger, CancellationToken.None, new RecordingConnectorProgress()));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Diagnostic.Thumbprint, Is.EqualTo("AABBCCDDEEFF00112233445566778899AABBCCDD"));
            Assert.That(exception!.Message, Does.Contain("Admin > Certificates"));
        });
    }

    [Test]
    public void ExportAsync_ProviderCertificateRefused_ReportsTheCertificateRatherThanAnOpaqueTransportFailure()
    {
        var connector = new CertificateRefusingScimConnector(UntrustedIssuer());
        connector.OpenExportConnection(Settings());

        var exception = Assert.ThrowsAsync<ServerCertificateRejectedException>(async () =>
            await connector.ExportAsync([new PendingExport { Id = Guid.NewGuid(), ChangeType = PendingExportChangeType.Create }], CancellationToken.None, new RecordingConnectorProgress()));

        Assert.That(exception!.Diagnostic.Thumbprint, Is.EqualTo("AABBCCDDEEFF00112233445566778899AABBCCDD"));
    }

    [Test]
    public void ValidateSettingValues_ProviderCouldNotBeReachedToLookAtItsCertificate_LeavesTheOriginalFailureAlone()
    {
        var connector = new CertificateRefusingScimConnector(diagnostic: null);

        var results = connector.ValidateSettingValues(Settings(), _logger);

        Assert.That(results.Single().IsValid, Is.False);
    }

    [Test]
    public void ValidateSettingValues_ValidationDeliberatelySkipped_DoesNotBlameTheCertificate()
    {
        // The administrator has already told JIM not to validate, so a failure now is not about trust and
        // showing them a certificate to add would be misleading.
        var connector = new CertificateRefusingScimConnector(UntrustedIssuer());

        var results = connector.ValidateSettingValues(Settings(certificateValidation: ScimConnectorConstants.CertValidationSkip), _logger);

        Assert.Multiple(() =>
        {
            Assert.That(results.Single().IsValid, Is.False);
            Assert.That(connector.Probed, Is.Null);
        });
    }
    #endregion
}
