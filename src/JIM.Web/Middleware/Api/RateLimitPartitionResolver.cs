// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Claims;
using JIM.Models.Core;

namespace JIM.Web.Middleware.Api;

/// <summary>
/// Decides which rate limiting partition a request belongs to. Extracted from the <c>AddRateLimiter</c> global
/// limiter factory in <see cref="RateLimitingExtensions"/> so the selection logic can be unit tested against a
/// plain <see cref="HttpContext"/> (e.g. <c>DefaultHttpContext</c>) without spinning up the ASP.NET Core pipeline
/// or a real <c>PartitionedRateLimiter</c>.
/// </summary>
public static class RateLimitPartitionResolver
{
    /// <summary>Both limiter kinds use a one-minute window; only the permit count is configurable.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Resolves the rate limiting decision for a request.
    /// </summary>
    /// <param name="context">The current request's <see cref="HttpContext"/>. Identity (<see cref="HttpContext.User"/>)
    /// must already be resolved (i.e. this must run after authentication) for principal partitioning to work.</param>
    /// <param name="settings">The current rate limiting Service Settings, as served by <see cref="IRateLimitSettingsCache"/>.</param>
    public static RateLimitDecision Resolve(HttpContext context, RateLimitSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        // The Blazor UI, the SignalR hub, and static assets are untouched by this feature.
        if (!context.Request.Path.StartsWithSegments("/api"))
            return RateLimitDecision.NoLimiter();

        // Load balancer and orchestrator probes must never be throttled; the endpoint is cheap and carries no data.
        if (IsHealthCheckPath(context.Request.Path))
            return RateLimitDecision.NoLimiter();

        if (!settings.Enabled)
            return RateLimitDecision.NoLimiter();

        if (context.User.Identity?.IsAuthenticated == true)
        {
            var principalId = ResolvePrincipalId(context.User);
            // The limit is baked into the partition key: PartitionedRateLimiter.Create caches limiter instances
            // per key, so without this a Service Settings change would not affect an already-cached partition.
            return new RateLimitDecision(
                RateLimitPartitionKind.AuthenticatedSlidingWindow,
                $"auth:{principalId}:{settings.AuthenticatedRequestsPerMinute}",
                settings.AuthenticatedRequestsPerMinute,
                Window);
        }

        // Unauthenticated /api requests, including requests that failed API key authentication (a failed
        // ApiKeyAuthenticationHandler result leaves HttpContext.User unauthenticated, not throwing), fall here.
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return new RateLimitDecision(
            RateLimitPartitionKind.UnauthenticatedFixedWindow,
            $"unauth:{clientIp}:{settings.UnauthenticatedRequestsPerMinute}",
            settings.UnauthenticatedRequestsPerMinute,
            Window);
    }

    /// <summary>
    /// Matches the versioned health controller's routes (<c>/api/v{version}/health</c> and its sub-routes such as
    /// <c>/ready</c>, <c>/live</c>, <c>/version</c>), per the <c>api/v{version:apiVersion}/[controller]</c>
    /// convention every JIM API controller uses.
    /// </summary>
    private static bool IsHealthCheckPath(PathString path)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments == null || segments.Length < 3)
            return false;

        return string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase) &&
               segments[1].Length > 1 && (segments[1][0] == 'v' || segments[1][0] == 'V') &&
               string.Equals(segments[2], "health", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Picks a stable identifier to partition an authenticated principal by. Preference order: the Metaverse
    /// Object ID claim JIM attaches to SSO/JWT Bearer principals (<see cref="Constants.BuiltInClaims.MetaverseObjectId"/>,
    /// via <c>Program.ResolveAndAttachJimIdentityAsync</c>), then the API key ID claim
    /// <see cref="ApiKeyAuthenticationHandler"/> attaches to API-key principals (<see cref="ClaimTypes.NameIdentifier"/>),
    /// then <see cref="ClaimsIdentity.Name"/> as a last resort so an authenticated request is never accidentally
    /// treated as anonymous.
    /// </summary>
    private static string ResolvePrincipalId(ClaimsPrincipal user)
    {
        var metaverseObjectId = user.FindFirstValue(Constants.BuiltInClaims.MetaverseObjectId);
        if (!string.IsNullOrEmpty(metaverseObjectId))
            return metaverseObjectId;

        var nameIdentifier = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrEmpty(nameIdentifier))
            return nameIdentifier;

        return user.Identity?.Name ?? "unknown";
    }
}
