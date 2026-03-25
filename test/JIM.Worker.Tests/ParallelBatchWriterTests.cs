using JIM.PostgresData;
using NUnit.Framework;

namespace JIM.Worker.Tests;

[TestFixture]
public class ParallelBatchWriterTests
{
    [Test]
    public void Partition_EmptyList_ReturnsEmptyPartitions()
    {
        var items = new List<int>();
        var partitions = ParallelBatchWriter.Partition(items, 4);

        Assert.That(partitions, Has.Count.EqualTo(0));
    }

    [Test]
    public void Partition_FewerItemsThanPartitions_ReturnsOneItemPerPartition()
    {
        var items = new List<int> { 1, 2, 3 };
        var partitions = ParallelBatchWriter.Partition(items, 8);

        Assert.That(partitions, Has.Count.EqualTo(3));
        Assert.That(partitions[0], Is.EqualTo(new[] { 1 }));
        Assert.That(partitions[1], Is.EqualTo(new[] { 2 }));
        Assert.That(partitions[2], Is.EqualTo(new[] { 3 }));
    }

    [Test]
    public void Partition_EvenSplit_DistributesEvenly()
    {
        var items = Enumerable.Range(1, 12).ToList();
        var partitions = ParallelBatchWriter.Partition(items, 4);

        Assert.That(partitions, Has.Count.EqualTo(4));
        Assert.That(partitions[0], Has.Count.EqualTo(3));
        Assert.That(partitions[1], Has.Count.EqualTo(3));
        Assert.That(partitions[2], Has.Count.EqualTo(3));
        Assert.That(partitions[3], Has.Count.EqualTo(3));

        // Verify all items are present
        var allItems = partitions.SelectMany(p => p).OrderBy(x => x).ToList();
        Assert.That(allItems, Is.EqualTo(Enumerable.Range(1, 12).ToList()));
    }

    [Test]
    public void Partition_UnevenSplit_DistributesRemainderAcrossFirstPartitions()
    {
        var items = Enumerable.Range(1, 10).ToList();
        var partitions = ParallelBatchWriter.Partition(items, 4);

        Assert.That(partitions, Has.Count.EqualTo(4));
        // 10 / 4 = 2 remainder 2, so first 2 partitions get 3 items, last 2 get 2
        Assert.That(partitions[0], Has.Count.EqualTo(3));
        Assert.That(partitions[1], Has.Count.EqualTo(3));
        Assert.That(partitions[2], Has.Count.EqualTo(2));
        Assert.That(partitions[3], Has.Count.EqualTo(2));

        // Verify all items are present
        var allItems = partitions.SelectMany(p => p).OrderBy(x => x).ToList();
        Assert.That(allItems, Is.EqualTo(Enumerable.Range(1, 10).ToList()));
    }

    [Test]
    public void Partition_SinglePartition_ReturnsSingleList()
    {
        var items = Enumerable.Range(1, 5).ToList();
        var partitions = ParallelBatchWriter.Partition(items, 1);

        Assert.That(partitions, Has.Count.EqualTo(1));
        Assert.That(partitions[0], Is.EqualTo(items));
    }

    [Test]
    public void Partition_SingleItem_ReturnsSinglePartition()
    {
        var items = new List<int> { 42 };
        var partitions = ParallelBatchWriter.Partition(items, 4);

        Assert.That(partitions, Has.Count.EqualTo(1));
        Assert.That(partitions[0], Is.EqualTo(new[] { 42 }));
    }

    [Test]
    public void Partition_PreservesOrder()
    {
        var items = new List<string> { "a", "b", "c", "d", "e", "f", "g" };
        var partitions = ParallelBatchWriter.Partition(items, 3);

        Assert.That(partitions, Has.Count.EqualTo(3));
        // 7 / 3 = 2 remainder 1, so first partition gets 3, rest get 2
        Assert.That(partitions[0], Is.EqualTo(new[] { "a", "b", "c" }));
        Assert.That(partitions[1], Is.EqualTo(new[] { "d", "e" }));
        Assert.That(partitions[2], Is.EqualTo(new[] { "f", "g" }));
    }

    [Test]
    public async Task ExecuteAsync_EmptyItems_DoesNotCallWriteAction()
    {
        var callCount = 0;

        await ParallelBatchWriter.ExecuteAsync(
            new List<int>(),
            parallelism: 4,
            connectionString: "Host=localhost", // won't be used
            writeAction: async (conn, items) =>
            {
                Interlocked.Increment(ref callCount);
                await Task.CompletedTask;
            });

        Assert.That(callCount, Is.EqualTo(0));
    }

    [Test]
    public async Task ExecuteAsync_SingleItem_CallsWriteActionOnce()
    {
        var processedItems = new List<int>();
        var lockObj = new object();

        await ParallelBatchWriter.ExecuteAsync(
            new List<int> { 1 },
            parallelism: 4,
            connectionString: null, // null signals dry-run / pass null to action
            writeAction: async (conn, items) =>
            {
                lock (lockObj) { processedItems.AddRange(items); }
                await Task.CompletedTask;
            });

        Assert.That(processedItems, Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public async Task ExecuteAsync_MultiplePartitions_ProcessesAllItems()
    {
        var processedItems = new List<int>();
        var lockObj = new object();

        await ParallelBatchWriter.ExecuteAsync(
            Enumerable.Range(1, 100).ToList(),
            parallelism: 4,
            connectionString: null,
            writeAction: async (conn, items) =>
            {
                lock (lockObj) { processedItems.AddRange(items); }
                await Task.CompletedTask;
            });

        Assert.That(processedItems.OrderBy(x => x).ToList(), Is.EqualTo(Enumerable.Range(1, 100).ToList()));
    }

    [Test]
    public async Task ExecuteAsync_RespectsConcurrencyLimit()
    {
        var maxConcurrent = 0;
        var currentConcurrent = 0;
        var lockObj = new object();

        await ParallelBatchWriter.ExecuteAsync(
            Enumerable.Range(1, 20).ToList(),
            parallelism: 3,
            connectionString: null,
            writeAction: async (conn, items) =>
            {
                var current = Interlocked.Increment(ref currentConcurrent);
                lock (lockObj) { maxConcurrent = Math.Max(maxConcurrent, current); }
                await Task.Delay(50); // simulate work
                Interlocked.Decrement(ref currentConcurrent);
            });

        Assert.That(maxConcurrent, Is.LessThanOrEqualTo(3));
    }

    [Test]
    public void GetWriteParallelism_DefaultsToProcessorCount()
    {
        // Clear env var to test default
        var original = Environment.GetEnvironmentVariable("JIM_WRITE_PARALLELISM");
        try
        {
            Environment.SetEnvironmentVariable("JIM_WRITE_PARALLELISM", null);
            var result = ParallelBatchWriter.GetWriteParallelism();
            Assert.That(result, Is.EqualTo(Math.Max(2, Environment.ProcessorCount)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("JIM_WRITE_PARALLELISM", original);
        }
    }

    [Test]
    public void GetWriteParallelism_ReadsEnvironmentVariable()
    {
        var original = Environment.GetEnvironmentVariable("JIM_WRITE_PARALLELISM");
        try
        {
            Environment.SetEnvironmentVariable("JIM_WRITE_PARALLELISM", "6");
            var result = ParallelBatchWriter.GetWriteParallelism();
            Assert.That(result, Is.EqualTo(6));
        }
        finally
        {
            Environment.SetEnvironmentVariable("JIM_WRITE_PARALLELISM", original);
        }
    }

    [Test]
    public void GetWriteParallelism_InvalidValue_FallsBackToDefault()
    {
        var original = Environment.GetEnvironmentVariable("JIM_WRITE_PARALLELISM");
        try
        {
            Environment.SetEnvironmentVariable("JIM_WRITE_PARALLELISM", "notanumber");
            var result = ParallelBatchWriter.GetWriteParallelism();
            Assert.That(result, Is.EqualTo(Math.Max(2, Environment.ProcessorCount)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("JIM_WRITE_PARALLELISM", original);
        }
    }

    [Test]
    public void GetWriteParallelism_ZeroOrNegative_FallsBackToDefault()
    {
        var original = Environment.GetEnvironmentVariable("JIM_WRITE_PARALLELISM");
        try
        {
            Environment.SetEnvironmentVariable("JIM_WRITE_PARALLELISM", "0");
            var result = ParallelBatchWriter.GetWriteParallelism();
            Assert.That(result, Is.EqualTo(Math.Max(2, Environment.ProcessorCount)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("JIM_WRITE_PARALLELISM", original);
        }
    }
}
