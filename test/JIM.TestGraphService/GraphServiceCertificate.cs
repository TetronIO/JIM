// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace JIM.TestGraphService;

/// <summary>
/// The self-signed certificate the stand-in serves HTTPS from, issued for Microsoft's own host names so the Connector
/// validates it exactly as it validates Microsoft's, once the scenario adds it to JIM's Trusted Certificates.
/// <para>
/// Generated fresh at every start rather than committed, as the SCIM test provider's is: a committed one is a private
/// key in the repository, and it would expire on a date nobody chose.
/// </para>
/// </summary>
public static class GraphServiceCertificate
{
    public static X509Certificate2 Create(IEnumerable<string> hostNames)
    {
        var names = hostNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0)
            throw new ArgumentException("At least one host name is needed.", nameof(hostNames));

        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={names[0]}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
            subjectAlternativeNames.AddDnsName(name);
        subjectAlternativeNames.AddDnsName("localhost");
        subjectAlternativeNames.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());

        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(1));

        // Kestrel needs the private key to survive, which on Linux means round-tripping through PKCS#12.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), password: null);
    }

    /// <summary>Writes the public certificate, never the key, where the scenario can read it and trust it.</summary>
    public static void Export(X509Certificate2 certificate, string? path, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(path))
            return;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        // Written to a temporary name and moved, so the scenario never reads a half-written file.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, certificate.ExportCertificatePem() + Environment.NewLine);
        File.Move(temporary, path, overwrite: true);
        logger.LogInformation("Wrote the public certificate to {Path} for the scenario to trust.", path);
    }
}
