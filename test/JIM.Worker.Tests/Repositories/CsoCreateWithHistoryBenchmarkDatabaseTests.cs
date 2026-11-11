// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Measures a Full Import create batch written the old way (the objects across the parallel connections, then the
/// execution items, then the change records on one connection) against the batch written with its history beside it
/// (#2909), for the two shapes the 500,000-object initial load writes: a user batch of 2,000 objects with 15 values each,
/// and a batch of 500 groups holding one very large group. Reports the median of five runs of each. Explicit: run by
/// hand, against <c>JIM_TEST_RESET_*</c>.
/// </summary>
[TestFixture]
[Explicit("Benchmark; run by hand.")]
[Category("RequiresPostgres")]
public class CsoCreateWithHistoryBenchmarkDatabaseTests
{
    private const int Runs = 5;
    private const int TextAttributes = 14;
    private static readonly DateTime Created = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid ActivityId = new("29090000-0000-0000-0000-0000000000b1");

    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the create-with-history benchmark.");
        _connectionString = connectionString!;

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    private sealed record Seeded(int SystemId, int CsTypeId, ConnectedSystemObjectTypeAttribute ExternalId,
        List<ConnectedSystemObjectTypeAttribute> Texts, ConnectedSystemObjectTypeAttribute Member);

    private enum Writer { OldSerial, WithHistory }

    private SyncRepository Repository(JimDbContext context, int parallelism)
    {
        var sync = (SyncRepository)new PostgresDataRepository(context).Sync;
        sync.ConnectionStringForParallelWrites = _connectionString;
        sync.WriteParallelismOverride = parallelism;
        return sync;
    }

    private async Task<Seeded> SeedAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "object", ConnectedSystem = system, Selected = true };
        var externalId = new ConnectedSystemObjectTypeAttribute
        {
            Name = "objectGUID", ConnectedSystemObjectType = csType, Type = AttributeDataType.Guid,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        var texts = Enumerable.Range(0, TextAttributes).Select(i => new ConnectedSystemObjectTypeAttribute
        {
            Name = $"text{i}", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        }).ToList();
        var member = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = csType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        csType.Attributes.Add(externalId);
        csType.Attributes.AddRange(texts);
        csType.Attributes.Add(member);
        var activity = new Activity
        {
            Id = ActivityId, TargetName = "Full Import", TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.InProgress, InitiatedByType = ActivityInitiatorType.System
        };
        seed.AddRange(connectorDefinition, system, csType, activity);
        await seed.SaveChangesAsync();
        return new Seeded(system.Id, csType.Id, externalId, texts, member);
    }

    private static ConnectedSystemObject NewCso(Seeded s, int textValues, string name)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = s.SystemId, TypeId = s.CsTypeId, ExternalIdAttributeId = s.ExternalId.Id,
            Status = ConnectedSystemObjectStatus.Normal, JoinType = ConnectedSystemObjectJoinType.NotJoined, Created = Created
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = s.ExternalId, AttributeId = s.ExternalId.Id, GuidValue = Guid.NewGuid(), ConnectedSystemObject = cso
        });
        for (var t = 0; t < textValues; t++)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Attribute = s.Texts[t], AttributeId = s.Texts[t].Id, StringValue = $"{name} {t}", ConnectedSystemObject = cso
            });
        return cso;
    }

    private static List<ActivityRunProfileExecutionItem> Items(IEnumerable<ConnectedSystemObject> objects) =>
        objects.Select(o =>
        {
            var item = new ActivityRunProfileExecutionItem
            {
                ActivityId = ActivityId, ObjectChangeType = ObjectChangeType.Added, ConnectedSystemObject = o, ObjectTypeSnapshot = "object"
            };
            item.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome { OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded });
            return item;
        }).ToList();

    /// <summary>A user batch: 2,000 objects of 15 values (an External ID and 14 Text values).</summary>
    private static List<ConnectedSystemObject> UserBatch(Seeded s) =>
        Enumerable.Range(0, 2_000).Select(i => NewCso(s, TextAttributes, $"User {i}")).ToList();

    /// <summary>
    /// A group batch: 500 groups, one holding every member written before it and the rest 20 members each.
    /// </summary>
    private static List<ConnectedSystemObject> GroupBatch(Seeded s, List<Guid> members)
    {
        var groups = Enumerable.Range(0, 500).Select(i => NewCso(s, 1, $"Group {i}")).ToList();
        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var count = g == 0 ? members.Count : 20;
            for (var m = 0; m < count; m++)
                group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                {
                    Attribute = s.Member, AttributeId = s.Member.Id, ConnectedSystemObject = group,
                    ReferenceValueId = members[(g * 20 + m) % members.Count],
                    UnresolvedReferenceValue = $"CN=Member {m},OU=People,DC=glitterband,DC=local"
                });
        }

        return groups;
    }

    private async Task<List<Guid>> SeedMembersAsync(Seeded s, int count)
    {
        var people = Enumerable.Range(0, count).Select(i => NewCso(s, 0, $"Person {i}")).ToList();
        await using var context = NewContext();
        await Repository(context, ParallelBatchWriter.GetWriteParallelism()).CreateConnectedSystemObjectsAsync(people);
        return people.Select(p => p.Id).ToList();
    }

    private async Task<double> TimeOneAsync(Writer writer, int parallelism, Func<Seeded, Task<(List<ConnectedSystemObject> Batch, HashSet<Guid> Earlier)>> build)
    {
        var s = await SeedAsync();
        var (batch, earlier) = await build(s);
        var items = Items(batch);
        foreach (var item in items)
            item.Id = Guid.NewGuid();

        await using var context = NewContext();
        var repository = Repository(context, parallelism);
        repository.PrepareConnectedSystemObjectsForCreate(batch, earlier);
        await using (var builderContext = NewContext())
        using (var jim = new JimApplication(new PostgresDataRepository(builderContext)))
            jim.ConnectedSystems.LinkCreateChangeRecords(batch, items, changeTrackingEnabled: true);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        if (writer == Writer.OldSerial)
        {
            await repository.CreateConnectedSystemObjectsAsync(batch, earlier);
            await repository.BulkInsertRpeisAsync(items);
            foreach (var item in items)
                item.ConnectedSystemObjectChange!.ActivityRunProfileExecutionItemId = item.Id;

            // The change records on one connection, as before #2909.
            await repository.PersistRpeiCsoChangesAsync(items, allowParallelWrite: false);
        }
        else
        {
            await repository.CreateConnectedSystemObjectsWithHistoryAsync(batch, items, earlier);
        }

        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private async Task MeasureAsync(string shape, int parallelism, Func<Seeded, Task<(List<ConnectedSystemObject> Batch, HashSet<Guid> Earlier)>> build)
    {
        var results = new Dictionary<Writer, List<double>> { [Writer.OldSerial] = [], [Writer.WithHistory] = [] };
        for (var run = 0; run < Runs; run++)
        {
            // Alternate which goes first, so neither always meets a warm or a cold cache.
            foreach (var writer in run % 2 == 0 ? new[] { Writer.OldSerial, Writer.WithHistory } : [Writer.WithHistory, Writer.OldSerial])
                results[writer].Add(await TimeOneAsync(writer, parallelism, build));
        }

        static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
        var old = Median(results[Writer.OldSerial]);
        var withHistory = Median(results[Writer.WithHistory]);
        TestContext.Out.WriteLine($"{shape}, {parallelism} connections: old serial median {old:F0} ms [{string.Join(", ", results[Writer.OldSerial].Select(v => v.ToString("F0")))}], " +
            $"with history median {withHistory:F0} ms [{string.Join(", ", results[Writer.WithHistory].Select(v => v.ToString("F0")))}], speed-up {old / withHistory:F2}x");
    }

    [TestCase(4)]
    [TestCase(16)]
    public async Task UserBatch_OldSerialAgainstWithHistoryAsync(int parallelism) =>
        await MeasureAsync("User batch (2,000 x 15 values)", parallelism, s => Task.FromResult((UserBatch(s), new HashSet<Guid>())));

    [TestCase(4)]
    [TestCase(16)]
    public async Task LargeGroupBatch_OldSerialAgainstWithHistoryAsync(int parallelism) =>
        await MeasureAsync("Group batch (500 groups, one of 100,000 members)", parallelism, async s =>
        {
            var members = await SeedMembersAsync(s, 100_000);
            return (GroupBatch(s, members), members.ToHashSet());
        });
}
