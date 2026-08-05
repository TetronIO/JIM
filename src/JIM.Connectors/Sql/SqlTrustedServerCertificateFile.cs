// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Serilog;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Connectors.Sql;

/// <summary>
/// A short-lived file holding one server certificate, for the drivers that will only take a trust
/// anchor as a path on disk.
/// </summary>
/// <remarks>
/// <para>
/// <c>Microsoft.Data.SqlClient</c> offers no validation callback: the only way to have it accept a
/// certificate the operating system's bundle does not vouch for is its <c>ServerCertificate</c>
/// connection setting, which names a file and accepts the server's certificate when it matches that
/// file exactly. This materialises the certificate for it.
/// </para>
/// <para>
/// Trust stays strictly additive. The file is only ever written after an ordinary connection was
/// refused, and only for a certificate JIM's own certificate store already vouches for, so it can never
/// widen what is accepted beyond what an administrator added in Admin &gt; Certificates. Nothing secret
/// is written: a server's certificate is public by definition.
/// </para>
/// </remarks>
internal sealed class SqlTrustedServerCertificateFile : IDisposable
{
    /// <summary>
    /// Prefix every file carries, so abandoned ones can be recognised and swept.
    /// </summary>
    private const string FileNamePrefix = "jim-sql-trust-";

    /// <summary>
    /// How old an abandoned file must be before the sweep removes it. Comfortably longer than any single
    /// run, so a file still in use by a long-running import is never taken out from under it.
    /// </summary>
    private static readonly TimeSpan AbandonedFileAge = TimeSpan.FromDays(7);

    private bool _disposed;

    /// <summary>
    /// Absolute path of the file, for the provider's connection string.
    /// </summary>
    internal string FilePath { get; }

    private SqlTrustedServerCertificateFile(string filePath)
    {
        FilePath = filePath;
    }

    /// <summary>
    /// Writes a certificate out in PEM form, which is one of the formats every supported driver reads.
    /// </summary>
    /// <param name="derEncodedCertificate">The certificate as the server presented it.</param>
    /// <param name="logger">Logger for the calling operation.</param>
    internal static SqlTrustedServerCertificateFile Create(byte[] derEncodedCertificate, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(derEncodedCertificate);

        RemoveAbandonedFiles(logger);

        using var certificate = X509CertificateLoader.LoadCertificate(derEncodedCertificate);
        var filePath = Path.Join(Path.GetTempPath(), $"{FileNamePrefix}{Guid.NewGuid():N}.pem");
        var trustedCertificateFile = new SqlTrustedServerCertificateFile(filePath);

        try
        {
            System.IO.File.WriteAllText(filePath, certificate.ExportCertificatePem());

            // Restricted to the account JIM runs as: anything able to write here could decide which
            // certificate a connection accepts.
            if (!OperatingSystem.IsWindows())
                System.IO.File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            logger.Debug("SqlTrustedServerCertificateFile: prepared the server certificate with thumbprint {Thumbprint} as an additional trust anchor for this Connected System", certificate.Thumbprint);
            return trustedCertificateFile;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            trustedCertificateFile.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Removes files left behind by a process that was killed before it could clean up its own.
    /// </summary>
    /// <remarks>
    /// Each file is removed when its Connector is disposed, but a killed process never reaches that, and
    /// a container that is restarted rather than replaced keeps its temporary files. Failures are logged
    /// and ignored: this is housekeeping, and must never stop a connection being established.
    /// </remarks>
    private static void RemoveAbandonedFiles(ILogger logger)
    {
        try
        {
            var cutoff = DateTime.UtcNow - AbandonedFileAge;
            var abandoned = Directory.EnumerateFiles(Path.GetTempPath(), $"{FileNamePrefix}*")
                .Where(file => System.IO.File.GetLastWriteTimeUtc(file) < cutoff)
                .ToList();

            foreach (var file in abandoned)
                System.IO.File.Delete(file);

            if (abandoned.Count > 0)
                logger.Debug("SqlTrustedServerCertificateFile: removed {Count} abandoned trust file(s) left by an earlier process", abandoned.Count);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            logger.Debug(ex, "SqlTrustedServerCertificateFile: could not sweep abandoned trust files");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            if (System.IO.File.Exists(FilePath))
                System.IO.File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            // The file holds a public certificate only and the containers JIM runs in have ephemeral
            // filesystems, so a failed clean-up must not take a synchronisation run down with it.
            Log.Warning(ex, "SqlTrustedServerCertificateFile: could not remove the temporary trust file. It holds a public certificate only and will go when the container is replaced");
        }
    }
}
