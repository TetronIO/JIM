// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Utilities;

namespace JIM.Web.Middleware.Api;

/// <summary>
/// Reads the credential from an <c>Authorization: Bearer</c> header (RFC 6750 s2.1). Every bearer credential JIM
/// accepts arrives this way: API keys (<c>jim_ak_...</c>) and SSO access tokens alike. The authentication scheme
/// selector, the API key handler and the /api challenge all read the header through this one class, so they cannot
/// disagree about what a credential, or an API key, looks like.
/// </summary>
public static class BearerToken
{
    private const string Scheme = "Bearer";

    /// <summary>
    /// Reads the bearer token from the request's <c>Authorization</c> header.
    /// </summary>
    /// <param name="request">The request to read.</param>
    /// <param name="token">The token when one was read; otherwise an empty string.</param>
    /// <returns>True when the request carries exactly one <c>Authorization</c> header holding a bearer token.
    /// Two headers are ambiguous, so neither is taken as the credential.</returns>
    public static bool TryRead(HttpRequest request, out string token)
    {
        ArgumentNullException.ThrowIfNull(request);

        var values = request.Headers.Authorization;
        if (values.Count != 1)
        {
            token = string.Empty;
            return false;
        }

        return TryParse(values[0], out token);
    }

    /// <summary>
    /// Parses an <c>Authorization</c> header value as a bearer credential. The scheme name is matched
    /// case-insensitively (RFC 9110 s11.1) and the token is trimmed.
    /// </summary>
    /// <param name="authorizationHeaderValue">The header value, for example <c>Bearer jim_ak_...</c>.</param>
    /// <param name="token">The token when the value is a bearer credential; otherwise an empty string.</param>
    public static bool TryParse(string? authorizationHeaderValue, out string token)
    {
        token = string.Empty;

        var value = authorizationHeaderValue?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length <= Scheme.Length)
            return false;

        if (!value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) || value[Scheme.Length] != ' ')
            return false;

        var candidate = value[Scheme.Length..].Trim();
        if (candidate.Length == 0)
            return false;

        token = candidate;
        return true;
    }

    /// <summary>
    /// Whether a bearer token is a JIM API key rather than an SSO access token. The prefix is matched exactly: keys
    /// are generated in lower case, and an SSO JWT can never begin with it.
    /// </summary>
    public static bool IsApiKey(string token) =>
        token.StartsWith(ApiKeyMaterial.Prefix, StringComparison.Ordinal);
}
