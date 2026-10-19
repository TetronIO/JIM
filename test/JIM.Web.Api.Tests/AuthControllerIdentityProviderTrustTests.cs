// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Web.Authentication;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The identity provider's trust status is readable without signing in, because the moment it matters most is when
/// nobody can sign in (#2363). Anonymous callers see only what any TLS client of the identity provider already sees;
/// what the JIM certificate store holds is for administrators.
/// </summary>
[TestFixture]
public class AuthControllerIdentityProviderTrustTests
{
    private Mock<IIdentityProviderTrustMonitor> _monitor = null!;
    private AuthController _controller = null!;
    private readonly Guid _storedId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _monitor = new Mock<IIdentityProviderTrustMonitor>();
        _monitor.Setup(m => m.GetFreshStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new IdentityProviderTrustStatus
        {
            State = IdentityProviderTrustState.Untrusted,
            Host = "adfs.corp.contoso.com",
            PresentedChain =
            [
                new IdentityProviderCertificate { CommonName = "adfs.corp.contoso.com", Thumbprint = "AA", IsServerCertificate = true },
                new IdentityProviderCertificate { CommonName = "Contoso Issuing CA 01", Thumbprint = "BB", TrustedCertificateId = _storedId, TrustedFor = CertificateTrustPurpose.Connectors }
            ],
            UnsentIssuer = "CN=Contoso Root CA"
        });
        _controller = new AuthController();
    }

    [Test]
    public void GetIdentityProviderTrustAsync_IsAnonymous()
    {
        var method = typeof(AuthController).GetMethod(nameof(AuthController.GetIdentityProviderTrustAsync))!;

        Assert.That(method.GetCustomAttribute<AllowAnonymousAttribute>(), Is.Not.Null);
    }

    [Test]
    public async Task GetIdentityProviderTrustAsync_Anonymous_ChainAndSuggestionButNothingAboutTheStoreAsync()
    {
        As(new ClaimsPrincipal(new ClaimsIdentity()));

        var status = await GetAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.State, Is.EqualTo(IdentityProviderTrustState.Untrusted));
            Assert.That(status.PresentedChain, Has.Count.EqualTo(2));
            Assert.That(status.SuggestedThumbprint, Is.EqualTo("BB"));
            Assert.That(status.UnsentIssuer, Is.EqualTo("CN=Contoso Root CA"));
            Assert.That(status.PresentedChain[1].TrustedFor, Is.Null);
            Assert.That(status.PresentedChain[1].TrustedCertificateId, Is.Null);
            Assert.That(status.VouchingCertificates, Is.Null);
        }
        _monitor.Verify(m => m.GetFreshStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
        _monitor.Verify(m => m.RecheckAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GetIdentityProviderTrustAsync_Administrator_SeesWhatTheStoreHoldsAsync()
    {
        As(new ClaimsPrincipal(new ClaimsIdentity([new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.Administrator)], "Test", "name", Constants.BuiltInRoles.RoleClaimType)));

        var status = await GetAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.PresentedChain[1].TrustedFor, Is.EqualTo(CertificateTrustPurpose.Connectors));
            Assert.That(status.PresentedChain[1].TrustedCertificateId, Is.EqualTo(_storedId));
            Assert.That(status.VouchingCertificates, Is.Not.Null);
        }
    }

    [Test]
    public async Task GetIdentityProviderTrustAsync_SignedInWithoutAdministrator_SeesNothingAboutTheStoreAsync()
    {
        As(new ClaimsPrincipal(new ClaimsIdentity([new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.User)], "Test", "name", Constants.BuiltInRoles.RoleClaimType)));

        var status = await GetAsync();

        Assert.That(status.PresentedChain[1].TrustedFor, Is.Null);
    }

    private async Task<IdentityProviderTrustStatusDto> GetAsync()
    {
        var result = await _controller.GetIdentityProviderTrustAsync(_monitor.Object, CancellationToken.None) as OkObjectResult;
        return (IdentityProviderTrustStatusDto)result!.Value!;
    }

    private void As(ClaimsPrincipal user) =>
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
}
