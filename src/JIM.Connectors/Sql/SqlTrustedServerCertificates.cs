// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Serilog;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace JIM.Connectors.Sql;

/// <summary>
/// Short-lived certificates on disk, for the drivers that will only take a trust anchor as a path.
/// </summary>
/// <remarks>
/// <para>
/// Neither priority 1 driver offers a validation callback. <c>Microsoft.Data.SqlClient</c>'s only way to accept a
/// certificate the operating system's bundle does not vouch for is its <c>ServerCertificate</c> setting, which names a
/// file and accepts the server's certificate when it matches that file exactly (<see cref="CreateFile"/>). ODP.NET's is
/// an Oracle wallet: a folder named by the connection's <c>WalletLocation</c>, which it reads as a PEM wallet
/// (<c>ewallet.pem</c>) with no password and no Oracle tooling (<see cref="CreateWallet"/>). It builds the chain from
/// the wallet alone, so the wallet holds the root and the intermediates beneath it.
/// </para>
/// <para>
/// Trust stays strictly additive. Nothing is written until an ordinary connection has been refused, and then only what
/// JIM's own certificate store vouches for, so it can never widen what is accepted beyond what an administrator added in
/// Admin &gt; Certificates. Nothing secret is written: certificates are public by definition.
/// </para>
/// </remarks>
internal sealed class SqlTrustedServerCertificates : IDisposable
{
    /// <summary>
    /// Prefix every file and wallet folder carries, so abandoned ones can be recognised and swept.
    /// </summary>
    private const string NamePrefix = "jim-sql-trust-";

    /// <summary>
    /// The file name ODP.NET looks for in a PEM wallet folder.
    /// </summary>
    internal const string PemWalletFileName = "ewallet.pem";

    /// <summary>
    /// How old an abandoned file or folder must be before the sweep removes it. Comfortably longer than any single
    /// run, so trust still in use by a long-running import is never taken out from under it.
    /// </summary>
    private static readonly TimeSpan AbandonedAge = TimeSpan.FromDays(7);

    private readonly bool _isWallet;
    private bool _disposed;

    /// <summary>
    /// Absolute path for the provider: the certificate file, or the wallet folder.
    /// </summary>
    internal string Path { get; }

    private SqlTrustedServerCertificates(string path, bool isWallet)
    {
        Path = path;
        _isWallet = isWallet;
    }

    /// <summary>
    /// Writes one certificate out in PEM form, for a driver that matches the server's certificate exactly.
    /// </summary>
    /// <param name="derEncodedCertificate">The certificate as the server presented it.</param>
    /// <param name="logger">Logger for the calling operation.</param>
    internal static SqlTrustedServerCertificates CreateFile(byte[] derEncodedCertificate, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(derEncodedCertificate);

        RemoveAbandoned(logger);

        using var certificate = X509CertificateLoader.LoadCertificate(derEncodedCertificate);
        var trusted = new SqlTrustedServerCertificates(System.IO.Path.Join(System.IO.Path.GetTempPath(), $"{NamePrefix}{Guid.NewGuid():N}.pem"), isWallet: false);

        try
        {
            WriteOwnerOnly(trusted.Path, certificate.ExportCertificatePem());
            logger.Debug("SqlTrustedServerCertificates: prepared the server certificate with thumbprint {Thumbprint} as an additional trust anchor for this Connected System", certificate.Thumbprint);
            return trusted;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            trusted.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes a PEM wallet holding the given certificates, in a folder of its own, for a driver that builds the chain
    /// from its wallet.
    /// </summary>
    /// <param name="derEncodedCertificates">The root the store trusts and the intermediates beneath it.</param>
    /// <param name="logger">Logger for the calling operation.</param>
    internal static SqlTrustedServerCertificates CreateWallet(IReadOnlyList<byte[]> derEncodedCertificates, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(derEncodedCertificates);
        if (derEncodedCertificates.Count == 0)
            throw new ArgumentException("A wallet needs at least one certificate.", nameof(derEncodedCertificates));

        RemoveAbandoned(logger);

        var pem = new StringBuilder();
        var thumbprints = new List<string>();
        foreach (var der in derEncodedCertificates)
        {
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            pem.AppendLine(certificate.ExportCertificatePem());
            thumbprints.Add(certificate.Thumbprint);
        }

        // A folder of its own per wallet: the driver takes the folder, not the file, and two Connected Systems must
        // never share, or overwrite, one another's trust.
        var trusted = new SqlTrustedServerCertificates(System.IO.Path.Join(System.IO.Path.GetTempPath(), $"{NamePrefix}{Guid.NewGuid():N}"), isWallet: true);

        try
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(trusted.Path);
            else
                Directory.CreateDirectory(trusted.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            WriteOwnerOnly(System.IO.Path.Join(trusted.Path, PemWalletFileName), pem.ToString());
            logger.Debug("SqlTrustedServerCertificates: prepared a wallet of {Count} certificate(s) ({Thumbprints}) as additional trust anchors for this Connected System",
                thumbprints.Count, string.Join(", ", thumbprints));
            return trusted;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            trusted.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Restricted to the account JIM runs as: anything able to write here could decide what a connection accepts.
    /// </summary>
    private static void WriteOwnerOnly(string path, string contents)
    {
        System.IO.File.WriteAllText(path, contents);

        if (!OperatingSystem.IsWindows())
            System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// Removes files and wallet folders left behind by a process that was killed before it could clean up its own.
    /// </summary>
    /// <remarks>
    /// Each is removed when its Connector is disposed, but a killed process never reaches that, and a container that
    /// is restarted rather than replaced keeps its temporary files. Failures are logged and ignored: this is
    /// housekeeping, and must never stop a connection being established.
    /// </remarks>
    private static void RemoveAbandoned(ILogger logger)
    {
        try
        {
            var cutoff = DateTime.UtcNow - AbandonedAge;
            var temporary = System.IO.Path.GetTempPath();

            var files = Directory.EnumerateFiles(temporary, $"{NamePrefix}*")
                .Where(file => System.IO.File.GetLastWriteTimeUtc(file) < cutoff)
                .ToList();
            foreach (var file in files)
                System.IO.File.Delete(file);

            var wallets = Directory.EnumerateDirectories(temporary, $"{NamePrefix}*")
                .Where(folder => Directory.GetLastWriteTimeUtc(folder) < cutoff)
                .ToList();
            foreach (var wallet in wallets)
                Directory.Delete(wallet, recursive: true);

            if (files.Count + wallets.Count > 0)
                logger.Debug("SqlTrustedServerCertificates: removed {Count} abandoned trust file(s) and wallet(s) left by an earlier process", files.Count + wallets.Count);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            logger.Debug(ex, "SqlTrustedServerCertificates: could not sweep abandoned trust files");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            if (_isWallet)
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            else if (System.IO.File.Exists(Path))
            {
                System.IO.File.Delete(Path);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            // Public certificates only, and the containers JIM runs in have ephemeral filesystems, so a failed clean-up
            // must not take a synchronisation run down with it.
            Log.Warning(ex, "SqlTrustedServerCertificates: could not remove the temporary trust file or wallet. It holds public certificates only and will go when the container is replaced");
        }
    }
}
