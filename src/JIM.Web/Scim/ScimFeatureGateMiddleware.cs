// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Models.Core;

namespace JIM.Web.Scim;

/// <summary>
/// Keeps the SCIM endpoints dark while <c>Features.ScimServiceProvider</c> is off (D16): every <c>/scim</c> request is
/// answered <c>404</c>, as though the route did not exist, before authentication runs. Registered for <c>/scim</c>
/// only.
/// </summary>
public class ScimFeatureGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IJimApplicationFactory jimFactory)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(jimFactory);

        using (var jim = jimFactory.Create())
        {
            if (!await jim.FeatureFlags.IsEnabledAsync(FeatureFlagCatalogue.ScimServiceProvider.Key))
            {
                await ScimResponses.WriteErrorAsync(context, StatusCodes.Status404NotFound);
                return;
            }
        }

        await next(context);
    }
}
