// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net.Http.Headers;
using System.Text;

namespace JIM.Connectors.SCIM.Authentication;

/// <summary>
/// HTTP Basic authentication (RFC 7617). The credentials are static, so nothing is cached or refreshed.
/// </summary>
public class ScimBasicAuthentication : IScimAuthenticationStrategy
{
    private readonly string _encodedCredentials;

    public ScimBasicAuthentication(string username, string password)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

        // Encoded once at construction so the plain-text password is not rebuilt per request.
        _encodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
    }

    public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _encodedCredentials);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Nothing to invalidate: a 401 against static credentials means they are wrong, not stale.
    /// </summary>
    public void InvalidateCachedCredentials()
    {
    }
}
