// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;

namespace JIM.Models.Utility;

/// <summary>
/// One window of the value changes recorded by one or more change records (#2498). A change to a large group carries
/// one value change per member, so its values are read a window at a time rather than with the change.
/// </summary>
public sealed record ChangeValueRowQuery
{
    /// <summary>The change records whose values are read, in the order <see cref="ChangeValueRowSort.SourceOrder"/> keeps.</summary>
    public required IReadOnlyList<Guid> ChangeIds { get; init; }

    /// <summary>Only this attribute's values, matched case-insensitively; every attribute when null.</summary>
    public string? AttributeName { get; init; }

    /// <summary>
    /// Whether a single-valued attribute's replacement reads as one Set row carrying the value it replaced, as the portal
    /// shows changes. When false, every value change is its own row, as the REST API pages them.
    /// </summary>
    public bool Normalise { get; init; } = true;

    /// <summary>Only rows of this operation; every operation when null.</summary>
    public ChangeValueOperation? Operation { get; init; }

    /// <summary>
    /// Only rows that carry the value they replaced (true), or only rows that do not (false); no filter when null. Lets a
    /// reader tell a single-valued attribute's replacement apart from its first value.
    /// </summary>
    public bool? HasPreviousValue { get; init; }

    /// <summary>Text the attribute name or the value must contain, case-insensitively; no filter when null or blank.</summary>
    public string? SearchText { get; init; }

    public ChangeValueRowSort Sort { get; init; } = ChangeValueRowSort.SourceOrder;

    public bool SortDescending { get; init; }

    /// <summary>How many rows to skip.</summary>
    public int Offset { get; init; }

    /// <summary>How many rows to read; capped by the repository.</summary>
    public required int Count { get; init; }

    /// <summary>Whether to count every matching row; counting is the expensive half of a window read at scale.</summary>
    public bool IncludeTotalCount { get; init; }
}
