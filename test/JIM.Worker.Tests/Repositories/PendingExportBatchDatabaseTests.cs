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
/// Real-PostgreSQL verification of the Pending Export batch scope (#2492): the cross-page reference step queues the
/// Pending Exports its export re-evaluation replaces and deletes them in one statement per batch, rather than one
/// round trip and two change-tracker passes per object. The queue must be invisible to every caller: a queued row
/// reads as already gone, and anything inserted for the same Connected System Object lands after its delete, which
/// the unique index on PendingExports.ConnectedSystemObjectId would otherwise refuse.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportBatchDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Pending Export batch tests.");

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

    private sealed record Seeded(int SystemId, ConnectedSystemObjectTypeAttribute Mail, Guid CsoId, Guid OtherCsoId, Guid PendingExportId);

    /// <summary>
    /// Two Connected System Objects; the first has a Pending Export with one change.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var mail = new ConnectedSystemObjectTypeAttribute
        {
            Name = "mail", ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        type.Attributes.Add(mail);
        var cso = new ConnectedSystemObject { Type = type, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal };
        var other = new ConnectedSystemObject { Type = type, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal };
        seed.AddRange(connectorDefinition, system, type, cso, other);
        await seed.SaveChangesAsync();

        var pendingExport = NewPendingExport(system.Id, cso.Id, mail, "old@example.com");
        seed.PendingExports.Add(pendingExport);
        await seed.SaveChangesAsync();
        return new Seeded(system.Id, mail, cso.Id, other.Id, pendingExport.Id);
    }

    private static PendingExport NewPendingExport(int systemId, Guid csoId, ConnectedSystemObjectTypeAttribute attribute, string value, Guid? id = null)
    {
        var pendingExport = new PendingExport
        {
            Id = id ?? Guid.NewGuid(),
            ConnectedSystemId = systemId,
            ConnectedSystemObjectId = csoId,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = attribute.Id,
            StringValue = value,
            ChangeType = PendingExportAttributeChangeType.Update
        });
        return pendingExport;
    }

    private async Task<int> CountRowsAsync(Guid csoId)
    {
        await using var verify = NewContext();
        return await verify.PendingExports.CountAsync(pe => pe.ConnectedSystemObjectId == csoId);
    }

    [Test]
    public async Task DeletePendingExportForMergeAsync_InABatch_HidesTheRowUntilTheFlushDeletesItAsync()
    {
        var seeded = await SeedAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        repository.Sync.BeginPendingExportBatch();
        var existing = await repository.Sync.GetPendingExportLightweightByConnectedSystemObjectIdAsync(seeded.CsoId);
        await repository.Sync.DeletePendingExportForMergeAsync(existing!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountRowsAsync(seeded.CsoId), Is.EqualTo(1), "The delete is queued, not yet executed.");
            Assert.That(await repository.Sync.GetPendingExportLightweightByConnectedSystemObjectIdAsync(seeded.CsoId), Is.Null);
            Assert.That(await repository.Sync.GetPendingExportByConnectedSystemObjectIdAsync(seeded.CsoId), Is.Null);
            Assert.That(await repository.Sync.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync([seeded.CsoId]), Is.Empty);
            Assert.That(await repository.Sync.GetPendingExportsByIdsAsync([seeded.PendingExportId]), Is.Empty);
            Assert.That(await repository.Sync.GetConnectedSystemObjectIdsWithPendingExportsAsync([seeded.CsoId]), Is.Empty);
        }

        await repository.Sync.FlushPendingExportBatchAsync();

        Assert.That(await CountRowsAsync(seeded.CsoId), Is.Zero);
    }

    [Test]
    public async Task FlushPendingExportBatchAsync_ReplacementKeepingTheSameId_IsInsertedAfterTheDeleteAsync()
    {
        // A replacement for an export still waiting keeps its id (#2100), and is batch-inserted at the page flush,
        // after the batch's deletes.
        var seeded = await SeedAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        repository.Sync.BeginPendingExportBatch();
        var existing = await repository.Sync.GetPendingExportLightweightByConnectedSystemObjectIdAsync(seeded.CsoId);
        await repository.Sync.DeletePendingExportForMergeAsync(existing!);
        await repository.Sync.FlushPendingExportBatchAsync();
        await repository.Sync.CreatePendingExportsAsync([NewPendingExport(seeded.SystemId, seeded.CsoId, seeded.Mail, "new@example.com", seeded.PendingExportId)]);

        await using var verify = NewContext();
        var persisted = await verify.PendingExports.Include(pe => pe.AttributeValueChanges).SingleAsync(pe => pe.ConnectedSystemObjectId == seeded.CsoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.Id, Is.EqualTo(seeded.PendingExportId));
            Assert.That(persisted.AttributeValueChanges.Single().StringValue, Is.EqualTo("new@example.com"));
        }
    }

    [Test]
    public async Task CreatePendingExportAsync_ForAnObjectWithAQueuedDelete_ExecutesTheDeleteFirstAsync()
    {
        // Some paths insert at once (a Delete Pending Export replacing whatever was queued). The unique index on
        // ConnectedSystemObjectId would refuse it while the queued row still exists, so the queued delete runs first.
        var seeded = await SeedAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        repository.Sync.BeginPendingExportBatch();
        var existing = await repository.Sync.GetPendingExportLightweightByConnectedSystemObjectIdAsync(seeded.CsoId);
        await repository.Sync.DeletePendingExportForMergeAsync(existing!);
        var immediate = NewPendingExport(seeded.SystemId, seeded.CsoId, seeded.Mail, "now@example.com");
        await repository.Sync.CreatePendingExportAsync(immediate);
        await repository.Sync.FlushPendingExportBatchAsync();

        await using var verify = NewContext();
        var persisted = await verify.PendingExports.SingleAsync(pe => pe.ConnectedSystemObjectId == seeded.CsoId);
        Assert.That(persisted.Id, Is.EqualTo(immediate.Id), "The immediate insert survives the flush; the queued row is gone.");
    }

    [Test]
    public async Task AbandonPendingExportBatch_KeepsTheQueuedRowsAsync()
    {
        // A batch that fails part way stages no replacements, so its queued deletes must not run: deleting them would
        // lose the queued changes outright.
        var seeded = await SeedAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        repository.Sync.BeginPendingExportBatch();
        var existing = await repository.Sync.GetPendingExportLightweightByConnectedSystemObjectIdAsync(seeded.CsoId);
        await repository.Sync.DeletePendingExportForMergeAsync(existing!);
        repository.Sync.AbandonPendingExportBatch();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountRowsAsync(seeded.CsoId), Is.EqualTo(1));
            Assert.That(await repository.Sync.GetPendingExportLightweightByConnectedSystemObjectIdAsync(seeded.CsoId), Is.Not.Null,
                "Once the batch is abandoned the row reads as it stands again.");
        }
    }

    [Test]
    public async Task UpdatePendingExportAsync_NamingAQueuedPendingExport_FailsFastAsync()
    {
        var seeded = await SeedAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        repository.Sync.BeginPendingExportBatch();
        var existing = await repository.Sync.GetPendingExportLightweightByConnectedSystemObjectIdAsync(seeded.CsoId);
        await repository.Sync.DeletePendingExportForMergeAsync(existing!);

        Assert.That(async () => await repository.Sync.UpdatePendingExportAsync(existing!), Throws.InvalidOperationException);
        repository.Sync.AbandonPendingExportBatch();
    }

    [Test]
    public async Task BeginPendingExportBatch_WhileOneIsOpen_ThrowsAsync()
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        repository.Sync.BeginPendingExportBatch();
        Assert.That(() => repository.Sync.BeginPendingExportBatch(), Throws.InvalidOperationException);
        repository.Sync.AbandonPendingExportBatch();
    }

    [Test]
    public async Task PrefetchPendingExportsForBatchAsync_AnswersLookupsWithTheTrackedInstanceAsync()
    {
        var seeded = await SeedAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        repository.Sync.BeginPendingExportBatch();
        await repository.Sync.PrefetchPendingExportsForBatchAsync([seeded.CsoId, seeded.OtherCsoId]);
        var prefetched = await repository.Sync.GetPendingExportLightweightByConnectedSystemObjectIdAsync(seeded.CsoId);
        var none = await repository.Sync.GetPendingExportLightweightByConnectedSystemObjectIdAsync(seeded.OtherCsoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(prefetched?.Id, Is.EqualTo(seeded.PendingExportId));
            Assert.That(ctx.Entry(prefetched!).State, Is.EqualTo(EntityState.Unchanged),
                "A prefetched Pending Export must be tracked, as the lookup's own query returns it.");
            Assert.That(prefetched!.AttributeValueChanges.Single().Attribute, Is.Not.Null);
            Assert.That(none, Is.Null);
        }
        repository.Sync.AbandonPendingExportBatch();
    }

    [Test]
    public async Task PrefetchPendingExportsForBatchAsync_AfterAWrite_ReadsTheDatabaseAgainAsync()
    {
        var seeded = await SeedAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        repository.Sync.BeginPendingExportBatch();
        await repository.Sync.PrefetchPendingExportsForBatchAsync([seeded.CsoId, seeded.OtherCsoId]);

        // An immediate insert for an object prefetched as having none.
        var created = NewPendingExport(seeded.SystemId, seeded.OtherCsoId, seeded.Mail, "other@example.com");
        await repository.Sync.CreatePendingExportAsync(created);
        // An append onto a prefetched one.
        await repository.Sync.AppendAttributeChangesToPendingExportAsync(seeded.PendingExportId,
        [
            new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(), AttributeId = seeded.Mail.Id, StringValue = "appended@example.com",
                ChangeType = PendingExportAttributeChangeType.Update
            }
        ], []);

        var afterCreate = await repository.Sync.GetPendingExportLightweightByConnectedSystemObjectIdAsync(seeded.OtherCsoId);
        ctx.ChangeTracker.Clear();
        var afterAppend = await repository.Sync.GetPendingExportLightweightByConnectedSystemObjectIdAsync(seeded.CsoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterCreate?.Id, Is.EqualTo(created.Id));
            Assert.That(afterAppend!.AttributeValueChanges.Select(c => c.StringValue),
                Does.Contain("appended@example.com"));
        }
        repository.Sync.AbandonPendingExportBatch();
    }

    [Test]
    public async Task PrefetchPendingExportsForBatchAsync_WithNoBatchOpen_ThrowsAsync()
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        Assert.That(async () => await repository.Sync.PrefetchPendingExportsForBatchAsync([Guid.NewGuid()]), Throws.InvalidOperationException);
    }

    [Test]
    public async Task DeletePendingExportForMergeAsync_OutsideABatch_DeletesAtOnceAsync()
    {
        var seeded = await SeedAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var existing = await repository.Sync.GetPendingExportLightweightByConnectedSystemObjectIdAsync(seeded.CsoId);
        await repository.Sync.DeletePendingExportForMergeAsync(existing!);

        Assert.That(await CountRowsAsync(seeded.CsoId), Is.Zero);
    }
}
