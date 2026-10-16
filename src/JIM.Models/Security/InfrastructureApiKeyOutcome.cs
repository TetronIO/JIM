// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Security;

/// <summary>
/// What <c>SecurityServer.EnsureInfrastructureApiKeyAsync</c> did with the infrastructure API key at startup.
/// </summary>
public enum InfrastructureApiKeyOutcome
{
    /// <summary>No key with the given hash existed, so one was created.</summary>
    Created,

    /// <summary>The key existed but had expired, so its expiry was moved forward.</summary>
    Renewed,

    /// <summary>The key existed and was still valid, or has no expiry, so nothing changed.</summary>
    Unchanged,

    /// <summary>The key existed but an administrator had disabled it; it is left disabled and not renewed.</summary>
    Disabled,

    /// <summary>The Administrator role does not exist, so no key could be created.</summary>
    AdministratorRoleMissing
}
