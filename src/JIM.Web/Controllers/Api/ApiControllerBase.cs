// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Claims;
using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Utilities;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// Base class for JIM REST API controllers. Centralises the authentication-context helpers that every write
/// endpoint needs (distinguishing API key authentication from an interactive user, and resolving the calling
/// user's Metaverse Object for Activity attribution) so they are defined once rather than copied per controller.
/// </summary>
/// <param name="application">The JIM application facade.</param>
/// <param name="logger">Logger used by the authentication-context helpers; pass the derived controller's own
/// typed logger so the helper log lines carry that controller's category.</param>
public abstract class ApiControllerBase(JimApplication application, ILogger logger) : ControllerBase
{
    /// <summary>
    /// The JIM application facade. Exposed to derived controllers; the shared helpers below also use it.
    /// </summary>
    protected JimApplication Application { get; } = application;

    /// <summary>
    /// Logger for the shared authentication-context helpers.
    /// </summary>
    protected ILogger Logger { get; } = logger;

    /// <summary>
    /// Whether the current request was authenticated via an API key (as opposed to an interactive user).
    /// </summary>
    protected bool IsApiKeyAuthenticated()
    {
        return User.HasClaim("auth_method", "api_key");
    }

    /// <summary>
    /// Gets the API key name if authenticated via API key; otherwise null.
    /// </summary>
    protected string? GetApiKeyName()
    {
        if (!IsApiKeyAuthenticated())
            return null;

        return User.Identity?.Name;
    }

    /// <summary>
    /// Gets the current API key entity if authenticated via API key; otherwise null.
    /// </summary>
    protected async Task<ApiKey?> GetCurrentApiKeyAsync()
    {
        if (!IsApiKeyAuthenticated())
            return null;

        // The API key ID is stored in the NameIdentifier claim
        var apiKeyIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(apiKeyIdClaim) || !Guid.TryParse(apiKeyIdClaim, out var apiKeyId))
            return null;

        return await Application.Security.GetApiKeyAsync(apiKeyId);
    }

    /// <summary>
    /// Resolves the initiating security principal from the current request's claims as an Activity initiator
    /// triad: an API key by the key's id and name (<see cref="Middleware.Api.ApiKeyAuthenticationHandler.BuildApiKeyClaims"/>),
    /// otherwise a person by the Metaverse Object authorisation was granted to, as <see cref="GetCurrentUserAsync"/>
    /// resolves them. Never the identity provider's subject, which is not a Metaverse Object id: a principal recorded
    /// by it links to nothing (#2066).
    /// Returns false when the principal carries no id to attribute to; the caller must then refuse the request with
    /// 401 rather than record the change against a principal that cannot be identified (#2096).
    /// </summary>
    protected bool TryGetInitiatorInfo(out ActivityInitiatorType initiatorType, out Guid initiatorId, out string? initiatorName)
    {
        if (IsApiKeyAuthenticated())
        {
            initiatorType = ActivityInitiatorType.ApiKey;
            initiatorName = GetApiKeyName() ?? "API Key";
            return Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out initiatorId);
        }

        initiatorType = ActivityInitiatorType.User;
        initiatorName = User.FindFirst("name")?.Value ?? User.Identity?.Name;
        if (IdentityUtilities.TryGetUserId(User, out initiatorId))
            return true;

        // Unreachable for an authorised caller: a principal the pipeline could not resolve receives no roles.
        Logger.LogWarning("Could not identify the initiating principal: no API key id or Metaverse Object id claim");
        return false;
    }

    /// <summary>
    /// The response for a write whose initiating principal could not be identified (<see cref="TryGetInitiatorInfo"/>).
    /// </summary>
    protected UnauthorizedObjectResult UnidentifiedInitiator()
    {
        return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
    }

    /// <summary>
    /// Returns the Metaverse Object of the signed-in (JWT) caller, read from the Metaverse Object id claim the
    /// authentication pipeline attached when it resolved them (Program.cs <c>ResolveAndAttachJimIdentityAsync</c>,
    /// <see cref="Middleware.Api.JimRoleEnrichmentMiddleware"/>). Deliberately not a second resolution from the SSO
    /// claim: the identity a change is attributed to must be the one authorisation was granted to. The portal resolves
    /// its user the same way (<see cref="Helpers.GetUserAsync"/>).
    /// Returns null for API key authentication (which is valid - use <see cref="IsApiKeyAuthenticated"/> to check).
    /// </summary>
    protected async Task<MetaverseObject?> GetCurrentUserAsync()
    {
        if (User.Identity?.IsAuthenticated != true)
            return null;

        // API key authentication doesn't map to a Metaverse user object
        // This is valid - the caller should check IsApiKeyAuthenticated() separately
        if (IsApiKeyAuthenticated())
        {
            Logger.LogDebug("API key authentication detected - no Metaverse user lookup needed");
            return null;
        }

        if (!IdentityUtilities.TryGetUserId(User, out var userId))
        {
            // Unreachable for an authorised caller: a principal the pipeline could not resolve receives no roles.
            Logger.LogWarning("Authenticated caller has no JIM identity (no Metaverse Object id claim)");
            return null;
        }

        return await Application.Metaverse.GetMetaverseObjectAsync(userId);
    }
}
