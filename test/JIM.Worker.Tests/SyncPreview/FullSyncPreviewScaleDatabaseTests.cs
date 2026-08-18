// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncPreview;

/// <summary>
/// Real-PostgreSQL verification of the full-system preview's scale mechanics (#288 plan Phase 4, PRD
/// requirements 12 and 14) over a populated multi-thousand-object Connected System: the count tier covers
/// the whole population, the sample tier stays bounded, the object cap truncates exactly where it says,
/// and the walk leaves the synchronisation-integrity tables byte-identical. The population here is
/// thousands, not the 100K+ a Scale integration template provides; the Scale-template run needs a host
/// with 20+ GB and is recorded in the plan as the follow-up verification.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class FullSyncPreviewScaleDatabaseTests
{
    private const int PopulationSize = 2_000;

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL full-system preview scale tests.");

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
    public async Task PreviewFullSyncAsync_ThousandsOfObjects_CountsAllSamplesBoundedTruncatesExactlyAndPersistsNothingAsync()
    {
        // Arrange - a projecting import topology over a multi-thousand-object population
        var connectedSystemId = await SeedPopulationAsync();

        var before = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);

        await using (var ctx = NewContext())
        {
            var repo = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repo, syncRepository: new JIM.PostgresData.Repositories.SyncRepository(repo));

            // Act 1 - whole-population walk (the default 10K object cap comfortably covers it)
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var full = await jim.SyncPreview.PreviewFullSyncAsync(connectedSystemId);
            stopwatch.Stop();
            TestContext.Out.WriteLine($"Full walk of {PopulationSize} objects took {stopwatch.Elapsed.TotalSeconds:0.0}s.");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(full.TotalObjectCount, Is.EqualTo(PopulationSize));
                Assert.That(full.EvaluatedObjectCount, Is.EqualTo(PopulationSize));
                Assert.That(full.Truncated, Is.False);
                Assert.That(full.Counts.WouldProject, Is.EqualTo(PopulationSize),
                    "Every object projects under this topology, and the count tier must say so");
                Assert.That(full.Samples, Has.Count.EqualTo(new FullSyncPreviewOptions().SampleTreesPerCategory),
                    "Tree retention stays at the per-category bound however large the population");
            }

            // Act 2 - the object cap truncates exactly where it says
            var capped = await jim.SyncPreview.PreviewFullSyncAsync(connectedSystemId,
                new FullSyncPreviewOptions { MaxObjects = 250 });
            using (Assert.EnterMultipleScope())
            {
                Assert.That(capped.EvaluatedObjectCount, Is.EqualTo(250));
                Assert.That(capped.Truncated, Is.True);
                Assert.That(capped.TruncationReason, Is.EqualTo(FullSyncPreviewTruncationReason.ObjectCapReached));
                Assert.That(capped.TotalObjectCount, Is.EqualTo(PopulationSize));
            }
        }

        // Assert - two full-system walks later, nothing the integrity snapshot watches has changed
        var after = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);
        Assert.That(() => after.AssertUnchangedSince(before), Throws.Nothing);
    }

    /// <summary>
    /// Seeds one Connected System with a projecting import Synchronisation Rule (one DisplayName flow) and
    /// <see cref="PopulationSize"/> Connected System Objects, batched to keep the change tracker flat.
    /// </summary>
    private async Task<int> SeedPopulationAsync()
    {
        int connectedSystemId;
        int csoTypeId;
        ConnectedSystemObjectTypeAttribute externalIdAttr;
        ConnectedSystemObjectTypeAttribute displayNameAttr;

        await using (var seedCtx = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Scale Preview Test Connector", BuiltIn = false };
            var connectedSystem = new ConnectedSystem
            {
                Name = "Scale Preview Source",
                ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule,
                ConnectorDefinition = connectorDefinition
            };
            seedCtx.ConnectorDefinitions.Add(connectorDefinition);
            seedCtx.ConnectedSystems.Add(connectedSystem);
            await seedCtx.SaveChangesAsync();
            connectedSystemId = connectedSystem.Id;

            var csoType = new ConnectedSystemObjectType
            {
                ConnectedSystemId = connectedSystem.Id,
                Name = "User",
                Selected = true,
                Attributes =
                [
                    new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
                    new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true }
                ]
            };
            seedCtx.ConnectedSystemObjectTypes.Add(csoType);

            var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = false };
            var mvDisplayNameAttr = new MetaverseAttribute
            {
                Name = "DisplayName",
                Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued,
                MetaverseObjectTypes = [mvType]
            };
            seedCtx.MetaverseObjectTypes.Add(mvType);
            seedCtx.MetaverseAttributes.Add(mvDisplayNameAttr);
            await seedCtx.SaveChangesAsync();

            csoTypeId = csoType.Id;
            externalIdAttr = csoType.Attributes.Single(a => a.IsExternalId);
            displayNameAttr = csoType.Attributes.Single(a => a.Name == "DisplayName");

            var importRule = new SyncRule
            {
                ConnectedSystemId = connectedSystem.Id,
                Name = "Scale Preview Import",
                Direction = SyncRuleDirection.Import,
                Enabled = true,
                ConnectedSystemObjectTypeId = csoType.Id,
                MetaverseObjectTypeId = mvType.Id,
                ProjectToMetaverse = true
            };
            importRule.AttributeFlowRules.Add(new SyncRuleMapping
            {
                TargetMetaverseAttribute = mvDisplayNameAttr,
                Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = displayNameAttr } }
            });
            seedCtx.SyncRules.Add(importRule);
            await seedCtx.SaveChangesAsync();
        }

        // Batched population seed on fresh contexts, so the change tracker never holds more than one batch.
        const int batchSize = 500;
        for (var seeded = 0; seeded < PopulationSize; seeded += batchSize)
        {
            await using var batchCtx = NewContext();
            for (var i = seeded; i < Math.Min(seeded + batchSize, PopulationSize); i++)
            {
                var cso = new ConnectedSystemObject
                {
                    Id = Guid.NewGuid(),
                    ConnectedSystemId = connectedSystemId,
                    TypeId = csoTypeId,
                    Status = ConnectedSystemObjectStatus.Normal
                };
                cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                {
                    Id = Guid.NewGuid(),
                    AttributeId = externalIdAttr.Id,
                    GuidValue = Guid.NewGuid()
                });
                cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                {
                    Id = Guid.NewGuid(),
                    AttributeId = displayNameAttr.Id,
                    StringValue = $"Scale Probe {i:00000}"
                });
                batchCtx.ConnectedSystemObjects.Add(cso);
            }
            await batchCtx.SaveChangesAsync();
        }

        return connectedSystemId;
    }
}
