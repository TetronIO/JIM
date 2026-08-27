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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(bundlePath), Is.True);
            Assert.That(File.ReadAllText(bundlePath), Is.EqualTo(bundleContent));
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(Path.Combine(trustDirectory.DirectoryPath, $"{certificate.Thumbprint}.crt")), Is.True);
            Assert.That(File.Exists(Path.Combine(trustDirectory.DirectoryPath, LdapTrustedCertificateDirectory.SystemBundleFileName)), Is.False);
        }
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

    /// <summary>
    /// OpenSSL-backed LDAP clients (Ubuntu 26.04 onwards) look trust anchors up by subject-hash file name and
    /// cannot see thumbprint-named files at all, while GnuTLS-backed clients (Debian and Ubuntu up to 24.04)
    /// read every file regardless of name. The directory must satisfy both, which openssl rehash provides.
    /// </summary>
    [Test]
    public void Create_WritesAnOpenSslSubjectHashEntryPerJimCertificate()
    {
        AssertOpenSslAvailable();
        var first = CreateCertificate("hash-one.example.test");
        var second = CreateCertificate("hash-two.example.test");

        using var trustDirectory = LdapTrustedCertificateDirectory.Create(new[] { first, second }, _logger, new[] { CreateFakeSystemBundle() });

        var hashEntries = GetSubjectHashEntries(trustDirectory.DirectoryPath);
        var thumbprints = hashEntries
            .Select(f => { using var c = X509CertificateLoader.LoadCertificateFromFile(f); return c.Thumbprint; })
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hashEntries, Has.Count.EqualTo(2));
            Assert.That(thumbprints, Is.EquivalentTo(new[] { first.Thumbprint, second.Thumbprint }));
        }
    }

    /// <summary>
    /// A bundle file holding many certificates is invisible to OpenSSL's by-hash lookup even when hash entries
    /// exist for the JIM certificates, so the operating system bundle must be split into one file per certificate
    /// and each given a hash entry, or a populated JIM store would stop system-trusted issuers validating on
    /// OpenSSL-backed platforms.
    /// </summary>
    [Test]
    public void Create_SplitsAPemSystemBundleIntoIndividuallyHashedCertificates()
    {
        AssertOpenSslAvailable();
        var jimCertificate = CreateCertificate("store.example.test");
        var bundleFirst = CreateCertificate("bundle-one.example.test");
        var bundleSecond = CreateCertificate("bundle-two.example.test");
        var bundlePath = CreateFakeSystemBundle(bundleFirst.ExportCertificatePem() + "\n" + bundleSecond.ExportCertificatePem());

        using var trustDirectory = LdapTrustedCertificateDirectory.Create(new[] { jimCertificate }, _logger, new[] { bundlePath });

        var hashEntries = GetSubjectHashEntries(trustDirectory.DirectoryPath);
        var thumbprints = hashEntries
            .Select(f => { using var c = X509CertificateLoader.LoadCertificateFromFile(f); return c.Thumbprint; })
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            // One hash entry per certificate: the JIM certificate plus each certificate from the bundle.
            Assert.That(thumbprints, Is.EquivalentTo(new[] { jimCertificate.Thumbprint, bundleFirst.Thumbprint, bundleSecond.Thumbprint }));
            // The split replaces the verbatim bundle copy, so nothing is loaded twice on GnuTLS platforms.
            Assert.That(File.Exists(Path.Combine(trustDirectory.DirectoryPath, LdapTrustedCertificateDirectory.SystemBundleFileName)), Is.False);
        }
    }

    /// <summary>
    /// A bundle that holds no PEM certificate blocks cannot be split, so the previous behaviour of copying it in
    /// verbatim is kept: GnuTLS-backed clients still read it, and nothing is lost on OpenSSL-backed ones.
    /// </summary>
    [Test]
    public void Create_CopiesANonPemSystemBundleVerbatim()
    {
        const string bundleContent = "-- not a PEM bundle --";
        var certificate = CreateCertificate("verbatim.example.test");

        using var trustDirectory = LdapTrustedCertificateDirectory.Create(new[] { certificate }, _logger, new[] { CreateFakeSystemBundle(bundleContent) });

        var bundlePath = Path.Combine(trustDirectory.DirectoryPath, LdapTrustedCertificateDirectory.SystemBundleFileName);
        Assert.That(File.ReadAllText(bundlePath), Is.EqualTo(bundleContent));
    }

    /// <summary>
    /// Finds the hash-named entries openssl rehash creates: 8 hex characters, a dot, then a collision counter.
    /// </summary>
    private static List<string> GetSubjectHashEntries(string directoryPath)
    {
        return Directory.GetFileSystemEntries(directoryPath)
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"^[0-9a-f]{8}\.\d+$"))
            .ToList();
    }

    private static void AssertOpenSslAvailable()
    {
        var found = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator)
            .Any(p => File.Exists(Path.Combine(p, "openssl"))) ?? false;
        if (!found)
            Assert.Ignore("The openssl binary is not on PATH; subject-hash entries cannot be verified on this host.");
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
