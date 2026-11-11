// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Npgsql;
using Serilog;

namespace JIM.PostgresData;

/// <summary>
/// Splits a collection of items across N concurrent database connections for parallel writes.
/// Each connection gets its own PostgreSQL process and CPU core, addressing the single-core
/// write bottleneck observed during large sync operations.
/// </summary>
public static class ParallelBatchWriter
{
    /// <summary>
    /// Partitions a list into N roughly equal sub-lists. Items are distributed contiguously
    /// (not round-robin) to preserve locality and enable range-based batching.
    /// </summary>
    /// <remarks>
    /// If items.Count &lt; partitionCount, returns items.Count partitions (one item each).
    /// If items is empty, returns an empty list.
    /// Remainder items are distributed one each to the first partitions.
    /// </remarks>
    public static List<List<T>> Partition<T>(IReadOnlyList<T> items, int partitionCount)
    {
        if (items.Count == 0)
            return [];

        var effectiveCount = Math.Min(partitionCount, items.Count);
        var baseSize = items.Count / effectiveCount;
        var remainder = items.Count % effectiveCount;

        var partitions = new List<List<T>>(effectiveCount);
        var offset = 0;

        for (var i = 0; i < effectiveCount; i++)
        {
            var size = baseSize + (i < remainder ? 1 : 0);
            var partition = new List<T>(size);
            for (var j = 0; j < size; j++)
                partition.Add(items[offset + j]);
            partitions.Add(partition);
            offset += size;
        }

        return partitions;
    }

    /// <summary>
    /// Partitions items and executes a write action on each partition concurrently.
    /// Each partition gets its own <see cref="NpgsqlConnection"/> from the pool.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="items">Items to write.</param>
    /// <param name="parallelism">Maximum concurrent connections.</param>
    /// <param name="connectionString">
    /// PostgreSQL connection string for opening independent connections.
    /// If null, the write action receives null (for testing without a database).
    /// </param>
    /// <param name="writeAction">
    /// Async action that writes a partition of items using the provided connection.
    /// The connection is opened and will be disposed after the action completes.
    /// </param>
    public static async Task ExecuteAsync<T>(
        IReadOnlyList<T> items,
        int parallelism,
        string? connectionString,
        Func<NpgsqlConnection?, IReadOnlyList<T>, Task> writeAction)
    {
        if (items.Count == 0)
            return;

        var partitions = Partition(items, parallelism);

        if (partitions.Count == 1)
        {
            // Single partition — no need for parallel overhead.
            // Open a connection if we have a connection string, otherwise pass null.
            if (connectionString != null)
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await writeAction(connection, partitions[0]);
            }
            else
            {
                await writeAction(null, partitions[0]);
            }
            return;
        }

        Log.Debug("ParallelBatchWriter: Writing {ItemCount} items across {PartitionCount} parallel connections",
            items.Count, partitions.Count);

        var tasks = new Task[partitions.Count];
        for (var i = 0; i < partitions.Count; i++)
        {
            var partition = partitions[i];
            tasks[i] = Task.Run(async () =>
            {
                if (connectionString != null)
                {
                    await using var connection = new NpgsqlConnection(connectionString);
                    await connection.OpenAsync();
                    await writeAction(connection, partition);
                }
                else
                {
                    await writeAction(null, partition);
                }
            });
        }

        await Task.WhenAll(tasks);

        Log.Debug("ParallelBatchWriter: All {PartitionCount} partitions completed", partitions.Count);
    }

    /// <summary>
    /// The number of rows a call must write before the bulk writers spread it across parallel connections. Rows, not
    /// objects: the cost of a bulk write is in its rows (each one COPY'd and checked against its foreign keys), and a
    /// page of 500 groups can carry 650,000 membership values. Fixed, not scaled by the core count: the old gate,
    /// <c>objects &gt;= parallelism * 50</c>, meant the larger the host the less it wrote in parallel, and a
    /// 500-object synchronisation page never went parallel on 11 or more cores (#2872).
    /// </summary>
    /// <remarks>
    /// Set from <c>ParallelBulkWriteBenchmarkDatabaseTests</c> (Metaverse Object create, values carrying their contributing
    /// Connected System and Synchronisation Rule as synchronisation's do; median of three, single connection against 4
    /// connections, on a 4-core host shared with PostgreSQL, two runs). At 2,500 rows parallel gained nothing measurable
    /// (1.03x to 1.10x). At 5,500 to 6,500 rows it varied between runs from no gain to 1.6x, a saving of 50 to 90 ms at
    /// best. From 21,500 rows it gained consistently: 1.33x to 1.45x at 21,500 rows, 1.5x to 1.8x at 51,500, 1.4x to 1.7x
    /// at 151,500, and 1.4x (21 s to 15 s) for a page of 500 groups carrying 650,000 members. The other two writers were
    /// measured at the sizes imports and synchronisation give them (seven runs each, the first three under heavy load from
    /// other work on the host), since under the old gate a Full Import's batches of 2,000 objects went parallel on a
    /// 16-core host and now do not. Connected System Object create, 2,000 objects of 6 values (14,000 rows): 1.00x to
    /// 1.19x, never consistently ahead, at best 60 ms of a 400 ms batch; 1,000 of 6 (7,000 rows): 0.75x to 1.52x; 2,000
    /// of 20 (42,000 rows): 1.35x to 1.76x in every run. Execution item flush, an import's 2,000 items with an outcome
    /// each (4,000 rows): 0.90x to 1.31x for the items and outcomes, and 0.95x to 1.09x once the change records written
    /// beside them are counted; a synchronisation page's 500 items with three outcomes each (2,000 rows): 0.52x to 1.24x,
    /// usually slower. Neither has a size below this one at which parallel reliably pays, so all three writers share
    /// it. The threshold sits where the
    /// gain becomes reliable rather than where it first appears, because a parallel write commits in phases and on
    /// several connections, and so is held to its callers' expectations by removing what it wrote when it fails (see
    /// <c>SyncRepository.RemoveRowsOfFailedParallelWriteAsync</c>) rather than by one transaction: below this size the
    /// few milliseconds it might save do not buy that.
    /// </remarks>
    public const int MinimumRowsForParallelWrite = 20_000;

    /// <summary>
    /// Whether a bulk write of <paramref name="rowCount"/> rows goes across parallel connections (#2872). It does only
    /// when it is large enough to gain from it, there is more than one connection to use, the repository has a
    /// connection string to open them with, and the caller holds no transaction on its own context: writes on
    /// connections of their own cannot join that transaction, so they would commit whatever the caller later rolls back
    /// and could wait forever on rows the caller has written and not yet committed.
    /// </summary>
    /// <param name="rowCount">Every row the call writes: objects plus their attribute values, or execution items plus
    /// their outcomes.</param>
    /// <param name="parallelism">The connections available (<see cref="GetWriteParallelism"/>).</param>
    /// <param name="connectionString">The connection string parallel connections are opened with; null when the
    /// repository could not build one.</param>
    /// <param name="callerHoldsTransaction">Whether the caller's context has a transaction open.</param>
    /// <param name="rowThreshold">The threshold; <see cref="MinimumRowsForParallelWrite"/> unless a test or benchmark
    /// needs another.</param>
    public static bool ShouldWriteInParallel(
        int rowCount,
        int parallelism,
        string? connectionString,
        bool callerHoldsTransaction,
        int rowThreshold = MinimumRowsForParallelWrite)
        => parallelism > 1
           && connectionString != null
           && !callerHoldsTransaction
           && rowCount >= rowThreshold;

    /// <summary>
    /// Shares <paramref name="parallelism"/> connections between two writes that run at the same time, in proportion to
    /// the rows each writes (#2909), so that together they never hold more connections than one parallel write would. A
    /// side with rows gets at least one connection; a side with none gets none, and the other side all of them.
    /// </summary>
    /// <param name="parallelism">The connections to share; at least two.</param>
    /// <param name="firstRows">The rows the first write partitions across its connections.</param>
    /// <param name="secondRows">The rows the second write partitions across its connections.</param>
    /// <returns>The connections each write may use; they add up to <paramref name="parallelism"/>.</returns>
    public static (int First, int Second) SplitConnections(int parallelism, int firstRows, int secondRows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(firstRows);
        ArgumentOutOfRangeException.ThrowIfNegative(secondRows);

        if (secondRows == 0)
            return (parallelism, 0);
        if (firstRows == 0)
            return (0, parallelism);

        var second = (int)Math.Round(parallelism * (double)secondRows / ((long)firstRows + secondRows), MidpointRounding.AwayFromZero);
        second = Math.Clamp(second, 1, parallelism - 1);
        return (parallelism - second, second);
    }

    /// <summary>
    /// Flattens each owner's values into (owner id, value) rows, owner by owner and each owner's values in order, so the
    /// rows can be partitioned evenly across connections by <see cref="Partition{T}"/>. Partitioning the owners instead
    /// puts every value of one owner on one connection, which for a large group is most of the page (#2872).
    /// </summary>
    public static List<(Guid OwnerId, TValue Value)> FlattenValueRows<TOwner, TValue>(
        IEnumerable<TOwner> owners,
        Func<TOwner, Guid> ownerId,
        Func<TOwner, IEnumerable<TValue>> valuesOf)
    {
        var rows = new List<(Guid OwnerId, TValue Value)>();
        foreach (var owner in owners)
        {
            var id = ownerId(owner);
            foreach (var value in valuesOf(owner))
                rows.Add((id, value));
        }

        return rows;
    }

    /// <summary>
    /// Gets the configured write parallelism from the JIM_WRITE_PARALLELISM environment variable.
    /// Defaults to <see cref="Environment.ProcessorCount"/> (minimum 2).
    /// </summary>
    public static int GetWriteParallelism()
    {
        var envValue = Environment.GetEnvironmentVariable("JIM_WRITE_PARALLELISM");
        if (envValue != null && int.TryParse(envValue, out var value) && value > 0)
            return value;

        return Math.Max(2, Environment.ProcessorCount);
    }
}
