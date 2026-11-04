// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the per-Partition Delta Import watermark write (#2715). The write is a raw-SQL
/// upsert joined to the Partitions table and run in one transaction with the persisted connector data, so only a round
/// trip can prove that each value lands in its column, that the join skips a Partition the run may not write, that the
/// whole write is atomic, and that two writers for one Partition do not fail each other. The in-memory provider runs
/// none of this SQL. Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemPartitionWatermarkDatabaseTests
{
    private static readonly DateTime T0 = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);

    private string _connectionString = null!;

    private JimDbContext NewContext(QueryTrackingBehavior tracking = QueryTrackingBehavior.NoTracking)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(tracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Partition watermark tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    [Test]
    public async Task RecordImportWatermarksAsync_NewAndExistingPartitions_UpsertsEveryFieldAndReadsThemBackAsync()
    {
        var (systemId, corp, partners) = await SeedAsync();
        await RecordAsync(systemId, new Dictionary<int, string> { [corp] = "corp-1" }, recordedAt: T0);

        // Corp's row is replaced and Partners' created by the same write.
        await RecordAsync(systemId, new Dictionary<int, string> { [corp] = "corp-2", [partners] = "partners-1" }, recordedAt: T0.AddHours(1));

        var rows = (await ReadAsync(systemId)).ToDictionary(w => w.PartitionId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows.Keys, Is.EquivalentTo(new[] { corp, partners }));
            Assert.That(rows[corp].ConnectedSystemId, Is.EqualTo(systemId));
            Assert.That(rows[corp].Value, Is.EqualTo("corp-2"));
            Assert.That(rows[corp].RecordedAt, Is.EqualTo(T0.AddHours(1)));
            Assert.That(rows[partners].ConnectedSystemId, Is.EqualTo(systemId));
            Assert.That(rows[partners].Value, Is.EqualTo("partners-1"));
            Assert.That(rows[partners].RecordedAt, Is.EqualTo(T0.AddHours(1)));
        }
    }

    [Test]
    public async Task RecordImportWatermarksAsync_SameValueAgain_RewritesRecordedAtAsync()
    {
        // Decision 6: RecordedAt reads as "last read", so an unchanged value still moves it.
        var (systemId, corp, _) = await SeedAsync();
        await RecordAsync(systemId, new Dictionary<int, string> { [corp] = "corp-1" }, recordedAt: T0);

        await RecordAsync(systemId, new Dictionary<int, string> { [corp] = "corp-1" }, recordedAt: T0.AddDays(1));

        var row = (await ReadAsync(systemId)).Single();
        Assert.That(row.RecordedAt, Is.EqualTo(T0.AddDays(1)));
    }

    [Test]
    public async Task RecordImportWatermarksAsync_NullValue_DeletesThatPartitionsRowAsync()
    {
        var (systemId, corp, partners) = await SeedAsync();
        await RecordAsync(systemId, new Dictionary<int, string> { [corp] = "corp-1", [partners] = "partners-1" });

        await using (var ctx = NewContext())
        {
            var result = await new PostgresDataRepository(ctx).ConnectedSystems.RecordImportWatermarksAsync(
                systemId, null, new Dictionary<int, string>(), [corp], T0);
            Assert.That(result.PartitionWatermarksCleared, Is.EqualTo(1));
        }

        var rows = await ReadAsync(systemId);
        Assert.That(rows.Select(w => w.PartitionId), Is.EqualTo(new[] { partners }), "only the cleared Partition's row goes");
    }

    [Test]
    public async Task RecordImportWatermarksAsync_PartitionRemovedDuringTheRun_SkipsItWithoutFailingAsync()
    {
        var (systemId, corp, partners) = await SeedAsync();

        // A hierarchy refresh removes Partners while the import is still running.
        await using (var ctx = NewContext())
            await ctx.Database.ExecuteSqlRawAsync(@"DELETE FROM ""ConnectedSystemPartitions"" WHERE ""Id"" = {0}", partners);

        ImportWatermarkRecordingResult result;
        await using (var ctx = NewContext())
        {
            result = await new PostgresDataRepository(ctx).ConnectedSystems.RecordImportWatermarksAsync(
                systemId, "connector-data", new Dictionary<int, string> { [corp] = "corp-1", [partners] = "partners-1" }, [], T0);
        }

        var rows = await ReadAsync(systemId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows.Select(w => w.PartitionId), Is.EqualTo(new[] { corp }));
            Assert.That(result.PartitionWatermarksRecorded, Is.EqualTo(1));
            Assert.That(await ReadPersistedConnectorDataAsync(systemId), Is.EqualTo("connector-data"),
                "the rest of the write still lands");
        }
    }

    [Test]
    public async Task RecordImportWatermarksAsync_PartitionOfAnotherConnectedSystem_WritesNothingForItAsync()
    {
        var (systemId, corp, _) = await SeedAsync();
        var (otherSystemId, otherCorp, _) = await SeedAsync("Other Directory");
        await RecordAsync(otherSystemId, new Dictionary<int, string> { [otherCorp] = "other-1" });

        await using (var ctx = NewContext())
        {
            await new PostgresDataRepository(ctx).ConnectedSystems.RecordImportWatermarksAsync(
                systemId, null, new Dictionary<int, string> { [corp] = "corp-1", [otherCorp] = "intruder" }, [otherCorp], T0.AddHours(1));
        }

        var otherRows = await ReadAsync(otherSystemId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await ReadAsync(systemId)).Select(w => w.PartitionId), Is.EqualTo(new[] { corp }));
            Assert.That(otherRows.Single().Value, Is.EqualTo("other-1"), "another Connected System's Partition is neither written...");
            Assert.That(otherRows.Single().RecordedAt, Is.EqualTo(T0));
            Assert.That(otherRows, Has.Count.EqualTo(1), "...nor cleared");
        }
    }

    [Test]
    public async Task RecordImportWatermarksAsync_ContextTracksTheRunsGraph_WritesOnlyTheWatermarksAsync()
    {
        // At the end of an import the worker's context still tracks the run's Activity and more; a save on it would
        // flush whatever that graph holds.
        var (systemId, corp, _) = await SeedAsync();
        Guid activityId;
        await using (var seed = NewContext())
        {
            var activity = new Activity { TargetName = "Delta Import", TargetType = ActivityTargetType.ConnectedSystemRunProfile, Message = "as staged" };
            seed.Activities.Add(activity);
            await seed.SaveChangesAsync();
            activityId = activity.Id;
        }

        await using (var ctx = NewContext(QueryTrackingBehavior.TrackAll))
        {
            var tracked = await ctx.Activities.SingleAsync(a => a.Id == activityId);
            tracked.Message = "unsaved change in the run's graph";
            Assert.That(ctx.ChangeTracker.HasChanges(), Is.True, "precondition: the context holds an unsaved change");

            await new PostgresDataRepository(ctx).ConnectedSystems.RecordImportWatermarksAsync(
                systemId, "connector-data", new Dictionary<int, string> { [corp] = "corp-1" }, [], T0);
        }

        await using var verify = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await verify.Activities.SingleAsync(a => a.Id == activityId)).Message, Is.EqualTo("as staged"),
                "the watermark write must not flush the context's tracked changes");
            Assert.That((await ReadAsync(systemId)).Single().Value, Is.EqualTo("corp-1"));
            Assert.That(await ReadPersistedConnectorDataAsync(systemId), Is.EqualTo("connector-data"));
        }
    }

    [Test]
    public async Task RecordImportWatermarksAsync_WritesPersistedConnectorDataInTheSameTransactionAsync()
    {
        // The persisted connector data is written first; a Partition value PostgreSQL refuses (text cannot hold a NUL)
        // fails a later statement, and the whole write must go with it.
        var (systemId, corp, partners) = await SeedAsync(persistedConnectorData: "original");

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            Assert.That(async () => await repository.ConnectedSystems.RecordImportWatermarksAsync(
                    systemId, "new-connector-data", new Dictionary<int, string> { [corp] = "corp-1", [partners] = "bad\0value" }, [], T0),
                Throws.InstanceOf<PostgresException>());
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ReadPersistedConnectorDataAsync(systemId), Is.EqualTo("original"));
            Assert.That(await ReadAsync(systemId), Is.Empty);
        }
    }

    [Test]
    public async Task RecordImportWatermarksAsync_AmbientTransactionRolledBack_LeavesNothingWrittenAsync()
    {
        var (systemId, corp, _) = await SeedAsync(persistedConnectorData: "original");

        await using (var ctx = NewContext())
        {
            await using var ambient = await ctx.Database.BeginTransactionAsync();
            await new PostgresDataRepository(ctx).ConnectedSystems.RecordImportWatermarksAsync(
                systemId, "new-connector-data", new Dictionary<int, string> { [corp] = "corp-1" }, [], T0);
            await ambient.RollbackAsync();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ReadPersistedConnectorDataAsync(systemId), Is.EqualTo("original"));
            Assert.That(await ReadAsync(systemId), Is.Empty, "the write must enrol in the caller's transaction");
        }
    }

    [Test]
    public async Task RecordImportWatermarksAsync_NoPersistedConnectorData_LeavesTheColumnAsItIsAsync()
    {
        var (systemId, corp, _) = await SeedAsync(persistedConnectorData: "original");

        await RecordAsync(systemId, new Dictionary<int, string> { [corp] = "corp-1" });

        Assert.That(await ReadPersistedConnectorDataAsync(systemId), Is.EqualTo("original"));
    }

    [Test]
    public async Task RecordImportWatermarksAsync_TwoWritersForOnePartition_LastWriterWinsWithoutErrorAsync()
    {
        // Parallel Schedule steps can run two imports of one Connected System at once.
        var (systemId, corp, _) = await SeedAsync();

        async Task WriteAsync(string value)
        {
            await using var ctx = NewContext();
            await new PostgresDataRepository(ctx).ConnectedSystems.RecordImportWatermarksAsync(
                systemId, null, new Dictionary<int, string> { [corp] = value }, [], T0);
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => WriteAsync($"writer-{i}")));
        var concurrent = (await ReadAsync(systemId)).Single();

        await WriteAsync("last");
        var sequential = (await ReadAsync(systemId)).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(concurrent.Value, Does.StartWith("writer-"), "one row, holding one writer's value");
            Assert.That(sequential.Value, Is.EqualTo("last"));
        }
    }

    [Test]
    public async Task RecordImportWatermarksAsync_DeselectedPartition_SweepsItsRowAsync()
    {
        var (systemId, corp, partners) = await SeedAsync();
        await RecordAsync(systemId, new Dictionary<int, string> { [corp] = "corp-1", [partners] = "partners-1" });
        await SetSelectedAsync(partners, selected: false);

        ImportWatermarkRecordingResult result;
        await using (var ctx = NewContext())
        {
            result = await new PostgresDataRepository(ctx).ConnectedSystems.RecordImportWatermarksAsync(
                systemId, null, new Dictionary<int, string> { [corp] = "corp-2" }, [], T0);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That((await ReadAsync(systemId)).Select(w => w.PartitionId), Is.EqualTo(new[] { corp }));
            Assert.That(result.DeselectedPartitionWatermarksRemoved, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task DeleteWatermarksOfDeselectedPartitionsAsync_DeselectedPartition_RemovesOnlyItsRowAsync()
    {
        var (systemId, corp, partners) = await SeedAsync();
        var (otherSystemId, otherCorp, _) = await SeedAsync("Other Directory");
        await RecordAsync(systemId, new Dictionary<int, string> { [corp] = "corp-1", [partners] = "partners-1" });
        await RecordAsync(otherSystemId, new Dictionary<int, string> { [otherCorp] = "other-1" });
        await SetSelectedAsync(partners, selected: false);
        await SetSelectedAsync(otherCorp, selected: false);

        int removed;
        await using (var ctx = NewContext())
            removed = await new PostgresDataRepository(ctx).ConnectedSystems.DeleteWatermarksOfDeselectedPartitionsAsync(systemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That((await ReadAsync(systemId)).Select(w => w.PartitionId), Is.EqualTo(new[] { corp }));
            Assert.That(await ReadAsync(otherSystemId), Has.Count.EqualTo(1), "another Connected System's rows are its own business");
        }
    }

    [Test]
    public async Task GetPartitionWatermarksAsync_RowsOfTwoConnectedSystems_ReturnsOnlyThoseOfTheOneAskedForAsync()
    {
        var (systemId, corp, _) = await SeedAsync();
        var (otherSystemId, otherCorp, _) = await SeedAsync("Other Directory");
        await RecordAsync(systemId, new Dictionary<int, string> { [corp] = "corp-1" });
        await RecordAsync(otherSystemId, new Dictionary<int, string> { [otherCorp] = "other-1" });

        var rows = await ReadAsync(systemId);

        Assert.That(rows.Select(w => (w.PartitionId, w.Value)), Is.EqualTo(new[] { (corp, "corp-1") }));
    }

    #region Helpers

    private async Task<(int SystemId, int Corp, int Partners)> SeedAsync(string name = "Directory", string? persistedConnectorData = null)
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = $"{name} Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = name, ConnectorDefinition = connectorDefinition, PersistedConnectorData = persistedConnectorData };
        var corp = new ConnectedSystemPartition { ConnectedSystem = system, ExternalId = "dc=corp,dc=local", Name = "corp.local", Selected = true };
        var partners = new ConnectedSystemPartition { ConnectedSystem = system, ExternalId = "dc=partners,dc=local", Name = "partners.local", Selected = true };
        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.ConnectedSystems.Add(system);
        seed.ConnectedSystemPartitions.AddRange(corp, partners);
        await seed.SaveChangesAsync();
        return (system.Id, corp.Id, partners.Id);
    }

    private async Task RecordAsync(int systemId, Dictionary<int, string> watermarks, DateTime? recordedAt = null)
    {
        await using var ctx = NewContext();
        await new PostgresDataRepository(ctx).ConnectedSystems.RecordImportWatermarksAsync(systemId, null, watermarks, [], recordedAt ?? T0);
    }

    private async Task<List<ConnectedSystemPartitionWatermark>> ReadAsync(int systemId)
    {
        await using var ctx = NewContext();
        return await new PostgresDataRepository(ctx).ConnectedSystems.GetPartitionWatermarksAsync(systemId);
    }

    private async Task<string?> ReadPersistedConnectorDataAsync(int systemId)
    {
        await using var ctx = NewContext();
        return (await ctx.ConnectedSystems.SingleAsync(cs => cs.Id == systemId)).PersistedConnectorData;
    }

    private async Task SetSelectedAsync(int partitionId, bool selected)
    {
        await using var ctx = NewContext();
        await ctx.ConnectedSystemPartitions.Where(p => p.Id == partitionId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.Selected, selected));
    }

    #endregion
}
