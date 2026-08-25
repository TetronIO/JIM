// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers.Preview;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL proof of the schema selection adapter (#1475) on two counts: that its three population reads
/// select what they claim against real SQL, and that running the preview leaves every synchronisation-integrity
/// table byte-identical.
///
/// The reads are the part a mocked fixture cannot check, and they are all status-sensitive in ways that are easy
/// to get quietly wrong: the freeze population is the LIVE objects (an obsolete one is already on its way out), the
/// attribute population is only the objects holding a value (the rest have nothing to freeze), and the obsoletion
/// toggle's population is the objects that are obsolete AND still joined (an obsolete object with no join has no
/// contributed values to withdraw). Each of those filters returns a plausible non-empty answer when wrong.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemSchemaPreviewIsolationDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL schema selection preview isolation tests.");

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
    public async Task EvaluateDeltasAsync_ObjectTypeDeselectedOverLiveDatabase_FreezesTheLiveObjectsAndPersistsNothingAsync()
    {
        var seeded = await SeedSchemaTopologyAsync();
        var before = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);

        var deltas = new List<PreviewDelta>();
        List<PreviewImpactCount> counts;
        await using (var ctx = NewContext())
        {
            var repo = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repo, syncRepository: new JIM.PostgresData.Repositories.SyncRepository(repo));
            var adapter = new ConnectedSystemSchemaPreviewAdapter(jim);
            var context = ContextFor(seeded, objectType => objectType with { Selected = false });

            await foreach (var delta in adapter.EvaluateDeltasAsync(context, CancellationToken.None))
                deltas.Add(delta);
            counts = await adapter.CountImpactAsync(context);
        }

        var after = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas.Select(d => d.ConnectedSystemObjectId),
                Is.EquivalentTo(new Guid?[] { seeded.JoinedLiveCsoId, seeded.UnjoinedLiveCsoId }),
                "the freeze is over the LIVE objects of the type. The obsolete one is already on its way out, and " +
                "counting it would double-report an object the next synchronisation disconnects anyway");
            Assert.That(deltas.Select(d => d.TransitionType),
                Is.All.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopBeingImported));
            Assert.That(counts.Sum(c => c.ObjectCount), Is.EqualTo(deltas.Count));
            Assert.That(() => after.AssertUnchangedSince(before), Throws.Nothing,
                "a preview of a schema change must not save the schema change");
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_AttributeDeselected_ReportsOnlyTheObjectsHoldingAValueAsync()
    {
        var seeded = await SeedSchemaTopologyAsync();
        var before = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);

        var deltas = new List<PreviewDelta>();
        await using (var ctx = NewContext())
        {
            var repo = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repo, syncRepository: new JIM.PostgresData.Repositories.SyncRepository(repo));
            var adapter = new ConnectedSystemSchemaPreviewAdapter(jim);

            var context = ContextFor(seeded, objectType => objectType with
            {
                SelectedAttributeIds = objectType.SelectedAttributeIds
                    .Where(id => id != seeded.DepartmentAttributeId).ToList()
            });

            await foreach (var delta in adapter.EvaluateDeltasAsync(context, CancellationToken.None))
                deltas.Add(delta);
        }

        var after = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas.Select(d => d.ConnectedSystemObjectId),
                Is.EquivalentTo(new Guid?[] { seeded.JoinedLiveCsoId }),
                "only the object holding a department value has anything to freeze; the other two hold none");
            Assert.That(deltas[0].AttributeName, Is.EqualTo("department"));
            Assert.That(() => after.AssertUnchangedSince(before), Throws.Nothing);
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_ObsoletionRecallTurnedOff_ReportsTheObsoleteJoinedObjectOnlyAsync()
    {
        var seeded = await SeedSchemaTopologyAsync();
        var before = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);

        var deltas = new List<PreviewDelta>();
        await using (var ctx = NewContext())
        {
            var repo = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repo, syncRepository: new JIM.PostgresData.Repositories.SyncRepository(repo));
            var adapter = new ConnectedSystemSchemaPreviewAdapter(jim);
            var context = ContextFor(seeded, objectType => objectType with
            {
                RemoveContributedAttributesOnObsoletion = false
            });

            await foreach (var delta in adapter.EvaluateDeltasAsync(context, CancellationToken.None))
                deltas.Add(delta);
        }

        var after = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas.Select(d => d.ConnectedSystemObjectId),
                Is.EquivalentTo(new Guid?[] { seeded.ObsoleteJoinedCsoId }),
                "the toggle changes the fate of the objects already obsolete AND still joined. A live object has " +
                "not been obsoleted, and an obsolete one with no join has no contributed values to withdraw");
            Assert.That(deltas[0].TransitionType,
                Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues));
            Assert.That(() => after.AssertUnchangedSince(before), Throws.Nothing);
        }
    }

    private static PreviewContext ContextFor(SeededSchema seeded,
        Func<ConnectedSystemObjectTypeSelectionProposal, ConnectedSystemObjectTypeSelectionProposal> edit) =>
        new()
        {
            Surface = ConfigurationChangePreviewSurface.ConnectedSystemSchema,
            ActivityId = Guid.CreateVersion7(),
            TargetId = seeded.ConnectedSystemId,
            ProposedConfiguration = new ConnectedSystemSchemaProposal([edit(seeded.StoredSelection)])
        };

    private sealed record SeededSchema(
        int ConnectedSystemId,
        int DepartmentAttributeId,
        Guid JoinedLiveCsoId,
        Guid UnjoinedLiveCsoId,
        Guid ObsoleteJoinedCsoId,
        Guid ObsoleteUnjoinedCsoId,
        ConnectedSystemObjectTypeSelectionProposal StoredSelection);

    /// <summary>
    /// Seeds one selected Object Type with four objects covering every combination the three population reads
    /// have to tell apart: live and joined (carrying a department value), live and unjoined, obsolete and joined,
    /// obsolete and unjoined.
    /// </summary>
    private async Task<SeededSchema> SeedSchemaTopologyAsync()
    {
        await using var seedCtx = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Schema Preview Test Connector", BuiltIn = false };
        var connectedSystem = new ConnectedSystem
        {
            Name = "Schema Preview Source",
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
            RemoveContributedAttributesOnObsoletion = true,
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

        var externalIdAttribute = csoType.Attributes.Single(a => a.IsExternalId);
        var departmentAttribute = csoType.Attributes.Single(a => a.Name == "department");

        var joinedMvo = new MetaverseObject { Id = Guid.CreateVersion7(), Type = mvType };
        var obsoleteMvo = new MetaverseObject { Id = Guid.CreateVersion7(), Type = mvType };
        seedCtx.MetaverseObjects.AddRange(joinedMvo, obsoleteMvo);
        await seedCtx.SaveChangesAsync();

        var joinedLive = BuildCso(connectedSystem.Id, csoType.Id, externalIdAttribute.Id,
            ConnectedSystemObjectStatus.Normal, joinedMvo.Id);
        joinedLive.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.CreateVersion7(),
            AttributeId = departmentAttribute.Id,
            StringValue = "Payroll"
        });

        var unjoinedLive = BuildCso(connectedSystem.Id, csoType.Id, externalIdAttribute.Id,
            ConnectedSystemObjectStatus.Normal, metaverseObjectId: null);
        var obsoleteJoined = BuildCso(connectedSystem.Id, csoType.Id, externalIdAttribute.Id,
            ConnectedSystemObjectStatus.Obsolete, obsoleteMvo.Id);
        var obsoleteUnjoined = BuildCso(connectedSystem.Id, csoType.Id, externalIdAttribute.Id,
            ConnectedSystemObjectStatus.Obsolete, metaverseObjectId: null);

        seedCtx.ConnectedSystemObjects.AddRange(joinedLive, unjoinedLive, obsoleteJoined, obsoleteUnjoined);
        await seedCtx.SaveChangesAsync();

        return new SeededSchema(
            connectedSystem.Id,
            departmentAttribute.Id,
            joinedLive.Id,
            unjoinedLive.Id,
            obsoleteJoined.Id,
            obsoleteUnjoined.Id,
            ConnectedSystemObjectTypeSelectionProposal.FromObjectType(csoType));
    }

    private static ConnectedSystemObject BuildCso(int connectedSystemId, int objectTypeId, int externalIdAttributeId,
        ConnectedSystemObjectStatus status, Guid? metaverseObjectId)
    {
        var id = Guid.CreateVersion7();
        return new ConnectedSystemObject
        {
            Id = id,
            ConnectedSystemId = connectedSystemId,
            TypeId = objectTypeId,
            ExternalIdAttributeId = externalIdAttributeId,
            Status = status,
            MetaverseObjectId = metaverseObjectId,
            Created = DateTime.UtcNow,
            AttributeValues =
            [
                new ConnectedSystemObjectAttributeValue
                {
                    Id = Guid.CreateVersion7(),
                    AttributeId = externalIdAttributeId,
                    GuidValue = id
                }
            ]
        };
    }
}
