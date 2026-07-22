// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The summary band content for a Run Profile Execution Item: one plain-English sentence (as
/// segments, never pre-rendered HTML) and the colour-coded outcome pill strip.
/// </summary>
public sealed class CausalitySummary
{
    /// <summary>
    /// The sentence segments, in reading order.
    /// </summary>
    public required IReadOnlyList<SummarySegment> Segments { get; init; }

    /// <summary>
    /// The outcome pills, in the order their outcome types first appear in the event tree.
    /// </summary>
    public required IReadOnlyList<CausalityPill> Pills { get; init; }
}
