// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Scim;

/// <summary>
/// Where the SCIM 2.0 Service Provider endpoints live and how they are authorised. They sit outside <c>/api</c>
/// (D15), so every <c>/api</c>-only stage of the request pipeline has a <c>/scim</c> counterpart keyed on
/// <see cref="IsScimRequest"/>.
/// </summary>
public static class ScimRoutes
{
    /// <summary>
    /// The path every SCIM endpoint is under.
    /// </summary>
    public static readonly PathString PathBase = new("/scim");

    /// <summary>
    /// The route template of one Connected System's SCIM base URL.
    /// </summary>
    public const string BaseTemplate = "scim/v2/{" + ConnectedSystemIdRouteValue + ":int}";

    /// <summary>
    /// The route value naming the Connected System a request is for.
    /// </summary>
    public const string ConnectedSystemIdRouteValue = "connectedSystemId";

    /// <summary>
    /// The authorisation policy every SCIM endpoint carries: an API key scoped to the Connected System in the route.
    /// </summary>
    public const string AuthorisationPolicy = "ScimServiceProvider";

    /// <summary>
    /// Whether a request is for a SCIM endpoint. Matches the <c>/scim</c> segment, not any path beginning with those
    /// letters.
    /// </summary>
    public static bool IsScimRequest(HttpRequest request) =>
        request.Path.StartsWithSegments(PathBase, StringComparison.OrdinalIgnoreCase);
}
