// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL cover for checking Scoping Criteria on save (#2054), on the path the portal uses: the rule is loaded
/// and saved on one tracked context. A criterion that stopped being valid after it was saved (here, its attribute's type
/// changed from DateTime to Text under a relative-date criterion, as an attribute type override or schema refresh can
/// do) never stops the rule being saved, so an administrator can always disable it; a save that changes that criterion
/// is refused. Telling the two apart reads what is stored while the change tracker holds the edit, which only a real
/// database can show. Opt-in via <c>JIM_TEST_RESET_DB</c>; ignored when it is absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncRuleScopingCriteriaSaveDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Scoping Criteria save tests.");

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
    public async Task CreateOrUpdateSyncRuleAsync_DisablingARuleWhoseRelativeDateCriterionIsNowOnText_SavesAndWarnsAsync()
    {
        var seed = await SeedRuleWithAStrandedRelativeDateAsync();

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var rule = (await jim.ConnectedSystems.GetSyncRuleAsync(seed.RuleId))!;
            rule.Enabled = false;

            Assert.That(await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, await LoadInitiatorAsync(seed)), Is.True);
            Assert.That(rule.ScopingSaveWarnings, Has.One.Contains("startDate"));
        }

        await using var verify = NewContext();
        Assert.That((await verify.SyncRules.SingleAsync(r => r.Id == seed.RuleId)).Enabled, Is.False);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ChangingTheStrandedCriterion_IsRefusedAndStoresNothingAsync()
    {
        var seed = await SeedRuleWithAStrandedRelativeDateAsync();

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var rule = (await jim.ConnectedSystems.GetSyncRuleAsync(seed.RuleId))!;
            // An edit on the tracked entity: the stored state must still be read as it is stored.
            rule.ObjectScopingCriteriaGroups.Single().Criteria.Single().RelativeCount = 60;
            var initiator = await LoadInitiatorAsync(seed);

            Assert.That(async () => await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator),
                Throws.TypeOf<ArgumentException>().With.Message.Contains("startDate"));
        }

        await using var verify = NewContext();
        Assert.That((await verify.SyncRuleScopingCriteria.SingleAsync(c => c.Id == seed.CriterionId)).RelativeCount, Is.EqualTo(30));
    }

    private record Seed(int RuleId, int CriterionId, Guid InitiatorId);

    /// <summary>
    /// An enabled import rule scoped to <c>startDate</c> before 30 days ago, saved while <c>startDate</c> was a DateTime,
    /// whose type is then changed to Text in the database itself, as an attribute type override or a schema refresh
    /// would leave it.
    /// </summary>
    private async Task<Seed> SeedRuleWithAStrandedRelativeDateAsync()
    {
        int systemId, csTypeId, mvTypeId, startDateId;
        Guid initiatorId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "HR", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem };
            var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            var startDate = new ConnectedSystemObjectTypeAttribute
            {
                Name = "startDate", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true
            };
            csType.Attributes.Add(startDate);
            var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
            var initiator = new MetaverseObject { Type = mvType, CachedDisplayName = "Test Administrator" };

            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            seed.ConnectedSystemObjectTypes.Add(csType);
            seed.MetaverseObjectTypes.Add(mvType);
            seed.MetaverseObjects.Add(initiator);
            await seed.SaveChangesAsync();
            (systemId, csTypeId, mvTypeId, startDateId, initiatorId) = (system.Id, csType.Id, mvType.Id, startDate.Id, initiator.Id);
        }

        int ruleId, criterionId;
        await using (var ctx = NewContext())
        {
            var rule = new SyncRule
            {
                Name = "Import People",
                Direction = SyncRuleDirection.Import,
                Enabled = true,
                ConnectedSystem = await ctx.ConnectedSystems.SingleAsync(x => x.Id == systemId),
                ConnectedSystemObjectType = await ctx.ConnectedSystemObjectTypes.Include(t => t.Attributes).SingleAsync(x => x.Id == csTypeId),
                MetaverseObjectType = await ctx.MetaverseObjectTypes.SingleAsync(x => x.Id == mvTypeId)
            };
            var criterion = new SyncRuleScopingCriteria
            {
                ConnectedSystemAttribute = rule.ConnectedSystemObjectType.Attributes.Single(a => a.Id == startDateId),
                ConnectedSystemAttributeId = startDateId,
                ComparisonType = SearchComparisonType.LessThan,
                ValueMode = DateCriteriaValueMode.Relative,
                RelativeCount = 30,
                RelativeUnit = RelativeDateUnit.Days,
                RelativeDirection = RelativeDateDirection.Ago
            };
            rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All, Criteria = [criterion] });

            var initiator = await ctx.MetaverseObjects.SingleAsync(x => x.Id == initiatorId);
            Assert.That(await NewJimApplication(ctx).ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator), Is.True);
            (ruleId, criterionId) = (rule.Id, criterion.Id);
        }

        // Change the type underneath the criterion, which the application would refuse to save now.
        await using (var ctx = NewContext())
        {
            var attribute = await ctx.ConnectedSystemAttributes.AsTracking().SingleAsync(a => a.Id == startDateId);
            attribute.Type = AttributeDataType.Text;
            await ctx.SaveChangesAsync();
        }

        return new Seed(ruleId, criterionId, initiatorId);
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
