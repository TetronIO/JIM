// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Builds self-signed certificates as Base64-encoded PFX, the form the Microsoft Graph Connector's Certificate setting
/// takes, for tests of how it loads and checks them.
/// </summary>
internal static class GraphTestCertificates
{
    public const string Password = "test-pfx-password";

    public static string RsaPfx(int keySize = 2048, string? password = Password, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        using var rsa = RSA.Create(keySize);
        var request = new CertificateRequest("CN=JIM Graph Connector Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddYears(1));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, password));
    }

    public static string EcdsaPfx()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=JIM Graph Connector Test", ecdsa, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, Password));
    }

    public static string PfxWithoutPrivateKey()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=JIM Graph Connector Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var withKey = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var publicOnly = X509CertificateLoader.LoadCertificate(withKey.RawData);
        return Convert.ToBase64String(publicOnly.Export(X509ContentType.Pkcs12, Password));
    }
}
