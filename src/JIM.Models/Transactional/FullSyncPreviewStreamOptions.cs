// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// The bounds of a streamed whole-system preview (#1530). Unlike <see cref="FullSyncPreviewOptions"/>, nothing is
/// capped by default: a stream holds one object's result at a time, so the whole population is evaluated unless the
/// caller asks for less, which is what makes a partial count read as a whole one impossible by omission.
/// </summary>
public class FullSyncPreviewStreamOptions
{
    /// <summary>
    /// The most objects to evaluate before the stream ends with a <see cref="FullSyncPreviewItemKind.Truncated"/> item.
    /// Null, the default, evaluates every object.
    /// </summary>
    public int? MaxObjects { get; set; }

    /// <summary>
    /// The most wall-clock time to spend before the stream ends with a <see cref="FullSyncPreviewItemKind.Truncated"/>
    /// item. Null, the default, sets no budget; cancelling the enumeration stops it as well.
    /// </summary>
    public TimeSpan? TimeBudget { get; set; }

    /// <summary>
    /// How many Connected System Objects to load per page while walking the population.
    /// </summary>
    public int PageSize { get; set; } = 500;
}
