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
/// Real-PostgreSQL proof of the G3 adapter's never-persists invariant (#1115): a destructive-toggle preview run
/// over a live database, imminent tier included, leaves every synchronisation-integrity table byte-identical.
/// The unit fixture cannot prove this (its mocks have nothing to corrupt); this is the test that would catch a
/// future edit staging a Pending Export or breaking a join while it evaluates.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncRuleDestructiveTogglePreviewIsolationDatabaseTests
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
    public async Task EvaluateDeltasAsync_InboundTightenedOverLiveDatabase_ReportsTheDisconnectionAndPersistsNothingAsync()
    {
        // Arrange - an import rule scoped to Sales, one joined object outside that scope
        var ruleId = await SeedImportTopologyAsync();

        var before = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);

        List<PreviewDelta> deltas;
        List<PreviewImpactCount> counts;
        await using (var ctx = NewContext())
        {
            var repo = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repo);
            var adapter = new SyncRuleDestructiveTogglePreviewAdapter(jim, new SyncEngine());
            var context = new PreviewContext
            {
                Surface = ConfigurationChangePreviewSurface.SynchronisationRule,
                ActivityId = Guid.CreateVersion7(),
                TargetId = ruleId,
                ProposedConfiguration = new SyncRuleDestructiveToggleProposal(
                    OutboundDeprovisionAction.Disconnect, InboundOutOfScopeAction.Disconnect)
            };

            // Act - the full evaluation surface a preview run exercises
            deltas = [];
            await foreach (var delta in adapter.EvaluateDeltasAsync(context, CancellationToken.None))
                deltas.Add(delta);
            counts = await adapter.CountImpactAsync(context);
        }

        // Assert - the imminent disconnection is reported, and nothing anywhere has changed
        var after = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas.Count(d => d.TransitionType ==
                ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject), Is.EqualTo(1));
            Assert.That(counts.Single(c => c.TransitionType ==
                ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject).ObjectCount, Is.EqualTo(1));
            Assert.That(() => after.AssertUnchangedSince(before), Throws.Nothing);
        }
    }

    /// <summary>
    /// Seeds one Connected System with an import Synchronisation Rule scoped to department == Sales
    /// (InboundOutOfScopeAction = RemainJoined) and one joined Connected System Object carrying
    /// department = Engineering, so tightening the action to Disconnect has exactly one imminent disconnection
    /// to report.
    /// </summary>
    private async Task<int> SeedImportTopologyAsync()
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
            InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined
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
            StringValue = "Engineering"
        });
        seedCtx.ConnectedSystemObjects.Add(cso);
        await seedCtx.SaveChangesAsync();

        return importRule.Id;
    }
}
