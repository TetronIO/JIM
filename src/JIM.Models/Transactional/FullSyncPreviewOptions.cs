// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// The work budget and sampling bounds for a full-system preview (#288, PRD decision D2 and requirement 14).
/// Defaults are deliberately conservative: a full-system preview must never run unbounded, so the object cap
/// has a value out of the box and lifting it is an explicit caller decision.
/// </summary>
public class FullSyncPreviewOptions
{
    /// <summary>
    /// The most objects the preview may evaluate before stopping and flagging truncation. Null removes the
    /// cap (the time budget still applies). Default 10,000: enough to cover most Connected Systems in full,
    /// bounded enough that a 100K+ system cannot run away by omission.
    /// </summary>
    public int? MaxObjects { get; set; } = 10_000;

    /// <summary>
    /// The most wall-clock time the preview may spend before stopping and flagging truncation. Null means
    /// no time budget; the object cap still applies.
    /// </summary>
    public TimeSpan? TimeBudget { get; set; }

    /// <summary>
    /// How many full outcome trees to retain per <see cref="FullSyncPreviewCategory"/>. Counting continues
    /// past this bound; only tree retention stops, which is what keeps a 100K-object preview's memory flat.
    /// </summary>
    public int SampleTreesPerCategory { get; set; } = 5;

    /// <summary>
    /// How many Connected System Objects to load per page while walking the population.
    /// </summary>
    public int PageSize { get; set; } = 500;
}
