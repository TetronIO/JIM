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
