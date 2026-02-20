using Microsoft.AspNetCore.Authentication;

namespace JIM.Web.Middleware.Api;

/// <summary>
/// Middleware that prevents authentication redirects for API requests.
/// API endpoints should return 401 Unauthorized instead of redirecting to SSO login.
/// </summary>
public class ApiAuthenticationMiddleware
{
    private readonly RequestDelegate _next;

    public ApiAuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Check if this is an API request
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            // Store original OnChallenge event for OIDC
            var originalOnChallenge = context.Features.Get<IAuthenticateResultFeature>();

            // Call next middleware
            await _next(context);

            // If the response is a redirect (302) and we haven't sent the response yet,
            // convert it to 401 Unauthorized for API requests
            if (context.Response.StatusCode == StatusCodes.Status302Found && !context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.Remove("Location");
                context.Response.Headers.Remove("Set-Cookie");
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized", message = "Authentication required" });
            }
        }
        else
        {
            await _next(context);
        }
    }
}

/// <summary>
/// Extension methods for registering API authentication middleware.
/// </summary>
public static class ApiAuthenticationMiddlewareExtensions
{
    /// <summary>
    /// Adds API authentication middleware that prevents redirects for API requests.
    /// Must be called after UseAuthentication() and before UseAuthorization().
    /// </summary>
    public static IApplicationBuilder UseApiAuthentication(this IApplicationBuilder app)
    {
        return app.UseMiddleware<ApiAuthenticationMiddleware>();
    }
}
