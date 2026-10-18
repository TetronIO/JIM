// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// One page of what the Service Provider Connector imports from a Connected System's store, in id order across every
/// resource type: either every live resource (a Full Import), or every resource, live or tombstone, whose change
/// sequence is after <see cref="ChangedAfter"/> and at or below <see cref="ChangedUpTo"/> (a Delta Import).
/// </summary>
public sealed class ScimStoreImportQuery
{
    /// <summary>
    /// The id of the last resource the previous page held; the page starts after it. Null for the first page.
    /// </summary>
    public Guid? After { get; init; }

    /// <summary>
    /// The most resources the page holds.
    /// </summary>
    public int Count { get; init; }

    /// <summary>
    /// For a Delta Import, the watermark the previous import reached: only changes after it are read. Null for a Full
    /// Import, which reads every live resource whatever its change sequence.
    /// </summary>
    public long? ChangedAfter { get; init; }

    /// <summary>
    /// For a Delta Import, the store's committed change sequence when the import began: changes after it are left for
    /// the next import. Required with <see cref="ChangedAfter"/>.
    /// </summary>
    public long? ChangedUpTo { get; init; }
}
