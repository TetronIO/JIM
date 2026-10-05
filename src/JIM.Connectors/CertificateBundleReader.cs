// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace JIM.Connectors;

/// <summary>
/// Reads the certificates an administrator supplies to complete a server's certificate chain.
/// </summary>
/// <remarks>
/// Accepts the forms a PKI team hands a chain over in: a single certificate (DER, as .cer or .crt), PEM text holding
/// one certificate or several, and PKCS#7 (.p7b, DER or PEM), the format Windows exports a chain in. Only public
/// certificate material is ever taken: a private key that arrives alongside is skipped, never read or kept.
/// <para>
/// PKCS#7 is read with the framework's own ASN.1 reader rather than a further package, because all that is needed is
/// the certificates field of an unsigned SignedData structure.
/// </para>
/// </remarks>
public static class CertificateBundleReader
{
    private const string Pkcs7SignedDataOid = "1.2.840.113549.1.7.2";

    private const string UnreadableMessage =
        "The file is not a certificate JIM can read. Supply certificates as .cer, .crt, .pem or .p7b files.";

    /// <summary>
    /// The certificates in a supplied file.
    /// </summary>
    /// <exception cref="InvalidDataException">The data holds no certificate JIM can read.</exception>
    public static List<X509Certificate2> Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
            throw new InvalidDataException(UnreadableMessage);

        try
        {
            var certificates = LooksLikePem(data) ? ReadPem(Encoding.ASCII.GetString(data)) : ReadDer(data);
            if (certificates.Count == 0)
                throw new InvalidDataException(UnreadableMessage);

            return certificates;
        }
        catch (Exception ex) when (ex is CryptographicException or AsnContentException or ArgumentException)
        {
            throw new InvalidDataException(UnreadableMessage, ex);
        }
    }

    private static bool LooksLikePem(byte[] data) =>
        Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 4096)).Contains("-----BEGIN ", StringComparison.Ordinal);

    private static List<X509Certificate2> ReadPem(string text)
    {
        var certificates = new List<X509Certificate2>();
        var remaining = text.AsMemory();

        while (PemEncoding.TryFind(remaining.Span, out var fields))
        {
            var label = remaining.Span[fields.Label].ToString();
            var der = Convert.FromBase64String(remaining.Span[fields.Base64Data].ToString());

            // Every other label (a private key, a request) is skipped without being decoded beyond its base64.
            if (label == "CERTIFICATE")
                certificates.Add(X509CertificateLoader.LoadCertificate(der));
            else if (label == "PKCS7")
                certificates.AddRange(ReadPkcs7(der));

            remaining = remaining[fields.Location.End..];
        }

        return certificates;
    }

    private static List<X509Certificate2> ReadDer(byte[] data) =>
        IsPkcs7(data) ? ReadPkcs7(data) : [X509CertificateLoader.LoadCertificate(data)];

    private static bool IsPkcs7(byte[] data)
    {
        try
        {
            var reader = new AsnReader(data, AsnEncodingRules.BER).ReadSequence();
            return reader.ReadObjectIdentifier() == Pkcs7SignedDataOid;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The certificates field of a PKCS#7 SignedData structure (RFC 5652 section 5.1). BER, since that is what some
    /// tools still write.
    /// </summary>
    private static List<X509Certificate2> ReadPkcs7(byte[] data)
    {
        var contentInfo = new AsnReader(data, AsnEncodingRules.BER).ReadSequence();
        if (contentInfo.ReadObjectIdentifier() != Pkcs7SignedDataOid)
            throw new InvalidDataException(UnreadableMessage);

        var signedData = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        signedData.ReadInteger();
        signedData.ReadSetOf();
        signedData.ReadSequence();

        var certificates = new List<X509Certificate2>();
        var certificatesTag = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
        if (!signedData.HasData || !signedData.PeekTag().HasSameClassAndValue(certificatesTag))
            return certificates;

        var certificateSet = signedData.ReadSetOf(certificatesTag);
        while (certificateSet.HasData)
        {
            // CertificateChoices: a plain Certificate is a SEQUENCE; the tagged alternatives (attribute and other
            // certificates) are not X.509 certificates, so they are skipped.
            var encoded = certificateSet.ReadEncodedValue();
            if (new AsnReader(encoded, AsnEncodingRules.BER).PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
                certificates.Add(X509CertificateLoader.LoadCertificate(encoded.Span));
        }

        return certificates;
    }
}
