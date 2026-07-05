// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Claims;
using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Utilities;
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
    /// Resolves the current user from JWT claims by looking up their SSO identifier in the Metaverse.
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

        // Get the service settings to know which claim type contains the unique identifier
        var serviceSettings = await Application.ServiceSettings.GetServiceSettingsAsync();
        if (serviceSettings?.SSOUniqueIdentifierClaimType == null ||
            serviceSettings.SSOUniqueIdentifierMetaverseAttribute == null)
        {
            Logger.LogError("Service settings are not configured for SSO claim mapping");
            return null;
        }

        // Get the unique identifier from the JWT claims
        var uniqueIdClaimValue = IdentityUtilities.GetSsoUniqueIdentifier(
            User,
            serviceSettings.SSOUniqueIdentifierClaimType);

        if (string.IsNullOrEmpty(uniqueIdClaimValue))
        {
            Logger.LogWarning("JWT does not contain the expected claim: {ClaimType}",
                serviceSettings.SSOUniqueIdentifierClaimType);
            return null;
        }

        // Look up the user in the Metaverse
        var userType = await Application.Metaverse.GetMetaverseObjectTypeAsync(
            Constants.BuiltInObjectTypes.User,
            false);

        if (userType == null)
        {
            Logger.LogError("Could not find User object type in Metaverse");
            return null;
        }

        return await Application.Metaverse.GetMetaverseObjectByTypeAndAttributeAsync(
            userType,
            serviceSettings.SSOUniqueIdentifierMetaverseAttribute,
            uniqueIdClaimValue);
    }
}
