// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Authentication;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="ApiAudienceResolver"/>: the audience JIM expects on SSO bearer tokens, derived from
/// <c>JIM_SSO_API_SCOPE</c> and <c>JIM_SSO_CLIENT_ID</c> (issue #2353).
/// </summary>
[TestFixture]
public class ApiAudienceResolverTests
{
    private const string ClientId = "jim-client";

    [TestCase("api://12345678-1234-1234-1234-123456789abc/access_as_user", "api://12345678-1234-1234-1234-123456789abc")]
    [TestCase("api://contoso.com/jim/access_as_user", "api://contoso.com/jim")]
    public void Resolve_ApiScopeWithPermission_ReturnsIdentifierWithoutPermission(string apiScope, string expected)
    {
        Assert.That(ApiAudienceResolver.Resolve(apiScope, ClientId), Is.EqualTo(expected));
    }

    [TestCase("api://e1234567-89ab-cdef-0123-456789abcdef", "api://e1234567-89ab-cdef-0123-456789abcdef")]
    [TestCase("api://default", "api://default")]
    [TestCase("api://default/", "api://default")]
    public void Resolve_BareApiIdentifier_ReturnsIdentifier(string apiScope, string expected)
    {
        Assert.That(ApiAudienceResolver.Resolve(apiScope, ClientId), Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("jim-api")]
    [TestCase("https://jim.example.com")]
    public void Resolve_NonApiScope_ReturnsClientId(string? apiScope)
    {
        Assert.That(ApiAudienceResolver.Resolve(apiScope, ClientId), Is.EqualTo(ClientId));
    }
}
