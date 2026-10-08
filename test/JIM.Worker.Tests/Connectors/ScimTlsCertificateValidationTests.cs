// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.SCIM;
using JIM.Models.Connectors;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.TestScimServiceProvider;
using Serilog;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Verifies the SCIM 2.0 Client Connector's certificate validation over a real TLS handshake (#1473), against an
/// HTTPS service provider presenting certificates generated for the run.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ScimCertificateValidatorTests"/> and <see cref="ScimCertificateDiagnosisTests"/> exercise the trust
/// decision against in-memory certificates, and every other SCIM test stubs the transport, so neither the validation
/// callback's wiring onto the HTTP client nor the handshake itself runs there. That is #1132's failure class: wire the
/// validation up wrongly and it never runs, every test passes, and a customer meets a misleading connection error.
/// These tests drive the connector the way the portal's connection test does and the Integration Scenario 015 run
/// does, end to end, with only the JIM certificate store stood in for.
/// </para>
/// <para>
/// Unlike the LDAPS and SQL Server tiers, nothing here is external. Those clients are native libraries talking to
/// servers that only exist as containers; the SCIM Connector's client is .NET's own HTTP stack, and its server is the
/// same Kestrel host serving the same <see cref="MockScimProvider"/> that Scenario 015's container runs. So these run
/// in every build, under the required <c>build-and-test</c> check, with no category and nothing to stand up.
/// </para>
/// </remarks>
[TestFixture]
public class ScimTlsCertificateValidationTests
{
    /// <summary>The name every server certificate here is issued for.</summary>
    private const string HostName = "localhost";

    /// <summary>The same server reached by a name its certificate does not carry.</summary>
    private const string MismatchedHostName = "127.0.0.1";

    private const string BearerToken = "scim-tls-test-token";

    private X509Certificate2 _certificateAuthority = null!;
    private X509Certificate2 _serverCertificate = null!;
    private X509Certificate2 _expiredServerCertificate = null!;
    private string _certificateAuthorityPath = null!;
    private string _serverCertificatePath = null!;
    private HttpsScimServiceProvider _server = null!;
    private HttpsScimServiceProvider _expiredServer = null!;
    private Serilog.Core.Logger _logger = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        // A certificate authority nobody trusts, standing in for a customer's internal PKI: no operating system bundle
        // carries it, so the only thing that can vouch for it is the JIM certificate store.
        _certificateAuthority = TestPki.CreateRoot("JIM SCIM TLS Test CA");
        _serverCertificate = TestPki.CreateServer(HostName, _certificateAuthority);
        _expiredServerCertificate = TestPki.CreateServer(HostName, _certificateAuthority, notAfter: DateTimeOffset.UtcNow.AddDays(-1));

        // The JIM certificate store holds public certificates only, as an administrator adds them.
        var directory = Directory.CreateTempSubdirectory("jim-scim-tls-test-").FullName;
        _certificateAuthorityPath = Path.Combine(directory, "ca.pem");
        _serverCertificatePath = Path.Combine(directory, "server.pem");
        await File.WriteAllTextAsync(_certificateAuthorityPath, _certificateAuthority.ExportCertificatePem());
        await File.WriteAllTextAsync(_serverCertificatePath, _serverCertificate.ExportCertificatePem());

        _server = await HttpsScimServiceProvider.StartAsync(_serverCertificate, NewProvider());
        _expiredServer = await HttpsScimServiceProvider.StartAsync(_expiredServerCertificate, NewProvider());
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        await _expiredServer.DisposeAsync();
        await _server.DisposeAsync();

        var directory = Path.GetDirectoryName(_certificateAuthorityPath);
        if (directory != null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);

        _expiredServerCertificate.Dispose();
        _serverCertificate.Dispose();
        _certificateAuthority.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        _logger.Dispose();
    }

    [Test]
    public void ValidateSettingValues_IssuerInTheJimStore_Connects()
    {
        // The issuer is in no operating system bundle, so the platform refuses the chain; the validator then rebuilds
        // it with the JIM certificate store's anchors and accepts it. Connecting means the provider answered a
        // discovery request with the bearer token, over the validated connection.
        var results = Validate(HostName, _server, ScimConnectorConstants.CertValidationFull, _certificateAuthorityPath);

        Assert.That(results, Is.Empty, string.Join("; ", results.Select(r => r.ErrorMessage)));
    }

    [Test]
    public void ValidateSettingValues_ServersOwnCertificateInTheJimStore_Connects()
    {
        // The provider sends its own certificate alone, so the issuer is not available to offer; trusting the
        // certificate that was presented is what an administrator shown a refusal can do. It has to be enough.
        var results = Validate(HostName, _server, ScimConnectorConstants.CertValidationFull, _serverCertificatePath);

        Assert.That(results, Is.Empty, string.Join("; ", results.Select(r => r.ErrorMessage)));
    }

    [Test]
    public void ValidateSettingValues_CertificateTrustedByNobody_IsRefusedAsAnUntrustedIssuerShowingTheCertificate()
    {
        // Nothing trusts the issuer, so this must not connect; if it does, validation is not happening at all. The
        // failure names the certificate the provider presented rather than blaming the network.
        var results = Validate(HostName, _server, ScimConnectorConstants.CertValidationFull);

        var rejection = RejectionIn(results);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rejection.Diagnostic.FailureReason, Is.EqualTo(ServerCertificateFailureReason.UntrustedIssuer));
            Assert.That(rejection.Diagnostic.Thumbprint, Is.EqualTo(_serverCertificate.Thumbprint),
                "The administrator must be shown the certificate the provider actually presented, so they can decide to trust it.");
        }
    }

    [Test]
    public void ValidateSettingValues_NameTheCertificateWasNotIssuedFor_IsRefusedAsANameMismatch()
    {
        // Same provider, same trusted issuer, reached by a name the certificate does not carry. Trusting an issuer must
        // not amount to trusting any name it ever signs: a mismatch is an interception signal, not a trust gap.
        var results = Validate(MismatchedHostName, _server, ScimConnectorConstants.CertValidationFull, _certificateAuthorityPath);

        Assert.That(RejectionIn(results).Diagnostic.FailureReason, Is.EqualTo(ServerCertificateFailureReason.NameMismatch));
    }

    [Test]
    public void ValidateSettingValues_ExpiredCertificateFromATrustedIssuer_IsRefusedAsExpired()
    {
        // The issuer is trusted, so the only thing wrong is the validity period, and trusting an issuer does not waive
        // it. Reported as what it is, so the administrator asks for a renewed certificate rather than adding trust.
        var results = Validate(HostName, _expiredServer, ScimConnectorConstants.CertValidationFull, _certificateAuthorityPath);

        Assert.That(RejectionIn(results).Diagnostic.FailureReason, Is.EqualTo(ServerCertificateFailureReason.Expired));
    }

    [Test]
    public void ValidateSettingValues_SkipValidationWithACertificateTrustedByNobody_Connects()
    {
        // Deliberate, and the only case here that should connect without trust: Skip Validation is the documented last
        // resort and accepts whatever the provider presents. Pinned so the escape hatch keeps working while it exists,
        // and so a change that made it validate anyway would be a decision rather than an accident.
        var results = Validate(HostName, _server, ScimConnectorConstants.CertValidationSkip);

        Assert.That(results, Is.Empty, string.Join("; ", results.Select(r => r.ErrorMessage)));
    }

    [Test]
    public void GetSchemaAsync_CertificateTrustedByNobody_ThrowsTheRejectionShowingTheCertificate()
    {
        // The run path, not the connection test: an import or schema refresh refused on trust must fail its Activity
        // with the certificate, because that is where an administrator looks after a run fails.
        var connector = NewConnector();

        var rejection = Assert.ThrowsAsync<ServerCertificateRejectedException>(() =>
            connector.GetSchemaAsync(BuildSettingValues(HostName, _server.Port, ScimConnectorConstants.CertValidationFull), _logger));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rejection!.Diagnostic.FailureReason, Is.EqualTo(ServerCertificateFailureReason.UntrustedIssuer));
            Assert.That(rejection!.Diagnostic.Thumbprint, Is.EqualTo(_serverCertificate.Thumbprint));
        }
    }

    private static MockScimProvider NewProvider()
    {
        var provider = new MockScimProvider();

        // Demanding the token means a connection that succeeds was answered by the provider itself, authenticated,
        // over the connection the validator accepted.
        provider.Options.RequiredBearerToken = BearerToken;
        return provider;
    }

    private List<ConnectorSettingValueValidationResult> Validate(
        string host,
        HttpsScimServiceProvider server,
        string certificateValidation,
        params string[] trustedCertificatePaths)
    {
        var connector = NewConnector(trustedCertificatePaths);

        return connector.ValidateSettingValues(BuildSettingValues(host, server.Port, certificateValidation), _logger);
    }

    private static ScimConnector NewConnector(params string[] trustedCertificatePaths)
    {
        var connector = new ScimConnector();
        connector.SetCertificateProvider(new LdapsTestConnections.FakeCertificateProvider(trustedCertificatePaths));
        return connector;
    }

    private static ServerCertificateRejectedException RejectionIn(List<ConnectorSettingValueValidationResult> results)
    {
        Assert.That(results, Has.Count.EqualTo(1), string.Join("; ", results.Select(r => r.ErrorMessage)));
        Assert.That(results[0].Exception, Is.TypeOf<ServerCertificateRejectedException>(),
            $"A refused certificate must reach the administrator as the certificate it was. Reported instead: {results[0].ErrorMessage}");

        return (ServerCertificateRejectedException)results[0].Exception!;
    }

    private static List<ConnectedSystemSettingValue> BuildSettingValues(string host, int port, string certificateValidation)
    {
        return
        [
            NewSetting(ScimConnectorConstants.SettingBaseUrl, stringValue: $"https://{host}:{port}/scim/v2"),
            NewSetting(ScimConnectorConstants.SettingAuthenticationMethod, stringValue: ScimConnectorConstants.AuthMethodStaticBearerToken),
            NewSetting(ScimConnectorConstants.SettingBearerToken, encryptedValue: BearerToken),
            NewSetting(ScimConnectorConstants.SettingCertificateValidation, stringValue: certificateValidation),
            NewSetting(ScimConnectorConstants.SettingConnectionTimeout, intValue: 15),
            // One attempt only: a refused handshake is a transport failure, and retrying it just multiplies the wait
            // before the test can assert.
            NewSetting(ScimConnectorConstants.SettingMaxRetries, intValue: 0)
        ];
    }

    private static ConnectedSystemSettingValue NewSetting(string name, string? stringValue = null, string? encryptedValue = null, int? intValue = null)
    {
        return new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = name },
            StringValue = stringValue,
            StringEncryptedValue = encryptedValue,
            IntValue = intValue
        };
    }
}
