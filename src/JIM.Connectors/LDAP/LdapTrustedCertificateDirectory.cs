// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Serilog;
using System.Security.Cryptography.X509Certificates;
namespace JIM.Connectors.LDAP;

/// <summary>
/// A short-lived on-disk directory of trust anchors handed to the platform LDAP client for LDAPS connections,
/// holding the certificates an administrator added to the JIM certificate store plus the operating system's own
/// certificate bundle.
/// </summary>
/// <remarks>
/// <para>
/// JIM does not make the trust decision itself. The platform LDAP client validates the chain, the validity period
/// and the certificate's name against the Directory Server value being connected to; JIM only supplies additional
/// issuers for it to consider. That is why there is no managed validation callback here:
/// <c>LdapSessionOptions.VerifyServerCertificate</c> is unsupported on Linux, which is the only platform JIM's
/// containers run on, and installing it throws.
/// </para>
/// <para>
/// The system bundle is copied in deliberately. Pointing the client at a trust directory <em>replaces</em> its
/// configured trust anchors rather than adding to them, so a directory holding only JIM's certificates would stop
/// every publicly-issued or otherwise system-trusted directory certificate validating. Copying the bundle in keeps
/// trust strictly additive: adding certificates to the JIM store can only ever allow more connections, never fewer.
/// </para>
/// </remarks>
internal sealed class LdapTrustedCertificateDirectory : IDisposable
{
    /// <summary>
    /// File name used for the operating system's certificate bundle inside the trust directory. The numeric prefix
    /// keeps it visibly distinct from the thumbprint-named JIM certificates.
    /// </summary>
    internal const string SystemBundleFileName = "00-system-ca-bundle.crt";

    /// <summary>
    /// Where the operating system's certificate bundle is expected to live, in probe order. The first entry is the
    /// path used by the Debian and Ubuntu images JIM ships on, and is the same file the platform LDAP client is
    /// configured with by default (<c>TLS_CACERT</c> in <c>/etc/ldap/ldap.conf</c>).
    /// </summary>
    private static readonly string[] DefaultSystemBundlePaths =
    [
        "/etc/ssl/certs/ca-certificates.crt",
        "/etc/pki/tls/certs/ca-bundle.crt",
        "/etc/ssl/ca-bundle.pem"
    ];

    /// <summary>
    /// Prefix every trust directory carries, so abandoned ones can be recognised and swept.
    /// </summary>
    private const string DirectoryNamePrefix = "jim-ldap-trust-";

    /// <summary>
    /// How old an abandoned trust directory must be before the sweep removes it. Comfortably longer than any single
    /// import can run, so a directory still in use by a long-running run is never taken out from under it.
    /// </summary>
    private static readonly TimeSpan AbandonedDirectoryAge = TimeSpan.FromDays(7);

    private bool _disposed;

    /// <summary>
    /// Absolute path of the directory. Pass this to <c>LdapSessionOptions.TrustedCertificatesDirectory</c>.
    /// </summary>
    internal string DirectoryPath { get; }

    private LdapTrustedCertificateDirectory(string directoryPath)
    {
        DirectoryPath = directoryPath;
    }

    /// <summary>
    /// Materialises the supplied certificates, and the operating system's certificate bundle, into a new directory.
    /// </summary>
    /// <param name="trustedCertificates">Certificates from the JIM certificate store. Must not be empty; callers that hold no certificates should leave the platform's own trust configuration alone rather than handing it an emptier one.</param>
    /// <param name="logger">Logger for the calling operation.</param>
    /// <param name="systemBundleCandidatePaths">Overrides the default operating system bundle locations. Intended for tests.</param>
    /// <exception cref="ArgumentException">No certificates were supplied.</exception>
    internal static LdapTrustedCertificateDirectory Create(
        IReadOnlyCollection<X509Certificate2> trustedCertificates,
        ILogger logger,
        IReadOnlyList<string>? systemBundleCandidatePaths = null)
    {
        ArgumentNullException.ThrowIfNull(trustedCertificates);

        if (trustedCertificates.Count == 0)
            throw new ArgumentException("At least one certificate is required; without one there is nothing to add to the platform's trust configuration.", nameof(trustedCertificates));

        RemoveAbandonedDirectories(logger);

        var directoryPath = Path.Combine(Path.GetTempPath(), $"{DirectoryNamePrefix}{Guid.NewGuid():N}");
        var trustDirectory = new LdapTrustedCertificateDirectory(directoryPath);

        try
        {
            // The directory holds trust anchors, so restrict it to the account JIM runs as: anything able to write
            // here could make JIM trust an issuer of its choosing.
            Directory.CreateDirectory(directoryPath);
            if (!OperatingSystem.IsWindows())
                System.IO.File.SetUnixFileMode(directoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // Filtering in the sequence rather than the body: the same certificate can legitimately appear twice in
            // the store, and writing it twice would be harmless but pointless. Add returns false once seen.
            var writtenThumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var certificate in trustedCertificates.Where(certificate => writtenThumbprints.Add(certificate.Thumbprint)))
            {
                var certificatePath = ResolveWithin(directoryPath, $"{certificate.Thumbprint}.crt");
                System.IO.File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
                if (!OperatingSystem.IsWindows())
                    System.IO.File.SetUnixFileMode(certificatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            CopySystemBundle(directoryPath, systemBundleCandidatePaths ?? DefaultSystemBundlePaths, logger);

            logger.Debug("LdapTrustedCertificateDirectory: prepared {Count} trusted certificate(s) from the JIM certificate store for LDAPS validation", writtenThumbprints.Count);
            return trustDirectory;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            trustDirectory.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Removes trust directories left behind by a process that was killed before it could clean up its own.
    /// </summary>
    /// <remarks>
    /// Each directory is removed when its connection closes, but a killed process never reaches that, and a container
    /// that is restarted rather than replaced keeps its temporary files. Sweeping here means no deployment ever needs
    /// manual clean-up. Failures are logged and ignored: this is housekeeping, and must never stop a connection being
    /// established.
    /// </remarks>
    private static void RemoveAbandonedDirectories(ILogger logger)
    {
        try
        {
            var cutoff = DateTime.UtcNow - AbandonedDirectoryAge;
            var abandoned = Directory.EnumerateDirectories(Path.GetTempPath(), $"{DirectoryNamePrefix}*")
                .Where(d => Directory.GetLastWriteTimeUtc(d) < cutoff)
                .ToList();

            foreach (var directory in abandoned)
                Directory.Delete(directory, true);

            if (abandoned.Count > 0)
                logger.Debug("LdapTrustedCertificateDirectory: removed {Count} abandoned trust director(ies) left by an earlier process", abandoned.Count);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            logger.Debug(ex, "LdapTrustedCertificateDirectory: could not sweep abandoned trust directories");
        }
    }

    /// <summary>
    /// Copies the operating system's certificate bundle into the trust directory so that system-trusted issuers keep
    /// validating. A missing bundle is not fatal, but it does narrow what will validate, so it is logged as a warning.
    /// </summary>
    /// <summary>
    /// Joins a file name onto the trust directory and proves the result is still inside it.
    /// </summary>
    /// <remarks>
    /// A certificate's thumbprint is a hex string, so today it cannot traverse anywhere. This is checked rather
    /// than assumed because the name is derived from a certificate JIM did not issue and it decides where a file
    /// is written; the trust directory's whole purpose is to hold anchors nothing else can tamper with.
    /// </remarks>
    private static string ResolveWithin(string directoryPath, string fileName)
    {
        var directoryFullPath = Path.GetFullPath(directoryPath);
        var candidate = Path.GetFullPath(Path.Combine(directoryFullPath, fileName));
        var prefix = directoryFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? directoryFullPath
            : directoryFullPath + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException($"'{fileName}' does not resolve to a file inside the trust directory.", nameof(fileName));

        return candidate;
    }

    private static void CopySystemBundle(string directoryPath, IReadOnlyList<string> candidatePaths, ILogger logger)
    {
        var bundlePath = candidatePaths.FirstOrDefault(System.IO.File.Exists);
        if (bundlePath == null)
        {
            logger.Warning("LdapTrustedCertificateDirectory: no operating system certificate bundle found in any of {CandidateCount} expected locations. Only certificates from the JIM certificate store will be trusted for LDAPS connections from this Connected System", candidatePaths.Count);
            return;
        }

        var destinationPath = Path.Combine(directoryPath, SystemBundleFileName);
        System.IO.File.Copy(bundlePath, destinationPath, true);
        if (!OperatingSystem.IsWindows())
            System.IO.File.SetUnixFileMode(destinationPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        logger.Debug("LdapTrustedCertificateDirectory: copied the operating system certificate bundle from {BundlePath} so system-trusted issuers continue to validate", bundlePath);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            // Nothing here is secret (public certificates only) and the containers JIM runs in have ephemeral
            // filesystems, so a failed clean-up must not take a synchronisation run down with it.
            Log.Warning(ex, "LdapTrustedCertificateDirectory: could not remove the temporary trust directory. It holds public certificates only and will go when the container is replaced");
        }
    }
}
