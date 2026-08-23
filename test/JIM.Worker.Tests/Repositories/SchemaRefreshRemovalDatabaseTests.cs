// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the schema refresh removal's raw SQL writes (#1485): the set-based
/// obsoletion of Connected System Objects, the Pending Export cleanup that rides with it, and the deletion of
/// stored attribute values by attribute id (the first attribute-id-keyed deletion in the codebase, which must
/// clear change-history references its FK does not cascade). The in-memory provider cannot see a wrong column,
/// a wrong predicate or a violated FK in hand-written SQL. Opt-in via JIM_TEST_RESET_*; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SchemaRefreshRemovalDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL schema refresh removal tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    /// <summary>
    /// Seeds the FK graph the removal operates over: a Connected System with one Object Type carrying an
    /// external id attribute and one removable attribute.
    /// </summary>
    private async Task<(int SystemId, int TypeId, int ExternalIdAttributeId, int RemovableAttributeId)> SeedConnectedSystemGraphAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = $"Test Connector {Guid.NewGuid():N}", BuiltIn = true };
        var system = new ConnectedSystem { Name = $"Test System {Guid.NewGuid():N}", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var idAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "objectGUID", ConnectedSystemObjectType = csType, Type = AttributeDataType.Guid,
            IsExternalId = true, Selected = true
        };
        var faxAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "faxNumber", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text, Selected = true
        };
        csType.Attributes.Add(idAttribute);
        csType.Attributes.Add(faxAttribute);
        seed.AddRange(connectorDefinition, system, csType);
        await seed.SaveChangesAsync();
        return (system.Id, csType.Id, idAttribute.Id, faxAttribute.Id);
    }

    private async Task<ConnectedSystemObject> SeedCsoAsync(int systemId, int typeId, int externalIdAttributeId, ConnectedSystemObjectStatus status)
    {
        await using var seed = NewContext();
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = systemId,
            TypeId = typeId,
            ExternalIdAttributeId = externalIdAttributeId,
            Status = status,
            JoinType = ConnectedSystemObjectJoinType.NotJoined,
            Created = DateTime.UtcNow
        };
        seed.ConnectedSystemObjects.Add(cso);
        await seed.SaveChangesAsync();
        return cso;
    }

    [Test]
    public async Task ObsoleteConnectedSystemObjectsByIds_NormalAndAlreadyObsolete_FlipsOnlyTheNormalOneAsync()
    {
        var (systemId, typeId, externalIdAttributeId, _) = await SeedConnectedSystemGraphAsync();
        var normalCso = await SeedCsoAsync(systemId, typeId, externalIdAttributeId, ConnectedSystemObjectStatus.Normal);
        var alreadyObsoleteCso = await SeedCsoAsync(systemId, typeId, externalIdAttributeId, ConnectedSystemObjectStatus.Obsolete);
        var untouchedCso = await SeedCsoAsync(systemId, typeId, externalIdAttributeId, ConnectedSystemObjectStatus.Normal);

        int updated;
        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            updated = await repository.ConnectedSystems.ObsoleteConnectedSystemObjectsByIdsAsync([normalCso.Id, alreadyObsoleteCso.Id]);
        }

        await using var readContext = NewContext();
        var statusesById = await readContext.ConnectedSystemObjects.AsNoTracking()
            .Where(cso => cso.ConnectedSystemId == systemId)
            .ToDictionaryAsync(cso => cso.Id, cso => cso.Status);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated, Is.EqualTo(1), "Only the Normal-status object is flipped; a re-run must not touch objects already draining.");
            Assert.That(statusesById[normalCso.Id], Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
            Assert.That(statusesById[alreadyObsoleteCso.Id], Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
            Assert.That(statusesById[untouchedCso.Id], Is.EqualTo(ConnectedSystemObjectStatus.Normal), "An object outside the id set must be left alone.");
        }
    }

    [Test]
    public async Task DeletePendingExportsForConnectedSystemObjects_ExportWithValueChanges_DeletesBothLevelsAsync()
    {
        var (systemId, typeId, externalIdAttributeId, removableAttributeId) = await SeedConnectedSystemGraphAsync();
        var cso = await SeedCsoAsync(systemId, typeId, externalIdAttributeId, ConnectedSystemObjectStatus.Normal);

        Guid pendingExportId;
        await using (var seed = NewContext())
        {
            var pendingExport = new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = systemId,
                ConnectedSystemObjectId = cso.Id,
                ChangeType = PendingExportChangeType.Update
            };
            pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                AttributeId = removableAttributeId,
                ChangeType = PendingExportAttributeChangeType.Add,
                StringValue = "555-0100"
            });
            seed.PendingExports.Add(pendingExport);
            await seed.SaveChangesAsync();
            pendingExportId = pendingExport.Id;
        }

        int deleted;
        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            deleted = await repository.ConnectedSystems.DeletePendingExportsForConnectedSystemObjectsAsync([cso.Id]);
        }

        await using var readContext = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted, Is.EqualTo(1));
            Assert.That(await readContext.PendingExports.AsNoTracking().AnyAsync(pe => pe.Id == pendingExportId), Is.False);
            Assert.That(await readContext.PendingExportAttributeValueChanges.AsNoTracking().AnyAsync(c => c.PendingExportId == pendingExportId), Is.False,
                "The attribute value changes do not cascade from their Pending Export, so the delete must clear them itself.");
        }
    }

    [Test]
    public async Task DeleteConnectedSystemAttributeValuesByAttributeIds_ValuesAndAChangeHistoryReference_DeletesValuesAndClearsTheReferenceAsync()
    {
        var (systemId, typeId, externalIdAttributeId, removableAttributeId) = await SeedConnectedSystemGraphAsync();
        var cso = await SeedCsoAsync(systemId, typeId, externalIdAttributeId, ConnectedSystemObjectStatus.Normal);

        Guid removableValueId;
        Guid survivingValueId;
        Guid changeId;
        await using (var seed = NewContext())
        {
            // The value's CSO foreign key is shadow state, so the association goes through the navigation, on
            // an instance tracked by this context so Add() cannot walk into a duplicate insert.
            var trackedCso = await seed.ConnectedSystemObjects.AsTracking().SingleAsync(o => o.Id == cso.Id);
            var removableValue = new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                ConnectedSystemObject = trackedCso,
                AttributeId = removableAttributeId,
                StringValue = "555-0100"
            };
            var survivingValue = new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                ConnectedSystemObject = trackedCso,
                AttributeId = externalIdAttributeId,
                GuidValue = Guid.NewGuid()
            };
            seed.ConnectedSystemObjectAttributeValues.AddRange(removableValue, survivingValue);

            // A change-history row referencing the removable value through the non-cascading FK: the delete
            // must clear this reference or the whole statement fails on the constraint.
            var change = new ConnectedSystemObjectChange
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = systemId,
                ConnectedSystemObjectId = cso.Id,
                ChangeTime = DateTime.UtcNow,
                ChangeType = ObjectChangeType.Deleted,
                InitiatedByType = ActivityInitiatorType.System,
                DeletedObjectExternalIdAttributeValue = removableValue
            };
            seed.Add(change);
            await seed.SaveChangesAsync();
            removableValueId = removableValue.Id;
            survivingValueId = survivingValue.Id;
            changeId = change.Id;
        }

        int deleted;
        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            deleted = await repository.ConnectedSystems.DeleteConnectedSystemAttributeValuesByAttributeIdsAsync(systemId, [removableAttributeId]);
        }

        await using var readContext = NewContext();
        // The change's reference to the value is shadow state, so it is read via EF.Property.
        var changeReferenceValueId = await readContext.Set<ConnectedSystemObjectChange>().AsNoTracking()
            .Where(c => c.Id == changeId)
            .Select(c => EF.Property<Guid?>(c, "DeletedObjectExternalIdAttributeValueId"))
            .SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted, Is.EqualTo(1));
            Assert.That(await readContext.ConnectedSystemObjectAttributeValues.AsNoTracking().AnyAsync(av => av.Id == removableValueId), Is.False);
            Assert.That(await readContext.ConnectedSystemObjectAttributeValues.AsNoTracking().AnyAsync(av => av.Id == survivingValueId), Is.True,
                "Values of attributes outside the removal must be left alone.");
            Assert.That(changeReferenceValueId, Is.Null,
                "The change-history reference to the deleted value must be cleared, not left to fail the delete.");
        }
    }
}
