// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Application.Servers.Preview;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL proof of the behaviour-toggle adapter's central claim (#1462): that disabling a Synchronisation
/// Rule is previewed as the rule ceasing to be evaluated, and that the preview writes nothing.
///
/// This is the case the preview engine could not express before. Substituting a disabled stand-in for the stored
/// rule left it in the evaluated set, because nothing downstream of the load re-checks Enabled, so the preview
/// reported that disabling a rule changes nothing at all. The mocked unit fixture cannot detect that: it never
/// runs the engine over a real rule set. This does.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncRuleBehaviourTogglePreviewIsolationDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL behaviour-toggle preview tests.");

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

    [Test]
    public async Task EvaluateDeltasAsync_DisablingTheProjectingRule_ReportsTheIdentityWouldNotBeCreatedAndPersistsNothingAsync()
    {
        // Arrange - an enabled import rule that projects, and one unjoined object it would create an identity for
        var seed = await SeedProjectingImportRuleAsync();

        var before = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);

        List<PreviewDelta> deltas;
        List<PreviewValidationFinding> findings;
        await using (var ctx = NewContext())
        {
            var repo = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repo, syncRepository: new JIM.PostgresData.Repositories.SyncRepository(repo));
            var adapter = new SyncRuleBehaviourTogglePreviewAdapter(jim, new SyncEngine());
            var context = new PreviewContext
            {
                Surface = ConfigurationChangePreviewSurface.SynchronisationRuleBehaviour,
                ActivityId = Guid.CreateVersion7(),
                TargetId = seed.RuleId,
                ProposedConfiguration = new SyncRuleBehaviourToggleProposal(
                    Enabled: false,
                    Direction: SyncRuleDirection.Import,
                    ProjectToMetaverse: true,
                    ProvisionToConnectedSystem: false,
                    EnforceState: true)
            };

            findings = await adapter.ValidateAsync(context);
            deltas = [];
            await foreach (var delta in adapter.EvaluateDeltasAsync(context, CancellationToken.None))
                deltas.Add(delta);
        }

        // Assert - the identity that would have been created no longer would be, and nothing has been written
        var after = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);
        var row = deltas.FirstOrDefault(d => d.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProjecting);
        Assert.That(row, Is.Not.Null,
            "disabling the only projecting rule stops the object getting an identity; the engine could not " +
            "express this before the rule set became proposable");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Blocking), Is.False);
            // The row names the object and carries its ids: the panel links a drill-down row only where it has a
            // name to hang the link on, so a nameless row is a blank an administrator can neither recognise nor open.
            Assert.That(row!.ObjectDisplayName, Is.EqualTo("Ada Lovelace"));
            Assert.That(row.ConnectedSystemObjectId, Is.EqualTo(seed.ConnectedSystemObjectId));
            Assert.That(row.ConnectedSystemId, Is.EqualTo(seed.ConnectedSystemId));
            Assert.That(() => after.AssertUnchangedSince(before), Throws.Nothing,
                "a preview that ran the synchronisation engine twice must not have projected anything");
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_TurningProvisioningOn_NamesTheIdentityOnEachRowAndPersistsNothingAsync()
    {
        // Arrange - an enabled export rule that does not provision, and one identity of its type with no target object
        var seed = await SeedNonProvisioningExportRuleAsync();
        var before = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);

        List<PreviewDelta> deltas;
        await using (var ctx = NewContext())
        {
            var repo = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repo, syncRepository: new JIM.PostgresData.Repositories.SyncRepository(repo));
            var adapter = new SyncRuleBehaviourTogglePreviewAdapter(jim, new SyncEngine());
            var context = new PreviewContext
            {
                Surface = ConfigurationChangePreviewSurface.SynchronisationRuleBehaviour,
                ActivityId = Guid.CreateVersion7(),
                TargetId = seed.RuleId,
                ProposedConfiguration = new SyncRuleBehaviourToggleProposal(
                    Enabled: true,
                    Direction: SyncRuleDirection.Export,
                    ProjectToMetaverse: false,
                    ProvisionToConnectedSystem: true,
                    EnforceState: true)
            };

            deltas = [];
            await foreach (var delta in adapter.EvaluateDeltasAsync(context, CancellationToken.None))
                deltas.Add(delta);
        }

        // Assert - the identity that would gain a target object is named on its row, and nothing has been written
        var after = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);
        var row = deltas.FirstOrDefault(d => d.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned);
        Assert.That(row, Is.Not.Null, "turning provisioning on creates a target object for the identity that has none");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row!.ObjectDisplayName, Is.EqualTo("Ada Lovelace"));
            Assert.That(row.MetaverseObjectId, Is.EqualTo(seed.MetaverseObjectId));
            Assert.That(row.ConnectedSystemId, Is.EqualTo(seed.ConnectedSystemId));
            Assert.That(row.ConnectedSystemObjectId, Is.Null, "there is no target object yet; that is the point of the row");
            Assert.That(() => after.AssertUnchangedSince(before), Throws.Nothing,
                "a preview that ran the synchronisation engine twice must not have provisioned anything");
        }
    }

    /// <summary>
    /// What a seed hands the test: the rule to preview and the ids its one row is expected to carry.
    /// </summary>
    private sealed record BehaviourPreviewSeed(int RuleId, int ConnectedSystemId, Guid? ConnectedSystemObjectId, Guid? MetaverseObjectId);

    /// <summary>
    /// The schema both seeds share: one Connected System whose User type carries an external id, an employee number
    /// and a display name, and a Person Metaverse Object Type carrying an Employee ID.
    /// </summary>
    private static async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemObjectType CsoType, MetaverseObjectType MvType,
        MetaverseAttribute EmployeeIdAttribute)> SeedSchemaAsync(JimDbContext seedCtx)
    {
        var connectorDefinition = new ConnectorDefinition { Name = "Behaviour Toggle Preview Test Connector", BuiltIn = false };
        var connectedSystem = new ConnectedSystem
        {
            Name = "Behaviour Toggle Preview Source",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule,
            ConnectorDefinition = connectorDefinition
        };
        seedCtx.ConnectorDefinitions.Add(connectorDefinition);
        seedCtx.ConnectedSystems.Add(connectedSystem);
        await seedCtx.SaveChangesAsync();

        var csoType = new ConnectedSystemObjectType
        {
            ConnectedSystemId = connectedSystem.Id,
            Name = "User",
            Selected = true,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
                new ConnectedSystemObjectTypeAttribute { Name = "employeeNumber", Type = AttributeDataType.Text, Selected = true },
                // the attribute ObjectNaming ranks first for a Connected System Object, so the seeded object has a name
                new ConnectedSystemObjectTypeAttribute { Name = "displayName", Type = AttributeDataType.Text, Selected = true }
            ]
        };
        seedCtx.ConnectedSystemObjectTypes.Add(csoType);

        var employeeIdAttribute = new MetaverseAttribute
        {
            Name = "Employee ID",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            BuiltIn = false
        };
        var mvType = new MetaverseObjectType
        {
            Name = "Person",
            PluralName = "People",
            BuiltIn = false,
            Attributes = [employeeIdAttribute]
        };
        seedCtx.MetaverseObjectTypes.Add(mvType);
        await seedCtx.SaveChangesAsync();

        return (connectedSystem, csoType, mvType, employeeIdAttribute);
    }

    /// <summary>
    /// Seeds an enabled import Synchronisation Rule that projects, and a single unjoined object of its type carrying
    /// the value the rule flows and a display name.
    /// </summary>
    private async Task<BehaviourPreviewSeed> SeedProjectingImportRuleAsync()
    {
        await using var seedCtx = NewContext();
        var (connectedSystem, csoType, mvType, employeeIdAttribute) = await SeedSchemaAsync(seedCtx);

        var employeeNumberAttribute = csoType.Attributes.Single(a => a.Name == "employeeNumber");
        var displayNameAttribute = csoType.Attributes.Single(a => a.Name == "displayName");
        var externalIdAttribute = csoType.Attributes.Single(a => a.IsExternalId);

        var importRule = new SyncRule
        {
            ConnectedSystemId = connectedSystem.Id,
            Name = "Behaviour Toggle Preview Import",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystemObjectTypeId = csoType.Id,
            MetaverseObjectTypeId = mvType.Id,
            ProjectToMetaverse = true
        };
        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = employeeIdAttribute };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 1, ConnectedSystemAttribute = employeeNumberAttribute });
        importRule.AttributeFlowRules.Add(mapping);
        seedCtx.SyncRules.Add(importRule);
        await seedCtx.SaveChangesAsync();

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            TypeId = csoType.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.NotJoined
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = externalIdAttribute.Id, GuidValue = Guid.NewGuid() });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = employeeNumberAttribute.Id, StringValue = "E123" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = displayNameAttribute.Id, StringValue = "Ada Lovelace" });
        seedCtx.ConnectedSystemObjects.Add(cso);
        await seedCtx.SaveChangesAsync();

        return new BehaviourPreviewSeed(importRule.Id, connectedSystem.Id, cso.Id, null);
    }

    /// <summary>
    /// Seeds an enabled export Synchronisation Rule with provisioning off, and a single identity of its type with no
    /// object in the target system, so turning provisioning on would create one.
    /// </summary>
    private async Task<BehaviourPreviewSeed> SeedNonProvisioningExportRuleAsync()
    {
        await using var seedCtx = NewContext();
        var (connectedSystem, csoType, mvType, employeeIdAttribute) = await SeedSchemaAsync(seedCtx);

        var employeeNumberAttribute = csoType.Attributes.Single(a => a.Name == "employeeNumber");

        var exportRule = new SyncRule
        {
            ConnectedSystemId = connectedSystem.Id,
            Name = "Behaviour Toggle Preview Export",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemObjectTypeId = csoType.Id,
            MetaverseObjectTypeId = mvType.Id,
            ProvisionToConnectedSystem = false,
            EnforceState = true
        };
        var mapping = new SyncRuleMapping { TargetConnectedSystemAttribute = employeeNumberAttribute };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 1, MetaverseAttribute = employeeIdAttribute });
        exportRule.AttributeFlowRules.Add(mapping);
        seedCtx.SyncRules.Add(exportRule);
        await seedCtx.SaveChangesAsync();

        // The population stream loads attribute values without their attribute entities, so the name it reads is
        // the cached one, which is what the application maintains on every Metaverse Object.
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = mvType,
            CachedDisplayName = "Ada Lovelace"
        };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = employeeIdAttribute.Id, StringValue = "E123" });
        seedCtx.MetaverseObjects.Add(mvo);
        await seedCtx.SaveChangesAsync();

        return new BehaviourPreviewSeed(exportRule.Id, connectedSystem.Id, null, mvo.Id);
    }
}
