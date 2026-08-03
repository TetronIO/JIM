// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the Connected System deletion orphan-marking path (#119):
/// - MarkMvosAsDisconnectedAsync is raw SQL, so the in-memory provider cannot catch column-name,
///   parameter-order or parameter-typing regressions in the UPDATE that records the disconnection date,
///   the triggering system fields and the decision-time policy snapshot; this fixture round-trips them.
/// - GetMvosOrphanedByConnectedSystemDeletionAsync's mode-aware predicate relies on EF translating a
///   primitive-collection Contains inside a correlated subquery; LINQ-to-Objects in the unit tests cannot
///   prove that translation, so it is exercised here against a real database.
/// Opt-in via JIM_TEST_RESET_*; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MvoOrphanMarkingDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL MVO orphan marking tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    /// <summary>
    /// Seeds a Connected System with the FK graph a CSO row needs and returns the ids required to
    /// build joined CSOs against it.
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

    /// <summary>
    /// Seeds a Projected MVO of the supplied type with one joined CSO per referenced system graph.
    /// </summary>
    private async Task<Guid> SeedProjectedMvoAsync(int metaverseObjectTypeId, params (int SystemId, int TypeId, int ExternalIdAttributeId)[] joinedSystems)
    {
        await using var seed = NewContext();
        var type = await seed.MetaverseObjectTypes.SingleAsync(t => t.Id == metaverseObjectTypeId);
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = type,
            Origin = MetaverseObjectOrigin.Projected
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

    private async Task<int> SeedMetaverseObjectTypeAsync(MetaverseObjectDeletionRule deletionRule, AuthoritativeSourceTriggerMode triggerMode, List<int> triggerSystemIds, TimeSpan? gracePeriod)
    {
        await using var seed = NewContext();
        var type = new MetaverseObjectType
        {
            Name = $"Person {Guid.NewGuid():N}",
            PluralName = "People",
            DeletionRule = deletionRule,
            DeletionTriggerMode = triggerMode,
            DeletionTriggerConnectedSystemIds = triggerSystemIds,
            DeletionGracePeriod = gracePeriod
        };
        seed.MetaverseObjectTypes.Add(type);
        await seed.SaveChangesAsync();
        return type.Id;
    }

    [Test]
    public async Task MarkMvosAsDisconnectedAsync_MarkedMvo_PersistsTriggerFieldsAndSnapshotAsync()
    {
        // Arrange
        var hr = await SeedConnectedSystemAsync($"HR {Guid.NewGuid():N}");
        var typeId = await SeedMetaverseObjectTypeAsync(
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            new List<int> { hr.SystemId },
            TimeSpan.FromDays(7));
        var mvoId = await SeedProjectedMvoAsync(typeId, hr);

        var snapshot = new MvoDeletionPolicySnapshot
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            TriggerMode = AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            SelectedSourceSystemIds = { hr.SystemId },
            SelectedSourceSystemNames = { "HR System" },
            GracePeriod = TimeSpan.FromDays(7),
            TriggeringSystemId = hr.SystemId,
            TriggeringSystemName = "HR System"
        };

        // Act: mark through the raw SQL path the Connected System deletion uses
        int markedCount;
        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            markedCount = await repository.Metaverse.MarkMvosAsDisconnectedAsync(
                new List<Guid> { mvoId }, hr.SystemId, "HR System", snapshot.ToJson());
        }

        // Assert: every marker the UPDATE writes must round-trip
        Assert.That(markedCount, Is.EqualTo(1));
        await using var readContext = NewContext();
        var persisted = await readContext.MetaverseObjects.AsNoTracking().SingleAsync(m => m.Id == mvoId);
        Assert.That(persisted.LastConnectorDisconnectedDate, Is.Not.Null,
            "The disconnection date must be set or housekeeping will never delete the orphaned MVO.");
        Assert.That(persisted.DeletionTriggeredBySystemId, Is.EqualTo(hr.SystemId));
        Assert.That(persisted.DeletionTriggeredBySystemName, Is.EqualTo("HR System"));

        var persistedSnapshot = MvoDeletionPolicySnapshot.FromJson(persisted.DeletionPolicySnapshotJson);
        Assert.That(persistedSnapshot, Is.Not.Null, "The decision-time policy snapshot must deserialise after the round trip.");
        Assert.That(persistedSnapshot!.DeletionRule, Is.EqualTo(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected));
        Assert.That(persistedSnapshot!.TriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.AllSourcesDisconnect));
        Assert.That(persistedSnapshot!.SelectedSourceSystemIds, Is.EqualTo(new List<int> { hr.SystemId }));
        Assert.That(persistedSnapshot!.GracePeriod, Is.EqualTo(TimeSpan.FromDays(7)));
        Assert.That(persistedSnapshot!.TriggeringSystemId, Is.EqualTo(hr.SystemId));
        Assert.That(persistedSnapshot!.TriggeringSystemName, Is.EqualTo("HR System"));
    }

    [Test]
    public async Task MarkMvosAsDisconnectedAsync_AlreadyMarkedMvo_DoesNotOverwriteExistingMarkersAsync()
    {
        // Arrange: an MVO already pending deletion from an earlier decision
        var hr = await SeedConnectedSystemAsync($"HR {Guid.NewGuid():N}");
        var typeId = await SeedMetaverseObjectTypeAsync(
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            new List<int>(),
            TimeSpan.FromDays(7));
        var mvoId = await SeedProjectedMvoAsync(typeId, hr);

        var originalDisconnectedDate = DateTime.UtcNow.AddDays(-3);
        await using (var seed = NewContext())
        {
            var mvo = await seed.MetaverseObjects.SingleAsync(m => m.Id == mvoId);
            mvo.LastConnectorDisconnectedDate = originalDisconnectedDate;
            mvo.DeletionTriggeredBySystemId = 999;
            mvo.DeletionTriggeredBySystemName = "Original Trigger System";
            await seed.SaveChangesAsync();
        }

        // Act
        int markedCount;
        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            markedCount = await repository.Metaverse.MarkMvosAsDisconnectedAsync(
                new List<Guid> { mvoId }, hr.SystemId, "HR System", null);
        }

        // Assert: the earlier decision's markers stand untouched
        Assert.That(markedCount, Is.Zero);
        await using var readContext = NewContext();
        var persisted = await readContext.MetaverseObjects.AsNoTracking().SingleAsync(m => m.Id == mvoId);
        Assert.That(persisted.LastConnectorDisconnectedDate, Is.EqualTo(originalDisconnectedDate).Within(TimeSpan.FromSeconds(1)));
        Assert.That(persisted.DeletionTriggeredBySystemId, Is.EqualTo(999));
        Assert.That(persisted.DeletionTriggeredBySystemName, Is.EqualTo("Original Trigger System"));
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_ModeAwarePredicate_TranslatesAndFiltersOnRealPostgresAsync()
    {
        // Arrange: two listed sources plus an unlisted target system
        var hr = await SeedConnectedSystemAsync($"HR {Guid.NewGuid():N}");
        var ad = await SeedConnectedSystemAsync($"AD {Guid.NewGuid():N}");
        var target = await SeedConnectedSystemAsync($"Target {Guid.NewGuid():N}");

        var allModeTypeId = await SeedMetaverseObjectTypeAsync(
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            new List<int> { hr.SystemId, ad.SystemId },
            TimeSpan.FromDays(30));
        var specificModeTypeId = await SeedMetaverseObjectTypeAsync(
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            new List<int> { hr.SystemId, ad.SystemId },
            TimeSpan.FromDays(30));

        // All mode: blocked by the other listed source remaining connected
        var allModeBlockedMvoId = await SeedProjectedMvoAsync(allModeTypeId, hr, ad);
        // All mode: markable, only an unlisted target remains
        var allModeMarkableMvoId = await SeedProjectedMvoAsync(allModeTypeId, hr, target);
        // Specific mode: markable even though the other listed source remains connected
        var specificModeMarkableMvoId = await SeedProjectedMvoAsync(specificModeTypeId, hr, ad);
        // Specific mode: unaffected, no CSO in the deleted system
        var specificModeUnaffectedMvoId = await SeedProjectedMvoAsync(specificModeTypeId, ad);

        // Act
        await using var context = NewContext();
        var repository = new PostgresDataRepository(context);
        var orphanedMvos = await repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(hr.SystemId);
        var orphanedCount = await repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionCountAsync(hr.SystemId);

        // Assert: the predicate translates and applies the mode semantics, and the preview count agrees
        var orphanedIds = orphanedMvos.Select(m => m.Id).ToList();
        Assert.That(orphanedIds, Is.EquivalentTo(new[] { allModeMarkableMvoId, specificModeMarkableMvoId }));
        Assert.That(orphanedIds, Does.Not.Contain(allModeBlockedMvoId));
        Assert.That(orphanedIds, Does.Not.Contain(specificModeUnaffectedMvoId));
        Assert.That(orphanedCount, Is.EqualTo(orphanedMvos.Count));
    }
}
