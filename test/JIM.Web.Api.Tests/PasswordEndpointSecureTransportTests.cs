// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JIM.Web.Controllers.Api;
using JIM.Web.Middleware.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Requirement 34's transport half: a REST endpoint that accepts a password refuses the request unless JIM can
/// confirm the transport is secure (#1119).
/// <para>
/// Until now the deployment's own HTTPS enforcement was the only guard, which is not the same thing: an operator
/// who has not enabled it, or who terminates TLS at a proxy JIM has not been told to trust, sends passwords over
/// the wire in the clear and nothing says so. Authorisation and the never-log invariant were already met; this
/// is the piece that was missing.
/// </para>
/// <para>
/// Enforced by a filter with a completeness guard behind it, following the pagination depth cap precedent: a new
/// password endpoint is protected because it carries a password, not because its author remembered.
/// </para>
/// </summary>
[TestFixture]
public class PasswordEndpointSecureTransportTests
{
    #region The filter itself

    private static ActionExecutingContext BuildContext(bool isHttps, bool development)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.IsHttps = isHttps;

        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(development ? Environments.Development : Environments.Production);

        var services = new Mock<IServiceProvider>();
        services.Setup(s => s.GetService(typeof(IWebHostEnvironment))).Returns(environment.Object);
        httpContext.RequestServices = services.Object;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller: null!);
    }

    [Test]
    public void Filter_OverHttps_AllowsTheRequest()
    {
        var context = BuildContext(isHttps: true, development: false);

        new RequireSecureTransportAttribute().OnActionExecuting(context);

        Assert.That(context.Result, Is.Null, "A secure transport is the whole point; it must pass through.");
    }

    [Test]
    public void Filter_OverHttp_RefusesTheRequest()
    {
        var context = BuildContext(isHttps: false, development: false);

        new RequireSecureTransportAttribute().OnActionExecuting(context);

        Assert.That(context.Result, Is.InstanceOf<ObjectResult>());
        var result = (ObjectResult)context.Result!;
        Assert.That(result.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden),
            "The request is understood and well-formed; what is refused is carrying a password over this transport.");
    }

    [Test]
    public void Filter_OverHttp_NamesTheRemedyIncludingTheProxyCase()
    {
        // The likeliest cause of a legitimate deployment hitting this is TLS terminating at a proxy JIM has not
        // been told to trust, in which case JIM cannot see the real scheme. An error that does not say so turns
        // a five-minute configuration fix into a support case.
        var context = BuildContext(isHttps: false, development: false);

        new RequireSecureTransportAttribute().OnActionExecuting(context);

        var body = ((ObjectResult)context.Result!).Value as ApiErrorResponse;
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Message, Does.Contain("JIM_TRUSTED_PROXIES"));
    }

    [Test]
    public void Filter_OverHttpInDevelopment_AllowsTheRequest()
    {
        // The development stack runs over plain HTTP, and a rule that made setting a password impossible there
        // would be worked around rather than obeyed. The exemption cannot hold in a released image, because the
        // environment is Production unless somebody sets it otherwise.
        var context = BuildContext(isHttps: false, development: true);

        new RequireSecureTransportAttribute().OnActionExecuting(context);

        Assert.That(context.Result, Is.Null);
    }

    #endregion

    #region Completeness

    private static IEnumerable<MethodInfo> GetApiActions()
    {
        return typeof(MetaverseController).Assembly
            .GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any());
    }

    /// <summary>
    /// Whether an action takes a password in its request body. Read off the bound type's properties rather than
    /// a list of endpoint names, so an endpoint added later is covered by having a password rather than by being
    /// remembered here.
    /// </summary>
    private static bool AcceptsAPassword(MethodInfo action)
    {
        return action.GetParameters().Any(p =>
            p.ParameterType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(property => property.PropertyType == typeof(string) &&
                                 property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)));
    }

    [Test]
    public void EveryActionAcceptingAPassword_RequiresASecureTransport()
    {
        var unprotected = GetApiActions()
            .Where(AcceptsAPassword)
            .Where(a => !a.GetCustomAttributes<RequireSecureTransportAttribute>(inherit: true).Any() &&
                        a.DeclaringType?.GetCustomAttributes<RequireSecureTransportAttribute>(inherit: true).Any() != true)
            .Select(a => $"{a.DeclaringType!.Name}.{a.Name}")
            .OrderBy(name => name)
            .ToList();

        Assert.That(unprotected, Is.Empty,
            "These endpoints accept a password and would carry it over whatever transport the caller used. " +
            "Add [RequireSecureTransport], or rename the property if it does not really carry a password.");
    }

    /// <summary>
    /// The guard is worthless if it matches nothing: a rename or a refactor that stopped it recognising password
    /// endpoints would leave it passing while covering nobody.
    /// </summary>
    [Test]
    public void TheCompletenessGuard_RecognisesTheEndpointsItIsMeantToCover()
    {
        var covered = GetApiActions().Where(AcceptsAPassword).Select(a => a.Name).ToList();

        Assert.That(covered, Has.Count.GreaterThanOrEqualTo(1));
        Assert.That(covered, Has.Some.Contain("SetConnectedSystemObjectPassword"),
            "Setting a password on one account is the endpoint this guard most obviously has to cover.");
    }

    #endregion
}
