// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.SCIM;

/// <summary>
/// A service provider JIM recognises because it behaves in a way its discovery documents do not say (#2094). The
/// connector otherwise works from what a provider advertises; recognising one is reserved for a behaviour that would
/// corrupt a synchronisation if JIM took the advertisement at its word.
/// </summary>
internal enum ScimKnownProvider
{
    /// <summary>Any provider JIM has no special knowledge of, which it trusts to do what it advertises.</summary>
    Unrecognised,

    /// <summary>
    /// Keycloak's built-in SCIM API (26.8.0 and later). It advertises filtering, but moves <c>meta.lastModified</c> only for
    /// a create, a SCIM PUT and a User SCIM PATCH, so a filter on it misses every other change.
    /// </summary>
    Keycloak
}
