// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// How many generated values involving one Synchronisation Rule or Connected System are held for a decision (Unique
/// Value Generation, #242, release 4, Phase 9): what the needs-attention indicator on the list pages shows. The two
/// counts are kept apart for the reason <see cref="InitialPasswordAttention"/> keeps its own apart: only a value
/// needing a decision needs a person; an allowed rename is already decided and will happen at the next export.
/// </summary>
public class GeneratedValueDecisionAttention
{
    /// <summary>Values waiting on a decision.</summary>
    public int NeedsDecisionCount { get; set; }

    /// <summary>Values whose rename has been allowed, waiting for the next export.</summary>
    public int RenameAllowedCount { get; set; }
}
