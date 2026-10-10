// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// A block of numbers reserved from a <see cref="GeneratedValueSequence"/> counter, and what the counter looked like
/// straight afterwards, so a run can hand back the numbers it never issued (#2044) only when nothing else has moved
/// the counter since.
/// </summary>
/// <param name="First">The block's first number; the block is <c>First, First + increment, ...</c>.</param>
/// <param name="CounterAfter">The counter's <see cref="GeneratedValueSequence.NextValue"/> straight after the
/// reservation: one step past the block's last number.</param>
/// <param name="CounterMovedStamp">An opaque marker of the counter's last administrator move
/// (<see cref="GeneratedValueSequence.LastMovedAt"/>, a raised start or Start again) as it stood straight after the
/// reservation; null when it has never been moved that way. A hand-back compares it, as well as
/// <paramref name="CounterAfter"/>, so a move made in the meantime is never undone, even one that happened to leave the
/// counter where this reservation did.</param>
public sealed record GeneratedValueSequenceBlock(long First, long CounterAfter, long? CounterMovedStamp);
