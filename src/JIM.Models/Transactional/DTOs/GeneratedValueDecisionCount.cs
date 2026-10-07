// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// One group of generated value decisions, counted by the repository (Unique Value Generation, #242, release 4,
/// Phase 9): every assignment of one generated flow with the same rejecting and anchoring systems. The application
/// layer folds these into the summary tiles and the per-Synchronisation Rule and per-Connected System indicators,
/// interpreting the flow's configuration (which rule holds it, which systems it is exported to) itself, so the whole
/// estate is counted in one grouped read however many rules and systems are asked about.
/// </summary>
public class GeneratedValueDecisionCount
{
    /// <summary>The generated flow (<c>SyncRuleMappingGeneration</c> id).</summary>
    public int GenerationId { get; set; }

    /// <summary>The Connected System that rejected these values.</summary>
    public int? RejectedByConnectedSystemId { get; set; }

    /// <summary>The Connected System anchoring these values, or that cannot tell.</summary>
    public int? AnchoredByConnectedSystemId { get; set; }

    /// <summary>Values waiting on a decision.</summary>
    public int NeedsDecisionCount { get; set; }

    /// <summary>Values whose rename an administrator has allowed, waiting for the next export.</summary>
    public int RenameAllowedCount { get; set; }

    /// <summary>Values Collision Remediation corrected on or after the read's cut-off.</summary>
    public int CorrectedCount { get; set; }
}
