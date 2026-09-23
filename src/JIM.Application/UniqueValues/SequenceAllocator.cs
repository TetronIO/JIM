// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Threading;
using JIM.Data.Repositories;

namespace JIM.Application.UniqueValues;

/// <summary>
/// Hands out numbers for <see cref="Models.Logic.GeneratedValueTokenKind.Sequence"/> candidates, block by
/// block, against the run-scoped state on <see cref="UniqueValueResolveOptions"/> (plan "SequenceAllocator"
/// and decision 3). Stateless itself: every cache it reads and writes lives on <paramref name="options"/>, so
/// the same block, and the same in-flight refill, are shared correctly across every
/// <see cref="UniqueValueGenerationServer.ResolveAsync"/> call in a run, not just within one call.
/// </summary>
internal static class SequenceAllocator
{
    /// <summary>
    /// Returns the next number for the attribute identified by exactly one of
    /// <paramref name="metaverseAttributeId"/> and <paramref name="connectedSystemObjectTypeAttributeId"/>.
    /// Draws from <paramref name="options"/>'s cached block for that attribute where one still has numbers left
    /// at or above <paramref name="sequenceStart"/>; otherwise refills it first, reserving (or, under
    /// <paramref name="dryRun"/>, simulating) <c>Math.Max(1, options.SequenceBlockSize)</c> more.
    /// <para>
    /// A block is keyed by attribute alone, so two flows sharing an attribute (decision 3) share its queued
    /// remainder. When the number this call would otherwise return is below <paramref name="sequenceStart"/> -
    /// the block was filled for a flow whose own start was lower - the whole remainder is discarded (those
    /// numbers become gaps, which is fine: the counter only ever moves forward) and a fresh block is reserved
    /// at this flow's own floor instead. A number found taken by a later gate is, likewise, simply never
    /// returned to the caller again: the block still advances, which is the documented, expected sequence gap.
    /// </para>
    /// </summary>
    public static async Task<long> NextNumberAsync(
        ISyncRepository repository,
        UniqueValueResolveOptions options,
        bool dryRun,
        int? metaverseAttributeId,
        int? connectedSystemObjectTypeAttributeId,
        long sequenceStart,
        int increment)
    {
        var key = (metaverseAttributeId, connectedSystemObjectTypeAttributeId);
        var queue = options.SequenceBlocks.GetOrAdd(key, static _ => new ConcurrentQueue<long>());

        if (TryDequeueAtOrAboveStart(queue, sequenceStart, out var number))
            return number;

        var gate = options.SequenceRefillGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            // Another caller may have refilled (or, like this one, drained a stale remainder from) the block
            // while this one waited for the gate.
            if (TryDequeueAtOrAboveStart(queue, sequenceStart, out number))
                return number;

            var floor = await ComputeFloorAsync(repository, options, dryRun, key, sequenceStart, increment);
            var count = Math.Max(1, options.SequenceBlockSize);
            long first;

            if (dryRun)
            {
                first = floor;
                options.SimulatedSequenceNext[key] = first + (long)count * increment;
            }
            else
            {
                first = await repository.ReserveGeneratedValueSequenceBlockAsync(
                    metaverseAttributeId, connectedSystemObjectTypeAttributeId, floor, count, increment);
            }

            for (var i = 1; i < count; i++)
                queue.Enqueue(first + (long)i * increment);

            return first;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Dequeues the next number, or discards the whole remainder and reports none available when that number
    /// (and so, since a block is a contiguous ascending range, every number after it) is below
    /// <paramref name="sequenceStart"/>. A block straddling the start value is not split; the numbers below it
    /// are simply gaps, per decision 3.
    /// </summary>
    private static bool TryDequeueAtOrAboveStart(ConcurrentQueue<long> queue, long sequenceStart, out long number)
    {
        if (!queue.TryDequeue(out number))
            return false;

        if (number >= sequenceStart)
            return true;

        while (queue.TryDequeue(out _)) { }
        return false;
    }

    /// <summary>
    /// The floor to reserve (or simulate) a fresh block from: a dry run's own simulated position once it has
    /// one (so a second dry-run block never re-reads the real counter or the attribute's highest existing
    /// value); otherwise, when a counter row already exists, <paramref name="sequenceStart"/> for a real
    /// reservation (the database's own <c>GREATEST(current, floor)</c> supplies the counter) or the higher of
    /// <paramref name="sequenceStart"/> and the row's <c>NextValue</c> for a dry run, which never reaches the
    /// database to have that applied for it; otherwise, on first use, the higher of
    /// <paramref name="sequenceStart"/> and the attribute's highest existing numeric value plus
    /// <paramref name="increment"/>.
    /// </summary>
    private static async Task<long> ComputeFloorAsync(
        ISyncRepository repository,
        UniqueValueResolveOptions options,
        bool dryRun,
        (int? MetaverseAttributeId, int? ConnectedSystemObjectTypeAttributeId) key,
        long sequenceStart,
        int increment)
    {
        if (options.SimulatedSequenceNext.TryGetValue(key, out var simulated))
            return Math.Max(sequenceStart, simulated);

        var existing = await repository.GetGeneratedValueSequenceAsync(key.MetaverseAttributeId, key.ConnectedSystemObjectTypeAttributeId);
        if (existing != null)
            return dryRun ? Math.Max(sequenceStart, existing.NextValue) : sequenceStart;

        var highest = await repository.GetHighestNumericValueForAttributeAsync(key.MetaverseAttributeId, key.ConnectedSystemObjectTypeAttributeId);
        return highest.HasValue ? Math.Max(sequenceStart, highest.Value + increment) : sequenceStart;
    }
}
