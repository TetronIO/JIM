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
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL proof of the G1 adapter's never-persists invariant (#1436): a Scoping Criteria preview run over
/// a live database leaves every synchronisation-integrity table byte-identical.
///
/// This adapter needs the proof more than its siblings do, because it is the one that asks the synchronisation
/// preview engine what an object entering scope would become. That path probes joins and builds prospective
/// Metaverse Objects, so "the preview persisted a projection it was only asked to imagine" is a real failure mode
/// rather than a theoretical one, and the unit fixture's mocks have nothing to corrupt.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncRuleScopingPreviewIsolationDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL destructive-toggle preview isolation tests.");

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
    public async Task EvaluateDeltasAsync_ScopeNarrowedOverLiveDatabase_ReportsTheDepartureAndPersistsNothingAsync()
    {
        // Arrange - an import rule scoped to Sales, one joined Sales object and one unjoined Marketing object
        var (ruleId, departmentAttributeId) = await SeedImportTopologyAsync();

        var before = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);

        List<PreviewDelta> deltas;
        List<PreviewImpactCount> counts;
        await using (var ctx = NewContext())
        {
            var repo = new PostgresDataRepository(ctx);

            // The sync repository is supplied because this adapter reaches the synchronisation preview engine for
            // the objects entering scope, and that path runs inside a rollback-only transaction on it. Its sibling
            // isolation fixtures leave it null because their adapters never get there.
            using var jim = new JimApplication(repo, syncRepository: new JIM.PostgresData.Repositories.SyncRepository(repo));
            var adapter = new SyncRuleScopingPreviewAdapter(jim, new SyncEngine());
            var context = new PreviewContext
            {
                Surface = ConfigurationChangePreviewSurface.SynchronisationRuleScope,
                ActivityId = Guid.CreateVersion7(),
                TargetId = ruleId,

                // Narrowed from department == Sales to department == Marketing, so the seeded Sales object leaves
                // scope and the seeded Marketing object enters it: both sides of the walk in one run, including the
                // engine-backed arrival path that builds a prospective Metaverse Object.
                ProposedConfiguration = new SyncRuleScopingProposal(
                [
                    new SyncRuleScopingCriteriaGroupProposal(
                        SearchGroupType.All,
                        [new SyncRuleScopingCriterionProposal(null, departmentAttributeId, SearchComparisonType.Equals, StringValue: "Marketing")],
                        [])
                ])
            };

            // Act - the full evaluation surface a preview run exercises
            deltas = [];
            await foreach (var delta in adapter.EvaluateDeltasAsync(context, CancellationToken.None))
                deltas.Add(delta);
            counts = await adapter.CountImpactAsync(context);
        }

        // Assert - both movements are reported, and nothing anywhere has changed
        var after = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas.Count(d => d.TransitionType ==
                ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject), Is.EqualTo(1),
                "the joined Sales object leaves scope and its join breaks");
            Assert.That(counts.Single(c => c.TransitionType ==
                ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject).ObjectCount, Is.EqualTo(1));
            Assert.That(deltas.Any(d => d.TransitionType is
                ActivityRunProfileExecutionItemSyncOutcomeType.Projected
                or ActivityRunProfileExecutionItemSyncOutcomeType.Joined
                or ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope), Is.True,
                "the unjoined Marketing object enters scope, which is the engine-backed half of the walk");
            Assert.That(() => after.AssertUnchangedSince(before), Throws.Nothing,
                "a preview that imagined a projection must not have written one");
        }
    }

    /// <summary>
    /// Seeds one Connected System with an import Synchronisation Rule scoped to department == Sales, one JOINED
    /// object carrying department = Sales (so narrowing the scope disconnects it) and one UNJOINED object carrying
    /// department = Marketing (so the same narrowing brings it into scope, exercising the engine-backed arrival
    /// path that builds a prospective Metaverse Object). Returns the rule and the department attribute's id.
    /// </summary>
    private async Task<(int RuleId, int DepartmentAttributeId)> SeedImportTopologyAsync()
    {
        await using var seedCtx = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "G3 Preview Test Connector", BuiltIn = false };
        var connectedSystem = new ConnectedSystem
        {
            Name = "G3 Preview Source",
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
                new ConnectedSystemObjectTypeAttribute { Name = "department", Type = AttributeDataType.Text, Selected = true }
            ]
        };
        seedCtx.ConnectedSystemObjectTypes.Add(csoType);

        var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = false };
        seedCtx.MetaverseObjectTypes.Add(mvType);
        await seedCtx.SaveChangesAsync();

        var departmentAttribute = csoType.Attributes.Single(a => a.Name == "department");
        var externalIdAttribute = csoType.Attributes.Single(a => a.IsExternalId);

        var importRule = new SyncRule
        {
            ConnectedSystemId = connectedSystem.Id,
            Name = "G3 Preview Import",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystemObjectTypeId = csoType.Id,
            MetaverseObjectTypeId = mvType.Id,
            InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect,
            ProjectToMetaverse = true
        };
        var scopingGroup = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        scopingGroup.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = departmentAttribute,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Sales"
        });
        importRule.ObjectScopingCriteriaGroups.Add(scopingGroup);
        seedCtx.SyncRules.Add(importRule);

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType };
        seedCtx.MetaverseObjects.Add(mvo);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            TypeId = csoType.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObject = mvo
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = externalIdAttribute.Id,
            GuidValue = Guid.NewGuid()
        });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = departmentAttribute.Id,
            StringValue = "Sales"
        });
        seedCtx.ConnectedSystemObjects.Add(cso);

        var unjoinedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            TypeId = csoType.Id,
            Status = ConnectedSystemObjectStatus.Normal
        };
        unjoinedCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = externalIdAttribute.Id,
            GuidValue = Guid.NewGuid()
        });
        unjoinedCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = departmentAttribute.Id,
            StringValue = "Marketing"
        });
        seedCtx.ConnectedSystemObjects.Add(unjoinedCso);
        await seedCtx.SaveChangesAsync();

        return (importRule.Id, departmentAttribute.Id);
    }
}
