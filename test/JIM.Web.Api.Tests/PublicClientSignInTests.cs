// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Authentication;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// How an interactive public client signs in: the PowerShell module, and the API reference's Try It (#2594). Both use
/// the same client and ask for the API in the same form, so a registration that lets one sign in lets the other.
/// </summary>
[TestFixture]
public class PublicClientSignInTests
{
    [Test]
    public void ClientId_PublicClientConfigured_IsThePublicClient()
    {
        Assert.That(PublicClientSignIn.ClientId("jim", "jim-powershell"), Is.EqualTo("jim-powershell"));
    }

    [TestCase(null)]
    [TestCase("")]
    public void ClientId_NoPublicClient_IsJimsOwnClient(string? publicClientId)
    {
        Assert.That(PublicClientSignIn.ClientId("jim", publicClientId), Is.EqualTo("jim"));
    }

    [TestCase("http://localhost:8181/realms/jim", "jim-api")]
    [TestCase("https://login.microsoftonline.com/tenant/v2.0", "api://11111111-1111-1111-1111-111111111111/access_as_user")]
    public void ApiScope_NotAdfs_IsTheApiScopeAsConfigured(string authority, string apiScope)
    {
        Assert.That(PublicClientSignIn.ApiScope(authority, apiScope, "11111111-1111-1111-1111-111111111111"), Is.EqualTo(apiScope));
    }

    [TestCase("api://11111111-1111-1111-1111-111111111111")]
    [TestCase("api://11111111-1111-1111-1111-111111111111/")]
    public void ApiScope_Adfs_IsTheWebApisOpenIdScope(string apiScope)
    {
        // AD FS takes a resource in the scope parameter only as [resource]/[scope]; a bare api://{id} is refused (MSIS9602).
        Assert.That(PublicClientSignIn.ApiScope("https://adfs.example.com/adfs", apiScope, "11111111-1111-1111-1111-111111111111"),
            Is.EqualTo("api://11111111-1111-1111-1111-111111111111/openid"));
    }

    [TestCase(null)]
    [TestCase("")]
    public void ApiScope_NoneConfigured_IsNone(string? apiScope)
    {
        Assert.That(PublicClientSignIn.ApiScope("https://adfs.example.com/adfs", apiScope, "jim"), Is.Null);
    }
}
