// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of creating a brand-new <see cref="SyncRule"/> through
/// <see cref="JIM.Application.Servers.ConnectedSystemServer.CreateOrUpdateSyncRuleAsync(SyncRule, MetaverseObject?, JIM.Models.Activities.Activity?)"/>.
/// </summary>
/// <remarks>
/// Regression guard: the web editor binds navigation properties (Connected System, object types, attributes) that were
/// loaded in an earlier, now-disposed request scope, and never sets the corresponding FK scalar ids. The create path
/// nulls those navigation properties before <c>Add()</c> to avoid duplicate inserts, but historically relied on the FK
/// scalars already being populated, so the insert sent FK 0 and PostgreSQL rejected it with a foreign-key violation
/// (<c>FK_SyncRules_ConnectedSystemObjectTypes_...</c>). The EF Core in-memory provider cannot reproduce this: it does
/// not enforce FK constraints and auto-fixes up navigation properties, masking the bug. Hence a real database.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as <see cref="ConnectedSystemDeletionDatabaseTests"/>;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncRuleCreationDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL sync-rule creation tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        _connectionString = $"Host={host};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(@"
            DO $$
            DECLARE r RECORD;
            BEGIN
                FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory') LOOP
                    EXECUTE 'TRUNCATE TABLE ""' || r.tablename || '"" RESTART IDENTITY CASCADE';
                END LOOP;
            END $$;");
    }

    /// <summary>
    /// Seeds the Connected System, object types and one attribute each, returning their ids.
    /// The Connected System uses Advanced (SyncRule) matching mode so matching rules added to a rule are kept.
    /// </summary>
    private async Task<SeedIds> SeedAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem
        {
            Name = "Test System",
            ConnectorDefinition = connectorDefinition,
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
        };
        var csType = new ConnectedSystemObjectType { Name = "jimGroup", ConnectedSystem = system, Selected = true };
        var csAttr = new ConnectedSystemObjectTypeAttribute { Name = "cn", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true };
        var mvType = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };
        var mvAttr = new MetaverseAttribute { Name = "DisplayName", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        mvType.Attributes.Add(mvAttr);
        csType.Attributes.Add(csAttr);

        // an initiator is required so the create operation's Activity can be attributed to a security principal
        var initiator = new MetaverseObject { Type = mvType, CachedDisplayName = "Test Administrator" };

        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.ConnectedSystems.Add(system);
        seed.ConnectedSystemObjectTypes.Add(csType);
        seed.MetaverseObjectTypes.Add(mvType);
        seed.MetaverseObjects.Add(initiator);
        await seed.SaveChangesAsync();

        return new SeedIds(system.Id, csType.Id, csAttr.Id, mvType.Id, mvAttr.Id, initiator.Id);
    }

    private record SeedIds(int SystemId, int CsTypeId, int CsAttrId, int MvTypeId, int MvAttrId, Guid InitiatorId);

    private async Task<MetaverseObject> LoadInitiatorAsync(SeedIds ids)
    {
        await using var ctx = NewContext();
        return await ctx.MetaverseObjects.SingleAsync(x => x.Id == ids.InitiatorId);
    }

    /// <summary>
    /// Loads the seeded principals as detached instances (NoTracking), exactly as the web editor would hold them
    /// after a prior, disposed request scope: navigation objects present, FK scalars unset.
    /// </summary>
    private async Task<(ConnectedSystem cs, ConnectedSystemObjectType csType, ConnectedSystemObjectTypeAttribute csAttr, MetaverseObjectType mvType, MetaverseAttribute mvAttr)> LoadDetachedAsync(SeedIds ids)
    {
        await using var ctx = NewContext();
        var cs = await ctx.ConnectedSystems.SingleAsync(x => x.Id == ids.SystemId);
        var csType = await ctx.ConnectedSystemObjectTypes.SingleAsync(x => x.Id == ids.CsTypeId);
        var csAttr = await ctx.ConnectedSystemAttributes.SingleAsync(x => x.Id == ids.CsAttrId);
        var mvType = await ctx.MetaverseObjectTypes.SingleAsync(x => x.Id == ids.MvTypeId);
        var mvAttr = await ctx.MetaverseAttributes.SingleAsync(x => x.Id == ids.MvAttrId);
        return (cs, csType, csAttr, mvType, mvAttr);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_NewBareRule_PersistsWithCorrectForeignKeysAsync()
    {
        var ids = await SeedAsync();
        var (cs, csType, _, mvType, _) = await LoadDetachedAsync(ids);
        var initiator = await LoadInitiatorAsync(ids);

        var rule = new SyncRule
        {
            Name = "Bare Import Rule",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystem = cs,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvType
        };

        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            // Before the fix this throws a PostgreSQL FK violation on ConnectedSystemObjectTypeId (sent as 0).
            var ok = await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator);
            Assert.That(ok, Is.True, "CreateOrUpdateSyncRuleAsync returned false; the rule was not created.");
        }

        await using var verify = NewContext();
        var persisted = await verify.SyncRules.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(persisted.ConnectedSystemId, Is.EqualTo(ids.SystemId));
            Assert.That(persisted.ConnectedSystemObjectTypeId, Is.EqualTo(ids.CsTypeId));
            Assert.That(persisted.MetaverseObjectTypeId, Is.EqualTo(ids.MvTypeId));
        });
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_NewRuleWithAttributeFlow_PersistsWithoutDuplicatingAttributesAsync()
    {
        var ids = await SeedAsync();
        var (cs, csType, csAttr, mvType, mvAttr) = await LoadDetachedAsync(ids);
        var initiator = await LoadInitiatorAsync(ids);

        var rule = new SyncRule
        {
            Name = "Import Rule With Flow",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystem = cs,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvType
        };
        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvAttr };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = csAttr });
        rule.AttributeFlowRules.Add(mapping);

        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            var ok = await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator);
            Assert.That(ok, Is.True, "CreateOrUpdateSyncRuleAsync returned false; the rule was not created.");
        }

        await using var verify = NewContext();
        var flow = await verify.SyncRuleMappings.Include(m => m.Sources).SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(flow.TargetMetaverseAttributeId, Is.EqualTo(ids.MvAttrId), "flow target FK");
            Assert.That(flow.Sources.Single().ConnectedSystemAttributeId, Is.EqualTo(ids.CsAttrId), "flow source FK");
            // No duplicate attribute rows should have been inserted by graph traversal.
            Assert.That(verify.MetaverseAttributes.Count(), Is.EqualTo(1), "metaverse attribute count");
            Assert.That(verify.ConnectedSystemAttributes.Count(), Is.EqualTo(1), "Connected System attribute count");
        });
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_NewRuleWithScopingCriteria_PersistsWithoutDuplicatingAttributesAsync()
    {
        var ids = await SeedAsync();
        var (cs, csType, csAttr, mvType, _) = await LoadDetachedAsync(ids);
        var initiator = await LoadInitiatorAsync(ids);

        var rule = new SyncRule
        {
            Name = "Import Rule With Scope",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystem = cs,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvType
        };
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = csAttr,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Staff"
        });
        rule.ObjectScopingCriteriaGroups.Add(group);

        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            var ok = await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator);
            Assert.That(ok, Is.True, "CreateOrUpdateSyncRuleAsync returned false; the rule was not created.");
        }

        await using var verify = NewContext();
        var persistedGroup = await verify.SyncRuleScopingCriteriaGroups.Include(g => g.Criteria).SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(persistedGroup.Criteria, Has.Count.EqualTo(1), "criteria persisted");
            // No duplicate connected-system attribute rows should have been inserted by graph traversal.
            Assert.That(verify.ConnectedSystemAttributes.Count(), Is.EqualTo(1), "Connected System attribute count");
        });
    }
}
