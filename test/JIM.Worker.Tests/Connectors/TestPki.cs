// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Builds certificate hierarchies for tests: a root, the certificate authorities beneath it, and server certificates,
/// each carrying its private key so it can sign the next or serve TLS.
/// </summary>
internal static class TestPki
{
    /// <summary>
    /// A self-signed root certificate authority.
    /// </summary>
    internal static X509Certificate2 CreateRoot(string commonName, int validDays = 60)
    {
        using var key = RSA.Create(2048);
        var request = NewRequest(commonName, key, isCertificateAuthority: true);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(validDays));
        return Exportable(certificate);
    }

    /// <summary>
    /// A certificate authority issued by <paramref name="issuer"/>.
    /// </summary>
    internal static X509Certificate2 CreateIntermediate(string commonName, X509Certificate2 issuer, int validDays = 45,
        bool isCertificateAuthority = true, DateTimeOffset? notAfter = null, string? issuerDownloadUrl = null)
    {
        using var key = RSA.Create(2048);
        var request = NewRequest(commonName, key, isCertificateAuthority);
        AddAuthorityExtensions(request, issuer, issuerDownloadUrl);
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        using var certificate = request.Create(issuer, notBefore, notAfter ?? DateTimeOffset.UtcNow.AddDays(validDays), RandomNumberGenerator.GetBytes(8));
        using var withKey = certificate.CopyWithPrivateKey(key);
        return Exportable(withKey);
    }

    /// <summary>
    /// A server certificate for <paramref name="hostName"/>, issued by <paramref name="issuer"/>.
    /// </summary>
    internal static X509Certificate2 CreateServer(string hostName, X509Certificate2 issuer, string? issuerDownloadUrl = null)
    {
        using var key = RSA.Create(2048);
        var request = NewRequest(hostName, key, isCertificateAuthority: false);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(hostName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        AddAuthorityExtensions(request, issuer, issuerDownloadUrl);
        using var certificate = SignedBy(request, issuer, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var withKey = certificate.CopyWithPrivateKey(key);
        return Exportable(withKey);
    }

    /// <summary>
    /// Signs with the issuer's key directly. Unlike <see cref="CertificateRequest.Create(X509Certificate2, DateTimeOffset, DateTimeOffset, byte[])"/>,
    /// this does not refuse an issuer that is not a certificate authority, which is exactly what some tests need.
    /// </summary>
    private static X509Certificate2 SignedBy(CertificateRequest request, X509Certificate2 issuer, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var issuerKey = issuer.GetRSAPrivateKey() ?? throw new InvalidOperationException("The issuer has no private key to sign with.");
        var generator = X509SignatureGenerator.CreateForRSA(issuerKey, RSASignaturePadding.Pkcs1);
        return request.Create(issuer.SubjectName, generator, notBefore, notAfter, RandomNumberGenerator.GetBytes(8));
    }

    /// <summary>
    /// A server certificate issued by an expired certificate authority, valid only within the issuer's own (past)
    /// validity period, since no issuer can sign a certificate that outlives it.
    /// </summary>
    internal static X509Certificate2 CreateServerAllowingExpiredIssuer(string hostName, X509Certificate2 issuer)
    {
        using var key = RSA.Create(2048);
        var request = NewRequest(hostName, key, isCertificateAuthority: false);
        AddAuthorityExtensions(request, issuer, issuerDownloadUrl: null);
        using var certificate = SignedBy(request, issuer, issuer.NotBefore, issuer.NotAfter);
        using var withKey = certificate.CopyWithPrivateKey(key);
        return Exportable(withKey);
    }

    /// <summary>
    /// The public part of a certificate, as a server sends it or a PKI team hands it over.
    /// </summary>
    internal static X509Certificate2 PublicOnly(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadCertificate(certificate.RawData);

    private static CertificateRequest NewRequest(string commonName, RSA key, bool isCertificateAuthority)
    {
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(isCertificateAuthority, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            isCertificateAuthority
                ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign
                : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        return request;
    }

    private static void AddAuthorityExtensions(CertificateRequest request, X509Certificate2 issuer, string? issuerDownloadUrl)
    {
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        if (issuerDownloadUrl != null)
            request.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(null, [issuerDownloadUrl]));
    }

    /// <summary>
    /// Round-trips through PKCS#12 so the private key is usable by SslStream on every platform.
    /// </summary>
    private static X509Certificate2 Exportable(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);

    /// <summary>
    /// The certificates as a PEM bundle, the way a PKI team usually hands a chain over.
    /// </summary>
    internal static byte[] ToPemBundle(params X509Certificate2[] certificates) =>
        System.Text.Encoding.ASCII.GetBytes(string.Concat(certificates.Select(certificate => certificate.ExportCertificatePem() + "\n")));

    /// <summary>
    /// The certificates as a degenerate PKCS#7 SignedData structure, the .p7b format Windows exports chains in.
    /// </summary>
    internal static byte[] ToPkcs7(params X509Certificate2[] certificates)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier("1.2.840.113549.1.7.2");
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSequence())
            {
                writer.WriteInteger(1);
                using (writer.PushSetOf()) { }
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier("1.2.840.113549.1.7.1");
                }

                using (writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    foreach (var certificate in certificates)
                        writer.WriteEncodedValue(certificate.RawData);
                }

                using (writer.PushSetOf()) { }
            }
        }

        return writer.Encode();
    }
}
