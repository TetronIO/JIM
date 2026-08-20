// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Connectors;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Extends the LDAPS certificate validation coverage (#1132, #1141) to unencrypted connections and to the Samba AD
/// directory type, alongside the LDAPS-over-OpenLDAP coverage in <see cref="LdapsCertificateValidationTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stand the servers up with <c>test/scripts/Start-LdapsCertificateTestServers.ps1 -IncludeSambaAd</c>, which prints
/// the environment variables read below. Unlike the fixture this one extends, there is no single mandatory
/// environment variable that gates the whole fixture: each test ignores itself independently when its own
/// variables are unset, so this fixture still runs to completion (all self-ignored) for someone who only started
/// the OpenLDAP servers, and the unencrypted-OpenLDAP test still runs for someone who did not pass
/// <c>-IncludeSambaAd</c>.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresLdaps")]
public class SambaAdAndUnencryptedLdapTests
{
    private Serilog.Core.Logger _logger = null!;

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
    /// Opens an import connection the way the synchronisation engine does, with the supplied certificates standing
    /// in for the JIM certificate store.
    /// </summary>
    private void OpenConnection(string host, int port, bool useSecureConnection, string username, string password, params string[] trustedCertificatePaths)
    {
        using var connector = new LdapConnector();
        connector.SetCertificateProvider(new FakeCertificateProvider(trustedCertificatePaths));

        try
        {
            connector.OpenImportConnection(BuildSettingValues(host, port, useSecureConnection, username, password), null, _logger);
        }
        finally
        {
            connector.CloseImportConnection();
        }
    }

    private static List<ConnectedSystemSettingValue> BuildSettingValues(string host, int port, bool useSecureConnection, string username, string password)
    {
        return
        [
            NewSetting("Host", stringValue: host),
            NewSetting("Port", intValue: port),
            NewSetting("Use Secure Connection (LDAPS)?", checkboxValue: useSecureConnection),
            NewSetting("Connection Timeout", intValue: 10),
            NewSetting("Username", stringValue: username),
            NewSetting("Password", encryptedValue: password),
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
    public void OpenImportConnection_OverAnUnencryptedConnectionToOpenLdap_Connects()
    {
        // Reuses the system-trusted OpenLDAP server from LdapsCertificateValidationTests's fixture script, over its
        // plain LDAP port rather than LDAPS, to prove the connector still works with "Use Secure Connection" off.
        var host = Environment.GetEnvironmentVariable("JIM_TEST_LDAP_PLAIN_HOST");
        var portValue = Environment.GetEnvironmentVariable("JIM_TEST_LDAP_PLAIN_PORT");
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(portValue))
            Assert.Ignore("JIM_TEST_LDAP_PLAIN_HOST/PORT not set; skipping the unencrypted OpenLDAP connection test.");

        var username = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_USERNAME") ?? "cn=admin,dc=example,dc=org";
        var password = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_PASSWORD") ?? "adminpassword";

        Assert.That(
            () => OpenConnection(host!, int.Parse(portValue!), useSecureConnection: false, username, password),
            Throws.Nothing);
    }

    [Test]
    public void OpenImportConnection_OverAnUnencryptedConnectionToSambaAd_Connects()
    {
        var host = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_SAMBA_HOST");
        var portValue = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_SAMBA_PLAIN_PORT");
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(portValue))
            Assert.Ignore("JIM_TEST_LDAPS_SAMBA_HOST/PLAIN_PORT not set; skipping the unencrypted Samba AD connection test. See test/scripts/Start-LdapsCertificateTestServers.ps1 -IncludeSambaAd.");

        var (username, password) = SambaCredentials();

        // Samba AD refuses simple binds over unencrypted LDAP by default ("ldap server require strong auth"); the
        // fixture script turns that off so this connects rather than being refused as a protocol error.
        Assert.That(
            () => OpenConnection(host!, int.Parse(portValue!), useSecureConnection: false, username, password),
            Throws.Nothing);
    }

    [Test]
    public void OpenImportConnection_OverLdapsToSambaAdWithIssuingCertificateInTheJimStore_Connects()
    {
        var (host, port, caCertificatePath) = SambaLdapsCoordinates();
        if (host is null)
            Assert.Ignore("JIM_TEST_LDAPS_SAMBA_HOST/PORT/CA_PATH not set; skipping the Samba AD LDAPS connection test. See test/scripts/Start-LdapsCertificateTestServers.ps1 -IncludeSambaAd.");

        var (username, password) = SambaCredentials();

        Assert.That(
            () => OpenConnection(host!, port, useSecureConnection: true, username, password, caCertificatePath!),
            Throws.Nothing);
    }

    [Test]
    public void OpenImportConnection_OverLdapsToSambaAdWithAnEmptyJimStore_IsRejected()
    {
        var (host, port, caCertificatePath) = SambaLdapsCoordinates();
        if (host is null)
            Assert.Ignore("JIM_TEST_LDAPS_SAMBA_HOST/PORT/CA_PATH not set; skipping the Samba AD untrusted issuer test. See test/scripts/Start-LdapsCertificateTestServers.ps1 -IncludeSambaAd.");

        var (username, password) = SambaCredentials();

        // Nothing added to the OS trust store for the Samba AD certificate, and none of Samba AD's own certificate
        // is supplied here either, so this must not connect.
        Assert.That(
            () => OpenConnection(host!, port, useSecureConnection: true, username, password),
            Throws.TypeOf<ServerCertificateRejectedException>()
                .With.Property(nameof(ServerCertificateRejectedException.Diagnostic))
                .Property(nameof(ServerCertificateDiagnostic.FailureReason))
                .EqualTo(ServerCertificateFailureReason.UntrustedIssuer));
    }

    [Test]
    public void OpenImportConnection_WhenTheSambaAdCertificateNameDoesNotMatch_IsRejected()
    {
        var (_, port, caCertificatePath) = SambaLdapsCoordinates();
        var mismatchedHost = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_SAMBA_MISMATCH_HOST");
        if (caCertificatePath is null || string.IsNullOrEmpty(mismatchedHost))
            Assert.Ignore("JIM_TEST_LDAPS_SAMBA_HOST/PORT/CA_PATH/MISMATCH_HOST not set; skipping the Samba AD name mismatch test. See test/scripts/Start-LdapsCertificateTestServers.ps1 -IncludeSambaAd.");

        var (username, password) = SambaCredentials();

        // Same server, reached by a name the certificate was not issued for. Trusting the issuer must not amount to
        // trusting any name it ever signs.
        Assert.That(
            () => OpenConnection(mismatchedHost!, port, useSecureConnection: true, username, password, caCertificatePath!),
            Throws.TypeOf<ServerCertificateRejectedException>()
                .With.Property(nameof(ServerCertificateRejectedException.Diagnostic))
                .Property(nameof(ServerCertificateDiagnostic.FailureReason))
                .EqualTo(ServerCertificateFailureReason.NameMismatch));
    }

    private static (string Username, string Password) SambaCredentials()
    {
        var username = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_SAMBA_USERNAME") ?? "CN=Administrator,CN=Users,DC=ldapstest,DC=local";
        var password = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_SAMBA_PASSWORD") ?? string.Empty;
        return (username, password);
    }

    /// <summary>
    /// Reads the Samba AD LDAPS host, port and CA certificate path, all of which the four LDAPS-specific tests
    /// above need. Returns a null host when any of the three are missing or the CA file does not exist, which each
    /// caller treats as "ignore this test".
    /// </summary>
    private static (string? Host, int Port, string? CaCertificatePath) SambaLdapsCoordinates()
    {
        var host = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_SAMBA_HOST");
        var portValue = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_SAMBA_PORT");
        var caCertificatePath = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_SAMBA_CA_PATH");

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(portValue) || string.IsNullOrEmpty(caCertificatePath) || !File.Exists(caCertificatePath))
            return (null, 0, null);

        return (host, int.Parse(portValue), caCertificatePath);
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
