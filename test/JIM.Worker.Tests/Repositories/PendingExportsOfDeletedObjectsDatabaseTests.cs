// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of
/// <see cref="JIM.PostgresData.Repositories.SyncRepository.DeletePendingExportsOfDeletedObjectsAsync"/> (#3087): it deletes
/// the given objects' Pending Exports with their changes, leaves every other object's alone, and reports each deleted export
/// as it stood, read from the statement that deleted it.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c> fixtures; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportsOfDeletedObjectsDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL deleted-object Pending Export tests.");

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
    public async Task DeletePendingExportsOfDeletedObjectsAsync_DeletesTheObjectsExportsAndReportsEachAsItStoodAsync()
    {
        var (connectedSystemId, leaver, mover, stayer, attributeId) = await SeedObjectsAsync();
        var leaverDelete = BuildPendingExport(connectedSystemId, leaver, PendingExportChangeType.Delete, PendingExportStatus.Exported);
        var moverUpdate = BuildPendingExport(connectedSystemId, mover, PendingExportChangeType.Update, PendingExportStatus.ExportNotConfirmed, attributeId);
        var stayerUpdate = BuildPendingExport(connectedSystemId, stayer, PendingExportChangeType.Update, PendingExportStatus.Pending, attributeId);

        await using (var seed = NewContext())
        {
            seed.PendingExports.AddRange(leaverDelete, moverUpdate, stayerUpdate);
            await seed.SaveChangesAsync();
        }

        IReadOnlyList<DeletedPendingExportSummary> deleted;
        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            // The leaver named twice, as two candidates resolving to one object name it; it is still reported once.
            deleted = await repository.Sync.DeletePendingExportsOfDeletedObjectsAsync([leaver, mover, leaver]);
        }

        await using var verify = NewContext();
        var remainingExports = await verify.PendingExports.AsNoTracking().Select(pe => pe.Id).ToListAsync();
        var remainingChanges = await verify.PendingExportAttributeValueChanges.AsNoTracking().Select(c => c.PendingExportId).ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted, Is.EquivalentTo(new[]
            {
                new DeletedPendingExportSummary(leaverDelete.Id, leaver, PendingExportChangeType.Delete, PendingExportStatus.Exported),
                new DeletedPendingExportSummary(moverUpdate.Id, mover, PendingExportChangeType.Update, PendingExportStatus.ExportNotConfirmed)
            }), "each deleted export is reported once, with its object, change type and status as they stood");
            Assert.That(remainingExports, Is.EqualTo(new[] { stayerUpdate.Id }), "only the objects named lose their exports");
            Assert.That(remainingChanges, Is.All.EqualTo(stayerUpdate.Id), "a deleted export's changes are deleted with it");
            Assert.That(remainingChanges, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task DeletePendingExportsOfDeletedObjectsAsync_ObjectsWithNoExports_ReportsNothingAsync()
    {
        var (_, leaver, _, _, _) = await SeedObjectsAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        Assert.That(await repository.Sync.DeletePendingExportsOfDeletedObjectsAsync([leaver]), Is.Empty);
    }

    /// <summary>
    /// Seeds a Connected System with three objects. Returns ids only: the entities' navigations reach back through their whole
    /// parent graph, which a later, separate context would try to insert again.
    /// </summary>
    private async Task<(int ConnectedSystemId, Guid Leaver, Guid Mover, Guid Stayer, int AttributeId)> SeedObjectsAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(attribute);
        var objects = Enumerable.Range(0, 3)
            .Select(_ => new ConnectedSystemObject { Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal })
            .ToList();

        seed.AddRange(connectorDefinition, system, csType);
        seed.AddRange(objects);
        await seed.SaveChangesAsync();

        return (system.Id, objects[0].Id, objects[1].Id, objects[2].Id, attribute.Id);
    }

    private static PendingExport BuildPendingExport(int connectedSystemId, Guid csoId, PendingExportChangeType changeType,
        PendingExportStatus status, int? attributeId = null)
    {
        var export = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemObjectId = csoId,
            ChangeType = changeType,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };
        if (attributeId is { } id)
        {
            export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(), AttributeId = id, ChangeType = PendingExportAttributeChangeType.Update, StringValue = "value"
            });
        }

        return export;
    }
}
