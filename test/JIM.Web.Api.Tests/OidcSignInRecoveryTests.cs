// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Web.Authentication;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// A remote OIDC sign-in failure is either recoverable by simply signing in again (a replayed callback whose
/// single-use code the IdP has already spent, or a lost correlation cookie) or it is not (the IdP refused the
/// user, or the configuration is wrong). These tests pin the line between the two, because redirecting the
/// unrecoverable cases would turn a loud, diagnosable failure into a silent sign-in loop.
/// </summary>
[TestFixture]
public class OidcSignInRecoveryTests
{
    [Test]
    public void ShouldRestartSignIn_NullFailure_DoesNotRestart()
    {
        Assert.That(OidcSignInRecovery.ShouldRestartSignIn(null), Is.False);
    }

    [Test]
    public void ShouldRestartSignIn_SpentAuthorisationCode_Restarts()
    {
        // The exact shape Keycloak produces for a replayed /signin-oidc callback: the code is single-use,
        // so a restored tab, back button or refreshed error page re-submitting it gets invalid_grant.
        var failure = new OpenIdConnectProtocolException(
            "Message contains error: 'invalid_grant', error_description: 'Code not valid', error_uri: 'error_uri is null'.");

        Assert.That(OidcSignInRecovery.ShouldRestartSignIn(failure), Is.True);
    }

    [Test]
    public void ShouldRestartSignIn_LostCorrelationCookie_Restarts()
    {
        // The ASP.NET Core handler raises this when the callback arrives without the correlation cookie the
        // challenge set, e.g. after cookies were cleared mid-flow; a fresh challenge recovers it completely.
        var failure = new Exception("Correlation failed.");

        Assert.That(OidcSignInRecovery.ShouldRestartSignIn(failure), Is.True);
    }

    [Test]
    public void ShouldRestartSignIn_CallbackWithoutAMessage_Restarts()
    {
        // A bare navigation to /signin-oidc (a bookmark, or stepping through history to the callback) carries
        // none of the protocol fields; nothing is wrong with the user or the configuration.
        var failure = new Exception("OpenIdConnectAuthenticationHandler: message.State is null or empty.");

        Assert.That(OidcSignInRecovery.ShouldRestartSignIn(failure), Is.True);
    }

    [Test]
    public void ShouldRestartSignIn_AccessDenied_DoesNotRestart()
    {
        // The IdP refused the user; retrying would loop them between JIM and the provider forever.
        var failure = new OpenIdConnectProtocolException(
            "Message contains error: 'access_denied', error_description: 'User declined consent'.");

        Assert.That(OidcSignInRecovery.ShouldRestartSignIn(failure), Is.False);
    }

    [Test]
    public void ShouldRestartSignIn_UnrecognisedFailure_DoesNotRestart()
    {
        Assert.That(OidcSignInRecovery.ShouldRestartSignIn(new InvalidOperationException("IDX20803: unable to obtain configuration")), Is.False);
    }

    [Test]
    public void GetSafeReturnPath_NoReturnUrl_FallsBackToTheRoot()
    {
        Assert.That(OidcSignInRecovery.GetSafeReturnPath(null), Is.EqualTo("/"));
    }

    [Test]
    public void GetSafeReturnPath_LocalPath_IsKept()
    {
        Assert.That(OidcSignInRecovery.GetSafeReturnPath("/admin/connected-systems"), Is.EqualTo("/admin/connected-systems"));
    }

    [Test]
    public void GetSafeReturnPath_ProtocolRelativeUrl_FallsBackToTheRoot()
    {
        // "//evil.example" is protocol-relative: the browser would leave the site. Never redirect off-host.
        Assert.That(OidcSignInRecovery.GetSafeReturnPath("//evil.example"), Is.EqualTo("/"));
    }

    [Test]
    public void GetSafeReturnPath_AbsoluteUrl_FallsBackToTheRoot()
    {
        Assert.That(OidcSignInRecovery.GetSafeReturnPath("https://evil.example/"), Is.EqualTo("/"));
    }
}
