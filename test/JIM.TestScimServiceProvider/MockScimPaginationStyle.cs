// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.TestScimServiceProvider;

/// <summary>
/// How the mock service provider pages its list responses. Real providers split roughly this way, and
/// the two styles fail differently, so both are worth driving the connector against.
/// </summary>
public enum MockScimPaginationStyle
{
    /// <summary>RFC 7644 <c>startIndex</c> and <c>count</c>, which every provider supports.</summary>
    Index = 0,

    /// <summary>RFC 9865 opaque cursors, which large providers prefer.</summary>
    Cursor = 1
}
