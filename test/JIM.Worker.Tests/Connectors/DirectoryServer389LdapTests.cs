// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Connectors;
using JIM.Models.Exceptions;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Extends the LDAPS certificate validation coverage (#1132, #1141) to the 389 Directory Server directory type
/// (#1744), alongside the LDAPS-over-OpenLDAP coverage in <see cref="LdapsCertificateValidationTests"/> and the
/// Samba AD coverage in <see cref="SambaAdAndUnencryptedLdapTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stand the servers up with <c>test/scripts/Start-LdapsCertificateTestServers.ps1 -Include389</c>, which prints
/// the environment variables read below. As with the Samba AD fixture, there is no single mandatory environment
/// variable that gates the whole fixture: each test ignores itself independently when its own variables are unset,
/// so this fixture still runs to completion (all self-ignored) for someone who did not pass <c>-Include389</c>.
/// </para>
/// <para>
/// The two 389 Directory Server containers present the same certificates as the OpenLDAP JIM-store-only and expired
/// servers (issued by a test CA that neither the operating system nor JIM trusts by default), so the trusted-issuer,
/// untrusted-issuer, name-mismatch and expired-certificate cases all exercise the JIM certificate store rather than
/// the host's anchors, against a directory server whose TLS stack (NSS) differs from OpenLDAP's.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresLdaps")]
public class DirectoryServer389LdapTests
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

    [Test]
    public void OpenImportConnection_OverAnUnencryptedConnectionToDirectoryServer389_Connects()
    {
        var host = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_389_HOST");
        var portValue = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_389_PLAIN_PORT");
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(portValue))
            Assert.Ignore("JIM_TEST_LDAPS_389_HOST/PLAIN_PORT not set; skipping the unencrypted 389 Directory Server connection test. See test/scripts/Start-LdapsCertificateTestServers.ps1 -Include389.");

        var (username, password) = DirectoryServer389Credentials();

        // Same server as the LDAPS tests below, over its plain LDAP port rather than LDAPS, to prove the connector
        // still works against 389 Directory Server with "Use Secure Connection" off.
        Assert.That(
            () => LdapsTestConnections.OpenConnection(host!, int.Parse(portValue!), useSecureConnection: false, username, password, _logger),
            Throws.Nothing);
    }

    [Test]
    public void OpenImportConnection_OverLdapsToDirectoryServer389WithIssuingCertificateInTheJimStore_Connects()
    {
        var (host, port, caCertificatePath) = DirectoryServer389LdapsCoordinates();
        if (host is null)
            Assert.Ignore("JIM_TEST_LDAPS_389_HOST/PORT/CA_PATH not set; skipping the 389 Directory Server LDAPS connection test. See test/scripts/Start-LdapsCertificateTestServers.ps1 -Include389.");

        var (username, password) = DirectoryServer389Credentials();

        Assert.That(
            () => LdapsTestConnections.OpenConnection(host!, port, useSecureConnection: true, username, password, _logger, caCertificatePath!),
            Throws.Nothing);
    }

    [Test]
    public void OpenImportConnection_OverLdapsToDirectoryServer389WithAnEmptyJimStore_IsRejected()
    {
        var (host, port, caCertificatePath) = DirectoryServer389LdapsCoordinates();
        if (host is null)
            Assert.Ignore("JIM_TEST_LDAPS_389_HOST/PORT/CA_PATH not set; skipping the 389 Directory Server untrusted issuer test. See test/scripts/Start-LdapsCertificateTestServers.ps1 -Include389.");

        var (username, password) = DirectoryServer389Credentials();

        // The issuing test CA is trusted by neither the operating system nor JIM, and nothing is supplied here
        // either, so this must not connect. If it does, validation is not happening at all.
        Assert.That(
            () => LdapsTestConnections.OpenConnection(host!, port, useSecureConnection: true, username, password, _logger),
            Throws.TypeOf<ServerCertificateRejectedException>()
                .With.Property(nameof(ServerCertificateRejectedException.Diagnostic))
                .Property(nameof(ServerCertificateDiagnostic.FailureReason))
                .EqualTo(ServerCertificateFailureReason.UntrustedIssuer));
    }

    [Test]
    public void OpenImportConnection_WhenTheDirectoryServer389CertificateNameDoesNotMatch_IsRejected()
    {
        var (_, port, caCertificatePath) = DirectoryServer389LdapsCoordinates();
        var mismatchedHost = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_389_MISMATCH_HOST");
        if (caCertificatePath is null || string.IsNullOrEmpty(mismatchedHost))
            Assert.Ignore("JIM_TEST_LDAPS_389_HOST/PORT/CA_PATH/MISMATCH_HOST not set; skipping the 389 Directory Server name mismatch test. See test/scripts/Start-LdapsCertificateTestServers.ps1 -Include389.");

        var (username, password) = DirectoryServer389Credentials();

        // Same server, same trusted issuer, reached by a name the certificate was not issued for. Trusting the issuer
        // must not amount to trusting any name it ever signs.
        Assert.That(
            () => LdapsTestConnections.OpenConnection(mismatchedHost!, port, useSecureConnection: true, username, password, _logger, caCertificatePath!),
            Throws.TypeOf<ServerCertificateRejectedException>()
                .With.Property(nameof(ServerCertificateRejectedException.Diagnostic))
                .Property(nameof(ServerCertificateDiagnostic.FailureReason))
                .EqualTo(ServerCertificateFailureReason.NameMismatch));
    }

    [Test]
    public void OpenImportConnection_WhenTheDirectoryServer389CertificateHasExpired_IsRejected()
    {
        var (_, _, caCertificatePath) = DirectoryServer389LdapsCoordinates();
        var expiredHost = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_389_EXPIRED_HOST");
        var expiredPort = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_389_EXPIRED_PORT");
        if (caCertificatePath is null || string.IsNullOrEmpty(expiredHost) || string.IsNullOrEmpty(expiredPort))
            Assert.Ignore("JIM_TEST_LDAPS_389_HOST/PORT/CA_PATH/EXPIRED_HOST/EXPIRED_PORT not set; skipping the 389 Directory Server expired certificate test. See test/scripts/Start-LdapsCertificateTestServers.ps1 -Include389.");

        var (username, password) = DirectoryServer389Credentials();

        // A second 389 Directory Server presenting an expired certificate from the same test CA. The issuer is in the
        // JIM certificate store, which vouches for who signed the certificate, not for how long ago it stopped being
        // valid.
        Assert.That(
            () => LdapsTestConnections.OpenConnection(expiredHost!, int.Parse(expiredPort!), useSecureConnection: true, username, password, _logger, caCertificatePath!),
            Throws.TypeOf<ServerCertificateRejectedException>()
                .With.Property(nameof(ServerCertificateRejectedException.Diagnostic))
                .Property(nameof(ServerCertificateDiagnostic.FailureReason))
                .EqualTo(ServerCertificateFailureReason.Expired));
    }

    private static (string Username, string Password) DirectoryServer389Credentials()
    {
        var username = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_389_USERNAME") ?? "cn=Directory Manager";
        var password = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_389_PASSWORD") ?? string.Empty;
        return (username, password);
    }

    /// <summary>
    /// Reads the 389 Directory Server LDAPS host, port and CA certificate path, all of which the four LDAPS-specific
    /// tests above need. Returns a null host when any of the three are missing or the CA file does not exist, which
    /// each caller treats as "ignore this test".
    /// </summary>
    private static (string? Host, int Port, string? CaCertificatePath) DirectoryServer389LdapsCoordinates()
    {
        var host = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_389_HOST");
        var portValue = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_389_PORT");
        var caCertificatePath = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_389_CA_PATH");

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(portValue) || string.IsNullOrEmpty(caCertificatePath) || !File.Exists(caCertificatePath))
            return (null, 0, null);

        return (host, int.Parse(portValue), caCertificatePath);
    }
}
