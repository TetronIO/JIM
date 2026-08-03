// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.SCIM.Authentication;

/// <summary>
/// An arbitrary header carrying an API key, for providers that authenticate outside the
/// <c>Authorization</c> header (for example <c>X-Api-Key</c>). The value is static.
/// </summary>
public class ScimCustomHeaderAuthentication : IScimAuthenticationStrategy
{
    private readonly string _headerName;
    private readonly string _headerValue;

    public ScimCustomHeaderAuthentication(string headerName, string headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerName))
            throw new ArgumentException("An authentication header name is required.", nameof(headerName));
        ArgumentNullException.ThrowIfNull(headerValue);

        _headerName = headerName;
        _headerValue = headerValue;
    }

    public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Remove first so re-applying to a request that already carries the header replaces the value
        // rather than sending both; header values accumulate otherwise.
        request.Headers.Remove(_headerName);
        request.Headers.TryAddWithoutValidation(_headerName, _headerValue);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Nothing to invalidate: the header value is configuration, not an acquired credential.
    /// </summary>
    public void InvalidateCachedCredentials()
    {
    }
}
