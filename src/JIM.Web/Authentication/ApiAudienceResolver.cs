// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Authentication;

/// <summary>
/// Derives the audience JIM expects on SSO bearer tokens from the configured API scope and client ID.
/// </summary>
public static class ApiAudienceResolver
{
    private const string ApiScheme = "api://";

    /// <summary>
    /// Returns the audience JIM expects on SSO bearer tokens.
    /// </summary>
    /// <remarks>
    /// An <c>api://</c> scope names an application ID URI, optionally followed by a permission:
    /// <c>api://{id}/access_as_user</c> (Entra ID) gives <c>api://{id}</c>, and a bare <c>api://{id}</c>
    /// (AD FS, Okta's <c>api://default</c>) is the audience as it stands. Any other scope, or none, gives the
    /// client ID. Only the part after <c>api://</c> is searched for a path separator, so the scheme's own
    /// slashes are never mistaken for one (issue #2353).
    /// </remarks>
    /// <param name="apiScope">The value of <c>JIM_SSO_API_SCOPE</c>.</param>
    /// <param name="clientId">The value of <c>JIM_SSO_CLIENT_ID</c>.</param>
    public static string? Resolve(string? apiScope, string? clientId)
    {
        if (string.IsNullOrEmpty(apiScope) || !apiScope.StartsWith(ApiScheme, StringComparison.Ordinal))
            return clientId;

        var trimmed = apiScope.TrimEnd('/');
        var lastSlashIndex = trimmed.LastIndexOf('/');
        return lastSlashIndex >= ApiScheme.Length ? trimmed[..lastSlashIndex] : trimmed;
    }
}
