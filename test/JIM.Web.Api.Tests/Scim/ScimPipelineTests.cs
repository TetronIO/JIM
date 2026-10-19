// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Scim;
using JIM.TestSupport;
using JIM.Web.Authentication;
using JIM.Web.Middleware.Api;
using JIM.Web.Scim;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests.Scim;

/// <summary>
/// The <c>/scim</c> request pipeline (#124, Phase 4b; D3, D15, D16): dark while the feature flag is off, a single
/// indistinguishable 401 for every credential that is not a key scoped to the Connected System in the URL (with the
/// real reason audited), and SCIM-shaped errors rather than the portal's or the REST API's.
/// </summary>
[TestFixture]
public class ScimPipelineTests
{
    private Mock<IFailedAuthenticationRecorder> _recorder = null!;

    [SetUp]
    public void SetUp()
    {
        _recorder = new Mock<IFailedAuthenticationRecorder>();
        _recorder.Setup(r => r.RecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
    }

    #region Feature gate

    [Test]
    public async Task FeatureGate_FlagOff_AnswersScim404AndGoesNoFurtherAsync()
    {
        var context = Request("/scim/v2/7/Users");
        var reachedNext = false;

        await new ScimFeatureGateMiddleware(_ => { reachedNext = true; return Task.CompletedTask; })
            .InvokeAsync(context, Factory(flagOn: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reachedNext, Is.False);
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(context.Response.ContentType, Does.StartWith(ScimResponses.ContentType));
            Assert.That(Body(context)?["schemas"]?[0]?.GetValue<string>(), Is.EqualTo(ScimUrns.Error));
        }
    }

    [Test]
    public async Task FeatureGate_FlagOn_PassesTheRequestOnAsync()
    {
        var context = Request("/scim/v2/7/Users");
        var reachedNext = false;

        await new ScimFeatureGateMiddleware(_ => { reachedNext = true; return Task.CompletedTask; })
            .InvokeAsync(context, Factory(flagOn: true));

        Assert.That(reachedNext, Is.True);
    }

    #endregion

    #region Secure transport

    [Test]
    public async Task SecureTransport_PlainHttpOutsideDevelopment_Scim403BeforeTheKeyIsReadAsync()
    {
        var context = Request("/scim/v2/7/Users", authorization: "Bearer jim_ak_presented");
        context.Request.IsHttps = false;
        var reachedNext = false;

        await new ScimSecureTransportMiddleware(_ => { reachedNext = true; return Task.CompletedTask; })
            .InvokeAsync(context, Environment(development: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reachedNext, Is.False);
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
            Assert.That(Body(context)?["detail"]?.GetValue<string>(), Does.Contain("JIM_TRUSTED_PROXIES"));
        }
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task SecureTransport_HttpsOrDevelopment_PassesTheRequestOnAsync(bool isHttps, bool development)
    {
        var context = Request("/scim/v2/7/Users");
        context.Request.IsHttps = isHttps;
        var reachedNext = false;

        await new ScimSecureTransportMiddleware(_ => { reachedNext = true; return Task.CompletedTask; })
            .InvokeAsync(context, Environment(development));

        Assert.That(reachedNext, Is.True);
    }

    private static Microsoft.AspNetCore.Hosting.IWebHostEnvironment Environment(bool development)
    {
        var environment = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(development ? "Development" : "Production");
        return environment.Object;
    }

    #endregion

    #region Scope against the route

    [TestCase(7, true)]
    [TestCase(8, false)]
    public async Task ScopeRequirement_KeyScopedToTheRoutesConnectedSystem_OnlyThenSucceedsAsync(int routeConnectedSystemId, bool expected)
    {
        var context = Request($"/scim/v2/{routeConnectedSystemId}/Users", routeConnectedSystemId);

        var succeeded = await EvaluateScopeAsync(ScopedPrincipal(connectedSystemId: 7), context);

        Assert.That(succeeded, Is.EqualTo(expected));
    }

    [Test]
    public async Task ScopeRequirement_UnscopedAdministratorKey_FailsAsync()
    {
        // Least privilege: an Administrator key opens all of /api and none of /scim.
        var principal = Principal(new ApiKey { Id = Guid.NewGuid(), Name = "Automation", KeyPrefix = "jim_ak_aaaa",
            Roles = [new Role { Name = Constants.BuiltInRoles.Administrator }] });

        var succeeded = await EvaluateScopeAsync(principal, Request("/scim/v2/7/Users", 7));

        Assert.That(succeeded, Is.False);
    }

    #endregion

    #region One 401 for every refusal

    [Test]
    public async Task ResultHandler_NoCredential_401WithTheScimChallengeAndNoErrorParameterAsync()
    {
        var context = Request("/scim/v2/7/Users", 7);

        await HandleAsync(context, PolicyAuthorizationResult.Challenge());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
            Assert.That(context.Response.Headers.WWWAuthenticate.ToString(), Is.EqualTo("Bearer realm=\"JIM SCIM\""));
            Assert.That(Body(context)?["status"]?.GetValue<string>(), Is.EqualTo("401"));
        }
    }

    [Test]
    public async Task ResultHandler_KeyForAnotherConnectedSystem_TheSame401AndTheReasonAuditedAsync()
    {
        var context = Request("/scim/v2/8/Users", 8, authorization: "Bearer jim_ak_presented");
        context.User = ScopedPrincipal(connectedSystemId: 7);

        await HandleAsync(context, PolicyAuthorizationResult.Forbid());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
            Assert.That(context.Response.Headers.WWWAuthenticate.ToString(), Is.EqualTo("Bearer realm=\"JIM SCIM\", error=\"invalid_token\""));
            _recorder.Verify(r => r.RecordAsync(FailedAuthenticationEventNames.ApiKey, "API key is for another Connected System",
                "jim_ak_efgh", It.IsAny<string?>()), Times.Once);
        }
    }

    [Test]
    public async Task ResultHandler_UnscopedKey_TheSame401AndTheReasonAuditedAsync()
    {
        var context = Request("/scim/v2/7/Users", 7, authorization: "Bearer jim_ak_presented");
        context.User = Principal(new ApiKey { Id = Guid.NewGuid(), Name = "Automation", KeyPrefix = "jim_ak_aaaa" });

        await HandleAsync(context, PolicyAuthorizationResult.Forbid());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
            _recorder.Verify(r => r.RecordAsync(FailedAuthenticationEventNames.ApiKey, "API key is not scoped to a SCIM endpoint",
                "jim_ak_aaaa", It.IsAny<string?>()), Times.Once);
        }
    }

    [Test]
    public async Task ResultHandler_OutsideScim_LeftToTheDefaultHandlerAsync()
    {
        var context = Request("/api/v1/userinfo");
        var fallback = new Mock<IAuthorizationMiddlewareResultHandler>();

        await new ScimAuthorisationResultHandler(_recorder.Object, fallback.Object)
            .HandleAsync(_ => Task.CompletedTask, context, new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(),
                PolicyAuthorizationResult.Challenge());

        fallback.Verify(f => f.HandleAsync(It.IsAny<RequestDelegate>(), context, It.IsAny<AuthorizationPolicy>(),
            It.IsAny<PolicyAuthorizationResult>()), Times.Once);
    }

    #endregion

    #region Errors

    [Test]
    public async Task ExceptionMiddleware_UnhandledException_Scim500WithoutDetailAsync()
    {
        var context = Request("/scim/v2/7/Users");

        await new ScimExceptionMiddleware(_ => throw new InvalidOperationException("secret internals")).InvokeAsync(context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status500InternalServerError));
            Assert.That(context.Response.ContentType, Does.StartWith(ScimResponses.ContentType));
            Assert.That(Body(context)?.ToJsonString(), Does.Not.Contain("secret internals"));
        }
    }

    [Test]
    public async Task RateLimitRejection_OnScim_Scim429WithRetryAfterAsync()
    {
        var context = Request("/scim/v2/7/Users");
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();

        await RateLimitingExtensions.WriteRejectionAsync(context, retryAfterSeconds: 12, System.Threading.CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status429TooManyRequests));
            Assert.That(context.Response.Headers.RetryAfter.ToString(), Is.EqualTo("12"));
            Assert.That(context.Response.ContentType, Does.StartWith(ScimResponses.ContentType));
            Assert.That(Body(context)?["status"]?.GetValue<string>(), Is.EqualTo("429"));
        }
    }

    #endregion

    #region helpers

    private async Task HandleAsync(HttpContext context, PolicyAuthorizationResult result) =>
        await new ScimAuthorisationResultHandler(_recorder.Object, new AuthorizationMiddlewareResultHandler())
            .HandleAsync(_ => Task.CompletedTask, context, new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(), result);

    private static async Task<bool> EvaluateScopeAsync(ClaimsPrincipal principal, HttpContext context)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJimAuthorisation();
        var provider = services.BuildServiceProvider();
        var policy = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(ScimRoutes.AuthorisationPolicy);
        return (await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(principal, context, policy!)).Succeeded;
    }

    private static DefaultHttpContext Request(string path, int? connectedSystemId = null, string? authorization = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (connectedSystemId is { } id)
            context.Request.RouteValues[ScimRoutes.ConnectedSystemIdRouteValue] = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (authorization != null)
            context.Request.Headers.Authorization = authorization;
        return context;
    }

    private static JsonNode? Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        var text = new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEnd();
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    private static IJimApplicationFactory Factory(bool flagOn)
    {
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ServiceSettings).Returns(flagOn
            ? InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled()
            : new InMemoryServiceSettingsRepository());
        var factory = new Mock<IJimApplicationFactory>();
        factory.Setup(f => f.Create()).Returns(() => new JimApplication(repository.Object));
        return factory.Object;
    }

    private static ClaimsPrincipal ScopedPrincipal(int connectedSystemId) => Principal(new ApiKey
    {
        Id = Guid.NewGuid(),
        Name = "Entra provisioning",
        KeyPrefix = "jim_ak_efgh",
        Scope = ApiKeyScope.ScimServiceProvider,
        ScopeConnectedSystemId = connectedSystemId
    });

    private static ClaimsPrincipal Principal(ApiKey apiKey) =>
        new(new ClaimsIdentity(ApiKeyAuthenticationHandler.BuildApiKeyClaims(apiKey), ApiKeyAuthenticationHandler.SchemeName,
            nameType: ClaimTypes.Name, roleType: Constants.BuiltInRoles.RoleClaimType));

    #endregion
}
