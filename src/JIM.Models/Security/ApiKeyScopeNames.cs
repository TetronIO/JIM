// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Security;

/// <summary>
/// The permission names scoped API keys carry on their principal: dotted <c>Resource.Action</c> in PascalCase, the
/// convention the RBAC permission catalogue will follow (#622). The resource instance a key is bound to is never part
/// of the name; it travels in its own claim.
/// </summary>
public static class ApiKeyScopeNames
{
    /// <summary>
    /// Reading and writing a Connected System's SCIM resources through its SCIM 2.0 Service Provider endpoint.
    /// </summary>
    public const string ScimServiceProviderReadWrite = "ScimServiceProvider.ReadWrite";

    /// <summary>
    /// The permission name for a scope, or null for <see cref="ApiKeyScope.None"/>.
    /// </summary>
    public static string? For(ApiKeyScope scope) => scope switch
    {
        ApiKeyScope.None => null,
        ApiKeyScope.ScimServiceProvider => ScimServiceProviderReadWrite,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "This scope has no permission name.")
    };
}
