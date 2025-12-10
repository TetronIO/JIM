using System.Security.Claims;
using JIM.Application;
using JIM.Models.Core;
using Serilog;

namespace JIM.Web.Middleware.Api;

/// <summary>
/// Middleware that enriches the authenticated user's claims with JIM roles from the database.
/// This mirrors the behaviour of JIM.Web's AuthoriseAndUpdateUserAsync method.
/// </summary>
/// <remarks>
/// For authenticated requests, this middleware:
/// 1. Extracts the unique identifier claim from the JWT token
/// 2. Looks up the corresponding Metaverse user in the database
/// 3. Retrieves the user's JIM role assignments
/// 4. Adds role claims to the ClaimsPrincipal for use with [Authorize(Roles = "...")] attributes
/// </remarks>
public class JimRoleEnrichmentMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context, JimApplication jim)
    {
        // Only process authenticated requests
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await EnrichWithJimRolesAsync(context, jim);
        }

        await _next(context);
    }

    private static async Task EnrichWithJimRolesAsync(HttpContext context, JimApplication jim)
    {
        try
        {
            var serviceSettings = await jim.ServiceSettings.GetServiceSettingsAsync();
            if (serviceSettings == null)
            {
                Log.Warning("JimRoleEnrichmentMiddleware: ServiceSettings not found. Cannot enrich roles.");
                return;
            }

            if (serviceSettings.SSOUniqueIdentifierMetaverseAttribute == null)
            {
                Log.Warning("JimRoleEnrichmentMiddleware: SSOUniqueIdentifierMetaverseAttribute not configured.");
                return;
            }

            if (string.IsNullOrEmpty(serviceSettings.SSOUniqueIdentifierClaimType))
            {
                Log.Warning("JimRoleEnrichmentMiddleware: SSOUniqueIdentifierClaimType not configured.");
                return;
            }

            // Get the unique identifier claim from the JWT token
            var uniqueIdClaimValue = context.User.FindFirstValue(serviceSettings.SSOUniqueIdentifierClaimType);
            if (string.IsNullOrEmpty(uniqueIdClaimValue))
            {
                Log.Debug("JimRoleEnrichmentMiddleware: User does not have claim '{ClaimType}'. Cannot map to JIM user.",
                    serviceSettings.SSOUniqueIdentifierClaimType);
                return;
            }

            Log.Debug("JimRoleEnrichmentMiddleware: Looking up user with {ClaimType}={ClaimValue}",
                serviceSettings.SSOUniqueIdentifierClaimType, uniqueIdClaimValue);

            // Look up the Metaverse user
            var userType = await jim.Metaverse.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.Users, false);
            if (userType == null)
            {
                Log.Warning("JimRoleEnrichmentMiddleware: User object type not found.");
                return;
            }

            var user = await jim.Metaverse.GetMetaverseObjectByTypeAndAttributeAsync(
                userType, serviceSettings.SSOUniqueIdentifierMetaverseAttribute, uniqueIdClaimValue);

            if (user == null)
            {
                Log.Debug("JimRoleEnrichmentMiddleware: No Metaverse user found for claim value '{ClaimValue}'.",
                    uniqueIdClaimValue);
                return;
            }

            // Retrieve JIM role assignments
            var userRoles = await jim.Security.GetMetaverseObjectRolesAsync(user);

            // Convert roles to claims
            var roleClaims = userRoles
                .Select(role => new Claim(Constants.BuiltInRoles.RoleClaimType, role.Name))
                .ToList();

            // Add the virtual "Users" role (basic access)
            roleClaims.Add(new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.Users));

            // Add the Metaverse object ID claim
            roleClaims.Add(new Claim(Constants.BuiltInClaims.MetaverseObjectId, user.Id.ToString()));

            // Create a new identity with JIM claims and add it to the principal
            var jimIdentity = new ClaimsIdentity(roleClaims) { Label = "JIM.Api" };
            context.User.AddIdentity(jimIdentity);

            Log.Debug("JimRoleEnrichmentMiddleware: Enriched user with {RoleCount} roles: {Roles}",
                userRoles.Count(), string.Join(", ", userRoles.Select(r => r.Name)));
        }
        catch (Exception ex)
        {
            // Log but don't fail the request - authorisation will handle missing roles
            Log.Error(ex, "JimRoleEnrichmentMiddleware: Error enriching user roles.");
        }
    }
}

/// <summary>
/// Extension methods for registering the JIM role enrichment middleware.
/// </summary>
public static class JimRoleEnrichmentMiddlewareExtensions
{
    /// <summary>
    /// Adds the JIM role enrichment middleware to the pipeline.
    /// Must be called after UseAuthentication() and before UseAuthorization().
    /// </summary>
    public static IApplicationBuilder UseJimRoleEnrichment(this IApplicationBuilder app)
    {
        return app.UseMiddleware<JimRoleEnrichmentMiddleware>();
    }
}
