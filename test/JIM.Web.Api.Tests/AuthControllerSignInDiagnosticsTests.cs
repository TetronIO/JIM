// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Reflection;
using JIM.Web.Authentication;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The sign-in problems JIM has met, for administrators (#2443), with the redirect URIs JIM sends the identity provider
/// when reached at the address the caller used, to compare with those it has registered.
/// </summary>
[TestFixture]
public class AuthControllerSignInDiagnosticsTests
{
    private SignInProblemLog _problems = null!;
    private AuthController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _problems = new SignInProblemLog(TimeProvider.System);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("jim.example.com");
        httpContext.Request.PathBase = "/jim";
        _controller = new AuthController { ControllerContext = new ControllerContext { HttpContext = httpContext } };
    }

    [Test]
    public void GetSignInDiagnostics_IsForAdministratorsOnly()
    {
        var method = typeof(AuthController).GetMethod(nameof(AuthController.GetSignInDiagnostics))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(method.GetCustomAttribute<AuthorizeAttribute>()?.Roles, Is.EqualTo("Administrator"));
            Assert.That(method.GetCustomAttribute<AllowAnonymousAttribute>(), Is.Null);
        }
    }

    [Test]
    public void GetSignInDiagnostics_RedirectUrisForTheAddressTheCallerUsed()
    {
        var diagnostics = Get();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnostics.RedirectUri, Is.EqualTo("https://jim.example.com/jim/signin-oidc"));
            Assert.That(diagnostics.PostLogoutRedirectUri, Is.EqualTo("https://jim.example.com/jim/signout-callback-oidc"));
        }
    }

    [Test]
    public void GetSignInDiagnostics_NoProblems_EmptyList()
    {
        Assert.That(Get().Problems, Is.Empty);
    }

    [Test]
    public void GetSignInDiagnostics_Problems_EachWithItsSettingAndValues()
    {
        _problems.Record(new SignInProblemReport(SignInProblemSource.Api, "JIM_SSO_API_SCOPE", "Refused for its audience.", "api://jim", "account", "Change it."));
        _problems.Record(new SignInProblemReport(SignInProblemSource.Api, "JIM_SSO_API_SCOPE", "Refused for its audience.", "api://jim", "account", "Change it."));

        var problem = Get().Problems[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.Source, Is.EqualTo(SignInProblemSource.Api));
            Assert.That(problem.Setting, Is.EqualTo("JIM_SSO_API_SCOPE"));
            Assert.That(problem.Description, Is.EqualTo("Refused for its audience."));
            Assert.That(problem.Expected, Is.EqualTo("api://jim"));
            Assert.That(problem.Received, Is.EqualTo("account"));
            Assert.That(problem.Fix, Is.EqualTo("Change it."));
            Assert.That(problem.Count, Is.EqualTo(2));
            Assert.That(problem.LastSeenUtc, Is.GreaterThanOrEqualTo(problem.FirstSeenUtc));
        }
    }

    private SignInDiagnosticsDto Get() => (SignInDiagnosticsDto)((OkObjectResult)_controller.GetSignInDiagnostics(_problems)).Value!;
}
