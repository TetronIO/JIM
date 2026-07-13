// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using JIM.Utilities;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.RateLimiting;

namespace JIM.Web.Middleware.Api;

/// <summary>
/// Wires up REST API rate limiting: a single global <see cref="PartitionedRateLimiter{TResource}"/> whose
/// partitioning decision comes from <see cref="RateLimitPartitionResolver"/>, and a rejection handler that
/// returns a 429 with a Retry-After header and a body matching the API's standard error shape
/// (<see cref="ApiErrorResponse"/>, mirroring <see cref="GlobalExceptionHandler"/>).
/// </summary>
/// <remarks>
/// Built entirely on <c>Microsoft.AspNetCore.RateLimiting</c> / <c>System.Threading.RateLimiting</c>, part of the
/// ASP.NET Core shared framework; no new NuGet package is required.
/// </remarks>
public static class RateLimitingExtensions
{
    /// <summary>10-second segments across the one-minute sliding window used for authenticated requests.</summary>
    private const int SlidingWindowSegmentsPerWindow = 6;

    private static readonly JsonSerializerOptions RejectionBodyJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Registers the settings cache and the global rate limiter. Call once during service configuration; pair
    /// with the built-in <c>app.UseRateLimiter()</c> in the request pipeline.
    /// </summary>
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddSingleton<IRateLimitSettingsCache, RateLimitSettingsCache>();

        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var cache = context.RequestServices.GetRequiredService<IRateLimitSettingsCache>();

                // PartitionedRateLimiter.Create requires a synchronous factory; there is no async overload.
                // IRateLimitSettingsCache.GetSnapshotAsync only ever awaits the database on a cache miss (at
                // most once per its TTL, and concurrent misses fall back to the stale value rather than all
                // awaiting), so in the overwhelming majority of calls this Task is already complete and
                // GetAwaiter().GetResult() does not block.
                var settings = cache.GetSnapshotAsync().GetAwaiter().GetResult();
                var decision = RateLimitPartitionResolver.Resolve(context, settings);
                return ToPartition(decision);
            });

            options.OnRejected = OnRejectedAsync;
        });

        return services;
    }

    private static RateLimitPartition<string> ToPartition(RateLimitDecision decision)
    {
        return decision.Kind switch
        {
            RateLimitPartitionKind.AuthenticatedSlidingWindow => RateLimitPartition.GetSlidingWindowLimiter(
                decision.PartitionKey,
                _ => new SlidingWindowRateLimiterOptions
                {
                    // A permit limit of 0 or less (an administrator misconfiguring the Service Setting) would
                    // otherwise throw from inside the limiter factory and take down every request in the
                    // partition; clamp to a minimum of 1 rather than fail closed on every API call.
                    PermitLimit = Math.Max(1, decision.PermitLimit),
                    Window = decision.Window,
                    SegmentsPerWindow = SlidingWindowSegmentsPerWindow,
                    QueueLimit = 0
                }),

            RateLimitPartitionKind.UnauthenticatedFixedWindow => RateLimitPartition.GetFixedWindowLimiter(
                decision.PartitionKey,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, decision.PermitLimit),
                    Window = decision.Window,
                    QueueLimit = 0
                }),

            _ => RateLimitPartition.GetNoLimiter(decision.PartitionKey)
        };
    }

    private static async ValueTask OnRejectedAsync(OnRejectedContext rejectedContext, CancellationToken cancellationToken)
    {
        var httpContext = rejectedContext.HttpContext;

        // Both limiter kinds populate this metadata on rejection; the fallback covers the (currently unreachable,
        // but non-throwing) case where a future limiter kind does not.
        var retryAfterSeconds = rejectedContext.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? (int)Math.Ceiling(retryAfter.TotalSeconds)
            : (int)RateLimitPartitionResolver.Window.TotalSeconds;

        httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        httpContext.Response.ContentType = "application/json";

        var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("JIM.Web.Middleware.Api.RateLimiting");
        logger.LogWarning(
            "Rate limit exceeded for {Method} {Path} from {RemoteIp}; Retry-After {RetryAfterSeconds}s",
            LogSanitiser.Sanitise(httpContext.Request.Method),
            LogSanitiser.Sanitise(httpContext.Request.Path.ToString()),
            httpContext.Connection.RemoteIpAddress,
            retryAfterSeconds);

        var errorResponse = new ApiErrorResponse
        {
            Code = ApiErrorCodes.TooManyRequests,
            Message = "Too many requests. Please retry after the indicated delay."
        };

        await httpContext.Response.WriteAsync(JsonSerializer.Serialize(errorResponse, RejectionBodyJsonOptions), cancellationToken);
    }
}
