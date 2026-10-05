// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors;
using JIM.Connectors.Sql;
using JIM.Models.Connectors;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using Serilog;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Verifies the JIM SQL Connector's certificate trust against a real Microsoft SQL Server presenting a real
/// certificate over an encrypted connection (#1472).
/// </summary>
/// <remarks>
/// <para>
/// The unit tests in <see cref="SqlConnectorCertificateTrustTests"/> script both the driver and the look at the
/// server's certificate, so no TLS handshake ever happens there. That is how JIM shipped a probe that opened a bare
/// TLS handshake against SQL Server, which only speaks TLS inside a TDS PRELOGIN exchange: every encrypted failure
/// was reported as "unable to connect", and a certificate added in Admin &gt; Certificates was never offered to the
/// driver. Only a real server shows that, so these tests are opt-in and need one standing by.
/// </para>
/// <para>
/// Stand it up with <c>test/scripts/Start-SqlServerTlsTestServer.ps1</c>, which prints the environment variables to
/// set. The fixture is ignored when <c>JIM_TEST_SQLTLS_HOST</c> is absent.
/// </para>
/// <para>
/// Oracle Database is deliberately not covered here; see the Oracle section of "SQL Server TLS Certificate
/// Validation Tests" in <c>engineering/TESTING_STRATEGY.md</c> for why.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresSqlTls")]
public class SqlServerTlsCertificateValidationTests
{
    private string _host = null!;
    private int _port;
    private string _username = null!;
    private string _password = null!;
    private string _caCertificatePath = null!;
    private string _serverCertificatePath = null!;
    private string _mismatchedHost = null!;
    private Serilog.Core.Logger _logger = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _host = Environment.GetEnvironmentVariable("JIM_TEST_SQLTLS_HOST") ?? string.Empty;
        if (string.IsNullOrEmpty(_host))
            Assert.Ignore("JIM_TEST_SQLTLS_HOST not set; skipping SQL Server TLS certificate validation tests. See test/scripts/Start-SqlServerTlsTestServer.ps1.");

        _port = int.Parse(Environment.GetEnvironmentVariable("JIM_TEST_SQLTLS_PORT") ?? "1433");
        _username = Environment.GetEnvironmentVariable("JIM_TEST_SQLTLS_USERNAME") ?? "sa";
        _password = Environment.GetEnvironmentVariable("JIM_TEST_SQLTLS_PASSWORD") ?? string.Empty;
        _caCertificatePath = Environment.GetEnvironmentVariable("JIM_TEST_SQLTLS_CA_PATH") ?? string.Empty;
        _serverCertificatePath = Environment.GetEnvironmentVariable("JIM_TEST_SQLTLS_SERVER_CERTIFICATE_PATH") ?? string.Empty;
        _mismatchedHost = Environment.GetEnvironmentVariable("JIM_TEST_SQLTLS_MISMATCH_HOST") ?? string.Empty;

        if (!File.Exists(_caCertificatePath) || !File.Exists(_serverCertificatePath))
            Assert.Ignore("JIM_TEST_SQLTLS_CA_PATH or JIM_TEST_SQLTLS_SERVER_CERTIFICATE_PATH is not set or does not exist; skipping SQL Server TLS certificate validation tests.");
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
    public void ValidateSettingValues_Unencrypted_Connects()
    {
        // Encryption off is a supported choice, and what Scenario 16 runs on throughout. The server still encrypts
        // the login packet with its own certificate, but nothing is validated on that path, so an untrusted issuer
        // must not get in the way.
        var results = Validate(_host, encrypt: false);

        Assert.That(results, Is.Empty, string.Join("; ", results.Select(r => r.ErrorMessage)));
    }

    [Test]
    public void ValidateSettingValues_EncryptedWithTheIssuerInTheJimStore_Connects()
    {
        // The issuer is in no operating system bundle, so the driver refuses the first attempt; JIM then reads the
        // certificate, finds the JIM certificate store vouches for it, and hands it to the driver for a second.
        var results = Validate(_host, encrypt: true, _caCertificatePath);

        Assert.That(results, Is.Empty, string.Join("; ", results.Select(r => r.ErrorMessage)));
    }

    [Test]
    public void ValidateSettingValues_EncryptedWithTheServersOwnCertificateInTheJimStore_Connects()
    {
        // #1914: the trust prompt offers the server's own certificate, but the retry demanded a root in the store, so
        // trusting it reported success and the connection was still refused.
        var results = Validate(_host, encrypt: true, _serverCertificatePath);

        Assert.That(results, Is.Empty, string.Join("; ", results.Select(r => r.ErrorMessage)));
    }

    [Test]
    public void ValidateSettingValues_EncryptedWithAnEmptyJimStore_IsRefusedAsAnUntrustedIssuer()
    {
        // Nothing trusts the issuer, so this must not connect; if it does, validation is not happening at all. The
        // failure names the certificate rather than blaming the network.
        var results = Validate(_host, encrypt: true);

        Assert.That(FailureReasonOf(results), Is.EqualTo(ServerCertificateFailureReason.UntrustedIssuer));
    }

    [Test]
    public void ValidateSettingValues_EncryptedToANameTheCertificateWasNotIssuedFor_IsRefusedAsANameMismatch()
    {
        if (string.IsNullOrEmpty(_mismatchedHost))
            Assert.Ignore("JIM_TEST_SQLTLS_MISMATCH_HOST not set; skipping the certificate name mismatch test.");

        // Same server, same trusted issuer, reached by a name the certificate does not carry. Trusting the issuer
        // must not amount to trusting any name it ever signs.
        var results = Validate(_mismatchedHost, encrypt: true, _caCertificatePath);

        Assert.That(FailureReasonOf(results), Is.EqualTo(ServerCertificateFailureReason.NameMismatch));
    }

    [Test]
    public void ReadServerCertificate_ForTheTrustPrompt_ReturnsTheCertificateTheServerPresents()
    {
        // The read behind "trust this certificate" in the portal, the REST API and PowerShell. Without it an
        // administrator shown a refusal has no way to add what the server presented.
        using var connector = new SqlConnector();
        var endpoint = connector.ResolveSecureEndpoint(BuildSettingValues(_host, encrypt: true));
        using var serverCertificate = X509CertificateLoader.LoadCertificateFromFile(_serverCertificatePath);

        var reading = new ServerCertificateReader().Read(endpoint!, []);

        Assert.That(reading?.Chain?.Leaf?.Thumbprint, Is.EqualTo(serverCertificate.Thumbprint),
            "SQL Server answered, so the certificate it presents must have been read.");
    }

    private List<ConnectorSettingValueValidationResult> Validate(string host, bool encrypt, params string[] trustedCertificatePaths)
    {
        using var connector = new SqlConnector();
        connector.SetCertificateProvider(new LdapsTestConnections.FakeCertificateProvider(trustedCertificatePaths));

        return connector.ValidateSettingValues(BuildSettingValues(host, encrypt), _logger);
    }

    private static ServerCertificateFailureReason? FailureReasonOf(List<ConnectorSettingValueValidationResult> results)
    {
        Assert.That(results, Has.Count.EqualTo(1), string.Join("; ", results.Select(r => r.ErrorMessage)));
        Assert.That(results[0].Exception, Is.TypeOf<ServerCertificateRejectedException>(),
            $"A refused certificate must reach the administrator as the certificate it was. Reported instead: {results[0].ErrorMessage}");

        return ((ServerCertificateRejectedException)results[0].Exception!).Diagnostic.FailureReason;
    }

    private List<ConnectedSystemSettingValue> BuildSettingValues(string host, bool encrypt)
    {
        return
        [
            NewSetting(SqlConnectorConstants.SettingDatabaseType, stringValue: SqlConnectorConstants.DatabaseTypeSqlServer),
            NewSetting(SqlConnectorConstants.SettingHost, stringValue: host),
            NewSetting(SqlConnectorConstants.SettingPort, intValue: _port),
            NewSetting(SqlConnectorConstants.SettingDatabaseName, stringValue: "master"),
            NewSetting(SqlConnectorConstants.SettingUsername, stringValue: _username),
            NewSetting(SqlConnectorConstants.SettingPassword, encryptedValue: _password),
            NewSetting(SqlConnectorConstants.SettingSqlServerEncryptConnection, checkboxValue: encrypt),
            NewSetting(SqlConnectorConstants.SettingConnectionTimeout, intValue: 15),
            NewSetting(SqlConnectorConstants.SettingDatabaseTimeZone, stringValue: SqlConnectorConstants.DefaultDatabaseTimeZone)
        ];
    }

    private static ConnectedSystemSettingValue NewSetting(string name, string? stringValue = null, string? encryptedValue = null, int? intValue = null, bool checkboxValue = false)
    {
        return new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = name },
            StringValue = stringValue,
            StringEncryptedValue = encryptedValue,
            IntValue = intValue,
            CheckboxValue = checkboxValue
        };
    }
}
