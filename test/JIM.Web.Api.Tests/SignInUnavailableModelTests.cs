// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Threading;
using System.Threading.Tasks;
using JIM.Models.Security;
using JIM.Web.Authentication;
using JIM.Web.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The page shown when sign-in cannot reach or trust the identity provider (#2363). Reachable without signing in,
/// because nobody can; it shows the problem and the command that fixes it, and never trusts anything itself.
/// </summary>
[TestFixture]
public class SignInUnavailableModelTests
{
    [Test]
    public void SignInUnavailableModel_AllowsAnonymousAccess()
    {
        Assert.That(typeof(SignInUnavailableModel).IsDefined(typeof(AllowAnonymousAttribute), inherit: true), Is.True);
    }

    [Test]
    public void SignInUnavailableModel_HasNoHandlerThatChangesAnything()
    {
        var handlers = typeof(SignInUnavailableModel).GetMethods();

        Assert.That(handlers, Has.None.Matches<System.Reflection.MethodInfo>(m => m.Name.StartsWith("OnPost", System.StringComparison.Ordinal) || m.Name.StartsWith("OnPut", System.StringComparison.Ordinal) || m.Name.StartsWith("OnDelete", System.StringComparison.Ordinal)));
    }

    [Test]
    public async Task OnGetAsync_Untrusted_OffersTheCommandNamingTheSuggestedCertificateAndThisJimAsync()
    {
        var model = BuildModel(Untrusted());

        await model.OnGetAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.CanTrustFromIdentityProvider, Is.True);
            Assert.That(model.TrustCommand, Does.Contain("Connect-JIM -Url https://jim.corp.contoso.com -ApiKey $env:JIM_INFRASTRUCTURE_API_KEY"));
            Assert.That(model.TrustCommand, Does.Contain("Add-JIMCertificate -FromIdentityProvider -Thumbprint 8F3A91D06B2E47C2C21D"));
            Assert.That(model.IsAvailable, Is.False);
        }
    }

    [Test]
    public async Task OnGetAsync_NameMismatch_OffersNoCommandBecauseTrustingCannotFixItAsync()
    {
        var model = BuildModel(new IdentityProviderTrustStatus { State = IdentityProviderTrustState.NameMismatch, Host = "adfs.corp.contoso.com" });

        await model.OnGetAsync(CancellationToken.None);

        Assert.That(model.CanTrustFromIdentityProvider, Is.False);
    }

    [Test]
    public async Task OnGetAsync_TrustedAgain_SaysSignInIsAvailableAsync()
    {
        var model = BuildModel(new IdentityProviderTrustStatus { State = IdentityProviderTrustState.Trusted, Host = "adfs.corp.contoso.com" });

        await model.OnGetAsync(CancellationToken.None);

        Assert.That(model.IsAvailable, Is.True);
    }

    private static IdentityProviderTrustStatus Untrusted() => new()
    {
        State = IdentityProviderTrustState.Untrusted,
        Host = "adfs.corp.contoso.com",
        PresentedChain =
        [
            new IdentityProviderCertificate { CommonName = "adfs.corp.contoso.com", Thumbprint = "AA11", IsServerCertificate = true },
            new IdentityProviderCertificate { CommonName = "Contoso Issuing CA 01", Thumbprint = "8F3A91D06B2E47C2C21D" }
        ],
        UnsentIssuer = "CN=Contoso Root CA"
    };

    private static SignInUnavailableModel BuildModel(IdentityProviderTrustStatus status)
    {
        var monitor = new Mock<IIdentityProviderTrustMonitor>();
        monitor.Setup(m => m.GetFreshStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(status);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("jim.corp.contoso.com");
        return new SignInUnavailableModel(monitor.Object) { PageContext = new PageContext { HttpContext = httpContext } };
    }
}
