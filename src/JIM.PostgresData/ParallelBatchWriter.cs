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
            // Single partition: no need for parallel overhead.
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
    /// The rows each connection of a parallel write is given, at most: a write of <c>n</c> rows uses
    /// <c>n / RowsPerParallelConnection</c> connections, at least two and at most the write parallelism
    /// (<see cref="ConnectionsForWrite"/>). So a write just over <see cref="MinimumRowsForParallelWrite"/> uses four
    /// connections, and a write of 20,000 rows or more uses all sixteen of a 16-core host.
    /// </summary>
    /// <remarks>
    /// Set from <c>ParallelBulkWriteBenchmarkDatabaseTests</c> run on a 16-core host against the database configuration the
    /// 500,000-object runs use (#2669; three runs, median of three each, single connection against 4, 8 and 16). Every
    /// write gained more from more connections once it was large: a page of 500 groups gained 2.7x at 4 connections and
    /// 3.6x to 4.2x at 16 from 21,500 rows, and 4.1x to 5.2x at 16 for 651,500 rows; Connected System Object create gained
    /// 2.1x at 4 and 2.5x to 2.9x at 16 at 14,000 rows. At about 6,000 rows the extra connections stopped paying
    /// reliably: the synchronisation people page (500 objects, 5,500 values) gained 2.2x to 2.65x at 4 connections, every
    /// one of nine measurements at least 1.8x, but at 16 its run medians ranged from 1.4x to 3.9x (21 to 72 ms against 82
    /// ms on one), and at 8 likewise. That is four connections at 6,000 rows, about 1,500 rows each, and sixteen from
    /// 20,000 rows, 1,250 each; 1,250 gives both (6,000 rows: 4; 20,000 rows: 16) and puts the writes measured between
    /// (6,500 to 14,000 rows) on 5 to 11 connections, where 4 and 16 both gained about 2x or more. The 5,500-row page gained
    /// more at 16 (3.3x to 3.8x) than at 4 (2.3x to 2.6x) in all three runs, so this leaves some gain unclaimed on the
    /// smallest writes in exchange for a fan-out that never measured below 1.8x.
    /// </remarks>
    public const int RowsPerParallelConnection = 1_250;

    /// <summary>
    /// The number of rows a call must write before the bulk writers spread it across parallel connections: four
    /// connections' worth (<see cref="RowsPerParallelConnection"/>), so the smallest parallel write uses four, the fan-out
    /// measured reliable at that size. Rows, not objects: the cost of a bulk write is in its rows (each one COPY'd and
    /// checked against its foreign keys), and a page of 500 groups can carry 650,000 membership values. Not scaled by the
    /// core count: the gate before #2872, <c>objects &gt;= parallelism * 50</c>, meant the larger the host the less it
    /// wrote in parallel.
    /// </summary>
    /// <remarks>
    /// The 500,000-object Full Synchronisation's people pages write 6,000 rows each, and under the previous threshold of
    /// 20,000 rows wrote them on one connection: 97.6 s across 1,000 pages, about 98 ms a page, while the database had
    /// nothing else to do (#2669). On the 16-core host that page gained 2.2x to 2.65x at 4 connections. Below this size
    /// the gain is real but small: 1.4x to 1.9x at 2,500 rows, about 15 ms of 40, and 1.5x to 1.9x for a synchronisation
    /// page's 2,000-row execution item flush, saving 7 to 8 ms. The threshold sits where the gain is worth tens of milliseconds a
    /// call rather than where it first appears, because a parallel write commits in phases and on several connections,
    /// and so is held to its callers' expectations by removing what it wrote when it fails (see
    /// <c>SyncRepository.RemoveRowsOfFailedParallelWriteAsync</c>) rather than by one transaction.
    /// The previous threshold was set on a 4-core host shared by the test host and PostgreSQL, where 4 connections gained
    /// nothing reliably below 20,000 rows (no gain to 1.6x for Metaverse Objects at 5,500 to 6,500 rows, 0.75x to 1.5x for
    /// a 7,000-row Connected System Object create, partly under load from other work) and only 1.3x to 1.8x above it:
    /// there the cores, not the writes, were the limit. A host that small now writes these sizes on its four connections,
    /// so its gain is no longer what the threshold is set from; <c>JIM_WRITE_PARALLELISM=1</c> keeps every write on one
    /// connection where that suits it better.
    /// </remarks>
    public const int MinimumRowsForParallelWrite = 4 * RowsPerParallelConnection;

    /// <summary>
    /// Whether a bulk write of <paramref name="rowCount"/> rows goes across parallel connections (#2872). It does only
    /// when it is large enough to gain from it, there is more than one connection to use, the repository has a
    /// connection string to open them with, and the caller holds no transaction on its own context: writes on
    /// connections of their own cannot join that transaction, so they would commit whatever the caller later rolls back
    /// and could wait forever on rows the caller has written and not yet committed. A writer that goes parallel takes
    /// its connection count from <see cref="ConnectionsForWrite"/>, which agrees with this.
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
    /// The connections a bulk write of <paramref name="rowCount"/> rows uses: one when it does not go parallel
    /// (<see cref="ShouldWriteInParallel"/>), otherwise one for every <paramref name="rowsPerConnection"/> rows, at least
    /// two and at most <paramref name="parallelism"/>. A small write is not spread across every core, where its extra
    /// connections gain little and vary most, and a large one keeps every connection (#2669).
    /// </summary>
    /// <param name="rowCount">Every row the call writes, counted as for <see cref="ShouldWriteInParallel"/>.</param>
    /// <param name="parallelism">The most connections the write may use (<see cref="GetWriteParallelism"/>).</param>
    /// <param name="connectionString">The connection string parallel connections are opened with; null when the
    /// repository could not build one.</param>
    /// <param name="callerHoldsTransaction">Whether the caller's context has a transaction open.</param>
    /// <param name="rowThreshold">The threshold; <see cref="MinimumRowsForParallelWrite"/> unless a test or benchmark
    /// needs another.</param>
    /// <param name="rowsPerConnection">The rows each connection is given; <see cref="RowsPerParallelConnection"/> unless
    /// a test or benchmark needs another (1 gives every write the whole <paramref name="parallelism"/>).</param>
    /// <returns>1 for a write on one connection; otherwise between 2 and <paramref name="parallelism"/>.</returns>
    public static int ConnectionsForWrite(
        int rowCount,
        int parallelism,
        string? connectionString,
        bool callerHoldsTransaction,
        int rowThreshold = MinimumRowsForParallelWrite,
        int rowsPerConnection = RowsPerParallelConnection)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rowsPerConnection, 1);

        if (!ShouldWriteInParallel(rowCount, parallelism, connectionString, callerHoldsTransaction, rowThreshold))
            return 1;

        return Math.Clamp(rowCount / rowsPerConnection, 2, parallelism);
    }

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
