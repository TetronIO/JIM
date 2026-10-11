// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Middleware.Api;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="BearerToken"/>: reading the credential from an <c>Authorization: Bearer</c> header
/// (RFC 6750 s2.1), shared by the authentication scheme selector, the API key handler and the /api challenge.
/// </summary>
[TestFixture]
public class BearerTokenTests
{
    [TestCase("Bearer jim_ak_abc", "jim_ak_abc")]
    [TestCase("bearer jim_ak_abc", "jim_ak_abc")]
    [TestCase("BEARER eyJhbGciOiJSUzI1NiJ9.e30.sig", "eyJhbGciOiJSUzI1NiJ9.e30.sig")]
    [TestCase("Bearer   jim_ak_abc  ", "jim_ak_abc")]
    [TestCase("  Bearer jim_ak_abc", "jim_ak_abc")]
    public void TryParse_BearerCredential_ReturnsToken(string headerValue, string expectedToken)
    {
        var parsed = BearerToken.TryParse(headerValue, out var token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(token, Is.EqualTo(expectedToken));
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("Bearer")]
    [TestCase("Bearer ")]
    [TestCase("Bearer    ")]
    [TestCase("Bearerjim_ak_abc")]
    [TestCase("Basic dXNlcjpwYXNz")]
    [TestCase("jim_ak_abc")]
    public void TryParse_NotABearerCredential_ReturnsFalse(string? headerValue)
    {
        var parsed = BearerToken.TryParse(headerValue, out var token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.False);
            Assert.That(token, Is.Empty);
        }
    }

    [Test]
    public void TryRead_SingleAuthorizationHeader_ReturnsToken()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = "Bearer jim_ak_abc";

        var read = BearerToken.TryRead(request, out var token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read, Is.True);
            Assert.That(token, Is.EqualTo("jim_ak_abc"));
        }
    }

    [Test]
    public void TryRead_TwoAuthorizationHeaders_ReturnsFalse()
    {
        // HTTP allows one Authorization header. Two are ambiguous, so neither is taken as the credential.
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = new Microsoft.Extensions.Primitives.StringValues(["Bearer jim_ak_one", "Bearer jim_ak_two"]);

        var read = BearerToken.TryRead(request, out var token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read, Is.False);
            Assert.That(token, Is.Empty);
        }
    }

    [Test]
    public void TryRead_NoAuthorizationHeader_ReturnsFalse()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers["X-API-Key"] = "jim_ak_abc";

        Assert.That(BearerToken.TryRead(request, out _), Is.False);
    }

    [TestCase("jim_ak_abc", true)]
    [TestCase("JIM_AK_abc", false)]
    [TestCase("jim_abc", false)]
    [TestCase("eyJhbGciOiJSUzI1NiJ9.e30.sig", false)]
    public void IsApiKey_ChecksTheJimApiKeyPrefixOrdinally(string token, bool expected)
    {
        Assert.That(BearerToken.IsApiKey(token), Is.EqualTo(expected));
    }
}
