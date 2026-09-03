// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the post-clear reconciliation join record and the state-convergent
/// zero-join pass (#1605 layer 2):
/// <list type="bullet">
/// <item><see cref="JIM.PostgresData.Repositories.ConnectedSystemRepository.DeleteAllConnectedSystemObjectsAndDependenciesAsync"/>'s
/// step zero is raw SQL (an <c>INSERT ... SELECT</c> preceded by a <c>DELETE</c>), so the in-memory
/// provider cannot prove it writes one row per joined Connected System Object, replaces the previous set on
/// a re-clear, or is skipped for Connected System deletion.</item>
/// <item><see cref="JIM.PostgresData.Repositories.MetaverseRepository.GetStateConvergentZeroJoinMetaverseObjectsAsync"/>
/// relies on EF translating a primitive-collection <c>Count</c>/<c>Contains</c> inside a boolean predicate;
/// LINQ-to-Objects in the unit tests cannot prove that translation, so it is exercised here.</item>
/// <item><see cref="JIM.PostgresData.Repositories.MetaverseRepository.MarkMvosAsDisconnectedWithNoTriggerAsync"/>
/// is raw SQL too, round-tripped here per src/CLAUDE.md's "every raw write path needs a RequiresPostgres
/// round-trip test" rule.</item>
/// </list>
/// Opt-in via JIM_TEST_RESET_*; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PostClearReconciliationDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL post-clear reconciliation tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    /// <summary>
    /// Seeds a Connected System with the FK graph a CSO row needs and returns the ids required to build
    /// joined CSOs against it.
    /// </summary>
    private async Task<(int SystemId, int TypeId, int ExternalIdAttributeId)> SeedConnectedSystemAsync(string name)
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = $"Test Connector {Guid.NewGuid():N}", BuiltIn = true };
        var system = new ConnectedSystem { Name = name, ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var idAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "objectGUID", ConnectedSystemObjectType = csType, Type = AttributeDataType.Guid,
            IsExternalId = true, Selected = true
        };
        csType.Attributes.Add(idAttribute);
        seed.AddRange(connectorDefinition, system, csType);
        await seed.SaveChangesAsync();
        return (system.Id, csType.Id, idAttribute.Id);
    }

    private async Task<int> SeedMetaverseObjectTypeAsync(
        MetaverseObjectDeletionRule deletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
        AuthoritativeSourceTriggerMode triggerMode = AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
        List<int>? triggerSystemIds = null,
        TimeSpan? gracePeriod = null)
    {
        await using var seed = NewContext();
        var type = new MetaverseObjectType
        {
            Name = $"Person {Guid.NewGuid():N}",
            PluralName = "People",
            DeletionRule = deletionRule,
            DeletionTriggerMode = triggerMode,
            DeletionTriggerConnectedSystemIds = triggerSystemIds ?? new List<int>(),
            DeletionGracePeriod = gracePeriod
        };
        seed.MetaverseObjectTypes.Add(type);
        await seed.SaveChangesAsync();
        return type.Id;
    }

    /// <summary>
    /// Seeds a Metaverse Object of the supplied type with zero or more joined CSOs, and optional
    /// already-pending-deletion marking, for the zero-join query's exclusion cases.
    /// </summary>
    private async Task<Guid> SeedMetaverseObjectAsync(
        int metaverseObjectTypeId,
        MetaverseObjectOrigin origin = MetaverseObjectOrigin.Projected,
        bool alreadyPendingDeletion = false,
        params (int SystemId, int TypeId, int ExternalIdAttributeId)[] joinedSystems)
    {
        await using var seed = NewContext();
        var type = await seed.MetaverseObjectTypes.SingleAsync(t => t.Id == metaverseObjectTypeId);
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = type,
            Origin = origin,
            LastConnectorDisconnectedDate = alreadyPendingDeletion ? DateTime.UtcNow.AddDays(-1) : null
        };
        seed.MetaverseObjects.Add(mvo);

        foreach (var (systemId, typeId, externalIdAttributeId) in joinedSystems)
        {
            seed.ConnectedSystemObjects.Add(new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = systemId,
                TypeId = typeId,
                ExternalIdAttributeId = externalIdAttributeId,
                Status = ConnectedSystemObjectStatus.Normal,
                JoinType = ConnectedSystemObjectJoinType.Joined,
                DateJoined = DateTime.UtcNow,
                Created = DateTime.UtcNow,
                MetaverseObjectId = mvo.Id
            });
        }

        await seed.SaveChangesAsync();
        return mvo.Id;
    }

    // -------------------------------------------------------------------------------------------------------
    // Join record write inside the clear transaction (#1605 Functional Requirement 6)
    // -------------------------------------------------------------------------------------------------------

    [Test]
    public async Task DeleteAllConnectedSystemObjectsAndDependenciesAsync_RecordJoinsTrue_WritesOneRecordPerJoinedCsoAsync()
    {
        var system = await SeedConnectedSystemAsync($"Clear {Guid.NewGuid():N}");
        var typeId = await SeedMetaverseObjectTypeAsync();
        var joinedMvoId = await SeedMetaverseObjectAsync(typeId, joinedSystems: system);
        // An unjoined CSO (no MetaverseObjectId): must not produce a join record.
        await using (var seed = NewContext())
        {
            seed.ConnectedSystemObjects.Add(new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = system.SystemId,
                TypeId = system.TypeId,
                ExternalIdAttributeId = system.ExternalIdAttributeId,
                Status = ConnectedSystemObjectStatus.Normal,
                JoinType = ConnectedSystemObjectJoinType.NotJoined,
                Created = DateTime.UtcNow,
                MetaverseObjectId = null
            });
            await seed.SaveChangesAsync();
        }

        ClearConnectedSystemResult result;
        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            result = await repository.ConnectedSystems.DeleteAllConnectedSystemObjectsAndDependenciesAsync(
                system.SystemId, deleteChangeHistory: true, recordJoinsForReconciliation: true);
        }

        Assert.That(result.JoinRecordsWritten, Is.EqualTo(1), "only the joined CSO produces a join record");

        await using var readContext = NewContext();
        var recorded = await readContext.ConnectorSpaceClearJoinRecords
            .Where(r => r.ConnectedSystemId == system.SystemId).ToListAsync();
        Assert.That(recorded, Has.Count.EqualTo(1));
        Assert.That(recorded[0].MetaverseObjectId, Is.EqualTo(joinedMvoId));
    }

    [Test]
    public async Task DeleteAllConnectedSystemObjectsAndDependenciesAsync_RecordJoinsFalse_WritesNoRecordsAsync()
    {
        var system = await SeedConnectedSystemAsync($"Delete {Guid.NewGuid():N}");
        var typeId = await SeedMetaverseObjectTypeAsync();
        await SeedMetaverseObjectAsync(typeId, joinedSystems: system);

        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            await repository.ConnectedSystems.DeleteAllConnectedSystemObjectsAndDependenciesAsync(
                system.SystemId, deleteChangeHistory: true, recordJoinsForReconciliation: false);
        }

        await using var readContext = NewContext();
        var recorded = await readContext.ConnectorSpaceClearJoinRecords
            .Where(r => r.ConnectedSystemId == system.SystemId).ToListAsync();
        Assert.That(recorded, Is.Empty, "the Connected System deletion path must never record joins for reconciliation");
    }

    [Test]
    public async Task DeleteAllConnectedSystemObjectsAndDependenciesAsync_ReClear_ReplacesThePreviousRecordSetAsync()
    {
        var system = await SeedConnectedSystemAsync($"ReClear {Guid.NewGuid():N}");
        var typeId = await SeedMetaverseObjectTypeAsync();
        var firstMvoId = await SeedMetaverseObjectAsync(typeId, joinedSystems: system);

        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            await repository.ConnectedSystems.DeleteAllConnectedSystemObjectsAndDependenciesAsync(
                system.SystemId, deleteChangeHistory: true, recordJoinsForReconciliation: true);
        }

        // A second Metaverse Object re-imports and re-joins after the first clear, then the system is
        // cleared again before any sweep consumed the first record set.
        var secondMvoId = await SeedMetaverseObjectAsync(typeId, joinedSystems: system);

        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            await repository.ConnectedSystems.DeleteAllConnectedSystemObjectsAndDependenciesAsync(
                system.SystemId, deleteChangeHistory: true, recordJoinsForReconciliation: true);
        }

        await using var readContext = NewContext();
        var recorded = await readContext.ConnectorSpaceClearJoinRecords
            .Where(r => r.ConnectedSystemId == system.SystemId).ToListAsync();
        Assert.That(recorded, Has.Count.EqualTo(1), "the re-clear must replace, not accumulate, the recorded set");
        Assert.That(recorded[0].MetaverseObjectId, Is.EqualTo(secondMvoId));
        Assert.That(recorded.Select(r => r.MetaverseObjectId), Does.Not.Contain(firstMvoId));
    }

    [Test]
    public async Task DeleteConnectedSystemAsync_RemovesJoinRecordsAsync()
    {
        var system = await SeedConnectedSystemAsync($"DeleteSystem {Guid.NewGuid():N}");
        var typeId = await SeedMetaverseObjectTypeAsync();
        await SeedMetaverseObjectAsync(typeId, joinedSystems: system);

        // Clear once (recording joins), then delete the system outright: the join records left behind by
        // the earlier clear must not survive, or the foreign key would block the system row's own delete.
        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            await repository.ConnectedSystems.DeleteAllConnectedSystemObjectsAndDependenciesAsync(
                system.SystemId, deleteChangeHistory: true, recordJoinsForReconciliation: true);
        }

        await using (var readContext = NewContext())
        {
            var recordedBeforeDelete = await readContext.ConnectorSpaceClearJoinRecords
                .Where(r => r.ConnectedSystemId == system.SystemId).ToListAsync();
            Assert.That(recordedBeforeDelete, Is.Not.Empty, "precondition: a join record exists before the system is deleted");
        }

        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            await repository.ConnectedSystems.DeleteConnectedSystemAsync(system.SystemId);
        }

        await using var finalReadContext = NewContext();
        var recordedAfterDelete = await finalReadContext.ConnectorSpaceClearJoinRecords
            .Where(r => r.ConnectedSystemId == system.SystemId).ToListAsync();
        Assert.That(recordedAfterDelete, Is.Empty, "join records must not survive the system's own deletion");
    }

    // -------------------------------------------------------------------------------------------------------
    // Set-based re-join shortfall query (#1605 Functional Requirement 9)
    // -------------------------------------------------------------------------------------------------------

    [Test]
    public async Task GetConnectorSpaceClearJoinRecordedMetaverseObjectIdsWithoutRejoinAsync_RecordedObjectRejoined_ExcludedAsync()
    {
        var system = await SeedConnectedSystemAsync($"Rejoined {Guid.NewGuid():N}");
        var typeId = await SeedMetaverseObjectTypeAsync();
        var mvoId = await SeedMetaverseObjectAsync(typeId, joinedSystems: system);
        await SeedJoinRecordAsync(system.SystemId, mvoId);

        await using var context = NewContext();
        var repository = new PostgresDataRepository(context);
        var missing = await repository.ConnectedSystems.GetConnectorSpaceClearJoinRecordedMetaverseObjectIdsWithoutRejoinAsync(system.SystemId);

        Assert.That(missing, Does.Not.Contain(mvoId), "a live Connected System Object of the same system means the object rejoined");
    }

    [Test]
    public async Task GetConnectorSpaceClearJoinRecordedMetaverseObjectIdsWithoutRejoinAsync_RecordedObjectJoinedToDifferentSystemOnly_IncludedAsync()
    {
        var clearedSystem = await SeedConnectedSystemAsync($"Cleared {Guid.NewGuid():N}");
        var otherSystem = await SeedConnectedSystemAsync($"Other {Guid.NewGuid():N}");
        var typeId = await SeedMetaverseObjectTypeAsync();
        // Joined to the OTHER system only: never rejoined the cleared one.
        var mvoId = await SeedMetaverseObjectAsync(typeId, joinedSystems: otherSystem);
        await SeedJoinRecordAsync(clearedSystem.SystemId, mvoId);

        await using var context = NewContext();
        var repository = new PostgresDataRepository(context);
        var missing = await repository.ConnectedSystems.GetConnectorSpaceClearJoinRecordedMetaverseObjectIdsWithoutRejoinAsync(clearedSystem.SystemId);

        Assert.That(missing, Contains.Item(mvoId), "a join to a different system does not count as a re-join to the cleared one");
    }

    [Test]
    public async Task GetConnectorSpaceClearJoinRecordedMetaverseObjectIdsWithoutRejoinAsync_RecordedObjectWithNoJoins_IncludedAsync()
    {
        var system = await SeedConnectedSystemAsync($"NoJoins {Guid.NewGuid():N}");
        var typeId = await SeedMetaverseObjectTypeAsync();
        var mvoId = await SeedMetaverseObjectAsync(typeId);
        await SeedJoinRecordAsync(system.SystemId, mvoId);

        await using var context = NewContext();
        var repository = new PostgresDataRepository(context);
        var missing = await repository.ConnectedSystems.GetConnectorSpaceClearJoinRecordedMetaverseObjectIdsWithoutRejoinAsync(system.SystemId);

        Assert.That(missing, Contains.Item(mvoId));
    }

    /// <summary>
    /// Writes a single ConnectorSpaceClearJoinRecord directly, independent of an actual clear, so the
    /// set-based re-join query's three selection cases can be composed without needing a full clear cycle.
    /// </summary>
    private async Task SeedJoinRecordAsync(int connectedSystemId, Guid metaverseObjectId)
    {
        await using var seed = NewContext();
        seed.ConnectorSpaceClearJoinRecords.Add(new ConnectorSpaceClearJoinRecord
        {
            ConnectedSystemId = connectedSystemId,
            MetaverseObjectId = metaverseObjectId,
            ClearedAt = DateTime.UtcNow
        });
        await seed.SaveChangesAsync();
    }

    // -------------------------------------------------------------------------------------------------------
    // State-convergent zero-join pass query (#1605 Functional Requirement 10)
    // -------------------------------------------------------------------------------------------------------

    [Test]
    public async Task GetStateConvergentZeroJoinMetaverseObjectsAsync_ProjectedZeroJoinLastConnectorRule_ReturnedAsync()
    {
        var typeId = await SeedMetaverseObjectTypeAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);
        var mvoId = await SeedMetaverseObjectAsync(typeId);

        await using var context = NewContext();
        var repository = new PostgresDataRepository(context);
        var results = await repository.Metaverse.GetStateConvergentZeroJoinMetaverseObjectsAsync(Guid.Empty, 10_000);

        Assert.That(results.Select(m => m.Id), Contains.Item(mvoId));
    }

    [Test]
    public async Task GetStateConvergentZeroJoinMetaverseObjectsAsync_InternalOrigin_NotReturnedAsync()
    {
        var typeId = await SeedMetaverseObjectTypeAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);
        var mvoId = await SeedMetaverseObjectAsync(typeId, origin: MetaverseObjectOrigin.Internal);

        await using var context = NewContext();
        var repository = new PostgresDataRepository(context);
        var results = await repository.Metaverse.GetStateConvergentZeroJoinMetaverseObjectsAsync(Guid.Empty, 10_000);

        Assert.That(results.Select(m => m.Id), Does.Not.Contain(mvoId), "an Internal-origin object (e.g. an admin account) must never be swept");
    }

    [Test]
    public async Task GetStateConvergentZeroJoinMetaverseObjectsAsync_AlreadyPendingDeletion_NotReturnedAsync()
    {
        var typeId = await SeedMetaverseObjectTypeAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);
        var mvoId = await SeedMetaverseObjectAsync(typeId, alreadyPendingDeletion: true);

        await using var context = NewContext();
        var repository = new PostgresDataRepository(context);
        var results = await repository.Metaverse.GetStateConvergentZeroJoinMetaverseObjectsAsync(Guid.Empty, 10_000);

        Assert.That(results.Select(m => m.Id), Does.Not.Contain(mvoId), "an earlier decision's markers must stand");
    }

    [Test]
    public async Task GetStateConvergentZeroJoinMetaverseObjectsAsync_SpecificSourcesMode_NotReturnedAsync()
    {
        var hr = await SeedConnectedSystemAsync($"HR {Guid.NewGuid():N}");
        var typeId = await SeedMetaverseObjectTypeAsync(
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            new List<int> { hr.SystemId });
        var mvoId = await SeedMetaverseObjectAsync(typeId);

        await using var context = NewContext();
        var repository = new PostgresDataRepository(context);
        var results = await repository.Metaverse.GetStateConvergentZeroJoinMetaverseObjectsAsync(Guid.Empty, 10_000);

        Assert.That(results.Select(m => m.Id), Does.Not.Contain(mvoId),
            "Specific-sources mode is event-only; state alone cannot distinguish a listed source departing from one that never joined");
    }

    [Test]
    public async Task GetStateConvergentZeroJoinMetaverseObjectsAsync_AllSourcesMode_ReturnedAsync()
    {
        var hr = await SeedConnectedSystemAsync($"HR {Guid.NewGuid():N}");
        var typeId = await SeedMetaverseObjectTypeAsync(
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            new List<int> { hr.SystemId });
        var mvoId = await SeedMetaverseObjectAsync(typeId);

        await using var context = NewContext();
        var repository = new PostgresDataRepository(context);
        var results = await repository.Metaverse.GetStateConvergentZeroJoinMetaverseObjectsAsync(Guid.Empty, 10_000);

        Assert.That(results.Select(m => m.Id), Contains.Item(mvoId), "All-sources mode is state-convergent");
    }

    [Test]
    public async Task GetStateConvergentZeroJoinMetaverseObjectsAsync_ObjectWithOneJoin_NotReturnedAsync()
    {
        var system = await SeedConnectedSystemAsync($"Joined {Guid.NewGuid():N}");
        var typeId = await SeedMetaverseObjectTypeAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);
        var mvoId = await SeedMetaverseObjectAsync(typeId, joinedSystems: system);

        await using var context = NewContext();
        var repository = new PostgresDataRepository(context);
        var results = await repository.Metaverse.GetStateConvergentZeroJoinMetaverseObjectsAsync(Guid.Empty, 10_000);

        Assert.That(results.Select(m => m.Id), Does.Not.Contain(mvoId), "an object with any live connector is not a zero-join candidate");
    }

    // -------------------------------------------------------------------------------------------------------
    // MarkMvosAsDisconnectedWithNoTriggerAsync round-trip (raw SQL write path)
    // -------------------------------------------------------------------------------------------------------

    [Test]
    public async Task MarkMvosAsDisconnectedWithNoTriggerAsync_MarkedMvo_PersistsNullTriggerAndInitiatorTriadAsync()
    {
        var typeId = await SeedMetaverseObjectTypeAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, gracePeriod: TimeSpan.FromDays(7));
        var mvoId = await SeedMetaverseObjectAsync(typeId);

        var snapshot = new MvoDeletionPolicySnapshot
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            ReasonCode = CausalReasonCode.NoConnectorRemainsStateConvergence,
            GracePeriod = TimeSpan.FromDays(7)
        };
        var initiatorId = Guid.NewGuid();

        int markedCount;
        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            markedCount = await repository.Metaverse.MarkMvosAsDisconnectedWithNoTriggerAsync(
                new List<Guid> { mvoId }, ActivityInitiatorType.System, initiatorId, "System", snapshot.ToJson());
        }

        Assert.That(markedCount, Is.EqualTo(1));

        await using var readContext = NewContext();
        var persisted = await readContext.MetaverseObjects.AsNoTracking().SingleAsync(m => m.Id == mvoId);
        Assert.That(persisted.LastConnectorDisconnectedDate, Is.Not.Null);
        Assert.That(persisted.DeletionTriggeredBySystemId, Is.Null, "a state-convergent marking has no triggering system");
        Assert.That(persisted.DeletionTriggeredBySystemName, Is.Null);
        Assert.That(persisted.DeletionInitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(persisted.DeletionInitiatedById, Is.EqualTo(initiatorId));
        Assert.That(persisted.DeletionInitiatedByName, Is.EqualTo("System"));

        var persistedSnapshot = MvoDeletionPolicySnapshot.FromJson(persisted.DeletionPolicySnapshotJson);
        Assert.That(persistedSnapshot, Is.Not.Null);
        Assert.That(persistedSnapshot!.ReasonCode, Is.EqualTo(CausalReasonCode.NoConnectorRemainsStateConvergence));
    }

    [Test]
    public async Task MarkMvosAsDisconnectedWithNoTriggerAsync_AlreadyMarkedMvo_DoesNotOverwriteExistingMarkersAsync()
    {
        var typeId = await SeedMetaverseObjectTypeAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);
        var mvoId = await SeedMetaverseObjectAsync(typeId, alreadyPendingDeletion: true);

        int markedCount;
        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            markedCount = await repository.Metaverse.MarkMvosAsDisconnectedWithNoTriggerAsync(
                new List<Guid> { mvoId }, ActivityInitiatorType.System, null, "System", null);
        }

        Assert.That(markedCount, Is.Zero, "an object already pending deletion must be skipped");
    }
}
