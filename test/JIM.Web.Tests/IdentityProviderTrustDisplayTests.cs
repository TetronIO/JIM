// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Security;
using JIM.Web.Models;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// What the administrators' banner says about the identity provider (#2363): something only when new sign-ins will
/// fail or are about to, and an offer to trust a certificate only where trusting one fixes it.
/// </summary>
[TestFixture]
public class IdentityProviderTrustDisplayTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

    [TestCase(IdentityProviderTrustState.NotChecked)]
    [TestCase(IdentityProviderTrustState.NotHttps)]
    [TestCase(IdentityProviderTrustState.Trusted)]
    public void Banner_NothingWrong_Nothing(IdentityProviderTrustState state)
    {
        Assert.That(IdentityProviderTrustDisplay.Banner(new IdentityProviderTrustStatus { State = state, Host = "adfs.corp.contoso.com" }, Now), Is.Null);
    }

    [Test]
    public void Banner_Untrusted_WarnsThatNewSignInsWillFailAndOffersToTrust()
    {
        var banner = IdentityProviderTrustDisplay.Banner(new IdentityProviderTrustStatus
        {
            State = IdentityProviderTrustState.Untrusted,
            Host = "adfs.corp.contoso.com",
            PresentedChain =
            [
                new IdentityProviderCertificate { CommonName = "adfs.corp.contoso.com", IsServerCertificate = true },
                new IdentityProviderCertificate { CommonName = "Contoso Issuing CA 02" }
            ]
        }, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(banner?.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(banner?.Sentence, Does.Contain("New sign-ins will fail").And.Contain("Contoso Issuing CA 02"));
            Assert.That(banner?.OfferToTrust, Is.True);
        }
    }

    [Test]
    public void Banner_NameMismatch_WarnsWithoutOfferingToTrust()
    {
        var banner = IdentityProviderTrustDisplay.Banner(new IdentityProviderTrustStatus { State = IdentityProviderTrustState.NameMismatch, Host = "adfs.corp.contoso.com" }, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(banner?.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(banner?.OfferToTrust, Is.False);
        }
    }

    [Test]
    public void Banner_Unreachable_WarnsWithoutOfferingToTrust()
    {
        var banner = IdentityProviderTrustDisplay.Banner(new IdentityProviderTrustStatus { State = IdentityProviderTrustState.Unreachable, Host = "adfs.corp.contoso.com" }, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(banner?.Sentence, Does.Contain("adfs.corp.contoso.com"));
            Assert.That(banner?.OfferToTrust, Is.False);
        }
    }

    [Test]
    public void Banner_TrustedByAStoreCertificateExpiringWithin30Days_SaysWhenSignInWillStop()
    {
        var banner = IdentityProviderTrustDisplay.Banner(new IdentityProviderTrustStatus
        {
            State = IdentityProviderTrustState.Trusted,
            Host = "adfs.corp.contoso.com",
            VouchingCertificates = [new IdentityProviderCertificate { CommonName = "Contoso Issuing CA 01", ValidTo = Now.AddDays(21) }]
        }, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(banner?.Severity, Is.EqualTo(Severity.Info));
            Assert.That(banner?.Sentence, Does.Contain("Contoso Issuing CA 01").And.Contain("08 Nov 2026"));
            Assert.That(banner?.OfferToTrust, Is.False);
            Assert.That(banner?.LinkToCertificates, Is.True);
        }
    }

    [Test]
    public void Banner_TrustedByAStoreCertificateWithYearsLeft_Nothing()
    {
        var banner = IdentityProviderTrustDisplay.Banner(new IdentityProviderTrustStatus
        {
            State = IdentityProviderTrustState.Trusted,
            VouchingCertificates = [new IdentityProviderCertificate { CommonName = "Contoso Root CA", ValidTo = Now.AddYears(5) }]
        }, Now);

        Assert.That(banner, Is.Null);
    }
}
