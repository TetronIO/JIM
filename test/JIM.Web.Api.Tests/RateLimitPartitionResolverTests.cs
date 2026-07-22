// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using JIM.Models.Core;
using JIM.Web.Middleware.Api;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="RateLimitPartitionResolver"/>: the pure partition-selection logic behind JIM's REST API
/// rate limiting (issue #500). Drives the resolver with a plain <see cref="DefaultHttpContext"/> so the pipeline
/// and the real ASP.NET Core rate limiter are never involved.
/// </summary>
[TestFixture]
public class RateLimitPartitionResolverTests
{
    private static readonly RateLimitSettingsSnapshot EnabledSettings =
        new(Enabled: true, AuthenticatedRequestsPerMinute: 300, UnauthenticatedRequestsPerMinute: 30, DateTime.UtcNow);

    private static readonly RateLimitSettingsSnapshot DisabledSettings =
        new(Enabled: false, AuthenticatedRequestsPerMinute: 300, UnauthenticatedRequestsPerMinute: 30, DateTime.UtcNow);

    [Test]
    public void Resolve_NonApiPath_ReturnsNoLimiter()
    {
        var context = BuildContext("/admin/settings");

        var decision = RateLimitPartitionResolver.Resolve(context, EnabledSettings);

        Assert.That(decision.Kind, Is.EqualTo(RateLimitPartitionKind.NoLimiter));
    }

    [Test]
    public void Resolve_BlazorHubPath_ReturnsNoLimiter()
    {
        var context = BuildContext("/_blazor");

        var decision = RateLimitPartitionResolver.Resolve(context, EnabledSettings);

        Assert.That(decision.Kind, Is.EqualTo(RateLimitPartitionKind.NoLimiter));
    }

    [TestCase("/api/v1/health")]
    [TestCase("/api/v1/health/ready")]
    [TestCase("/api/v1/health/live")]
    [TestCase("/api/v2/health")]
    public void Resolve_HealthCheckPath_ReturnsNoLimiter(string path)
    {
        var context = BuildContext(path);

        var decision = RateLimitPartitionResolver.Resolve(context, EnabledSettings);

        Assert.That(decision.Kind, Is.EqualTo(RateLimitPartitionKind.NoLimiter));
    }

    [Test]
    public void Resolve_HealthLookalikePath_IsNotExemptedAsHealthCheck()
    {
        // "healthreport" is a different controller/resource to "health"; only an exact "health" segment counts.
        var context = BuildContext("/api/v1/healthreport");
        SetAuthenticated(context, metaverseObjectId: null, nameIdentifier: null, name: null);

        var decision = RateLimitPartitionResolver.Resolve(context, EnabledSettings);

        Assert.That(decision.Kind, Is.Not.EqualTo(RateLimitPartitionKind.NoLimiter));
    }

    [Test]
    public void Resolve_RateLimitingDisabled_ReturnsNoLimiterEvenForApiPath()
    {
        var context = BuildContext("/api/v1/connected-systems");
        SetAuthenticated(context, metaverseObjectId: Guid.NewGuid().ToString(), nameIdentifier: null, name: null);

        var decision = RateLimitPartitionResolver.Resolve(context, DisabledSettings);

        Assert.That(decision.Kind, Is.EqualTo(RateLimitPartitionKind.NoLimiter));
    }

    [Test]
    public void Resolve_RateLimitingDisabled_ReturnsNoLimiterForUnauthenticatedApiPath()
    {
        var context = BuildContext("/api/v1/connected-systems");

        var decision = RateLimitPartitionResolver.Resolve(context, DisabledSettings);

        Assert.That(decision.Kind, Is.EqualTo(RateLimitPartitionKind.NoLimiter));
    }

    [Test]
    public void Resolve_AuthenticatedRequestWithMetaverseObjectIdClaim_UsesSlidingWindowKeyedByMvoId()
    {
        var mvoId = Guid.NewGuid().ToString();
        var context = BuildContext("/api/v1/connected-systems");
        SetAuthenticated(context, metaverseObjectId: mvoId, nameIdentifier: "should-not-be-used", name: "should-not-be-used");

        var decision = RateLimitPartitionResolver.Resolve(context, EnabledSettings);

        Assert.That(decision.Kind, Is.EqualTo(RateLimitPartitionKind.AuthenticatedSlidingWindow));
        Assert.That(decision.PartitionKey, Is.EqualTo($"auth:{mvoId}:300"));
        Assert.That(decision.PermitLimit, Is.EqualTo(300));
        Assert.That(decision.Window, Is.EqualTo(RateLimitPartitionResolver.Window));
    }

    [Test]
    public void Resolve_AuthenticatedRequestWithoutMvoIdClaim_FallsBackToNameIdentifierClaim()
    {
        // Mirrors an API-key-authenticated principal: ApiKeyAuthenticationHandler sets ClaimTypes.NameIdentifier
        // to the API key's id, but never a Metaverse Object ID claim (that is only attached for SSO/JWT users).
        var apiKeyId = Guid.NewGuid().ToString();
        var context = BuildContext("/api/v1/connected-systems");
        SetAuthenticated(context, metaverseObjectId: null, nameIdentifier: apiKeyId, name: "My API Key");

        var decision = RateLimitPartitionResolver.Resolve(context, EnabledSettings);

        Assert.That(decision.Kind, Is.EqualTo(RateLimitPartitionKind.AuthenticatedSlidingWindow));
        Assert.That(decision.PartitionKey, Is.EqualTo($"auth:{apiKeyId}:300"));
    }

    [Test]
    public void Resolve_AuthenticatedRequestWithNoStableIdClaims_FallsBackToIdentityName()
    {
        var context = BuildContext("/api/v1/connected-systems");
        SetAuthenticated(context, metaverseObjectId: null, nameIdentifier: null, name: "someone");

        var decision = RateLimitPartitionResolver.Resolve(context, EnabledSettings);

        Assert.That(decision.Kind, Is.EqualTo(RateLimitPartitionKind.AuthenticatedSlidingWindow));
        Assert.That(decision.PartitionKey, Is.EqualTo("auth:someone:300"));
    }

    [Test]
    public void Resolve_AuthenticatedInfrastructureKey_ReturnsNoLimiter()
    {
        // Infrastructure API keys are trusted backend automation (CI/CD, integration testing, bulk configuration),
        // authenticated from a pre-shared bootstrap secret and holding the Administrator role. The rate limiter
        // exists to blunt untrusted/interactive/runaway abuse, not to throttle trusted automation, which
        // legitimately bursts far past the per-principal cap. Such principals are therefore fully exempt.
        var apiKeyId = Guid.NewGuid().ToString();
        var context = BuildContext("/api/v1/synchronisation/sync-rules");
        SetAuthenticated(context, metaverseObjectId: null, nameIdentifier: apiKeyId, name: "Infrastructure Key", isInfrastructureKey: true);

        var decision = RateLimitPartitionResolver.Resolve(context, EnabledSettings);

        Assert.That(decision.Kind, Is.EqualTo(RateLimitPartitionKind.NoLimiter));
    }

    [Test]
    public void Resolve_AuthenticatedNonInfrastructureKey_IsStillRateLimited()
    {
        // The exemption is scoped strictly to the infrastructure-key claim; an ordinary API key principal that
        // happens to share the code path must remain throttled.
        var apiKeyId = Guid.NewGuid().ToString();
        var context = BuildContext("/api/v1/synchronisation/sync-rules");
        SetAuthenticated(context, metaverseObjectId: null, nameIdentifier: apiKeyId, name: "Ordinary Key", isInfrastructureKey: false);

        var decision = RateLimitPartitionResolver.Resolve(context, EnabledSettings);

        Assert.That(decision.Kind, Is.EqualTo(RateLimitPartitionKind.AuthenticatedSlidingWindow));
    }

    [Test]
    public void Resolve_UnauthenticatedApiRequest_UsesFixedWindowKeyedByClientIp()
    {
        var context = BuildContext("/api/v1/connected-systems");
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");

        var decision = RateLimitPartitionResolver.Resolve(context, EnabledSettings);

        Assert.That(decision.Kind, Is.EqualTo(RateLimitPartitionKind.UnauthenticatedFixedWindow));
        Assert.That(decision.PartitionKey, Is.EqualTo("unauth:203.0.113.7:30"));
        Assert.That(decision.PermitLimit, Is.EqualTo(30));
        Assert.That(decision.Window, Is.EqualTo(RateLimitPartitionResolver.Window));
    }

    [Test]
    public void Resolve_FailedApiKeyAuthentication_IsTreatedAsUnauthenticated()
    {
        // A failed ApiKeyAuthenticationHandler result leaves HttpContext.User unauthenticated (AuthenticateResult.Fail
        // does not throw); such requests must still be throttled per-IP, not skipped entirely.
        var context = BuildContext("/api/v1/connected-systems");
        context.User = new ClaimsPrincipal(new ClaimsIdentity()); // unauthenticated: no authenticationType
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.9");

        var decision = RateLimitPartitionResolver.Resolve(context, EnabledSettings);

        Assert.That(decision.Kind, Is.EqualTo(RateLimitPartitionKind.UnauthenticatedFixedWindow));
        Assert.That(decision.PartitionKey, Does.StartWith("unauth:198.51.100.9:"));
    }

    [Test]
    public void Resolve_PartitionKey_ChangesWhenConfiguredAuthenticatedLimitChanges()
    {
        var mvoId = Guid.NewGuid().ToString();
        var context = BuildContext("/api/v1/connected-systems");
        SetAuthenticated(context, metaverseObjectId: mvoId, nameIdentifier: null, name: null);
        var higherLimitSettings = EnabledSettings with { AuthenticatedRequestsPerMinute = 500 };

        var originalDecision = RateLimitPartitionResolver.Resolve(context, EnabledSettings);
        var changedDecision = RateLimitPartitionResolver.Resolve(context, higherLimitSettings);

        // PartitionedRateLimiter.Create caches limiter instances per key; a settings change must produce a
        // different key so a stale cached limiter is never reused with the old limit.
        Assert.That(originalDecision.PartitionKey, Is.Not.EqualTo(changedDecision.PartitionKey));
    }

    [Test]
    public void Resolve_PartitionKey_ChangesWhenConfiguredUnauthenticatedLimitChanges()
    {
        var context = BuildContext("/api/v1/connected-systems");
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        var higherLimitSettings = EnabledSettings with { UnauthenticatedRequestsPerMinute = 60 };

        var originalDecision = RateLimitPartitionResolver.Resolve(context, EnabledSettings);
        var changedDecision = RateLimitPartitionResolver.Resolve(context, higherLimitSettings);

        Assert.That(originalDecision.PartitionKey, Is.Not.EqualTo(changedDecision.PartitionKey));
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private static DefaultHttpContext BuildContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        return context;
    }

    private static void SetAuthenticated(HttpContext context, string? metaverseObjectId, string? nameIdentifier, string? name, bool isInfrastructureKey = false)
    {
        var claims = new List<Claim>();
        if (metaverseObjectId != null)
            claims.Add(new Claim(Constants.BuiltInClaims.MetaverseObjectId, metaverseObjectId));
        if (nameIdentifier != null)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, nameIdentifier));
        if (name != null)
            claims.Add(new Claim(ClaimTypes.Name, name));
        if (isInfrastructureKey)
            claims.Add(new Claim(Constants.BuiltInClaims.IsInfrastructureKey, "true"));

        // A non-null authenticationType is what makes ClaimsIdentity.IsAuthenticated true.
        var identity = new ClaimsIdentity(claims, authenticationType: "Test", nameType: ClaimTypes.Name, roleType: null);
        context.User = new ClaimsPrincipal(identity);
    }
}
