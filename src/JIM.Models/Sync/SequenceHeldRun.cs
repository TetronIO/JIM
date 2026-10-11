// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// The run of held numbers a Sequence has reached (#2031): the first number at or above the query's start that
/// nothing holds, and every holding of the numbers below it, so one lookup serves every object that reached the
/// run, each deciding in memory whether a number inside it is held only by itself.
/// </summary>
/// <param name="FirstFreeNumber">The lowest number at or above the query's start, stepping by its increment, that
/// nothing holds. The query's start itself when that is free, in which case <paramref name="Holders"/> is empty.</param>
/// <param name="Holders">Every holding of a number from the query's start up to, not including,
/// <paramref name="FirstFreeNumber"/>; each such number has at least one.</param>
public sealed record SequenceHeldRun(long FirstFreeNumber, IReadOnlyList<SequenceNumberHolder> Holders);
