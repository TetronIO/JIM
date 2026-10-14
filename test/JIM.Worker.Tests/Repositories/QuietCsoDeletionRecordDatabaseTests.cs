// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL check that a quiet Connected System Object deletion leaves its deletion record (#2100): the object's
/// row goes, and a Deleted change belonging to no execution item stays, with every field the read side relies on to find
/// the object's execution items again. The raw-SQL delete and the raw COPY of the record both bypass the EF model, so
/// the in-memory provider cannot see either.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class QuietCsoDeletionRecordDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL quiet deletion record tests.");

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
    public async Task DeleteConnectedSystemObjectsQuietlyAsync_ChangeTrackingOn_DeletesTheObjectAndPersistsItsDeletionRecordAsync()
    {
        // Arrange
        var connectorDefinition = new ConnectorDefinition { Name = $"Connector-{Guid.NewGuid():N}", BuiltIn = false };
        var system = new ConnectedSystem { Name = $"System-{Guid.NewGuid():N}", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var uidAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "uid", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        var displayNameAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(uidAttribute);
        csType.Attributes.Add(displayNameAttribute);

        await using (var seed = NewContext())
        {
            seed.AddRange(connectorDefinition, system, csType);
            await seed.SaveChangesAsync();
        }

        var csoId = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            var cso = new ConnectedSystemObject
            {
                Id = csoId,
                ConnectedSystemId = system.Id,
                TypeId = csType.Id,
                ExternalIdAttributeId = uidAttribute.Id,
                JoinType = ConnectedSystemObjectJoinType.NotJoined,
                Status = ConnectedSystemObjectStatus.Obsolete
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = uidAttribute.Id, StringValue = "tina.adams399" });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = displayNameAttribute.Id, StringValue = "Tina Adams" });
            seed.ConnectedSystemObjects.Add(cso);
            await seed.SaveChangesAsync();
        }

        var activity = new Activity { Id = Guid.NewGuid(), InitiatedByType = ActivityInitiatorType.System, InitiatedByName = "Scheduler" };

        // Act: as the worker's page flush does, on a context holding the object tracked with its values.
        await using (var workCtx = NewContext())
        {
            var loaded = await workCtx.ConnectedSystemObjects
                .AsTracking()
                .Include(c => c.Type)
                .Include(c => c.AttributeValues).ThenInclude(av => av.Attribute)
                .SingleAsync(c => c.Id == csoId);

            using var jim = new JimApplication(new PostgresDataRepository(workCtx), syncRepository: new SyncRepository(new PostgresDataRepository(workCtx)));
            await new SyncServer(jim).DeleteConnectedSystemObjectsQuietlyAsync([loaded], activity);
        }

        // Assert
        await using var verify = NewContext();
        var stillThere = await verify.ConnectedSystemObjects.AsNoTracking().AnyAsync(c => c.Id == csoId);
        var record = await verify.ConnectedSystemObjectChanges
            .AsNoTracking()
            .Include(c => c.AttributeChanges)
            .Include(c => c.DeletedObjectType)
            .SingleOrDefaultAsync(c => c.DeletedConnectedSystemObjectId == csoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stillThere, Is.False, "the object is deleted");
            Assert.That(record, Is.Not.Null, "its deletion is recorded");
            Assert.That(record!.ChangeType, Is.EqualTo(ObjectChangeType.Deleted));
            Assert.That(record!.ConnectedSystemId, Is.EqualTo(system.Id));
            Assert.That(record!.ActivityRunProfileExecutionItemId, Is.Null);
            Assert.That(record!.ConnectedSystemObjectId, Is.Null);
            Assert.That(record!.DeletedObjectExternalId, Is.EqualTo("tina.adams399"));
            Assert.That(record!.DeletedObjectDisplayName, Is.EqualTo("Tina Adams"));
            Assert.That(record!.DeletedObjectType?.Id, Is.EqualTo(csType.Id));
            Assert.That(record!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
            Assert.That(record!.InitiatedByName, Is.EqualTo("Scheduler"));
            Assert.That(record!.AttributeChanges.Select(a => a.AttributeName), Is.EquivalentTo(new[] { "uid", "displayName" }));
        }
    }
}
