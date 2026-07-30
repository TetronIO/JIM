// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using Serilog;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers the on-disk trust directory the LDAP Connector hands to the platform LDAP client so that
/// certificates held in the JIM certificate store are honoured for LDAPS connections.
/// </summary>
[TestFixture]
public class LdapTrustedCertificateDirectoryTests
{
    private Serilog.Core.Logger _logger = null!;
    private List<X509Certificate2> _certificates = null!;
    private string _scratchDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
        _certificates = new List<X509Certificate2>();
        _scratchDirectory = Path.Combine(Path.GetTempPath(), $"jim-trust-dir-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var certificate in _certificates)
            certificate.Dispose();

        if (Directory.Exists(_scratchDirectory))
            Directory.Delete(_scratchDirectory, true);

        _logger.Dispose();
    }

    /// <summary>
    /// Builds a throw-away self-signed certificate. Only the encoding matters here; these are never presented to a server.
    /// </summary>
    private X509Certificate2 CreateCertificate(string commonName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        _certificates.Add(certificate);
        return certificate;
    }

    private string CreateFakeSystemBundle(string content = "-- fake system bundle --")
    {
        var path = Path.Combine(_scratchDirectory, "ca-certificates.crt");
        File.WriteAllText(path, content);
        return path;
    }

    [Test]
    public void Create_WritesOneFilePerCertificate()
    {
        var first = CreateCertificate("first.example.test");
        var second = CreateCertificate("second.example.test");

        using var trustDirectory = LdapTrustedCertificateDirectory.Create(new[] { first, second }, _logger, new[] { CreateFakeSystemBundle() });

        var certificateFiles = Directory.GetFiles(trustDirectory.DirectoryPath, "*.crt")
            .Where(f => Path.GetFileName(f) != LdapTrustedCertificateDirectory.SystemBundleFileName)
            .ToList();

        Assert.That(certificateFiles, Has.Count.EqualTo(2));
    }

    [Test]
    public void Create_WritesCertificatesInPemFormatThatRoundTrips()
    {
        var certificate = CreateCertificate("roundtrip.example.test");

        using var trustDirectory = LdapTrustedCertificateDirectory.Create(new[] { certificate }, _logger, new[] { CreateFakeSystemBundle() });

        var certificateFile = Directory.GetFiles(trustDirectory.DirectoryPath, "*.crt")
            .Single(f => Path.GetFileName(f) != LdapTrustedCertificateDirectory.SystemBundleFileName);
        using var readBack = X509CertificateLoader.LoadCertificateFromFile(certificateFile);

        Assert.That(readBack.Thumbprint, Is.EqualTo(certificate.Thumbprint));
    }

    [Test]
    public void Create_NamesCertificateFilesByThumbprint()
    {
        var certificate = CreateCertificate("named.example.test");

        using var trustDirectory = LdapTrustedCertificateDirectory.Create(new[] { certificate }, _logger, new[] { CreateFakeSystemBundle() });

        var expectedPath = Path.Combine(trustDirectory.DirectoryPath, $"{certificate.Thumbprint}.crt");
        Assert.That(File.Exists(expectedPath), Is.True);
    }

    [Test]
    public void Create_DeduplicatesCertificatesWithTheSameThumbprint()
    {
        var certificate = CreateCertificate("duplicate.example.test");
        var duplicate = X509CertificateLoader.LoadCertificate(certificate.RawData);
        _certificates.Add(duplicate);

        using var trustDirectory = LdapTrustedCertificateDirectory.Create(new[] { certificate, duplicate }, _logger, new[] { CreateFakeSystemBundle() });

        var certificateFiles = Directory.GetFiles(trustDirectory.DirectoryPath, "*.crt")
            .Where(f => Path.GetFileName(f) != LdapTrustedCertificateDirectory.SystemBundleFileName)
            .ToList();

        Assert.That(certificateFiles, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// The platform LDAP client replaces its configured trust anchors when handed a trust directory, rather than
    /// supplementing them, so the system bundle has to be copied in alongside the JIM certificates. Without this,
    /// adding a certificate to the JIM store would stop every publicly-issued directory certificate validating.
    /// </summary>
    [Test]
    public void Create_CopiesTheSystemCertificateBundleAlongsideJimCertificates()
    {
        const string bundleContent = "-- system bundle marker --";
        var certificate = CreateCertificate("additive.example.test");

        using var trustDirectory = LdapTrustedCertificateDirectory.Create(new[] { certificate }, _logger, new[] { CreateFakeSystemBundle(bundleContent) });

        var bundlePath = Path.Combine(trustDirectory.DirectoryPath, LdapTrustedCertificateDirectory.SystemBundleFileName);
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(bundlePath), Is.True);
            Assert.That(File.ReadAllText(bundlePath), Is.EqualTo(bundleContent));
        });
    }

    [Test]
    public void Create_UsesTheFirstSystemBundleThatExists()
    {
        const string bundleContent = "-- second candidate --";
        var bundlePath = CreateFakeSystemBundle(bundleContent);
        var certificate = CreateCertificate("candidates.example.test");

        using var trustDirectory = LdapTrustedCertificateDirectory.Create(
            new[] { certificate },
            _logger,
            new[] { Path.Combine(_scratchDirectory, "does-not-exist.crt"), bundlePath });

        var copiedBundle = Path.Combine(trustDirectory.DirectoryPath, LdapTrustedCertificateDirectory.SystemBundleFileName);
        Assert.That(File.ReadAllText(copiedBundle), Is.EqualTo(bundleContent));
    }

    [Test]
    public void Create_WithNoSystemBundleAvailable_StillTrustsJimCertificates()
    {
        var certificate = CreateCertificate("nobundle.example.test");

        using var trustDirectory = LdapTrustedCertificateDirectory.Create(
            new[] { certificate },
            _logger,
            new[] { Path.Combine(_scratchDirectory, "does-not-exist.crt") });

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Combine(trustDirectory.DirectoryPath, $"{certificate.Thumbprint}.crt")), Is.True);
            Assert.That(File.Exists(Path.Combine(trustDirectory.DirectoryPath, LdapTrustedCertificateDirectory.SystemBundleFileName)), Is.False);
        });
    }

    [Test]
    public void Create_WithNoCertificates_Throws()
    {
        Assert.That(
            () => LdapTrustedCertificateDirectory.Create(Array.Empty<X509Certificate2>(), _logger),
            Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void Dispose_DeletesTheDirectory()
    {
        var certificate = CreateCertificate("disposed.example.test");
        var trustDirectory = LdapTrustedCertificateDirectory.Create(new[] { certificate }, _logger, new[] { CreateFakeSystemBundle() });
        var path = trustDirectory.DirectoryPath;

        trustDirectory.Dispose();

        Assert.That(Directory.Exists(path), Is.False);
    }

    [Test]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var certificate = CreateCertificate("twice.example.test");
        var trustDirectory = LdapTrustedCertificateDirectory.Create(new[] { certificate }, _logger, new[] { CreateFakeSystemBundle() });

        trustDirectory.Dispose();

        Assert.That(() => trustDirectory.Dispose(), Throws.Nothing);
    }

    /// <summary>
    /// The directory holds trust anchors, so anything able to write to it could make JIM trust an attacker's issuer.
    /// </summary>
    [Test]
    public void Create_RestrictsDirectoryPermissionsToTheOwningUser()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var certificate = CreateCertificate("permissions.example.test");

            using var trustDirectory = LdapTrustedCertificateDirectory.Create(new[] { certificate }, _logger, new[] { CreateFakeSystemBundle() });

            var mode = File.GetUnixFileMode(trustDirectory.DirectoryPath);
            Assert.That(mode, Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
        }
        else
        {
            Assert.Ignore("Unix file mode is only meaningful on Unix-like platforms.");
        }
    }

    /// <summary>
    /// Every directory is removed when its connection closes, but a process killed mid-run never gets to do that, and
    /// a container that is restarted rather than replaced keeps its temporary files. Without a sweep, debris from
    /// those kills accumulates until someone deletes it by hand.
    /// </summary>
    [Test]
    public void Create_RemovesAbandonedTrustDirectoriesLeftByAKilledProcess()
    {
        var abandoned = Path.Combine(Path.GetTempPath(), $"jim-ldap-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(abandoned);
        File.WriteAllText(Path.Combine(abandoned, "debris.crt"), "-- left behind --");
        Directory.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddDays(-8));

        var certificate = CreateCertificate("sweep.example.test");
        using var trustDirectory = LdapTrustedCertificateDirectory.Create(new[] { certificate }, _logger, new[] { CreateFakeSystemBundle() });

        Assert.That(Directory.Exists(abandoned), Is.False);
    }

    /// <summary>
    /// A long-running import can hold its trust directory open for hours, so the sweep must only take directories old
    /// enough that no run could still be using them.
    /// </summary>
    [Test]
    public void Create_LeavesRecentTrustDirectoriesAlone()
    {
        var inUse = Path.Combine(Path.GetTempPath(), $"jim-ldap-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(inUse);
        Directory.SetLastWriteTimeUtc(inUse, DateTime.UtcNow.AddHours(-6));

        try
        {
            var certificate = CreateCertificate("recent.example.test");
            using var trustDirectory = LdapTrustedCertificateDirectory.Create(new[] { certificate }, _logger, new[] { CreateFakeSystemBundle() });

            Assert.That(Directory.Exists(inUse), Is.True);
        }
        finally
        {
            if (Directory.Exists(inUse))
                Directory.Delete(inUse, true);
        }
    }

    [Test]
    public void Create_ProducesADistinctDirectoryPerInstance()
    {
        var certificate = CreateCertificate("distinct.example.test");
        var bundle = CreateFakeSystemBundle();

        using var first = LdapTrustedCertificateDirectory.Create(new[] { certificate }, _logger, new[] { bundle });
        using var second = LdapTrustedCertificateDirectory.Create(new[] { certificate }, _logger, new[] { bundle });

        Assert.That(first.DirectoryPath, Is.Not.EqualTo(second.DirectoryPath));
    }
}
