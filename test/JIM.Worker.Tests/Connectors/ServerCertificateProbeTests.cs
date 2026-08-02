// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors;
using JIM.Models.Connectors;
using Serilog;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Verifies that the certificate a directory server presents is reported accurately when a connection to it is
/// refused (#1132), against real servers presenting real certificates.
/// </summary>
/// <remarks>
/// Opt-in, like <see cref="LdapsCertificateValidationTests"/>; stand the servers up with
/// <c>test/scripts/Start-LdapsCertificateTestServers.ps1</c>. Ignored when <c>JIM_TEST_LDAPS_HOST</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresLdaps")]
public class ServerCertificateProbeTests
{
    private string _host = null!;
    private int _port;
    private string _caCertificatePath = null!;
    private Serilog.Core.Logger _logger = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _host = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_HOST") ?? string.Empty;
        if (string.IsNullOrEmpty(_host))
            Assert.Ignore("JIM_TEST_LDAPS_HOST not set; skipping certificate probe tests. See test/scripts/Start-LdapsCertificateTestServers.ps1.");

        _port = int.Parse(Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_PORT") ?? "636");
        _caCertificatePath = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_CA_PATH") ?? string.Empty;

        if (string.IsNullOrEmpty(_caCertificatePath) || !File.Exists(_caCertificatePath))
            Assert.Ignore("JIM_TEST_LDAPS_CA_PATH is not set or does not exist; skipping certificate probe tests.");
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

    private List<X509Certificate2> TrustedCertificates()
    {
        return [X509CertificateLoader.LoadCertificateFromFile(_caCertificatePath)];
    }

    private ServerCertificateDiagnostic? Probe(string host, int port, IReadOnlyCollection<X509Certificate2>? trusted = null)
    {
        var certificates = trusted ?? [];

        try
        {
            return ServerCertificateProbe.Probe(host, port, certificates, TimeSpan.FromSeconds(10), _logger);
        }
        finally
        {
            foreach (var certificate in certificates)
                certificate.Dispose();
        }
    }

    [Test]
    public void Probe_WithAnIssuerNobodyTrusts_ReportsAnUntrustedIssuer()
    {
        var diagnostic = Probe(_host, _port);

        Assert.That(diagnostic, Is.Not.Null);
        Assert.That(diagnostic!.FailureReason, Is.EqualTo(ServerCertificateFailureReason.UntrustedIssuer));
    }

    [Test]
    public void Probe_ReturnsTheCertificateDetailsAnAdministratorNeedsToIdentifyIt()
    {
        var diagnostic = Probe(_host, _port);

        Assert.That(diagnostic, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic!.Subject, Does.Contain(_host));
            Assert.That(diagnostic!.Issuer, Is.Not.Null.And.Not.Empty);
            Assert.That(diagnostic!.Thumbprint, Is.Not.Null.And.Not.Empty);
            Assert.That(diagnostic!.SubjectAlternativeNames, Does.Contain(_host));
            Assert.That(diagnostic!.ValidFrom, Is.Not.Null);
            Assert.That(diagnostic!.ValidTo, Is.Not.Null);
            Assert.That(diagnostic!.Remediation, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public void Probe_WhenTheCertificateWasIssuedForAnotherName_ReportsANameMismatch()
    {
        var mismatchedHost = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_MISMATCH_HOST");
        if (string.IsNullOrEmpty(mismatchedHost))
            Assert.Ignore("JIM_TEST_LDAPS_MISMATCH_HOST not set; skipping the name mismatch test.");

        var diagnostic = Probe(mismatchedHost!, _port, TrustedCertificates());

        Assert.That(diagnostic, Is.Not.Null);
        Assert.Multiple(() =>
        {
            // Reported ahead of the issuer, because trusting the issuer is not what fixes this.
            Assert.That(diagnostic!.FailureReason, Is.EqualTo(ServerCertificateFailureReason.NameMismatch));
            Assert.That(diagnostic!.Remediation, Does.Contain(mismatchedHost!));
        });
    }

    [Test]
    public void Probe_WhenTheCertificateHasExpired_ReportsItAsExpired()
    {
        var expiredHost = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_EXPIRED_HOST");
        var expiredPort = Environment.GetEnvironmentVariable("JIM_TEST_LDAPS_EXPIRED_PORT");
        if (string.IsNullOrEmpty(expiredHost) || string.IsNullOrEmpty(expiredPort))
            Assert.Ignore("JIM_TEST_LDAPS_EXPIRED_HOST/PORT not set; skipping the expired certificate test.");

        var diagnostic = Probe(expiredHost!, int.Parse(expiredPort!), TrustedCertificates());

        Assert.That(diagnostic, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic!.FailureReason, Is.EqualTo(ServerCertificateFailureReason.Expired));
            Assert.That(diagnostic!.IsExpired, Is.True);
        });
    }

    [Test]
    public void Probe_WhenTheServerSendsItsIssuer_ReportsTheIssuerAsSomethingThatCanBeTrusted()
    {
        var diagnostic = Probe(_host, _port);

        Assert.That(diagnostic, Is.Not.Null);
        Assert.Multiple(() =>
        {
            // The test servers are issued by a certificate authority they send alongside their own certificate,
            // which is what lets an administrator trust the authority instead of repeating this at every renewal.
            Assert.That(diagnostic!.IsSelfSigned, Is.False);
            Assert.That(diagnostic!.IsIssuerCertificateAvailable, Is.True);
            Assert.That(diagnostic!.IssuerThumbprint, Is.Not.EqualTo(diagnostic!.Thumbprint));
        });
    }

    [Test]
    public void Read_ReturnsTheCertificatesThemselvesSoTheyCanBeTrusted()
    {
        var reading = ServerCertificateProbe.Read(_host, _port, [], TimeSpan.FromSeconds(10), _logger);

        Assert.That(reading, Is.Not.Null);
        Assert.That(reading!.Chain, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(reading!.Chain!.Leaf.Thumbprint, Is.EqualTo(reading!.Diagnostic.Thumbprint));
            Assert.That(reading!.Chain!.Leaf.Data, Is.Not.Empty);
            Assert.That(reading!.Chain!.Issuer, Is.Not.Null);
            Assert.That(reading!.Chain!.Issuer!.Data, Is.Not.Empty);
            Assert.That(reading!.Chain!.Issuer!.Thumbprint, Is.EqualTo(reading!.Diagnostic.IssuerThumbprint));
        });
    }

    [Test]
    public void Probe_WithTheJimCertificateStoreSupplied_FindsNothingWrong()
    {
        var diagnostic = Probe(_host, _port, TrustedCertificates());

        Assert.That(diagnostic, Is.Not.Null);
        Assert.That(diagnostic!.FailureReason, Is.EqualTo(ServerCertificateFailureReason.None));
    }

    [Test]
    public void Probe_WhenTheServerCannotBeReached_ReturnsNothingRatherThanBlamingTheCertificate()
    {
        // Nothing listening: a connectivity problem, which must not be reported as a certificate one.
        var diagnostic = Probe(_host, 65014, TrustedCertificates());

        Assert.That(diagnostic, Is.Null);
    }
}
