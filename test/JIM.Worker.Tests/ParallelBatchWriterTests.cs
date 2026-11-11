// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
    public void ShouldWriteInParallel_BelowRowThreshold_ReturnsFalse()
    {
        var result = ParallelBatchWriter.ShouldWriteInParallel(
            ParallelBatchWriter.MinimumRowsForParallelWrite - 1, parallelism: 16, connectionString: "Host=db", callerHoldsTransaction: false);

        Assert.That(result, Is.False);
    }

    [Test]
    public void ShouldWriteInParallel_AtRowThreshold_ReturnsTrue()
    {
        var result = ParallelBatchWriter.ShouldWriteInParallel(
            ParallelBatchWriter.MinimumRowsForParallelWrite, parallelism: 16, connectionString: "Host=db", callerHoldsTransaction: false);

        Assert.That(result, Is.True);
    }

    /// <summary>
    /// The gate used to be <c>objects &gt;= parallelism * 50</c>, so the more cores a host had the less it wrote in
    /// parallel: a 500-object synchronisation page never went parallel on 11 or more cores (#2872). The row threshold
    /// must give the same answer whatever the parallelism.
    /// </summary>
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(16)]
    [TestCase(64)]
    public void ShouldWriteInParallel_RowThreshold_DoesNotScaleWithParallelism(int parallelism)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ParallelBatchWriter.ShouldWriteInParallel(
                ParallelBatchWriter.MinimumRowsForParallelWrite - 1, parallelism, "Host=db", callerHoldsTransaction: false), Is.False);
            Assert.That(ParallelBatchWriter.ShouldWriteInParallel(
                ParallelBatchWriter.MinimumRowsForParallelWrite, parallelism, "Host=db", callerHoldsTransaction: false), Is.True);
        }
    }

    /// <summary>
    /// The page that motivated #2872: 500 groups carrying 649,735 membership values, written on one connection on a
    /// 16-core host because the gate counted the 500 objects and not the rows.
    /// </summary>
    [Test]
    public void ShouldWriteInParallel_PageOfFewObjectsCarryingManyValues_ReturnsTrueOnLargeHost()
    {
        var result = ParallelBatchWriter.ShouldWriteInParallel(
            rowCount: 500 + 649_735, parallelism: 16, connectionString: "Host=db", callerHoldsTransaction: false);

        Assert.That(result, Is.True);
    }

    [Test]
    public void ShouldWriteInParallel_ParallelismOfOne_ReturnsFalse()
    {
        var result = ParallelBatchWriter.ShouldWriteInParallel(
            1_000_000, parallelism: 1, connectionString: "Host=db", callerHoldsTransaction: false);

        Assert.That(result, Is.False);
    }

    [Test]
    public void ShouldWriteInParallel_NoConnectionString_ReturnsFalse()
    {
        var result = ParallelBatchWriter.ShouldWriteInParallel(
            1_000_000, parallelism: 16, connectionString: null, callerHoldsTransaction: false);

        Assert.That(result, Is.False);
    }

    /// <summary>
    /// Writes on connections of their own cannot join a transaction the caller holds on its context: they would commit
    /// whatever the caller later rolls back, and could wait forever on rows that transaction has written and not yet
    /// committed. A caller holding a transaction is written on its own connection, inside it.
    /// </summary>
    [Test]
    public void ShouldWriteInParallel_CallerHoldsTransaction_ReturnsFalse()
    {
        var result = ParallelBatchWriter.ShouldWriteInParallel(
            1_000_000, parallelism: 16, connectionString: "Host=db", callerHoldsTransaction: true);

        Assert.That(result, Is.False);
    }

    [Test]
    public void ShouldWriteInParallel_ExplicitRowThreshold_IsHonoured()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ParallelBatchWriter.ShouldWriteInParallel(9, 4, "Host=db", false, rowThreshold: 10), Is.False);
            Assert.That(ParallelBatchWriter.ShouldWriteInParallel(10, 4, "Host=db", false, rowThreshold: 10), Is.True);
        }
    }

    /// <summary>
    /// The 500,000-object Full Synchronisation's people pages write 500 Metaverse Objects and 5,500 values, 6,000 rows,
    /// and took about 98 ms each on one connection while the database had nothing else to do. On a 16-core host the same
    /// page gained 2.2x to 2.65x on four connections (#2669), so it must go parallel.
    /// </summary>
    [Test]
    public void ShouldWriteInParallel_SynchronisationPeoplePage_ReturnsTrue()
    {
        var result = ParallelBatchWriter.ShouldWriteInParallel(
            rowCount: 500 + 5_500, parallelism: 16, connectionString: "Host=db", callerHoldsTransaction: false);

        Assert.That(result, Is.True);
    }

    /// <summary>
    /// The connections a write uses grow with its rows, so a write just over the gate is not spread across every core
    /// (16 connections on a 6,000-row page varied from 21 to 72 ms; 4 never did worse than 1.8x), while a large write
    /// keeps all of them.
    /// </summary>
    [TestCase(4_999, 16, 1)] // below the gate: one connection
    [TestCase(5_000, 16, 4)] // at the gate: four connections' worth of rows
    [TestCase(6_000, 16, 4)] // a synchronisation people page
    [TestCase(7_000, 16, 5)] // a Full Import batch of 1,000 objects with six values
    [TestCase(14_000, 16, 11)]
    [TestCase(19_999, 16, 15)]
    [TestCase(20_000, 16, 16)] // every connection from here
    [TestCase(651_500, 16, 16)] // a page of 500 groups carrying 650,000 members
    [TestCase(6_000, 4, 4)]
    [TestCase(651_500, 4, 4)]
    [TestCase(5_000, 2, 2)]
    [TestCase(651_500, 64, 64)]
    public void ConnectionsForWrite_ScalesWithRowsUpToTheParallelism(int rowCount, int parallelism, int expected)
    {
        var result = ParallelBatchWriter.ConnectionsForWrite(rowCount, parallelism, "Host=db", callerHoldsTransaction: false);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void ConnectionsForWrite_ParallelismOfOne_ReturnsOne()
    {
        var result = ParallelBatchWriter.ConnectionsForWrite(1_000_000, parallelism: 1, connectionString: "Host=db", callerHoldsTransaction: false);

        Assert.That(result, Is.EqualTo(1));
    }

    [Test]
    public void ConnectionsForWrite_NoConnectionString_ReturnsOne()
    {
        var result = ParallelBatchWriter.ConnectionsForWrite(1_000_000, parallelism: 16, connectionString: null, callerHoldsTransaction: false);

        Assert.That(result, Is.EqualTo(1));
    }

    [Test]
    public void ConnectionsForWrite_CallerHoldsTransaction_ReturnsOne()
    {
        var result = ParallelBatchWriter.ConnectionsForWrite(1_000_000, parallelism: 16, connectionString: "Host=db", callerHoldsTransaction: true);

        Assert.That(result, Is.EqualTo(1));
    }

    /// <summary>
    /// A write that goes parallel uses at least two connections, whatever its rows: a test that lowers the threshold to
    /// force the parallel path on a handful of rows still gets one.
    /// </summary>
    [Test]
    public void ConnectionsForWrite_ThresholdLoweredBelowOneConnectionsRows_UsesTwo()
    {
        var result = ParallelBatchWriter.ConnectionsForWrite(10, parallelism: 16, "Host=db", callerHoldsTransaction: false, rowThreshold: 0);

        Assert.That(result, Is.EqualTo(2));
    }

    [Test]
    public void ConnectionsForWrite_ExplicitRowsPerConnection_IsHonoured()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ParallelBatchWriter.ConnectionsForWrite(10, 4, "Host=db", false, rowThreshold: 0, rowsPerConnection: 1), Is.EqualTo(4),
                "one row a connection: every connection the parallelism allows");
            Assert.That(ParallelBatchWriter.ConnectionsForWrite(30, 16, "Host=db", false, rowThreshold: 0, rowsPerConnection: 10), Is.EqualTo(3));
        }
    }

    [Test]
    public void ConnectionsForWrite_RowsPerConnectionBelowOne_Throws()
    {
        Assert.That(() => ParallelBatchWriter.ConnectionsForWrite(10_000, 16, "Host=db", false, rowsPerConnection: 0),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
    }

    /// <summary>
    /// The two decisions never disagree: a write goes parallel exactly when it is given more than one connection.
    /// </summary>
    [TestCase(0)]
    [TestCase(4_999)]
    [TestCase(5_000)]
    [TestCase(1_000_000)]
    public void ConnectionsForWrite_AgreesWithShouldWriteInParallel(int rowCount)
    {
        using (Assert.EnterMultipleScope())
        {
            foreach (var (parallelism, connectionString, transaction) in new[] { (16, "Host=db", false), (1, "Host=db", false), (16, (string?)null, false), (16, "Host=db", true) })
            {
                Assert.That(ParallelBatchWriter.ConnectionsForWrite(rowCount, parallelism, connectionString, transaction) > 1,
                    Is.EqualTo(ParallelBatchWriter.ShouldWriteInParallel(rowCount, parallelism, connectionString, transaction)));
            }
        }
    }

    [Test]
    public void FlattenValueRows_PreservesOwnerOrderThenValueOrder_AndPairsEachValueWithItsOwner()
    {
        var owners = new List<(Guid Id, List<string> Values)>
        {
            (Guid.NewGuid(), ["a1", "a2"]),
            (Guid.NewGuid(), []),
            (Guid.NewGuid(), ["c1", "c2", "c3"])
        };

        var rows = ParallelBatchWriter.FlattenValueRows(owners, o => o.Id, o => o.Values);

        Assert.That(rows, Is.EqualTo(new List<(Guid, string)>
        {
            (owners[0].Id, "a1"), (owners[0].Id, "a2"),
            (owners[2].Id, "c1"), (owners[2].Id, "c2"), (owners[2].Id, "c3")
        }));
    }

    /// <summary>
    /// A synchronisation page of groups is a few very large objects and many small ones. Partitioned by object, the
    /// largest group's values all land on one connection, which then writes alone long after the others finish (#2872).
    /// Partitioned by value row, every connection gets an equal share, the largest group spreads across all of them,
    /// and each row is written exactly once, in order.
    /// </summary>
    [Test]
    public void Partition_ValueRowsOfPageWithOneLargeGroup_SpreadsTheGroupEvenlyAndWritesEveryRowOnceInOrder()
    {
        var owners = new List<(Guid Id, List<int> Values)> { (Guid.NewGuid(), Enumerable.Range(0, 200_000).ToList()) };
        for (var i = 0; i < 499; i++)
            owners.Add((Guid.NewGuid(), Enumerable.Range(0, 3).ToList()));
        var rows = ParallelBatchWriter.FlattenValueRows(owners, o => o.Id, o => o.Values);

        var partitions = ParallelBatchWriter.Partition(rows, 8);

        var sizes = partitions.Select(p => p.Count).ToList();
        var largeGroupRowsPerPartition = partitions.Select(p => p.Count(r => r.OwnerId == owners[0].Id)).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(partitions, Has.Count.EqualTo(8));
            Assert.That(sizes.Max() - sizes.Min(), Is.LessThanOrEqualTo(1), "every connection gets an equal share of rows");
            Assert.That(largeGroupRowsPerPartition, Has.All.GreaterThan(0), "the large group spreads across every connection");
            Assert.That(partitions.SelectMany(p => p).ToList(), Is.EqualTo(rows), "every row exactly once, in order");
        }
    }

    /// <summary>
    /// Two writes running at once share one write's connections (#2909): an import batch's objects and its history must
    /// never together hold more than a single parallel write did, or two of them would exhaust the pool.
    /// </summary>
    [TestCase(16, 32_000, 62_000)] // a Full Import user batch: 2,000 objects of 15 values, and their change records
    [TestCase(16, 1_220_000, 1_220_500)] // a batch holding a large group
    [TestCase(4, 1, 1_000_000)]
    [TestCase(4, 1_000_000, 1)]
    [TestCase(2, 10, 10)]
    public void SplitConnections_BothWritesHaveRows_GivesEachAtLeastOneAndTogetherNoMoreThanTheParallelism(int parallelism, int firstRows, int secondRows)
    {
        var (first, second) = ParallelBatchWriter.SplitConnections(parallelism, firstRows, secondRows);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first + second, Is.EqualTo(parallelism));
            Assert.That(first, Is.GreaterThanOrEqualTo(1));
            Assert.That(second, Is.GreaterThanOrEqualTo(1));
        }
    }

    [Test]
    public void SplitConnections_RowsInProportion_SharesConnectionsInProportion()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ParallelBatchWriter.SplitConnections(16, 32_000, 62_000), Is.EqualTo((5, 11)),
                "a user batch's history writes about twice the rows its objects do");
            Assert.That(ParallelBatchWriter.SplitConnections(16, 1_000_000, 1_000_000), Is.EqualTo((8, 8)));
        }
    }

    [Test]
    public void SplitConnections_OneWriteHasNoRows_GivesTheOtherEveryConnection()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ParallelBatchWriter.SplitConnections(16, 32_000, 0), Is.EqualTo((16, 0)), "change tracking off: no change records");
            Assert.That(ParallelBatchWriter.SplitConnections(16, 0, 62_000), Is.EqualTo((0, 16)));
        }
    }

    [Test]
    public void SplitConnections_FewerThanTwoConnections_Throws()
    {
        Assert.That(() => ParallelBatchWriter.SplitConnections(1, 10, 10), Throws.InstanceOf<ArgumentOutOfRangeException>(),
            "two writes at once need a connection each; the parallel gate never lets one connection through");
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
