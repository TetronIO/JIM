// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// What the portal asks the identity provider for when someone signs in (#2439). AD FS applies the Web API's claim rules
/// to the ID token only when the client names that API as its resource and asks for <c>allatclaims</c>; without them the
/// portal's ID token lacks the <c>oid</c>, name and email the rules issue, and the portal and <c>/api</c> would identify
/// the same person differently. Other providers must get no resource and no <c>allatclaims</c>.
/// </summary>
[TestFixture]
public class PortalSignInRequestTests
{
    private const string ClientId = "4f3c2a10-7d8e-4b9a-a1c2-0d9e8f7a6b5c";

    [TestCase("https://keycloak.example.com/realms/jim", null)]
    [TestCase("https://keycloak.example.com/realms/jim", "api://jim-api")]
    [TestCase("https://login.microsoftonline.com/contoso.onmicrosoft.com/v2.0", "api://4f3c2a10-7d8e-4b9a-a1c2-0d9e8f7a6b5c/access_as_user")]
    [TestCase("http://localhost:8181/realms/jim", "jim-api")]
    public void Configure_NotAdfs_AsksForOpenIdProfileAndEmailOnly(string authority, string? apiScope)
    {
        var options = new OpenIdConnectOptions();

        PortalSignInRequest.Configure(options, authority, apiScope, ClientId);

        Assert.Multiple(() =>
        {
            Assert.That(options.Scope, Is.EquivalentTo(new[] { "openid", "profile", "email" }));
            Assert.That(options.Resource, Is.Null);
        });
    }

    [TestCase("https://adfs.panoply.local/adfs")]
    [TestCase("https://adfs.panoply.local/adfs/")]
    [TestCase("https://ADFS.panoply.local/ADFS")]
    public void Configure_Adfs_AsksForTheApiAsItsResourceWithAllAtClaims(string authority)
    {
        var options = new OpenIdConnectOptions();

        PortalSignInRequest.Configure(options, authority, $"api://{ClientId}", ClientId);

        Assert.Multiple(() =>
        {
            Assert.That(options.Scope, Is.EquivalentTo(new[] { "openid", "profile", "email", "allatclaims" }));
            Assert.That(options.Resource, Is.EqualTo($"api://{ClientId}"));
        });
    }

    [Test]
    public void Configure_AdfsWithoutAnApiScope_AsksForNoResource()
    {
        // With no Web API configured there are no API claim rules to bring into the ID token, and naming the portal's own
        // client as the resource would only change the access token's audience.
        var options = new OpenIdConnectOptions();

        PortalSignInRequest.Configure(options, "https://adfs.panoply.local/adfs", null, ClientId);

        Assert.Multiple(() =>
        {
            Assert.That(options.Scope, Is.EquivalentTo(new[] { "openid", "profile", "email" }));
            Assert.That(options.Resource, Is.Null);
        });
    }

    [TestCase("https://adfs.panoply.local/adfs", true)]
    [TestCase("https://adfs.panoply.local/adfs/", true)]
    [TestCase("https://sts.contoso.com/ADFS", true)]
    [TestCase("https://keycloak.example.com/realms/adfs", false)]
    [TestCase("https://idp.example.com/adfs/oauth2", false)]
    [TestCase("https://adfs.example.com/", false)]
    [TestCase(null, false)]
    [TestCase("not a url", false)]
    public void IsAdfsAuthority_ReadsTheAuthoritysPath(string? authority, bool expected)
    {
        Assert.That(PortalSignInRequest.IsAdfsAuthority(authority), Is.EqualTo(expected));
    }
}
