// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// The outcome of an outbound synchronisation preview over a population of Metaverse Objects (#288 plan
/// Phase 2): the decision records a real evaluation would act on, with nothing staged and nothing persisted.
/// </summary>
public class OutboundPreviewResult
{
    /// <summary>
    /// One entry per (Metaverse Object, export Synchronisation Rule) decision.
    /// </summary>
    public List<OutboundPreviewEntry> Entries { get; init; } = [];

    /// <summary>
    /// How many of the requested Metaverse Objects were evaluated.
    /// </summary>
    public int EvaluatedMetaverseObjectCount { get; set; }

    /// <summary>
    /// How many of the requested Metaverse Objects were skipped because they no longer exist or carry no
    /// Type (no export Synchronisation Rule can be matched without one).
    /// </summary>
    public int SkippedMetaverseObjectCount { get; set; }
}
