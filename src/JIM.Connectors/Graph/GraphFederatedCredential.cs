// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Http;

namespace JIM.Connectors.Graph;

/// <summary>
/// Authenticates an app registration with a federated credential: a token from a workload identity the app trusts, sent
/// as the client assertion, so JIM holds no secret and no private key at all.
/// </summary>
/// <remarks>
/// Entra ID checks the token's issuer, subject and audience against a federated credential on the app registration.
/// The token is fetched for every token request, since its source keeps it current and it lives only an hour or so.
/// </remarks>
internal sealed class GraphFederatedCredential : IOAuthClientCredential
{
    private const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    private readonly IGraphFederatedTokenSource _source;

    public GraphFederatedCredential(IGraphFederatedTokenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    public async Task<IEnumerable<KeyValuePair<string, string>>> GetTokenRequestParametersAsync(Uri tokenEndpoint, DateTimeOffset utcNow, CancellationToken cancellationToken) =>
    [
        new("client_assertion_type", AssertionType),
        new("client_assertion", await _source.GetTokenAsync(cancellationToken))
    ];
}
