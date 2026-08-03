// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.SCIM.Authentication;

/// <summary>
/// Applies an authentication method to outbound SCIM requests.
/// <para>
/// One strategy instance is shared across a run, including parallel export workers, so implementations
/// must be safe for concurrent use. Strategies that hold acquired credentials (rather than static ones)
/// are responsible for refreshing them before they lapse.
/// </para>
/// </summary>
public interface IScimAuthenticationStrategy
{
    /// <summary>
    /// Adds the authentication material to the request, acquiring or refreshing it if necessary.
    /// </summary>
    /// <exception cref="ScimAuthenticationException">The credential could not be acquired.</exception>
    Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken);

    /// <summary>
    /// Discards any cached credential, so the next <see cref="ApplyAsync"/> acquires a fresh one.
    /// Called after a 401, which can mean a token was revoked or expired earlier than advertised.
    /// Implementations holding static credentials do nothing.
    /// </summary>
    void InvalidateCachedCredentials();
}
