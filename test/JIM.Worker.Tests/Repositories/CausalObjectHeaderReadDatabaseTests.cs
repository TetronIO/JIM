// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the reads the Causality Explorer (#2100) names objects with: a focus's header and the
/// trail's labels, for objects live and deleted. Each live read loads only what names and identifies an object, through
/// filtered includes the in-memory provider would satisfy whatever they said; a Group's members must never be loaded to
/// put its name on a trail.
///
/// Opt-in via JIM_TEST_RESET_*; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class CausalObjectHeaderReadDatabaseTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL causal object header read tests.");

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
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    [Test]
    public async Task GetConnectedSystemObjectsCoreByIdsAsync_ObjectsById_ArriveNamedWithoutTheirOtherValuesAsync()
    {
        var (groupId, userId) = await SeedConnectedSystemObjectsAsync();

        await using var ctx = NewContext();
        var objects = await new PostgresDataRepository(ctx).ConnectedSystems
            .GetConnectedSystemObjectsCoreByIdsAsync([groupId, userId, Guid.NewGuid()]);

        var group = objects.Single(o => o.Id == groupId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objects.Select(o => o.Id), Is.EquivalentTo(new[] { groupId, userId }), "an unknown id is absent");
            Assert.That(group.NameOrId, Is.EqualTo("Project Pulse"));
            Assert.That(group.ExternalIdAttributeValue?.StringValue, Is.EqualTo("G-100"));
            Assert.That(group.Type.Name, Is.EqualTo("group"));
            Assert.That(group.ConnectedSystem.Name, Is.EqualTo("Glitterband EMEA"));
            Assert.That(group.AttributeValues.Select(v => v.Attribute.Name), Is.EquivalentTo(new[] { "id", "displayName" }),
                "the members are not loaded to name the Group");
            Assert.That(objects.Single(o => o.Id == userId).NameOrId, Is.EqualTo("U-200"), "no name: its external ID");
        }
    }

    [Test]
    public async Task GetDeletedCsoChangesByObjectIdsAsync_DeletedObjects_EachGetsItsLatestDeletionRecordAsync()
    {
        var system = await SeedConnectedSystemAsync("Yellowstone APAC");
        var tina = Guid.NewGuid();
        var mia = Guid.NewGuid();
        await SeedCsoDeletionRecordAsync(system, tina, "Tina Adams", T0);
        var latest = await SeedCsoDeletionRecordAsync(system, tina, "Tina Adams (rehired)", T0.AddDays(30));
        await SeedCsoDeletionRecordAsync(system, mia, "Mia Young", T0);

        await using var ctx = NewContext();
        var records = await new PostgresDataRepository(ctx).ConnectedSystems
            .GetDeletedCsoChangesByObjectIdsAsync([tina, mia, Guid.NewGuid()]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(records.Select(r => r.DeletedConnectedSystemObjectId), Is.EquivalentTo(new Guid?[] { tina, mia }));
            Assert.That(records.Single(r => r.DeletedConnectedSystemObjectId == tina).Id, Is.EqualTo(latest), "one record per id: the latest");
            Assert.That(records.Single(r => r.DeletedConnectedSystemObjectId == mia).DeletedObjectType?.Name, Is.EqualTo("person"));
        }
    }

    [Test]
    public async Task GetMetaverseObjectsCoreByIdsAsync_ObjectsById_ArriveNamedWithoutTheirOtherValuesAsync()
    {
        var (tina, unnamed) = await SeedMetaverseObjectsAsync();

        await using var ctx = NewContext();
        var objects = await new PostgresDataRepository(ctx).Metaverse
            .GetMetaverseObjectsCoreByIdsAsync([tina, unnamed, Guid.NewGuid()]);

        var tinaObject = objects.Single(o => o.Id == tina);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objects.Select(o => o.Id), Is.EquivalentTo(new[] { tina, unnamed }));
            Assert.That(tinaObject.Name, Is.EqualTo("Tina Adams"));
            Assert.That(tinaObject.Type.Name, Is.EqualTo("User"));
            Assert.That(tinaObject.Type.PluralName, Is.EqualTo("Users"));
            Assert.That(tinaObject.AttributeValues.Select(v => v.Attribute.Name), Is.EquivalentTo(new[] { "Display Name" }),
                "only what names it");
            Assert.That(objects.Single(o => o.Id == unnamed).Name, Is.Null);
        }
    }

    [Test]
    public async Task GetDeletedMvoChangesByObjectIdsAsync_DeletedObjects_EachGetsItsLatestDeletionRecordAsync()
    {
        var type = await SeedMetaverseObjectTypeAsync();
        var tina = Guid.NewGuid();
        await SeedMvoDeletionRecordAsync(type, tina, "Tina Adams", T0);
        var latest = await SeedMvoDeletionRecordAsync(type, tina, "Tina Adams", T0.AddMinutes(1));
        await SeedMvoChangeAsync(tina, ObjectChangeType.AttributeFlow, T0.AddMinutes(-5));

        await using var ctx = NewContext();
        var records = await new PostgresDataRepository(ctx).Metaverse
            .GetDeletedMvoChangesByObjectIdsAsync([tina, Guid.NewGuid()]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(records.Select(r => r.Id), Is.EqualTo(new[] { latest }), "the deletion record, the latest, not its earlier changes");
            Assert.That(records.Single().DeletedObjectType?.PluralName, Is.EqualTo("Users"));
            Assert.That(records.Single().DeletedObjectDisplayName, Is.EqualTo("Tina Adams"));
        }
    }

    // ─── Helpers ───

    private async Task<(int SystemId, int TypeId)> SeedConnectedSystemAsync(string name)
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = $"Test Connector {Guid.NewGuid():N}", BuiltIn = true };
        var system = new ConnectedSystem { Name = name, ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = system, Selected = true };
        seed.AddRange(connectorDefinition, system, type);
        await seed.SaveChangesAsync();
        return (system.Id, type.Id);
    }

    /// <summary>
    /// A Group named by its display name and carrying members, and a user with no naming attribute at all.
    /// </summary>
    private async Task<(Guid GroupId, Guid UserId)> SeedConnectedSystemObjectsAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = $"Test Connector {Guid.NewGuid():N}", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };
        var id = new ConnectedSystemObjectTypeAttribute
            { Name = "id", ConnectedSystemObjectType = type, Type = AttributeDataType.Text, IsExternalId = true, Selected = true };
        var displayName = new ConnectedSystemObjectTypeAttribute
            { Name = "displayName", ConnectedSystemObjectType = type, Type = AttributeDataType.Text, Selected = true };
        var member = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = type, Type = AttributeDataType.Text, Selected = true,
            AttributePlurality = AttributePlurality.MultiValued
        };
        type.Attributes.AddRange([id, displayName, member]);
        seed.AddRange(connectorDefinition, system, type);
        await seed.SaveChangesAsync();

        var group = NewCso(system.Id, type.Id, id.Id);
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = id.Id, StringValue = "G-100" });
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = displayName.Id, StringValue = "Project Pulse" });
        for (var i = 0; i < 5; i++)
            group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = member.Id, StringValue = $"U-{i}" });
        var user = NewCso(system.Id, type.Id, id.Id);
        user.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = id.Id, StringValue = "U-200" });
        seed.ConnectedSystemObjects.AddRange(group, user);
        await seed.SaveChangesAsync();
        return (group.Id, user.Id);
    }

    private static ConnectedSystemObject NewCso(int systemId, int typeId, int externalIdAttributeId) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = systemId,
        TypeId = typeId,
        ExternalIdAttributeId = externalIdAttributeId,
        Status = ConnectedSystemObjectStatus.Normal,
        Created = DateTime.UtcNow
    };

    private async Task<Guid> SeedCsoDeletionRecordAsync((int SystemId, int TypeId) system, Guid deletedCsoId, string displayName, DateTime changeTime)
    {
        await using var ctx = NewContext();
        var record = new ConnectedSystemObjectChange
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.SystemId,
            ChangeType = ObjectChangeType.Deleted,
            ChangeTime = changeTime,
            InitiatedByType = ActivityInitiatorType.System,
            DeletedConnectedSystemObjectId = deletedCsoId,
            DeletedObjectDisplayName = displayName,
            DeletedObjectType = await ctx.ConnectedSystemObjectTypes.AsTracking().SingleAsync(t => t.Id == system.TypeId)
        };
        ctx.ConnectedSystemObjectChanges.Add(record);
        await ctx.SaveChangesAsync();
        return record.Id;
    }

    private async Task<MetaverseObjectType> SeedMetaverseObjectTypeAsync()
    {
        await using var ctx = NewContext();
        var type = new MetaverseObjectType { Name = "User", PluralName = "Users" };
        ctx.MetaverseObjectTypes.Add(type);
        await ctx.SaveChangesAsync();
        return type;
    }

    /// <summary>
    /// Tina, named by her Display Name and carrying a Department too, and an object with no name at all.
    /// </summary>
    private async Task<(Guid Tina, Guid Unnamed)> SeedMetaverseObjectsAsync()
    {
        await using var ctx = NewContext();
        var type = new MetaverseObjectType { Name = "User", PluralName = "Users" };
        var displayName = new MetaverseAttribute { Name = "Display Name", Type = AttributeDataType.Text };
        var department = new MetaverseAttribute { Name = "Department", Type = AttributeDataType.Text };
        var tina = new MetaverseObject { Id = Guid.NewGuid(), Type = type, Origin = MetaverseObjectOrigin.Projected };
        tina.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), Attribute = displayName, MetaverseObject = tina, StringValue = "Tina Adams" });
        tina.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), Attribute = department, MetaverseObject = tina, StringValue = "Finance" });
        var unnamed = new MetaverseObject { Id = Guid.NewGuid(), Type = type, Origin = MetaverseObjectOrigin.Projected };
        ctx.AddRange(type, displayName, department, tina, unnamed);
        await ctx.SaveChangesAsync();
        return (tina.Id, unnamed.Id);
    }

    private async Task<Guid> SeedMvoDeletionRecordAsync(MetaverseObjectType type, Guid deletedMvoId, string displayName, DateTime changeTime)
    {
        await using var ctx = NewContext();
        var record = new MetaverseObjectChange
        {
            Id = Guid.NewGuid(),
            ChangeType = ObjectChangeType.Deleted,
            ChangeTime = changeTime,
            InitiatedByType = ActivityInitiatorType.System,
            ChangeInitiatorType = MetaverseObjectChangeInitiatorType.System,
            DeletedMetaverseObjectId = deletedMvoId,
            DeletedObjectDisplayName = displayName,
            DeletedObjectTypeId = type.Id
        };
        ctx.MetaverseObjectChanges.Add(record);
        await ctx.SaveChangesAsync();
        return record.Id;
    }

    private async Task SeedMvoChangeAsync(Guid deletedMvoId, ObjectChangeType changeType, DateTime changeTime)
    {
        await using var ctx = NewContext();
        ctx.MetaverseObjectChanges.Add(new MetaverseObjectChange
        {
            Id = Guid.NewGuid(),
            ChangeType = changeType,
            ChangeTime = changeTime,
            InitiatedByType = ActivityInitiatorType.System,
            ChangeInitiatorType = MetaverseObjectChangeInitiatorType.SynchronisationRule,
            DeletedMetaverseObjectId = deletedMvoId
        });
        await ctx.SaveChangesAsync();
    }
}
