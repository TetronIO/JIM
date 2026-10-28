// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.TestSupport;
using NUnit.Framework;
using Serilog;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The certificates JIM hands a SQL driver as additional trust anchors: a file for Microsoft SQL Server, and a wallet
/// folder for Oracle Database (#2581). Both decide what a connection accepts, so who can read and replace them, and
/// whether they outlive the Connector, matter as much as what they hold.
/// </summary>
[TestFixture]
public class SqlTrustedServerCertificatesTests
{
    private ILogger _logger = null!;
    private X509Certificate2 _root = null!;
    private X509Certificate2 _intermediate = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
        _root = TestPki.CreateRoot("Corp Root CA");
        _intermediate = TestPki.CreateIntermediate("Corp Issuing CA 2", _root);
    }

    [TearDown]
    public void TearDown()
    {
        _intermediate.Dispose();
        _root.Dispose();
        (_logger as IDisposable)?.Dispose();
    }

    [Test]
    public void CreateWallet_WritesEveryCertificateToAPemWalletInAFolderOfItsOwn()
    {
        using var wallet = SqlTrustedServerCertificates.CreateWallet([_root.RawData, _intermediate.RawData], _logger);

        var walletFile = Path.Combine(wallet.Path, "ewallet.pem");
        Assert.That(File.Exists(walletFile), Is.True, "ODP.NET reads ewallet.pem from the folder WalletLocation names.");

        var written = new X509Certificate2Collection();
        written.ImportFromPemFile(walletFile);
        Assert.That(written.Select(certificate => certificate.Thumbprint), Is.EqualTo(new[] { _root.Thumbprint, _intermediate.Thumbprint }));
    }

    [Test]
    public void CreateWallet_EachCall_GetsAFolderOfItsOwn()
    {
        using var first = SqlTrustedServerCertificates.CreateWallet([_root.RawData], _logger);
        using var second = SqlTrustedServerCertificates.CreateWallet([_root.RawData], _logger);

        Assert.That(second.Path, Is.Not.EqualTo(first.Path), "Two Connected Systems must never share, or overwrite, one another's trust.");
    }

    [Test]
    public void CreateWallet_OnUnix_IsReadableAndWritableByJimAlone()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            using var wallet = SqlTrustedServerCertificates.CreateWallet([_root.RawData], _logger);

            using (Assert.EnterMultipleScope())
            {
                // Anything able to write here could decide which certificates a connection accepts.
                Assert.That(File.GetUnixFileMode(wallet.Path), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
                Assert.That(File.GetUnixFileMode(Path.Combine(wallet.Path, "ewallet.pem")), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
            }
        }
        else
        {
            Assert.Ignore("Unix file mode is only meaningful on Unix-like platforms.");
        }
    }

    [Test]
    public void Dispose_RemovesTheWalletFolder()
    {
        var wallet = SqlTrustedServerCertificates.CreateWallet([_root.RawData], _logger);
        var path = wallet.Path;

        wallet.Dispose();

        Assert.That(Directory.Exists(path), Is.False);
    }

    [Test]
    public void CreateFile_WritesTheOneCertificateAndDisposeRemovesIt()
    {
        var file = SqlTrustedServerCertificates.CreateFile(_root.RawData, _logger);
        var path = file.Path;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(path), Is.True);
            Assert.That(X509CertificateLoader.LoadCertificateFromFile(path).Thumbprint, Is.EqualTo(_root.Thumbprint));
        }

        file.Dispose();

        Assert.That(File.Exists(path), Is.False);
    }

    [Test]
    public void CreateWallet_WithAWalletAbandonedByAKilledProcess_SweepsItAway()
    {
        // A killed process never disposes its Connector, and a restarted container keeps its temporary folder.
        var abandoned = Path.Join(Path.GetTempPath(), $"jim-sql-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(abandoned);
        File.WriteAllText(Path.Combine(abandoned, "ewallet.pem"), string.Empty);
        Directory.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddDays(-8));

        using var wallet = SqlTrustedServerCertificates.CreateWallet([_root.RawData], _logger);

        Assert.That(Directory.Exists(abandoned), Is.False);
    }

    [Test]
    public void CreateWallet_WithAnotherConnectorsRecentWallet_LeavesItAlone()
    {
        var recent = Path.Join(Path.GetTempPath(), $"jim-sql-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(recent);

        try
        {
            using var wallet = SqlTrustedServerCertificates.CreateWallet([_root.RawData], _logger);

            Assert.That(Directory.Exists(recent), Is.True, "A long import may still be using it.");
        }
        finally
        {
            Directory.Delete(recent, recursive: true);
        }
    }
}
