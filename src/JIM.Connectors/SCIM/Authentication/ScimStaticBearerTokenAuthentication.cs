// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net.Http.Headers;

namespace JIM.Connectors.SCIM.Authentication;

/// <summary>
/// A long-lived bearer token supplied by the administrator, for providers that issue API tokens rather
/// than running an OAuth 2.0 authorisation server. The token is static, so nothing is refreshed.
/// </summary>
public class ScimStaticBearerTokenAuthentication : IScimAuthenticationStrategy
{
    private readonly AuthenticationHeaderValue _header;

    public ScimStaticBearerTokenAuthentication(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A bearer token is required.", nameof(token));

        _header = new AuthenticationHeaderValue("Bearer", token);
    }

    public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Headers.Authorization = _header;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Nothing to invalidate: a 401 means the configured token is wrong or has been revoked, and no
    /// amount of re-reading the setting will produce a different one.
    /// </summary>
    public void InvalidateCachedCredentials()
    {
    }
}
