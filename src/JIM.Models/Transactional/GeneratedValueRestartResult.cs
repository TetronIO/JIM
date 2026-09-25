// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// The outcome of "Start again" on a generated mapping (Unique Value Generation, #242, Phase 3, plan "The
/// service": <c>StartAgainAsync</c>). For a Sequence mapping, the counter is moved back (or forward) to the
/// flow's configured start value; existing values and assignments are left untouched (there is no recall). For
/// every other token kind this is a documented no-op: there is no counter to reset, and the retired values
/// register (release 2) does not exist yet, so <see cref="RetiredValuesForgotten"/> is always 0 in this release.
/// </summary>
public class GeneratedValueRestartResult
{
    /// <summary>
    /// How many retired values were forgotten for this attribute. Always 0 in release 1: the retired values
    /// register ships in release 2 (Phase 6).
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
