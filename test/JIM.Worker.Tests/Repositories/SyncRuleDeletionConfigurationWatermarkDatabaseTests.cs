// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification that deleting a Synchronisation Rule advances the configuration watermark (#2309).
/// <c>GetLatestSyncRuleConfigurationChangeAsync</c> reads the newest timestamp of the rules and mappings that exist, so a
/// deleted rule leaves no trace there; without a durable stamp the next Full Synchronisation keeps its unchanged-object
/// optimisation on and never re-applies the resolution the rule's deletion changed. Deleting a single mapping already
/// stamps its parent rule (#1533). Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other
/// RequiresPostgres fixtures.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncRuleDeletionConfigurationWatermarkDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL configuration watermark tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    private sealed record Seeded(int HrSystemId, int HrRuleId, int DirectorySystemId, int DirectoryRuleId, int RulelessSystemId);

    /// <summary>
    /// The Service Settings singleton, and three Connected Systems: HR and Directory each with one import rule, created
    /// and never updated two hours ago, and a third with no rules at all.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewContext();
        var twoHoursAgo = DateTime.UtcNow.AddHours(-2);

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var mvoType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        seed.AddRange(new ServiceSettings(), connectorDefinition, mvoType);

        (ConnectedSystem System, SyncRule? Rule) System(string name, bool withRule)
        {
            var system = new ConnectedSystem { Name = name, ConnectorDefinition = connectorDefinition };
            var type = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = system, Selected = true };
            seed.AddRange(system, type);
            if (!withRule)
                return (system, null);

            var rule = new SyncRule
            {
                Name = $"{name} Import", ConnectedSystem = system, ConnectedSystemObjectType = type, MetaverseObjectType = mvoType,
                Direction = SyncRuleDirection.Import, Created = twoHoursAgo
            };
            seed.Add(rule);
            return (system, rule);
        }

        var hr = System("HR", withRule: true);
        var directory = System("Directory", withRule: true);
        var ruleless = System("Ruleless", withRule: false);
        await seed.SaveChangesAsync();

        return new Seeded(hr.System.Id, hr.Rule!.Id, directory.System.Id, directory.Rule!.Id, ruleless.System.Id);
    }

    private async Task<DateTime?> ReadLatestConfigurationChangeAsync()
    {
        await using var read = NewContext();
        return await new PostgresDataRepository(read).Sync.GetLatestSyncRuleConfigurationChangeAsync();
    }

    [Test]
    public async Task DeleteSyncRuleAsync_AdvancesTheConfigurationWatermarkAsync()
    {
        var s = await SeedAsync();
        Assert.That(await ReadLatestConfigurationChangeAsync(), Is.LessThan(DateTime.UtcNow.AddHours(-1)), "precondition");
        var beforeDelete = DateTime.UtcNow;

        // JIM.Web deletes on a NoTracking context (JimDbContext's own default), handing over the rule it loaded.
        await using (var web = NewContext())
        {
            var rule = await web.SyncRules.AsNoTracking().SingleAsync(r => r.Id == s.HrRuleId);
            await new PostgresDataRepository(web).ConnectedSystems.DeleteSyncRuleAsync(rule);
        }

        using (Assert.EnterMultipleScope())
        {
            await using var read = NewContext();
            Assert.That(await read.SyncRules.AnyAsync(r => r.Id == s.HrRuleId), Is.False, "precondition: the rule is deleted");
            Assert.That(await ReadLatestConfigurationChangeAsync(), Is.GreaterThanOrEqualTo(beforeDelete),
                "the deletion must advance the watermark, though the only rows carrying timestamps that remain predate it");
        }
    }

    [Test]
    public async Task DeleteConnectedSystemAsync_SystemWithRules_AdvancesTheConfigurationWatermarkAsync()
    {
        // Deleting a Connected System deletes its rules, which affect other systems' resolution (attribute priority).
        var s = await SeedAsync();
        var beforeDelete = DateTime.UtcNow;

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.DeleteConnectedSystemAsync(s.HrSystemId);

        Assert.That(await ReadLatestConfigurationChangeAsync(), Is.GreaterThanOrEqualTo(beforeDelete));
    }

    [Test]
    public async Task DeleteConnectedSystemAsync_SystemWithoutRules_LeavesTheConfigurationWatermarkAloneAsync()
    {
        // Nothing about synchronisation configuration changed, so nothing need be re-applied to every object.
        var s = await SeedAsync();

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.DeleteConnectedSystemAsync(s.RulelessSystemId);

        Assert.That(await ReadLatestConfigurationChangeAsync(), Is.LessThan(DateTime.UtcNow.AddHours(-1)));
    }
}
