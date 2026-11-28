// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL round trip for the export rule coverage record (#2826): the raw-SQL insert writes every column in order,
/// a row already held is left as it was, the read answers per object, the delete removes exactly the pairs named, and
/// deleting the object or the rule takes its rows with it. The in-memory store cannot see any of this.
/// Opt-in via JIM_TEST_RESET_*; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ExportRuleCoveragePersistenceDatabaseTests
{
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
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL export rule coverage tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [Test]
    public async Task AddExportRuleCoveragesAsync_ThenRead_RoundTripsEveryColumnAsync()
    {
        var (csoIds, ruleIds) = await SeedAsync(objects: 2, rules: 2);
        var recorded = new DateTime(2026, 11, 26, 9, 30, 15, DateTimeKind.Utc);

        await using (var write = NewContext())
        {
            await new PostgresDataRepository(write).Sync.AddExportRuleCoveragesAsync(
            [
                new ExportRuleCoverage { ConnectedSystemObjectId = csoIds[0], SyncRuleId = ruleIds[0], Recorded = recorded },
                new ExportRuleCoverage { ConnectedSystemObjectId = csoIds[0], SyncRuleId = ruleIds[1], Recorded = recorded },
                new ExportRuleCoverage { ConnectedSystemObjectId = csoIds[1], SyncRuleId = ruleIds[1], Recorded = recorded }
            ]);
        }

        await using var read = NewContext();
        var coverage = await new PostgresDataRepository(read).Sync.GetExportRuleCoveragesAsync(csoIds);
        var row = await read.ExportRuleCoverages.AsNoTracking().SingleAsync(c => c.ConnectedSystemObjectId == csoIds[1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(coverage[csoIds[0]], Is.EquivalentTo(ruleIds));
            Assert.That(coverage[csoIds[1]], Is.EquivalentTo(new[] { ruleIds[1] }));
            Assert.That(row.SyncRuleId, Is.EqualTo(ruleIds[1]));
            Assert.That(row.Recorded.ToUniversalTime(), Is.EqualTo(recorded));
        }
    }

    [Test]
    public async Task AddExportRuleCoveragesAsync_RowAlreadyHeld_KeepsTheFirstRecordAsync()
    {
        var (csoIds, ruleIds) = await SeedAsync(objects: 1, rules: 1);
        var first = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var write = NewContext())
        {
            var sync = new PostgresDataRepository(write).Sync;
            await sync.AddExportRuleCoveragesAsync([new ExportRuleCoverage { ConnectedSystemObjectId = csoIds[0], SyncRuleId = ruleIds[0], Recorded = first }]);
            await sync.AddExportRuleCoveragesAsync([new ExportRuleCoverage { ConnectedSystemObjectId = csoIds[0], SyncRuleId = ruleIds[0], Recorded = first.AddDays(5) }]);
        }

        await using var read = NewContext();
        var row = await read.ExportRuleCoverages.AsNoTracking().SingleAsync(c => c.ConnectedSystemObjectId == csoIds[0]);
        Assert.That(row.Recorded.ToUniversalTime(), Is.EqualTo(first));
    }

    [Test]
    public async Task DeleteExportRuleCoveragesAsync_RemovesOnlyTheNamedPairsAsync()
    {
        var (csoIds, ruleIds) = await SeedAsync(objects: 1, rules: 2);
        await using (var write = NewContext())
        {
            var sync = new PostgresDataRepository(write).Sync;
            await sync.AddExportRuleCoveragesAsync(ruleIds.Select(id =>
                new ExportRuleCoverage { ConnectedSystemObjectId = csoIds[0], SyncRuleId = id, Recorded = DateTime.UtcNow }).ToList());
            await sync.DeleteExportRuleCoveragesAsync([(csoIds[0], ruleIds[0])]);
        }

        await using var read = NewContext();
        var coverage = await new PostgresDataRepository(read).Sync.GetExportRuleCoveragesAsync(csoIds);
        Assert.That(coverage[csoIds[0]], Is.EquivalentTo(new[] { ruleIds[1] }));
    }

    [Test]
    public async Task DeletingTheObjectOrTheRule_TakesItsCoverageWithItAsync()
    {
        var (csoIds, ruleIds) = await SeedAsync(objects: 2, rules: 2);
        await using (var write = NewContext())
        {
            await new PostgresDataRepository(write).Sync.AddExportRuleCoveragesAsync(
            [
                new ExportRuleCoverage { ConnectedSystemObjectId = csoIds[0], SyncRuleId = ruleIds[0], Recorded = DateTime.UtcNow },
                new ExportRuleCoverage { ConnectedSystemObjectId = csoIds[1], SyncRuleId = ruleIds[1], Recorded = DateTime.UtcNow }
            ]);
        }

        await using (var delete = NewContext())
        {
            await delete.ConnectedSystemObjects.Where(o => o.Id == csoIds[0]).ExecuteDeleteAsync();
            await delete.SyncRules.Where(r => r.Id == ruleIds[1]).ExecuteDeleteAsync();
        }

        await using var read = NewContext();
        var remaining = await read.ExportRuleCoverages.AsNoTracking()
            .CountAsync(c => csoIds.Contains(c.ConnectedSystemObjectId));
        Assert.That(remaining, Is.Zero);
    }

    [Test]
    public async Task GetJoinedConnectedSystemObjectPageAsync_PagesJoinedAccountsInIdOrderAsync()
    {
        var (csoIds, ruleIds) = await SeedAsync(objects: 3, rules: 1);
        var mvoIds = await JoinAsync(csoIds.Take(2).ToList());
        int systemId, typeId;
        await using (var read = NewContext())
        {
            var rule = await read.SyncRules.AsNoTracking().SingleAsync(r => r.Id == ruleIds[0]);
            (systemId, typeId) = (rule.ConnectedSystemId, rule.ConnectedSystemObjectTypeId);
        }

        await using var context = NewContext();
        var sync = new PostgresDataRepository(context).Sync;
        var first = await sync.GetJoinedConnectedSystemObjectPageAsync(systemId, typeId, Guid.Empty, take: 1);
        var second = await sync.GetJoinedConnectedSystemObjectPageAsync(systemId, typeId, first[0].ConnectedSystemObjectId, take: 1);
        var third = await sync.GetJoinedConnectedSystemObjectPageAsync(systemId, typeId, second[0].ConnectedSystemObjectId, take: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Concat(second).ToList(), Is.EquivalentTo(csoIds.Take(2).Zip(mvoIds).ToList()),
                "each joined account once, with the Metaverse Object it is joined to");
            Assert.That(third, Is.Empty, "the unjoined account is not paged");
        }
    }

    [Test]
    public async Task MarkSyncRuleCoverageRecordedAsync_SetsTheColumnOnceAndLeavesLastUpdatedAloneAsync()
    {
        var (_, ruleIds) = await SeedAsync(objects: 0, rules: 1);
        var first = new DateTime(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);

        await using (var write = NewContext())
        {
            var sync = new PostgresDataRepository(write).Sync;
            await sync.MarkSyncRuleCoverageRecordedAsync(ruleIds[0], first);
            await sync.MarkSyncRuleCoverageRecordedAsync(ruleIds[0], first.AddHours(1));
        }

        await using var read = NewContext();
        var rule = await read.SyncRules.AsNoTracking().SingleAsync(r => r.Id == ruleIds[0]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rule.CoverageRecordedAt?.ToUniversalTime(), Is.EqualTo(first));
            Assert.That(rule.LastUpdated, Is.Null, "recording the rule's coverage is not a change to its configuration");
        }
    }

    private async Task<List<Guid>> JoinAsync(List<Guid> csoIds)
    {
        await using var context = NewContext();
        var mvType = await context.MetaverseObjectTypes.OrderByDescending(t => t.Id).FirstAsync();
        var mvos = csoIds.Select(_ => new MetaverseObject { Id = JIM.Models.Utility.TimeOrderedIds.New(), Type = mvType, Created = DateTime.UtcNow }).ToList();
        context.MetaverseObjects.AddRange(mvos);
        await context.SaveChangesAsync();
        foreach (var (csoId, mvo) in csoIds.Zip(mvos))
            await context.ConnectedSystemObjects.Where(o => o.Id == csoId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(o => o.MetaverseObjectId, mvo.Id));
        return mvos.Select(mvo => mvo.Id).ToList();
    }

    private async Task<(List<Guid> CsoIds, List<int> RuleIds)> SeedAsync(int objects, int rules)
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = $"Test Connector {Guid.NewGuid():N}", BuiltIn = true };
        var system = new ConnectedSystem { Name = $"Test System {Guid.NewGuid():N}", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var mvType = new MetaverseObjectType { Name = $"Person {Guid.NewGuid():N}", PluralName = "People" };
        seed.AddRange(connectorDefinition, system, csType, mvType);

        var syncRules = Enumerable.Range(1, rules).Select(i => new SyncRule
        {
            Name = $"Export Rule {i} {Guid.NewGuid():N}",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystem = system,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvType
        }).ToList();
        seed.SyncRules.AddRange(syncRules);

        var csos = Enumerable.Range(1, objects).Select(_ => new ConnectedSystemObject
        {
            Id = JIM.Models.Utility.TimeOrderedIds.New(),
            ConnectedSystem = system,
            Type = csType,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.NotJoined,
            Created = DateTime.UtcNow
        }).ToList();
        seed.ConnectedSystemObjects.AddRange(csos);
        await seed.SaveChangesAsync();

        return (csos.Select(o => o.Id).ToList(), syncRules.Select(r => r.Id).ToList());
    }
}
