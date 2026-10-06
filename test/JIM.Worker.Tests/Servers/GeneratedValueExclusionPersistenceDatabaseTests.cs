// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification that a generated value's exclusions (Unique Value Generation, #242, release 3) persist,
/// are replaced and cleared, and are recorded in the Synchronisation Rule's change history, through both save paths an
/// administrator reaches: the single-mapping settings update (REST PATCH, <c>Set-JIMSyncRuleMapping</c>) and the
/// whole-rule save (the portal's Attribute Flow tab). Exclusions are a child collection with a composite key, so
/// whether a tracked replace deletes, inserts and keeps the right rows is exactly what the in-memory provider cannot
/// show. The context runs NoTracking, as JIM.Web's does. Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class GeneratedValueExclusionPersistenceDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL generated value exclusion tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp() => await PostgresTestDatabase.ResetAsync(_connectionString);

    private sealed record SeedIds(int ImportRuleId, int MappingId, int AdSystemId, int PayrollSystemId, Guid InitiatorId);

    private static JimApplication CreateApplication(JimDbContext ctx)
    {
        var repository = new PostgresDataRepository(ctx);
        return new JimApplication(repository, syncRepository: new JIM.PostgresData.Repositories.SyncRepository(repository));
    }

    /// <summary>
    /// HR imports into Person, with a generated Account Name; Active Directory and Payroll each export Account Name
    /// unchanged, so either may be excluded.
    /// </summary>
    private async Task<SeedIds> SeedAsync()
    {
        await using var seed = NewContext();
        var connector = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
        var accountName = new MetaverseAttribute { Name = "Account Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        mvType.Attributes.Add(accountName);

        (ConnectedSystem System, ConnectedSystemObjectType Type, ConnectedSystemObjectTypeAttribute Attribute) NewSystem(string name, string attributeName)
        {
            var system = new ConnectedSystem { Name = name, ConnectorDefinition = connector, ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule };
            var type = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            var attribute = new ConnectedSystemObjectTypeAttribute { Name = attributeName, Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = type, Selected = true };
            type.Attributes.Add(attribute);
            return (system, type, attribute);
        }

        var hr = NewSystem("HR", "employeeId");
        var ad = NewSystem("Active Directory", "sAMAccountName");
        var payroll = NewSystem("Payroll", "login");

        SyncRule ExportRule((ConnectedSystem System, ConnectedSystemObjectType Type, ConnectedSystemObjectTypeAttribute Attribute) target)
        {
            var rule = new SyncRule
            {
                Name = $"{target.System.Name} Export", Direction = SyncRuleDirection.Export, Enabled = true,
                ConnectedSystem = target.System, ConnectedSystemObjectType = target.Type, MetaverseObjectType = mvType
            };
            var mapping = new SyncRuleMapping { TargetConnectedSystemAttribute = target.Attribute };
            mapping.Sources.Add(new SyncRuleMappingSource { MetaverseAttribute = accountName });
            rule.AttributeFlowRules.Add(mapping);
            return rule;
        }

        var importRule = new SyncRule
        {
            Name = "HR Import", Direction = SyncRuleDirection.Import, Enabled = true,
            ConnectedSystem = hr.System, ConnectedSystemObjectType = hr.Type, MetaverseObjectType = mvType
        };
        var generated = new SyncRuleMapping
        {
            TargetMetaverseAttribute = accountName,
            Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Random }
        };
        importRule.AttributeFlowRules.Add(generated);

        var initiator = new MetaverseObject { Type = mvType, CachedDisplayName = "Test Administrator" };

        seed.AddRange(connector, mvType, hr.System, ad.System, payroll.System, importRule, ExportRule(ad), ExportRule(payroll), initiator);
        await seed.SaveChangesAsync();

        return new SeedIds(importRule.Id, generated.Id, ad.System.Id, payroll.System.Id, initiator.Id);
    }

    private async Task<MetaverseObject> LoadInitiatorAsync(SeedIds ids)
    {
        await using var ctx = NewContext();
        return await ctx.MetaverseObjects.SingleAsync(x => x.Id == ids.InitiatorId);
    }

    private async Task<int[]> ReadExclusionsAsync(SeedIds ids)
    {
        await using var ctx = NewContext();
        return await ctx.SyncRuleMappingGenerationExclusions
            .Where(e => e.Generation!.SyncRuleMappingId == ids.MappingId)
            .Select(e => e.ConnectedSystemId)
            .OrderBy(id => id)
            .ToArrayAsync();
    }

    private async Task<string?> ReadLatestSnapshotAsync(SeedIds ids)
    {
        await using var ctx = NewContext();
        return await ctx.Activities
            .Where(a => a.SyncRuleId == ids.ImportRuleId && a.ConfigurationChangeSnapshot != null)
            .OrderByDescending(a => a.ConfigurationChangeVersion)
            .Select(a => a.ConfigurationChangeSnapshot)
            .FirstOrDefaultAsync();
    }

    private async Task UpdateExclusionsAsync(SeedIds ids, MetaverseObject initiator, List<int> exclusions)
    {
        await using var ctx = NewContext();
        var update = new SyncRuleMappingSettingsUpdate { Generation = new SyncRuleMappingGenerationSettingsUpdate { Exclusions = exclusions } };
        var result = await CreateApplication(ctx).ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(ids.MappingId, update, initiator);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_AddReplaceAndClear_PersistsEachAndRecordsTheChangeAsync()
    {
        var ids = await SeedAsync();
        var initiator = await LoadInitiatorAsync(ids);

        await UpdateExclusionsAsync(ids, initiator, [ids.AdSystemId]);
        Assert.That(await ReadExclusionsAsync(ids), Is.EqualTo(new[] { ids.AdSystemId }));
        var snapshot = await ReadLatestSnapshotAsync(ids);
        Assert.That(snapshot, Does.Contain("\"Excluded Connected System\""), "the change history must record the exclusion");

        await UpdateExclusionsAsync(ids, initiator, [ids.AdSystemId, ids.PayrollSystemId]);
        Assert.That(await ReadExclusionsAsync(ids), Is.EqualTo(new[] { ids.AdSystemId, ids.PayrollSystemId }.Order().ToArray()),
            "keeping an exclusion while adding another must neither drop nor duplicate it");

        await UpdateExclusionsAsync(ids, initiator, [ids.PayrollSystemId]);
        Assert.That(await ReadExclusionsAsync(ids), Is.EqualTo(new[] { ids.PayrollSystemId }));

        await UpdateExclusionsAsync(ids, initiator, []);
        Assert.That(await ReadExclusionsAsync(ids), Is.Empty);
        Assert.That(await ReadLatestSnapshotAsync(ids), Does.Not.Contain("\"Excluded Connected System\""), "clearing must be recorded too");
    }

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_RefusedExclusion_LeavesTheStoredExclusionsAloneAsync()
    {
        var ids = await SeedAsync();
        var initiator = await LoadInitiatorAsync(ids);
        await UpdateExclusionsAsync(ids, initiator, [ids.AdSystemId]);

        Assert.ThrowsAsync<ArgumentException>(() => UpdateExclusionsAsync(ids, initiator, [ids.AdSystemId, 999_999]));

        Assert.That(await ReadExclusionsAsync(ids), Is.EqualTo(new[] { ids.AdSystemId }));
    }

    private static async Task<SyncRule> LoadRuleTrackedAsync(JimDbContext ctx, int ruleId) =>
        await ctx.SyncRules
            .AsTracking()
            .Include(r => r.AttributeFlowRules).ThenInclude(m => m.Generation).ThenInclude(g => g!.Exclusions)
            .Include(r => r.AttributeFlowRules).ThenInclude(m => m.TargetMetaverseAttribute)
            .Include(r => r.AttributeFlowRules).ThenInclude(m => m.Sources)
            .Include(r => r.ConnectedSystem)
            .Include(r => r.ConnectedSystemObjectType)
            .Include(r => r.MetaverseObjectType)
            .SingleAsync(r => r.Id == ruleId);

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ExclusionsEditedOnTheTrackedRule_PersistAndAreRecordedAsync()
    {
        var ids = await SeedAsync();
        var initiator = await LoadInitiatorAsync(ids);

        // As the portal does: load the rule tracked, change the generation in place, save the whole rule.
        await using (var ctx = NewContext())
        {
            var rule = await LoadRuleTrackedAsync(ctx, ids.ImportRuleId);
            rule.AttributeFlowRules.Single(m => m.Id == ids.MappingId).Generation!.Exclusions
                .Add(new SyncRuleMappingGenerationExclusion { ConnectedSystemId = ids.AdSystemId });
            Assert.That(await CreateApplication(ctx).ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator), Is.True);
        }

        Assert.That(await ReadExclusionsAsync(ids), Is.EqualTo(new[] { ids.AdSystemId }));
        Assert.That(await ReadLatestSnapshotAsync(ids), Does.Contain("\"Excluded Connected System\""));

        await using (var ctx = NewContext())
        {
            var rule = await LoadRuleTrackedAsync(ctx, ids.ImportRuleId);
            var exclusions = rule.AttributeFlowRules.Single(m => m.Id == ids.MappingId).Generation!.Exclusions;
            exclusions.RemoveAll(e => e.ConnectedSystemId == ids.AdSystemId);
            exclusions.Add(new SyncRuleMappingGenerationExclusion { ConnectedSystemId = ids.PayrollSystemId });
            Assert.That(await CreateApplication(ctx).ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator), Is.True);
        }

        Assert.That(await ReadExclusionsAsync(ids), Is.EqualTo(new[] { ids.PayrollSystemId }));
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ExclusionOfASystemTheValueIsNotExportedTo_IsRefusedAndNothingIsSavedAsync()
    {
        var ids = await SeedAsync();
        var initiator = await LoadInitiatorAsync(ids);

        await using (var ctx = NewContext())
        {
            var rule = await LoadRuleTrackedAsync(ctx, ids.ImportRuleId);
            rule.AttributeFlowRules.Single(m => m.Id == ids.MappingId).Generation!.Exclusions
                .Add(new SyncRuleMappingGenerationExclusion { ConnectedSystemId = rule.ConnectedSystemId });
            var ex = Assert.ThrowsAsync<ArgumentException>(() => CreateApplication(ctx).ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator));
            Assert.That(ex!.Message, Does.Contain("HR"));
        }

        Assert.That(await ReadExclusionsAsync(ids), Is.Empty);
    }

    [Test]
    public async Task GetGeneratedValueParticipantsAsync_SavedMapping_ReadsTheExportRulesFromTheDatabaseAsync()
    {
        var ids = await SeedAsync();
        var initiator = await LoadInitiatorAsync(ids);
        await UpdateExclusionsAsync(ids, initiator, [ids.PayrollSystemId]);

        await using var ctx = NewContext();
        var rows = await CreateApplication(ctx).ConnectedSystems.GetGeneratedValueParticipantsAsync(ids.MappingId);

        Assert.That(rows.Select(r => (r.ConnectedSystemName, r.AttributeName, r.IsExcluded)), Is.EqualTo(new[]
        {
            ("Active Directory", "sAMAccountName", false),
            ("Payroll", "login", true)
        }));
    }
}
