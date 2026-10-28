// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JIM.Web.Authentication;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace JIM.Web.Api.Tests;

// The identity provider's side of the tests that run JIM's real /api bearer scheme: SigningKeyRolloverTests and
// AccessTokenSignInProblemTests.

/// <summary>
/// The identity provider's side: discovery, and a key set the test replaces at will.
/// </summary>
internal sealed class FakeIdentityProvider
{
    private volatile string _keySet = """{"keys":[]}""";
    private int _keySetFetches;

    public int Port { get; set; }

    public string Issuer => $"https://localhost:{Port}/realms/jim";

    /// <summary>An access token issuer in the form AD FS publishes: its federation service identifier.</summary>
    public string AdfsAccessTokenIssuer => $"http://localhost:{Port}/adfs/services/trust";

    /// <summary>The <c>access_token_issuer</c> discovery publishes, if any.</summary>
    public string? AccessTokenIssuer { get; set; }

    public int KeySetFetches => Volatile.Read(ref _keySetFetches);

    public void Reset()
    {
        _keySet = """{"keys":[]}""";
        AccessTokenIssuer = null;
        Interlocked.Exchange(ref _keySetFetches, 0);
    }

    public void Publish(params SigningKey[] keys) =>
        _keySet = JsonSerializer.Serialize(new { keys = keys.Select(k => k.ToJwk()).ToArray() });

    public string? Respond(string path)
    {
        if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
        {
            var discovery = new Dictionary<string, string>
            {
                ["issuer"] = Issuer,
                ["jwks_uri"] = $"{Issuer}/protocol/openid-connect/certs",
                ["authorization_endpoint"] = $"{Issuer}/protocol/openid-connect/auth",
                ["token_endpoint"] = $"{Issuer}/protocol/openid-connect/token"
            };
            if (AccessTokenIssuer is { } accessTokenIssuer)
                discovery["access_token_issuer"] = accessTokenIssuer;
            return JsonSerializer.Serialize(discovery);
        }

        if (path.EndsWith("/protocol/openid-connect/certs", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _keySetFetches);
            return _keySet;
        }

        return null;
    }
}

/// <summary>
/// An RSA token-signing key with a key id, as an identity provider holds one.
/// </summary>
internal sealed class SigningKey : IDisposable
{
    private readonly RSA _rsa;

    private SigningKey(string keyId, RSA rsa)
    {
        KeyId = keyId;
        _rsa = rsa;
    }

    public string KeyId { get; }

    public static SigningKey Create(string name) => new($"{name}-{Guid.NewGuid():N}", RSA.Create(2048));

    public string IssueToken(string issuer, string audience, DateTime? expires = null)
    {
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(5);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", "rollover-test")]),
            IssuedAt = expiry.AddMinutes(-10),
            NotBefore = expiry.AddMinutes(-10),
            Expires = expiry,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256)
        });
    }

    public object ToJwk()
    {
        var parameters = _rsa.ExportParameters(includePrivateParameters: false);
        return new Dictionary<string, string>
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["alg"] = "RS256",
            ["kid"] = KeyId,
            ["n"] = Base64UrlEncoder.Encode(parameters.Modulus),
            ["e"] = Base64UrlEncoder.Encode(parameters.Exponent)
        };
    }

    public void Dispose() => _rsa.Dispose();
}

internal sealed class RecordingFailedAuthenticationRecorder : IFailedAuthenticationRecorder
{
    private readonly List<string> _reasons = [];

    public IReadOnlyList<string> Reasons
    {
        get { lock (_reasons) return _reasons.ToArray(); }
    }

    public Task RecordAsync(string eventName, string reason, string? apiKeyPrefix, string? clientIp)
    {
        lock (_reasons) _reasons.Add(reason);
        return Task.CompletedTask;
    }
}
