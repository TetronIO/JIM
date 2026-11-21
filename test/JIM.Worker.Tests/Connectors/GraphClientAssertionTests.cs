// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using JIM.Connectors.Graph;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The client assertion follows Microsoft's certificate credential format: a PS256-signed JWT carrying the
/// certificate's SHA-256 thumbprint, addressed to the token endpoint, issued by and about the application.
/// </summary>
[TestFixture]
public class GraphClientAssertionTests
{
    private const string ClientId = "1c2d3e4f-5a6b-4c7d-8e9f-a0b1c2d3e4f5";
    private static readonly Uri TokenEndpoint = new("https://login.microsoftonline.com/7a1f0c2e-3b4d-4e5f-8a9b-0c1d2e3f4a5b/oauth2/v2.0/token");
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    private GraphClientCertificateLoadResult _certificate = null!;

    [SetUp]
    public void SetUp()
    {
        _certificate = GraphClientCertificate.Load(GraphTestCertificates.RsaPfx(), GraphTestCertificates.Password, DateTime.UtcNow);
    }

    [TearDown]
    public void TearDown()
    {
        _certificate.Dispose();
    }

    [Test]
    public void GetTokenRequestParameters_SendsAJwtBearerAssertionAndNoSecret()
    {
        var parameters = Parameters();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parameters["client_assertion_type"], Is.EqualTo("urn:ietf:params:oauth:client-assertion-type:jwt-bearer"));
            Assert.That(parameters["client_assertion"].Split('.'), Has.Length.EqualTo(3));
            Assert.That(parameters.ContainsKey("client_secret"), Is.False);
        }
    }

    [Test]
    public void GetTokenRequestParameters_HeaderNamesPs256AndTheCertificatesSha256Thumbprint()
    {
        var header = Decode(Parameters()["client_assertion"].Split('.')[0]);
        var expectedThumbprint = Base64Url(SHA256.HashData(_certificate.Certificate!.RawData));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.GetProperty("alg").GetString(), Is.EqualTo("PS256"));
            Assert.That(header.GetProperty("typ").GetString(), Is.EqualTo("JWT"));
            Assert.That(header.GetProperty("x5t#S256").GetString(), Is.EqualTo(expectedThumbprint));
        }
    }

    [Test]
    public void GetTokenRequestParameters_ClaimsAddressTheTokenEndpointFromTheApplication()
    {
        var claims = Decode(Parameters()["client_assertion"].Split('.')[1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(claims.GetProperty("aud").GetString(), Is.EqualTo(TokenEndpoint.AbsoluteUri));
            Assert.That(claims.GetProperty("iss").GetString(), Is.EqualTo(ClientId));
            Assert.That(claims.GetProperty("sub").GetString(), Is.EqualTo(ClientId));
            Assert.That(Guid.TryParse(claims.GetProperty("jti").GetString(), out _), Is.True);
        }
    }

    [Test]
    public void GetTokenRequestParameters_IsValidForTenMinutesFromNow()
    {
        var claims = Decode(Parameters()["client_assertion"].Split('.')[1]);
        var now = Now.ToUnixTimeSeconds();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(claims.GetProperty("nbf").GetInt64(), Is.EqualTo(now));
            Assert.That(claims.GetProperty("iat").GetInt64(), Is.EqualTo(now));
            Assert.That(claims.GetProperty("exp").GetInt64(), Is.EqualTo(now + 600));
        }
    }

    [Test]
    public void GetTokenRequestParameters_SignatureVerifiesWithTheCertificatesPublicKeyUsingPss()
    {
        var parts = Parameters()["client_assertion"].Split('.');
        using var publicKey = _certificate.Certificate!.GetRSAPublicKey()!;

        var verified = publicKey.VerifyData(
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            FromBase64Url(parts[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);

        Assert.That(verified, Is.True);
    }

    [Test]
    public void GetTokenRequestParameters_EachCallHasANewJwtId()
    {
        // Entra ID may refuse an assertion it has seen before, so every token request needs a fresh one.
        var first = Decode(Parameters()["client_assertion"].Split('.')[1]).GetProperty("jti").GetString();
        var second = Decode(Parameters()["client_assertion"].Split('.')[1]).GetProperty("jti").GetString();

        Assert.That(second, Is.Not.EqualTo(first));
    }

    private Dictionary<string, string> Parameters() =>
        new GraphClientAssertion(_certificate.Certificate!, ClientId)
            .GetTokenRequestParameters(TokenEndpoint, Now)
            .ToDictionary(p => p.Key, p => p.Value);

    private static JsonElement Decode(string segment) => JsonDocument.Parse(FromBase64Url(segment)).RootElement.Clone();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string segment)
    {
        var base64 = segment.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
    }
}
