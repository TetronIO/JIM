// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.SCIM;

/// <summary>
/// How the connector walks a service provider's resources.
/// </summary>
public enum ScimPaginationMode
{
    /// <summary>
    /// Start index-based, and switch to cursors if the provider volunteers a <c>nextCursor</c>.
    /// <para>
    /// Index pagination is the one RFC 7644 makes mandatory, so it is the only safe opening move; a
    /// provider that ignores an unknown <c>cursor</c> parameter, or rejects it, would otherwise fail the
    /// first page. Cursors are the better choice against a large or busy provider, because index
    /// pagination over a set that changes mid-walk can skip or repeat resources, so administrators of
    /// such providers should select <see cref="Cursor"/> explicitly.
    /// </para>
    /// </summary>
    Auto = 0,

    /// <summary>Index-based paging with <c>startIndex</c> and <c>count</c> (RFC 7644 section 3.4.2.4).</summary>
    Index = 1,

    /// <summary>Cursor-based paging with <c>cursor</c> and <c>nextCursor</c> (RFC 9865).</summary>
    Cursor = 2
}
