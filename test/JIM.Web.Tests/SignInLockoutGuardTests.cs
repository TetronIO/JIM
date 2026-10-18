// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Removing the one certificate sign-in depends on stops everyone signing in, the person doing it included (#2363).
/// The portal warns before that and asks for the certificate's name; it does not block, because replacing a
/// certificate is routine.
/// </summary>
[TestFixture]
public class SignInLockoutGuardTests
{
    private static readonly Guid Certificate = Guid.NewGuid();

    [Test]
    public void WouldStopSignIn_DeletingTheOnlyVouchingCertificate_True()
    {
        Assert.That(SignInLockoutGuard.WouldStopSignIn(DependsOn(Certificate), Certificate, deleting: true), Is.True);
    }

    [Test]
    public void WouldStopSignIn_DisablingIt_True()
    {
        Assert.That(SignInLockoutGuard.WouldStopSignIn(DependsOn(Certificate), Certificate, enabledAfter: false), Is.True);
    }

    [Test]
    public void WouldStopSignIn_NoLongerTrustingItForSignIn_True()
    {
        Assert.That(SignInLockoutGuard.WouldStopSignIn(DependsOn(Certificate), Certificate, trustedForAfter: CertificateTrustPurpose.Connectors), Is.True);
    }

    [Test]
    public void WouldStopSignIn_RenamingIt_False()
    {
        Assert.That(SignInLockoutGuard.WouldStopSignIn(DependsOn(Certificate), Certificate, enabledAfter: true, trustedForAfter: CertificateTrustPurpose.SignIn), Is.False);
    }

    [Test]
    public void WouldStopSignIn_AnotherCertificateAlsoVouches_False()
    {
        var status = new IdentityProviderTrustStatus
        {
            State = IdentityProviderTrustState.Trusted,
            VouchingCertificates = [new IdentityProviderCertificate { TrustedCertificateId = Certificate }, new IdentityProviderCertificate { TrustedCertificateId = Guid.NewGuid() }]
        };

        Assert.That(SignInLockoutGuard.WouldStopSignIn(status, Certificate, deleting: true), Is.False);
    }

    [Test]
    public void WouldStopSignIn_CertificateSignInDoesNotUse_False()
    {
        Assert.That(SignInLockoutGuard.WouldStopSignIn(DependsOn(Guid.NewGuid()), Certificate, deleting: true), Is.False);
    }

    private static IdentityProviderTrustStatus DependsOn(Guid certificateId) => new()
    {
        State = IdentityProviderTrustState.Trusted,
        VouchingCertificates = [new IdentityProviderCertificate { TrustedCertificateId = certificateId }]
    };
}
