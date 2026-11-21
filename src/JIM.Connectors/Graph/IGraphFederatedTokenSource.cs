// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Graph;

/// <summary>
/// Where a federated credential's token comes from: a workload identity the app registration trusts, whose token Entra
/// ID accepts in place of a secret or a certificate.
/// </summary>
internal interface IGraphFederatedTokenSource
{
    /// <summary>A current token for the federated credential's audience, fetched for each token request.</summary>
    /// <exception cref="Http.HttpAuthenticationException">No token could be had, with what to do about it.</exception>
    Task<string> GetTokenAsync(CancellationToken cancellationToken);
}
