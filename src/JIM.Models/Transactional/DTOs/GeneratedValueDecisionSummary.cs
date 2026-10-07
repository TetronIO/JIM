// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// The counts above the Generated Values tab and behind its REST and PowerShell summary (Unique Value Generation, #242,
/// release 4, Phase 9), optionally narrowed to a Connected System or a Synchronisation Rule.
/// </summary>
public class GeneratedValueDecisionSummary
{
    /// <summary>Values whose export is held until an administrator acts.</summary>
    public int NeedsDecisionCount { get; set; }

    /// <summary>Values whose rename an administrator has allowed, to be renamed at the next export.</summary>
    public int RenameAllowedCount { get; set; }

    /// <summary>
    /// Values Collision Remediation corrected on or after <see cref="CorrectedSince"/>, counted per value from its most
    /// recent correction: a value corrected twice in the window counts once, and a value whose object has since been
    /// deleted no longer counts. These need no action; the count says the feature is working.
    /// </summary>
    public int CorrectedRecentlyCount { get; set; }

    /// <summary>The start of the window <see cref="CorrectedRecentlyCount"/> covers (UTC): seven days before the read.</summary>
    public DateTime CorrectedSince { get; set; }
}
