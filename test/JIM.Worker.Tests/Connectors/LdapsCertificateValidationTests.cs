// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Verifies LDAPS certificate validation against real directory servers presenting real certificates (#1132).
/// </summary>
/// <remarks>
/// <para>
/// None of this is unit-testable. JIM deliberately no longer makes the trust decision itself: the platform LDAP
/// client validates the chain, the validity period and the certificate's name, and JIM only supplies additional
/// trust anchors. What that client does with those anchors can only be observed by connecting to a directory
/// server over TLS, so these tests are opt-in and need servers standing by.
/// </para>
/// <para>
/// Stand the servers up with <c>scripts/Start-LdapsCertificateTestServers.ps1</c>, which prints the environment
/// variables to set. The fixture is ignored when <c>JIM_TEST_LDAPS_HOST</c> is absent.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresLdaps")]
public class LdapsCertificateValidationTests
{
    private string _host = null!;
    private int _port;
    private string _username = null!;
    private string _password = null!;
    private string _caCertificatePath = null!;
    private Serilog.Core.Logger _logger = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _host = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_HOST") ?? string.Empty;
        if (string.IsNullOrEmpty(_host))
            Assert.Ignore("JIM_TEST_LDAPS_HOST not set; skipping LDAPS certificate validation tests. See scripts/Start-LdapsCertificateTestServers.ps1.");

        _port = int.Parse(Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_PORT") ?? "636");
        _username = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_USERNAME") ?? "cn=admin,dc=example,dc=org";
        _password = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_PASSWORD") ?? "adminpassword";
        _caCertificatePath = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_CA_PATH") ?? string.Empty;

        if (string.IsNullOrEmpty(_caCertificatePath) || !File.Exists(_caCertificatePath))
            Assert.Ignore("JIM_TEST_LDAPS_CA_PATH is not set or does not exist; skipping LDAPS certificate validation tests.");
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

    /// <summary>
    /// Opens an import connection the way the synchronisation engine does, with the supplied certificates standing in
    /// for the JIM certificate store.
    /// </summary>
    private void OpenConnection(string host, int port, params string[] trustedCertificatePaths)
    {
        using var connector = new LdapConnector();
        connector.SetCertificateProvider(new FakeCertificateProvider(trustedCertificatePaths));

        try
        {
            connector.OpenImportConnection(BuildSettingValues(host, port), _logger);
        }
        finally
        {
            connector.CloseImportConnection();
        }
    }

    private List<ConnectedSystemSettingValue> BuildSettingValues(string host, int port)
    {
        return
        [
            NewSetting("Host", stringValue: host),
            NewSetting("Port", intValue: port),
            NewSetting("Use Secure Connection (LDAPS)?", checkboxValue: true),
            NewSetting("Connection Timeout", intValue: 10),
            NewSetting("Username", stringValue: _username),
            NewSetting("Password", encryptedValue: _password),
            NewSetting("Authentication Type", stringValue: "Simple"),
            // One attempt only: a rejected certificate reports as a down server, which the connector treats as
            // transient, and retrying it just multiplies the wait before the test can assert.
            NewSetting("Maximum Retries", intValue: 0)
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

    [Test]
    public void OpenImportConnection_WithIssuingCertificateInTheJimStore_Connects()
    {
        Assert.That(() => OpenConnection(_host, _port, _caCertificatePath), Throws.Nothing);
    }

    [Test]
    public void OpenImportConnection_WithAnEmptyJimStore_IsRejected()
    {
        // The issuing CA is trusted by neither the operating system nor JIM, so this must not connect. If it does,
        // validation is not happening at all.
        Assert.That(() => OpenConnection(_host, _port), Throws.TypeOf<LdapException>());
    }

    [Test]
    public void OpenImportConnection_WhenTheCertificateNameDoesNotMatchTheDirectoryServer_IsRejected()
    {
        var mismatchedHost = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_MISMATCH_HOST");
        if (string.IsNullOrEmpty(mismatchedHost))
            Assert.Ignore("JIM_TEST_LDAPS_MISMATCH_HOST not set; skipping the certificate name mismatch test.");

        // Same server, same trusted issuer, reached by a name the certificate was not issued for. Trusting the issuer
        // must not amount to trusting any name it ever signs.
        Assert.That(() => OpenConnection(mismatchedHost!, _port, _caCertificatePath), Throws.TypeOf<LdapException>());
    }

    [Test]
    public void OpenImportConnection_WhenTheCertificateHasExpired_IsRejected()
    {
        var expiredHost = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_EXPIRED_HOST");
        var expiredPort = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_EXPIRED_PORT");
        if (string.IsNullOrEmpty(expiredHost) || string.IsNullOrEmpty(expiredPort))
            Assert.Ignore("JIM_TEST_LDAPS_EXPIRED_HOST/PORT not set; skipping the expired certificate test.");

        // The issuer is in the JIM certificate store, which vouches for who signed the certificate, not for how long
        // ago it stopped being valid.
        Assert.That(() => OpenConnection(expiredHost!, int.Parse(expiredPort!), _caCertificatePath), Throws.TypeOf<LdapException>());
    }

    /// <summary>
    /// The acceptance criterion that populating JIM's certificate store never weakens validation. Supplying trust
    /// anchors to the platform LDAP client replaces the ones it was configured with, so a directory whose certificate
    /// the host already trusts has to keep connecting after an unrelated certificate is added to the JIM store.
    /// </summary>
    [Test]
    public void OpenImportConnection_WithAnUnrelatedCertificateInTheJimStore_StillTrustsTheOperatingSystemAnchors()
    {
        var systemTrustedHost = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_SYSTEM_TRUSTED_HOST");
        var systemTrustedPort = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_SYSTEM_TRUSTED_PORT");
        if (string.IsNullOrEmpty(systemTrustedHost) || string.IsNullOrEmpty(systemTrustedPort))
            Assert.Ignore("JIM_TEST_LDAPS_SYSTEM_TRUSTED_HOST/PORT not set; skipping the additive trust test.");

        Assert.That(
            () => OpenConnection(systemTrustedHost!, int.Parse(systemTrustedPort!), _caCertificatePath),
            Throws.Nothing);
    }

    /// <summary>
    /// Supplies certificates from PEM files in place of the JIM certificate store.
    /// </summary>
    private sealed class FakeCertificateProvider : ICertificateProvider
    {
        private readonly string[] _certificatePaths;

        internal FakeCertificateProvider(string[] certificatePaths)
        {
            _certificatePaths = certificatePaths;
        }

        public Task<List<X509Certificate2>> GetTrustedCertificatesAsync()
        {
            return Task.FromResult(_certificatePaths
                .Select(X509CertificateLoader.LoadCertificateFromFile)
                .ToList());
        }
    }
}
