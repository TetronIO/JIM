// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Http;

/// <summary>
/// How a client proves its identity to an OAuth 2.0 token endpoint: the form parameters a client credentials token
/// request carries alongside <c>grant_type</c>, <c>client_id</c> and <c>scope</c>.
/// </summary>
public interface IOAuthClientCredential
{
    /// <summary>
    /// The parameters that authenticate the client in a token request to <paramref name="tokenEndpoint"/>. Called
    /// once per token request, so a credential that must be fresh each time (a signed assertion, or one fetched from a
    /// workload identity's token source) is built here.
    /// </summary>
    Task<IEnumerable<KeyValuePair<string, string>>> GetTokenRequestParametersAsync(Uri tokenEndpoint, DateTimeOffset utcNow, CancellationToken cancellationToken);
}
