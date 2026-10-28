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
/// Verifies the JIM SQL Connector's certificate trust against a real Oracle Database TCPS listener presenting a
/// certificate under a root and an intermediate nobody trusts (#2581).
/// </summary>
/// <remarks>
/// <para>
/// ODP.NET makes the trust decision itself, from an Oracle wallet JIM writes for the connection, and it builds the chain
/// from that wallet alone: the root and the intermediates are needed together, and neither works on its own. None of
/// that is visible to a unit test, which scripts the driver, so these tests are opt-in and need a real listener standing
/// by. They also pin host name verification (#2589), which only the driver performs.
/// </para>
/// <para>
/// Stand it up with <c>test/scripts/Start-OracleTlsTestServer.ps1</c>, which prints the environment variables to set.
/// The fixture is ignored when <c>JIM_TEST_ORACLETLS_HOST</c> is absent.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresOracleTls")]
public class OracleTlsCertificateValidationTests
{
    private string _host = null!;
    private int _port;
    private string _serviceName = null!;
    private string _username = null!;
    private string _password = null!;
    private string _rootPath = null!;
    private string _intermediatePath = null!;
    private string _serverCertificatePath = null!;
    private string _mismatchedHost = null!;
    private Serilog.Core.Logger _logger = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _host = Environment.GetEnvironmentVariable("JIM_TEST_ORACLETLS_HOST") ?? string.Empty;
        if (string.IsNullOrEmpty(_host))
            Assert.Ignore("JIM_TEST_ORACLETLS_HOST not set; skipping Oracle TCPS certificate validation tests. See test/scripts/Start-OracleTlsTestServer.ps1.");

        _port = int.Parse(Environment.GetEnvironmentVariable("JIM_TEST_ORACLETLS_PORT") ?? "2484");
        _serviceName = Environment.GetEnvironmentVariable("JIM_TEST_ORACLETLS_SERVICE_NAME") ?? "FREEPDB1";
        _username = Environment.GetEnvironmentVariable("JIM_TEST_ORACLETLS_USERNAME") ?? string.Empty;
        _password = Environment.GetEnvironmentVariable("JIM_TEST_ORACLETLS_PASSWORD") ?? string.Empty;
        _rootPath = Environment.GetEnvironmentVariable("JIM_TEST_ORACLETLS_ROOT_CA_PATH") ?? string.Empty;
        _intermediatePath = Environment.GetEnvironmentVariable("JIM_TEST_ORACLETLS_INTERMEDIATE_CA_PATH") ?? string.Empty;
        _serverCertificatePath = Environment.GetEnvironmentVariable("JIM_TEST_ORACLETLS_SERVER_CERTIFICATE_PATH") ?? string.Empty;
        _mismatchedHost = Environment.GetEnvironmentVariable("JIM_TEST_ORACLETLS_MISMATCH_HOST") ?? string.Empty;

        if (!File.Exists(_rootPath) || !File.Exists(_intermediatePath) || !File.Exists(_serverCertificatePath))
            Assert.Ignore("A JIM_TEST_ORACLETLS_*_PATH certificate is not set or does not exist; skipping Oracle TCPS certificate validation tests.");
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
    public void ValidateSettingValues_TcpsWithTheRootInTheJimStore_Connects()
    {
        // The root is in no operating system bundle, so the driver refuses the first attempt; JIM then reads the chain,
        // finds the root in the JIM certificate store, and hands the driver a wallet of the root and the intermediate.
        var results = Validate(_host, _rootPath);

        Assert.That(results, Is.Empty, string.Join("; ", results.Select(r => r.ErrorMessage)));
    }

    [Test]
    public void ValidateSettingValues_TcpsWithAnEmptyJimStore_IsRefusedAsAnUntrustedIssuer()
    {
        // Nothing trusts the root, so this must not connect; if it does, validation is not happening at all. The failure
        // names the certificate rather than blaming the network.
        var results = Validate(_host);

        Assert.That(FailureReasonOf(results), Is.EqualTo(ServerCertificateFailureReason.UntrustedIssuer));
    }

    [TestCase("intermediate")]
    [TestCase("server")]
    public void ValidateSettingValues_TcpsWithOnlyANonRootCertificateInTheJimStore_IsRefusedAsAnUntrustedIssuer(string trusted)
    {
        // ODP.NET accepts a wallet's chain only when it ends at a root trusted there, so an intermediate or the server's
        // own certificate cannot make the connection work, and JIM must say so rather than call the chain trusted.
        var results = Validate(_host, trusted == "server" ? _serverCertificatePath : _intermediatePath);

        Assert.That(FailureReasonOf(results), Is.EqualTo(ServerCertificateFailureReason.UntrustedIssuer));
    }

    [Test]
    public void ValidateSettingValues_TcpsToANameTheCertificateWasNotIssuedFor_IsRefusedAsANameMismatch()
    {
        if (string.IsNullOrEmpty(_mismatchedHost))
            Assert.Ignore("JIM_TEST_ORACLETLS_MISMATCH_HOST not set; skipping the certificate name mismatch test.");

        // Same server, same trusted root, reached by a name the certificate does not carry (#2589). Trusting the root
        // must not amount to trusting any name it ever signs.
        var results = Validate(_mismatchedHost, _rootPath);

        Assert.That(FailureReasonOf(results), Is.EqualTo(ServerCertificateFailureReason.NameMismatch));
    }

    [Test]
    public void ReadServerCertificate_ForTheTrustPrompt_ReturnsTheChainTheServerPresents()
    {
        // The read behind "trust this certificate" in the portal, the REST API and PowerShell, which for Oracle offers
        // the root alone: the chain has to reach it.
        using var connector = new SqlConnector();
        var endpoint = connector.ResolveSecureEndpoint(BuildSettingValues(_host));
        using var serverCertificate = X509CertificateLoader.LoadCertificateFromFile(_serverCertificatePath);
        using var root = X509CertificateLoader.LoadCertificateFromFile(_rootPath);

        var reading = new ServerCertificateReader().Read(endpoint!, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(endpoint!.TrustAnchorMustBeRoot, Is.True);
            Assert.That(reading?.Chain?.Leaf?.Thumbprint, Is.EqualTo(serverCertificate.Thumbprint));
            Assert.That(reading?.Chain?.Root?.Thumbprint, Is.EqualTo(root.Thumbprint), "The server sends its root, so the prompt can offer it.");
        }
    }

    private List<ConnectorSettingValueValidationResult> Validate(string host, params string[] trustedCertificatePaths)
    {
        using var connector = new SqlConnector();
        connector.SetCertificateProvider(new LdapsTestConnections.FakeCertificateProvider(trustedCertificatePaths));

        return connector.ValidateSettingValues(BuildSettingValues(host), _logger);
    }

    private static ServerCertificateFailureReason? FailureReasonOf(List<ConnectorSettingValueValidationResult> results)
    {
        Assert.That(results, Has.Count.EqualTo(1), string.Join("; ", results.Select(r => r.ErrorMessage)));
        Assert.That(results[0].Exception, Is.TypeOf<ServerCertificateRejectedException>(),
            $"A refused certificate must reach the administrator as the certificate it was. Reported instead: {results[0].ErrorMessage}");

        return ((ServerCertificateRejectedException)results[0].Exception!).Diagnostic.FailureReason;
    }

    private List<ConnectedSystemSettingValue> BuildSettingValues(string host)
    {
        return
        [
            NewSetting(SqlConnectorConstants.SettingDatabaseType, stringValue: SqlConnectorConstants.DatabaseTypeOracle),
            NewSetting(SqlConnectorConstants.SettingHost, stringValue: host),
            NewSetting(SqlConnectorConstants.SettingPort, intValue: _port),
            NewSetting(SqlConnectorConstants.SettingOracleDatabaseIdentifiedBy, stringValue: SqlConnectorConstants.OracleIdentifiedByServiceName),
            NewSetting(SqlConnectorConstants.SettingOracleServiceName, stringValue: _serviceName),
            NewSetting(SqlConnectorConstants.SettingUsername, stringValue: _username),
            NewSetting(SqlConnectorConstants.SettingPassword, encryptedValue: _password),
            NewSetting(SqlConnectorConstants.SettingOracleEncryption, stringValue: SqlConnectorConstants.OracleEncryptionTcps),
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
