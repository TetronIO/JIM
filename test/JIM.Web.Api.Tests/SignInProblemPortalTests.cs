// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Web.Authentication;
using JIM.Web.Pages;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// A portal sign-in a setting stopped (#2443) goes to a page naming the setting, the value JIM expected and the value it
/// received, instead of the error page. Everything else is left to <see cref="OidcSignInRecovery"/> and the error page.
/// The page is anonymous, since whoever lands there could not sign in, and shows only portal problems: those values
/// passed through the visitor's own browser.
/// </summary>
[TestFixture]
public class SignInProblemPortalTests
{
    private SignInProblemLog _problems = null!;

    [SetUp]
    public void SetUp()
    {
        _problems = new SignInProblemLog(TimeProvider.System);
    }

    [Test]
    public void TryHandleRemoteFailure_FailureASettingExplains_RecordsItAndShowsThePageNamingIt()
    {
        var context = RemoteFailure(new SecurityTokenInvalidAudienceException("IDX10214") { InvalidAudience = "another-client" });

        var handled = SignInProblemDiagnosis.TryHandleRemoteFailure(context);

        var problem = _problems.GetRecent().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(handled, Is.True);
            Assert.That(context.Result?.Handled, Is.True);
            Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo($"/sign-in-problem?id={problem.Id}"));
            Assert.That(problem.Setting, Is.EqualTo("JIM_SSO_CLIENT_ID"));
        }
    }

    [Test]
    public void TryHandleRemoteFailure_FailureNoSettingExplains_LeftAlone()
    {
        var failure = new OpenIdConnectProtocolException("Message contains error: 'access_denied', error_description: 'error_description is null', error_uri: 'error_uri is null'.");
        var context = RemoteFailure(failure);

        var handled = SignInProblemDiagnosis.TryHandleRemoteFailure(context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handled, Is.False);
            Assert.That(context.Result, Is.Null);
            Assert.That(_problems.GetRecent(), Is.Empty);
        }
    }

    [Test]
    public void PagePathFor_Problem_CarriesOnlyItsIdentifier()
    {
        var problem = _problems.Record(PortalAudience());

        Assert.That(SignInProblemDiagnosis.PagePathFor(problem), Is.EqualTo($"/sign-in-problem?id={problem.Id}"));
    }

    [Test]
    public void SignInProblemModel_AllowsAnonymousAccess()
    {
        Assert.That(typeof(SignInProblemModel).IsDefined(typeof(AllowAnonymousAttribute), inherit: true), Is.True);
    }

    [Test]
    public void OnGet_PortalProblem_ShowsIt()
    {
        var problem = _problems.Record(PortalAudience());
        var model = Page();

        model.OnGet(problem.Id);

        Assert.That(model.Problem?.Received, Is.EqualTo("another-client"));
    }

    [Test]
    public void OnGet_ApiProblem_NotShown()
    {
        var problem = _problems.Record(PortalAudience() with { Source = SignInProblemSource.Api });
        var model = Page();

        model.OnGet(problem.Id);

        Assert.That(model.Problem, Is.Null);
    }

    [Test]
    public void OnGet_ProblemJimNoLongerHolds_ShowsNothing()
    {
        var model = Page();

        model.OnGet(Guid.NewGuid());

        Assert.That(model.Problem, Is.Null);
    }

    [Test]
    public void OnGet_NoIdentifier_ShowsNothing()
    {
        var model = Page();

        model.OnGet(null);

        Assert.That(model.Problem, Is.Null);
    }

    private SignInProblemModel Page() => new(_problems) { PageContext = new PageContext { HttpContext = new DefaultHttpContext() } };

    private static SignInProblemReport PortalAudience() => new(
        SignInProblemSource.Portal, "JIM_SSO_CLIENT_ID", "The ID token is for another client.", "jim-web", "another-client", "Set it.");

    private RemoteFailureContext RemoteFailure(Exception failure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_problems);
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var scheme = new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler));
        return new RemoteFailureContext(httpContext, scheme, new OpenIdConnectOptions { ClientId = "jim-web" }, failure);
    }
}
