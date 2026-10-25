// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Utility;
using JIM.Web.Shared;

namespace JIM.Web.Models;

/// <summary>
/// Reads a change's values for the change history timeline when the change carries only the first of them (#2498): a
/// change to a large group records one value change per member, and the change history caps each attribute's values.
/// The page hosting the timeline supplies the reader for its side, as it supplies the change groups.
/// </summary>
public interface IChangeHistoryValueReader
{
    /// <summary>One window of the change's values, as the timeline's details dialog shows them.</summary>
    Task<RangeResultSet<ChangeHistoryTimeline.AttributeChangeDisplay>> ReadValuesAsync(ChangeHistoryTimeline.ChangeGroup change, ChangeValueRowQuery query);

    /// <summary>How many of each attribute's values the change set, replaced, added and removed.</summary>
    Task<List<ChangeValueAttributeSummary>> ReadAttributeSummariesAsync(ChangeHistoryTimeline.ChangeGroup change);
}
