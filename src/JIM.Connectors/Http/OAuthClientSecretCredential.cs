// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Http;

/// <summary>
/// A client secret sent in the token request body (client_secret_post, RFC 6749 section 2.3.1), the form most widely
/// accepted across authorisation servers.
/// </summary>
public sealed class OAuthClientSecretCredential : IOAuthClientCredential
{
    private readonly string _clientSecret;

    public OAuthClientSecretCredential(string clientSecret)
    {
        ArgumentNullException.ThrowIfNull(clientSecret);
        _clientSecret = clientSecret;
    }

    public Task<IEnumerable<KeyValuePair<string, string>>> GetTokenRequestParametersAsync(Uri tokenEndpoint, DateTimeOffset utcNow, CancellationToken cancellationToken) =>
        Task.FromResult<IEnumerable<KeyValuePair<string, string>>>([new("client_secret", _clientSecret)]);
}
