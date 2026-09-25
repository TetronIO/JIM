// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// A read-only view of a generated Sequence mapping's counter, as the portal, REST and PowerShell surfaces show
/// it before an administrator saves a change (Unique Value Generation, #242, Phase 3). Allocates nothing: the
/// next number shown here is exactly what the real allocator would hand out next, computed the same way
/// (<see cref="Application.UniqueValues.SequenceAllocator"/>'s seeding rule), but without reserving it.
/// </summary>
public class GeneratedValueSequenceState
{
    /// <summary>The name of the target attribute this counter serves.</summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>
    /// The next number this counter would issue, unformatted. The higher of the counter's current position and
    /// the flow's configured start value; when the counter has never been seeded, the higher of the flow's start
    /// value and the attribute's highest existing numeric value plus the increment.
    /// </summary>
    public long NextNumber { get; set; }

    /// <summary>
    /// <see cref="NextNumber"/> rendered per the flow's configured width (zero-padded, or left unpadded when no
    /// fixed width is configured).
    /// </summary>
    public string NextNumberFormatted { get; set; } = null!;

    /// <summary>
    /// True when <see cref="NextNumber"/> no longer fits the flow's configured fixed width under
    /// <see cref="Logic.GeneratedValueWidthOverflowBehaviour.StopAndReport"/>: the next real generation attempt
    /// for this attribute would fail until the width or the counter is corrected.
    /// </summary>
    public bool NextNumberWidthExceeded { get; set; }

    /// <summary>How many numbers this counter has issued in total so far. Zero when never seeded.</summary>
    public long AssignedCount { get; set; }

    /// <summary>
    /// False when the counter has never been seeded (no <see cref="GeneratedValueSequence"/> row exists yet for
    /// this attribute): the first real value will seed it from the higher of the flow's start value and the
    /// attribute's highest existing numeric value. True once a row exists, whether or not any value has yet been
    /// issued from it.
    /// </summary>
    public bool IsSeeded { get; set; }
}
