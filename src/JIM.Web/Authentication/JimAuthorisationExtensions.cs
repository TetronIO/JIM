// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Web.Middleware.Api;
using JIM.Web.Scim;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace JIM.Web.Authentication;

/// <summary>
/// Registers JIM's authorisation policies. Extracted from <c>Program.cs</c> so tests evaluate exactly the policies
/// the application runs with.
/// </summary>
public static class JimAuthorisationExtensions
{
    /// <summary>
    /// Adds authorisation with JIM's policies. The default policy, which every bare <c>[Authorize]</c> applies,
    /// requires an authenticated principal that is not a scoped API key: a scoped key is for one endpoint, which
    /// authorises it with a policy of its own. Endpoints that name roles do not apply the default policy, and refuse
    /// a scoped key because it carries no role claims.
    /// </summary>
    /// <remarks>
    /// The SCIM policy authenticates with the API key scheme alone, so a portal session cookie is never consulted and a
    /// refusal is never a redirect; <see cref="ScimAuthorisationResultHandler"/> turns every refusal into the same 401.
    /// </remarks>
    public static IServiceCollection AddJimAuthorisation(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireAssertion(context => !context.User.HasClaim(c => c.Type == Constants.BuiltInClaims.KeyScope))
                .Build();

            options.AddPolicy(ScimRoutes.AuthorisationPolicy, policy => policy
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .AddRequirements(new ScimConnectedSystemScopeRequirement()));
        });
        services.AddSingleton<IAuthorizationHandler, ScimConnectedSystemScopeHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler>(provider => new ScimAuthorisationResultHandler(
            provider.GetRequiredService<IFailedAuthenticationRecorder>(), new AuthorizationMiddlewareResultHandler()));
        return services;
    }
}
