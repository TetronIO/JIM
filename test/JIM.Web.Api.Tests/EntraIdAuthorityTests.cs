// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Authentication;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="EntraIdAuthority"/>: recognising a Microsoft Entra ID authority, and the issuer its version 2
/// tokens carry (#2367).
/// </summary>
[TestFixture]
public class EntraIdAuthorityTests
{
    private const string TenantId = "34599ee0-829e-4dd4-8da2-4727a0664d51";

    [TestCase("https://login.microsoftonline.com/" + TenantId + "/v2.0")]
    [TestCase("https://login.microsoftonline.com/" + TenantId + "/v2.0/")]
    [TestCase("https://LOGIN.MICROSOFTONLINE.COM/" + TenantId + "/v2.0")]
    public void IsEntraId_EntraIdAuthority_True(string authority)
    {
        Assert.That(EntraIdAuthority.IsEntraId(authority), Is.True);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not a url")]
    [TestCase("https://sso.example.com/realms/jim")]
    [TestCase("https://adfs.example.com/adfs")]
    [TestCase("https://login.microsoftonline.com.example.com/" + TenantId + "/v2.0")]
    [TestCase("https://example.com/login.microsoftonline.com/" + TenantId + "/v2.0")]
    public void IsEntraId_OtherAuthority_False(string? authority)
    {
        Assert.That(EntraIdAuthority.IsEntraId(authority), Is.False);
    }

    [Test]
    public void ValidIssuers_EntraIdAuthority_IsTheTenantsVersion2IssuerOnly()
    {
        // Version 1 tokens, issued by sts.windows.net, are not accepted: the registration asks for version 2.
        Assert.That(EntraIdAuthority.ValidIssuers("https://login.microsoftonline.com/" + TenantId + "/v2.0"),
            Is.EqualTo(new[] { "https://login.microsoftonline.com/" + TenantId + "/v2.0" }));
    }

    [Test]
    public void ValidIssuers_AuthorityWithNoTenant_Empty()
    {
        Assert.That(EntraIdAuthority.ValidIssuers("https://login.microsoftonline.com/"), Is.Empty);
    }
}
