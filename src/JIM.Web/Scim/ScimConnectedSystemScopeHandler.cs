// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Security;
using Microsoft.AspNetCore.Authorization;

namespace JIM.Web.Scim;

/// <summary>
/// Meets <see cref="ScimConnectedSystemScopeRequirement"/> when the principal's key scope and Connected System match
/// the route. An unscoped key, however privileged, never does: an Administrator key opens none of <c>/scim</c>.
/// </summary>
public sealed class ScimConnectedSystemScopeHandler : AuthorizationHandler<ScimConnectedSystemScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ScimConnectedSystemScopeRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Resource is HttpContext httpContext
            && IsScopedTo(context.User, RouteConnectedSystemId(httpContext)))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }

    /// <summary>
    /// The Connected System the route names, or null when it names none.
    /// </summary>
    public static int? RouteConnectedSystemId(HttpContext context) =>
        context.Request.RouteValues.TryGetValue(ScimRoutes.ConnectedSystemIdRouteValue, out var value)
        && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    private static bool IsScopedTo(System.Security.Claims.ClaimsPrincipal user, int? connectedSystemId) =>
        connectedSystemId is { } id
        && user.HasClaim(Constants.BuiltInClaims.KeyScope, ApiKeyScopeNames.ScimServiceProviderReadWrite)
        && user.HasClaim(Constants.BuiltInClaims.KeyScopeConnectedSystemId, id.ToString(CultureInfo.InvariantCulture));
}
