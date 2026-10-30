// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using JIM.Web.Authentication;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The issuers JIM accepts a bearer token from (#2440). The bearer scheme itself is driven end to end by
/// <see cref="SigningKeyRolloverTests"/>.
/// </summary>
[TestFixture]
public class DiscoveredIssuersTests
{
    private const string Authority = "https://adfs.example.com/adfs";
    private const string AccessTokenIssuer = "http://adfs.example.com/adfs/services/trust";

    [Test]
    public void From_DiscoveryWithAccessTokenIssuer_NamesBoth()
    {
        var configuration = OpenIdConnectConfiguration.Create(
            $$"""{"issuer":"{{Authority}}","access_token_issuer":"{{AccessTokenIssuer}}"}""");

        Assert.That(DiscoveredIssuers.From(configuration), Is.EqualTo(new[] { Authority, AccessTokenIssuer }));
    }

    [Test]
    public void From_DiscoveryWithoutAccessTokenIssuer_NamesTheIssuerOnly()
    {
        var configuration = OpenIdConnectConfiguration.Create("""{"issuer":"https://idp.example.com/realms/jim"}""");

        Assert.That(DiscoveredIssuers.From(configuration), Is.EqualTo(new[] { "https://idp.example.com/realms/jim" }));
    }

    [Test]
    public void From_NoConfiguration_NamesNothing()
    {
        Assert.That(DiscoveredIssuers.From(null), Is.Empty);
    }

    [Test]
    public void ValidateIssuer_AccessTokenIssuerFromDiscovery_Accepted()
    {
        var configuration = OpenIdConnectConfiguration.Create(
            $$"""{"issuer":"{{Authority}}","access_token_issuer":"{{AccessTokenIssuer}}"}""");

        var issuer = DiscoveredIssuers.ValidateIssuer(AccessTokenIssuer, new JsonWebToken("e30.e30."), Parameters(Authority), configuration);

        Assert.That(issuer, Is.EqualTo(AccessTokenIssuer));
    }

    [Test]
    public void ValidateIssuer_ConfiguredIssuer_AcceptedWithoutDiscovery()
    {
        var issuer = DiscoveredIssuers.ValidateIssuer("https://other.example.com", new JsonWebToken("e30.e30."),
            Parameters(Authority, "https://other.example.com"), configuration: null);

        Assert.That(issuer, Is.EqualTo("https://other.example.com"));
    }

    [TestCase("https://attacker.example.com/adfs/services/trust")]
    [TestCase("HTTP://ADFS.EXAMPLE.COM/ADFS/SERVICES/TRUST")]
    [TestCase("")]
    public void ValidateIssuer_AnyOtherIssuer_Refused(string tokenIssuer)
    {
        var configuration = OpenIdConnectConfiguration.Create(
            $$"""{"issuer":"{{Authority}}","access_token_issuer":"{{AccessTokenIssuer}}"}""");

        var refusal = Assert.Throws<SecurityTokenInvalidIssuerException>(() =>
            DiscoveredIssuers.ValidateIssuer(tokenIssuer, new JsonWebToken("e30.e30."), Parameters(Authority), configuration));

        Assert.That(refusal!.InvalidIssuer, Is.EqualTo(tokenIssuer));
    }

    [Test]
    public void Configure_JimBearerScheme_ValidatesIssuersWithDiscovery()
    {
        var options = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions();

        JimJwtBearerOptions.Configure(options, Authority, "api://jim", [Authority], _ => System.Threading.Tasks.Task.CompletedTask);

        Assert.Multiple(() =>
        {
            Assert.That(options.TokenValidationParameters.ValidateIssuer, Is.True);
            Assert.That(options.TokenValidationParameters.IssuerValidatorUsingConfiguration, Is.Not.Null);
        });
    }

    private static TokenValidationParameters Parameters(params string[] validIssuers) => new()
    {
        ValidateIssuer = true,
        ValidIssuers = validIssuers.ToArray()
    };
}
