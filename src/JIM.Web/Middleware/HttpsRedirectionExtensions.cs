// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Middleware;

/// <summary>
/// Extension methods for registering HTTPS redirection.
/// </summary>
public static class HttpsRedirectionExtensions
{
    /// <summary>
    /// The health endpoints (<c>HealthController</c>), which container runtimes and load balancers probe.
    /// </summary>
    public static readonly PathString HealthPath = new("/api/v1/health");

    /// <summary>
    /// Adds HTTPS redirection to the pipeline for every request except the health endpoints.
    /// </summary>
    /// <remarks>
    /// In production JIM.Web serves HTTPS, plus plain HTTP on the container's loopback interface for the health
    /// probes. Redirected, a probe would receive a 307 whatever JIM's state, and <c>curl -f</c> counts that as
    /// success, so a service that is not ready would report healthy. Exempting the endpoints exposes nothing:
    /// they are anonymous, and in production plain HTTP is only reachable from inside the container.
    /// </remarks>
    public static IApplicationBuilder UseHttpsRedirectionExceptHealthProbes(this IApplicationBuilder app)
    {
        return app.UseWhen(
            context => !context.Request.Path.StartsWithSegments(HealthPath, StringComparison.OrdinalIgnoreCase),
            branch => branch.UseHttpsRedirection());
    }
}
