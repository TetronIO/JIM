// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// How many generated values are in each decision state, optionally narrowed to a Connected System or a
/// Synchronisation Rule.
/// </summary>
public class GeneratedValueDecisionSummary
{
    /// <summary>Values whose export is held until an administrator acts.</summary>
    public int NeedsDecisionCount { get; set; }

    /// <summary>Values whose rename an administrator has allowed, to be renamed at the next export.</summary>
    public int RenameAllowedCount { get; set; }

    /// <summary>
    /// How many values Collision Remediation corrected on or after CorrectedSince, each counted once from its most
    /// recent correction; a value whose object has since been deleted no longer counts. These need no action.
    /// </summary>
    public int CorrectedRecentlyCount { get; set; }

    /// <summary>
    /// The start of the window CorrectedRecentlyCount covers (UTC): seven days before the read.
    /// </summary>
    public DateTime CorrectedSince { get; set; }
}
