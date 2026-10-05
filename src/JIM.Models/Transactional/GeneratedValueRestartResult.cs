// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// The outcome of "Start again" on a generated mapping (Unique Value Generation, #242, Phase 3, plan "The
/// service": <c>StartAgainAsync</c>). For a Sequence mapping, the counter is moved back (or forward) to the
/// flow's configured start value, and the target attribute's retired values are forgotten (Phase 6); existing values
/// and assignments are left untouched (there is no recall). For every other token kind this is a documented no-op:
/// "Start again" is offered only for Sequence flows, and only it purges the register.
/// </summary>
public class GeneratedValueRestartResult
{
    /// <summary>
    /// How many retired values were forgotten for the target attribute, and so can be issued again. Always 0 for a
    /// mapping that is not a Sequence.
    /// </summary>
    public int RetiredValuesForgotten { get; set; }

    /// <summary>
    /// The counter's next number before the restart. Null when the counter had never been seeded (nothing to
    /// move) or the mapping is not a Sequence mapping.
    /// </summary>
    public long? CounterFrom { get; set; }

    /// <summary>
    /// The counter's next number after the restart (the mapping's configured
    /// <see cref="Logic.SyncRuleMappingGeneration.SequenceStart"/>). Null under the same conditions as
    /// <see cref="CounterFrom"/>.
    /// </summary>
    public long? CounterTo { get; set; }
}
