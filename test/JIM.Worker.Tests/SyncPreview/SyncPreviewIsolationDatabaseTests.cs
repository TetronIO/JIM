// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncPreview;

/// <summary>
/// Real-PostgreSQL verification that a full per-object preview (#288 plan Phase 3) leaves the
/// synchronisation-integrity tables byte-identical (PRD requirement 10), over a POPULATED topology: a
/// Connected System Object that projects and flows an attribute, so the preview exercises scoping,
/// projection, Attribute Flow and the outbound cache against a live database rather than the empty-lookup
/// path the Phase 2 fixture covers. The in-memory provider cannot run the digest SQL, so only this fixture
/// can prove it.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncPreviewIsolationDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL preview isolation tests.");

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
    public async Task PreviewSyncForCsoAsync_PopulatedProjectionTopology_LeavesTheIntegrityTablesByteIdenticalAsync()
    {
        // Arrange - a projecting import topology: system, types, rule with one DisplayName flow, and a CSO
        int connectedSystemId;
        Guid csoId;
        await using (var seedCtx = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Isolation Test Connector", BuiltIn = false };
            var connectedSystem = new ConnectedSystem
            {
                Name = "Isolation Source",
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

            var csoDisplayNameAttr = csoType.Attributes.Single(a => a.Name == "DisplayName");
            var importRule = new SyncRule
            {
                ConnectedSystemId = connectedSystem.Id,
                Name = "Isolation Import",
                Direction = SyncRuleDirection.Import,
                Enabled = true,
                ConnectedSystemObjectTypeId = csoType.Id,
                MetaverseObjectTypeId = mvType.Id,
                ProjectToMetaverse = true
            };
            importRule.AttributeFlowRules.Add(new SyncRuleMapping
            {
                TargetMetaverseAttribute = mvDisplayNameAttr,
                Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = csoDisplayNameAttr } }
            });
            seedCtx.SyncRules.Add(importRule);

            var cso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = connectedSystem.Id,
                TypeId = csoType.Id,
                Status = ConnectedSystemObjectStatus.Normal
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                Attribute = csoType.Attributes.Single(a => a.IsExternalId),
                GuidValue = Guid.NewGuid()
            });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                Attribute = csoDisplayNameAttr,
                StringValue = "Isolation Probe"
            });
            seedCtx.ConnectedSystemObjects.Add(cso);
            await seedCtx.SaveChangesAsync();
            csoId = cso.Id;
        }

        var before = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);

        // Act - a full populated preview: scoping, projection, Attribute Flow, outbound cache build
        await using (var ctx = NewContext())
        {
            var repo = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repo, syncRepository: new JIM.PostgresData.Repositories.SyncRepository(repo));
            var preview = await jim.SyncPreview.PreviewSyncForCsoAsync(connectedSystemId, csoId);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(preview.HasBlockingErrors, Is.False,
                    $"The populated preview must complete: {string.Join("; ", preview.Errors.Select(e => e.Detail))}");
                Assert.That(preview.Inbound!.WouldProject, Is.True, "The topology projects, and the preview must say so");
                Assert.That(preview.Inbound!.AttributeFlowChanges, Is.Not.Empty, "The DisplayName flow must be captured");
            }
        }

        // Assert - nothing persisted anywhere the integrity snapshot watches
        var after = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);
        Assert.That(() => after.AssertUnchangedSince(before), Throws.Nothing);
    }

    [Test]
    public async Task PreviewSyncForMvoAsync_UnknownObject_LeavesTheIntegrityTablesByteIdenticalAsync()
    {
        var before = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);

        await using (var ctx = NewContext())
        {
            var repo = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repo, syncRepository: new JIM.PostgresData.Repositories.SyncRepository(repo));
            var preview = await jim.SyncPreview.PreviewSyncForMvoAsync(Guid.NewGuid());
            Assert.That(preview.Errors.Single().Code, Is.EqualTo(JIM.Models.Transactional.SyncPreviewMessageCode.ObjectNotFound));
        }

        var after = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);
        Assert.That(() => after.AssertUnchangedSince(before), Throws.Nothing);
    }
}
