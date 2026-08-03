// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace JIM.TestScimServiceProvider;

/// <summary>
/// The self-signed certificate this provider serves HTTPS from.
/// <para>
/// Generated fresh at every start rather than committed. A certificate in the repository is a private
/// key in the repository, and one that expires eventually breaks the integration suite on a date nobody
/// chose; generating it means the scenario trusts whatever this run produced, which is the situation a
/// customer with an internal certificate authority is in anyway.
/// </para>
/// </summary>
public static class ScimTestProviderCertificate
{
    /// <summary>
    /// Builds a certificate for the host name JIM will connect to. The name has to match, because the
    /// scenario connects with Full Validation on: a certificate that failed the name check would prove
    /// only that JIM can be told to ignore certificates.
    /// </summary>
    public static X509Certificate2 Create(string hostName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);

        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={hostName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName(hostName);
        subjectAlternativeNames.AddDnsName("localhost");
        subjectAlternativeNames.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());

        var now = DateTimeOffset.UtcNow;
        var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(1));

        // Kestrel needs the private key to survive, which on Linux means round-tripping through a PKCS#12
        // export rather than handing it the in-memory certificate.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), password: null);
    }

    /// <summary>
    /// Writes the public certificate where the integration scenario can read it, so it can be added to
    /// JIM's Trusted Certificates before a Connected System points at this provider. Only the public
    /// certificate is written; the private key never leaves the process.
    /// </summary>
    public static void Export(X509Certificate2 certificate, string? path, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, certificate.ExportCertificatePem() + Environment.NewLine);
            logger.LogInformation("Wrote the public certificate to {Path} for the integration scenario to trust.", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // The provider is still usable without it; the scenario can fall back to fetching the
            // certificate from the endpoint, which is what an administrator does in the portal.
            logger.LogWarning(ex, "Could not write the public certificate to {Path}.", path);
        }
    }
}
