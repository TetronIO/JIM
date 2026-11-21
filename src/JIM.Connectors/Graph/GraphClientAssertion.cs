// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using JIM.Connectors.Http;

namespace JIM.Connectors.Graph;

/// <summary>
/// Authenticates an app registration to Microsoft Entra ID with a certificate: a JWT client assertion signed with the
/// certificate's private key (OpenID Connect <c>private_key_jwt</c>), built fresh for every token request.
/// </summary>
/// <remarks>
/// Follows Microsoft's certificate credential format (learn.microsoft.com/entra/identity-platform/certificate-credentials):
/// PS256 with the certificate's SHA-256 thumbprint in <c>x5t#S256</c>, and <c>aud</c> set to the token endpoint the
/// request is sent to. Written against the BCL rather than MSAL, which would be a dependency for this alone. The
/// certificate is owned by the caller, which disposes it when the run ends.
/// </remarks>
internal sealed class GraphClientAssertion : IOAuthClientCredential
{
    private const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>Microsoft asks for an assertion lifetime of 5 to 10 minutes at most.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly X509Certificate2 _certificate;
    private readonly string _clientId;
    private readonly string _encodedHeader;

    public GraphClientAssertion(X509Certificate2 certificate, string clientId)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        if (!certificate.HasPrivateKey)
            throw new ArgumentException("The certificate has no private key to sign the assertion with.", nameof(certificate));

        _certificate = certificate;
        _clientId = clientId;
        _encodedHeader = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["alg"] = "PS256",
            ["typ"] = "JWT",
            ["x5t#S256"] = Base64Url(SHA256.HashData(certificate.RawData))
        }));
    }

    public Task<IEnumerable<KeyValuePair<string, string>>> GetTokenRequestParametersAsync(Uri tokenEndpoint, DateTimeOffset utcNow, CancellationToken cancellationToken) =>
        Task.FromResult(GetTokenRequestParameters(tokenEndpoint, utcNow));

    internal IEnumerable<KeyValuePair<string, string>> GetTokenRequestParameters(Uri tokenEndpoint, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(tokenEndpoint);

        var issuedAt = utcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>
        {
            ["aud"] = tokenEndpoint.AbsoluteUri,
            ["iss"] = _clientId,
            ["sub"] = _clientId,
            // random-id: a JWT ID must be unguessable and unique per assertion, not a row id.
            ["jti"] = Guid.NewGuid().ToString(),
            ["nbf"] = issuedAt,
            ["iat"] = issuedAt,
            ["exp"] = issuedAt + (long)Lifetime.TotalSeconds
        };

        var signingInput = $"{_encodedHeader}.{Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims))}";
        using var rsa = _certificate.GetRSAPrivateKey()
                        ?? throw new InvalidOperationException("The certificate does not have an RSA private key.");
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        return
        [
            new("client_assertion_type", AssertionType),
            new("client_assertion", $"{signingInput}.{Base64Url(signature)}")
        ];
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
