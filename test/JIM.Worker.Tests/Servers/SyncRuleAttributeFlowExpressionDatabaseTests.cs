// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
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
/// Real-PostgreSQL cover for checking Attribute Flow expressions on save (#2055 Phase 3), on the path the portal uses:
/// the rule is loaded and saved on one tracked context. A mapping the save leaves as it was is never refused, so an
/// administrator can always disable a rule, remove another mapping, or delete the rule while one mapping is broken; a
/// mapping the save changes is refused. Telling the two apart reads what is stored while the change tracker holds the
/// edit, which only a real database can show. Opt-in via <c>JIM_TEST_RESET_DB</c>; ignored when it is absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncRuleAttributeFlowExpressionDatabaseTests
{
    private const string Broken = "Upper(cs[\"surname\"])";

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Attribute Flow expression save tests.");

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

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_DisablingARuleWhoseMappingIsBroken_SavesAndWarnsAsync()
    {
        var seed = await SeedRuleWithABrokenMappingAsync();

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var rule = (await jim.ConnectedSystems.GetSyncRuleAsync(seed.RuleId))!;
            rule.Enabled = false;

            Assert.That(await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, await LoadInitiatorAsync(seed)), Is.True);
            Assert.That(rule.AttributeFlowSaveWarnings, Has.One.Contains("cs[\"surname\"]"));
        }

        await using var verify = NewContext();
        Assert.That((await verify.SyncRules.SingleAsync(r => r.Id == seed.RuleId)).Enabled, Is.False);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_RemovingAnotherMappingWhileOneIsBroken_SavesAsync()
    {
        var seed = await SeedRuleWithABrokenMappingAsync();

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var rule = (await jim.ConnectedSystems.GetSyncRuleAsync(seed.RuleId))!;
            rule.AttributeFlowRules.RemoveAll(m => m.Id == seed.WorkingMappingId);

            Assert.That(await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, await LoadInitiatorAsync(seed)), Is.True);
        }

        await using var verify = NewContext();
        Assert.That(await verify.SyncRuleMappings.Where(m => m.SyncRule!.Id == seed.RuleId).Select(m => m.Id).ToListAsync(),
            Is.EqualTo(new[] { seed.BrokenMappingId }));
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ChangingTheBrokenExpressionToAnotherBrokenOne_IsRefusedAndStoresNothingAsync()
    {
        var seed = await SeedRuleWithABrokenMappingAsync();

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var rule = (await jim.ConnectedSystems.GetSyncRuleAsync(seed.RuleId))!;
            // An edit on the tracked entity: the stored state must still be read as it is stored.
            rule.AttributeFlowRules.Single(m => m.Id == seed.BrokenMappingId).Sources[0].Expression = "Upper(cs[\"forename\"])";
            var initiator = await LoadInitiatorAsync(seed);

            Assert.That(async () => await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator),
                Throws.TypeOf<ArgumentException>().With.Message.Contains("cs[\"forename\"]"));
        }

        await using var verify = NewContext();
        Assert.That(await verify.SyncRuleMappingSources.Where(s => EF.Property<int?>(s, "SyncRuleMappingId") == seed.BrokenMappingId).Select(s => s.Expression).SingleAsync(),
            Is.EqualTo(Broken));
    }

    [Test]
    public async Task DeleteSyncRuleAsync_RuleWhoseMappingIsBroken_DeletesItAsync()
    {
        var seed = await SeedRuleWithABrokenMappingAsync();

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var rule = (await jim.ConnectedSystems.GetSyncRuleAsync(seed.RuleId))!;
            await jim.ConnectedSystems.DeleteSyncRuleAsync(rule, await LoadInitiatorAsync(seed));
        }

        await using var verify = NewContext();
        Assert.That(await verify.SyncRules.AnyAsync(r => r.Id == seed.RuleId), Is.False);
    }

    private record Seed(int RuleId, int BrokenMappingId, int WorkingMappingId, Guid InitiatorId);

    /// <summary>
    /// An enabled import rule with two working Attribute Flows, one of which is then broken in the database itself, as
    /// a schema change or a save from before the check would leave it: it reads an attribute the type does not have.
    /// </summary>
    private async Task<Seed> SeedRuleWithABrokenMappingAsync()
    {
        int systemId, csTypeId, mvTypeId, displayNameId, descriptionId;
        Guid initiatorId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "HR", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem };
            var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            csType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Name = "sn", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true });
            var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
            var displayName = new MetaverseAttribute { Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
            var description = new MetaverseAttribute { Name = "Description", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
            mvType.Attributes.Add(displayName);
            mvType.Attributes.Add(description);
            var initiator = new MetaverseObject { Type = mvType, CachedDisplayName = "Test Administrator" };

            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            seed.ConnectedSystemObjectTypes.Add(csType);
            seed.MetaverseObjectTypes.Add(mvType);
            seed.MetaverseObjects.Add(initiator);
            await seed.SaveChangesAsync();
            (systemId, csTypeId, mvTypeId, displayNameId, descriptionId, initiatorId) = (system.Id, csType.Id, mvType.Id, displayName.Id, description.Id, initiator.Id);
        }

        int ruleId, brokenId, workingId;
        await using (var ctx = NewContext())
        {
            var rule = new SyncRule
            {
                Name = "Import People",
                Direction = SyncRuleDirection.Import,
                Enabled = true,
                ConnectedSystem = await ctx.ConnectedSystems.SingleAsync(x => x.Id == systemId),
                ConnectedSystemObjectType = await ctx.ConnectedSystemObjectTypes.SingleAsync(x => x.Id == csTypeId),
                MetaverseObjectType = await ctx.MetaverseObjectTypes.SingleAsync(x => x.Id == mvTypeId)
            };
            var broken = Mapping(await ctx.MetaverseAttributes.AsTracking().SingleAsync(x => x.Id == displayNameId), "Upper(cs[\"sn\"])");
            var working = Mapping(await ctx.MetaverseAttributes.AsTracking().SingleAsync(x => x.Id == descriptionId), "Lower(cs[\"sn\"])");
            rule.AttributeFlowRules.Add(broken);
            rule.AttributeFlowRules.Add(working);

            var initiator = await ctx.MetaverseObjects.SingleAsync(x => x.Id == initiatorId);
            Assert.That(await NewJimApplication(ctx).ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator), Is.True);
            (ruleId, brokenId, workingId) = (rule.Id, broken.Id, working.Id);
        }

        // Break it underneath the application, which would refuse to save it now.
        await using (var ctx = NewContext())
        {
            var source = await ctx.SyncRuleMappingSources.AsTracking().SingleAsync(s => EF.Property<int?>(s, "SyncRuleMappingId") == brokenId);
            source.Expression = Broken;
            await ctx.SaveChangesAsync();
        }

        return new Seed(ruleId, brokenId, workingId, initiatorId);
    }

    private static SyncRuleMapping Mapping(MetaverseAttribute target, string expression)
    {
        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = target };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = expression });
        return mapping;
    }

    private async Task<MetaverseObject> LoadInitiatorAsync(Seed seed)
    {
        await using var ctx = NewContext();
        return await ctx.MetaverseObjects.SingleAsync(x => x.Id == seed.InitiatorId);
    }

    private static JimApplication NewJimApplication(JimDbContext context)
    {
        var repository = new PostgresDataRepository(context);
        return new JimApplication(repository, syncRepository: repository.Sync);
    }
}
