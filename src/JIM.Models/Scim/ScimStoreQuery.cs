// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// One page of a list of a Connected System's live resources of one type, ordered by id.
/// </summary>
public sealed class ScimStoreQuery
{
    /// <summary>
    /// The resource type listed.
    /// </summary>
    public int ResourceTypeId { get; init; }

    /// <summary>
    /// Which resources; null for all of them.
    /// </summary>
    public ScimStoreFilter? Filter { get; init; }

    /// <summary>
    /// The most resources the page holds. Zero counts the matches and returns none.
    /// </summary>
    public int Count { get; init; }

    /// <summary>
    /// For index paging, the 1-based position of the page's first resource. Ignored when <see cref="After"/> is set.
    /// </summary>
    public int StartIndex { get; init; } = 1;

    /// <summary>
    /// For cursor paging, the id of the last resource the previous page held: the page starts after it. A cursor is a
    /// position in the id order rather than a count, so a resource deleted mid-walk cannot make the walk skip another.
    /// </summary>
    public Guid? After { get; init; }
}
