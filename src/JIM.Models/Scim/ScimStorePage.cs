// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// One page of live resources (<see cref="ScimStoreQuery"/>).
/// </summary>
public sealed class ScimStorePage
{
    /// <summary>
    /// How many live resources match the query across every page.
    /// </summary>
    public long TotalResults { get; init; }

    /// <summary>
    /// The page, in id order.
    /// </summary>
    public IReadOnlyList<ScimStoreResource> Resources { get; init; } = [];

    /// <summary>
    /// Whether a later page holds more matches.
    /// </summary>
    public bool HasMore { get; init; }
}
