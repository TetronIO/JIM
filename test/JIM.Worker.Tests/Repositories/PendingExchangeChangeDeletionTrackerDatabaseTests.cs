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
/// Real-PostgreSQL regression cover for #2799: a Pending Exchange Change tracked on the Worker's context while the
/// account or identity it names is deleted by raw SQL.
/// </summary>
/// <remarks>
/// <para>
/// Both of the change's references to the objects are <c>ON DELETE SET NULL</c>, so the database clears them when the
/// object goes. The deletion's tracker fix-up must clear the tracked copy to match: left holding the deleted id, a later
/// save of the change as a whole row writes the id back, the database refuses it with <c>23503</c>, and every later
/// save on the context fails with it. The Connected System Object deletion's fix-up named each referencing type and
/// had no case for the Exchange queue; the Metaverse Object deletion's is driven by the model and already covered it.
/// </para>
/// <para>
/// <see cref="DeletionTrackerForeignKeyCoverageTests"/> holds both fix-ups to every such foreign key without a
/// database; this proves the failure end to end, which only a real database can show (the in-memory provider has no
/// foreign keys). Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables, as the other database-backed tests;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExchangeChangeDeletionTrackerDatabaseTests
{
    public enum DeletionEntryPoint
    {
        /// <summary><c>ConnectedSystemRepository.DeleteConnectedSystemObjectsAsync</c>: the synchronisation flush's.</summary>
        ConnectedSystemObjects,

        /// <summary><c>SyncRepository.DeleteConnectedSystemObjectsByIdsAsync</c>: the export path's.</summary>
        ConnectedSystemObjectIds
    }

    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Pending Exchange Change deletion tracker tests.");
        _connectionString = connectionString!;

        using var ctx = NewContext();
        // The repository sets Npgsql's timestamp switch, as the Worker does, before anything is written.
        _ = new PostgresDataRepository(ctx);
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    /// <summary>
    /// A tracking context, as the Worker holds one for a whole run.
    /// </summary>
    private JimDbContext NewWorkerContext()
    {
        var context = NewContext();
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        return context;
    }

    [Test]
    public async Task DeleteConnectedSystemObjects_ExchangeChangeForTheAccountTrackedThenSaved_SavesWithTheReferenceClearedAsync(
        [Values] DeletionEntryPoint entryPoint)
    {
        var seeded = await SeedAccountWithExchangeChangeAsync();

        await using var ctx = NewWorkerContext();
        var repository = new PostgresDataRepository(ctx);
        var account = await ctx.ConnectedSystemObjects.SingleAsync(c => c.Id == seeded.AccountId);
        var change = await ctx.PendingExchangeChanges.SingleAsync(c => c.Id == seeded.ChangeId);

        if (entryPoint == DeletionEntryPoint.ConnectedSystemObjects)
            await repository.ConnectedSystems.DeleteConnectedSystemObjectsAsync([account]);
        else
            await repository.Sync.DeleteConnectedSystemObjectsByIdsAsync([seeded.AccountId]);

        // The change saved later on the same context as a whole row, as an EF write of one entity does.
        change.Status = PendingExchangeChangeStatus.Parked;
        ctx.Entry(change).State = EntityState.Modified;
        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.Nothing,
            "saving the change does not write the deleted account's id back (23503)");

        await using var verify = NewContext();
        var stored = await verify.PendingExchangeChanges.SingleAsync(c => c.Id == seeded.ChangeId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.ConnectedSystemObjectId, Is.Null, "the tracked change's account is cleared, as the database cleared it");
            Assert.That(stored.ConnectedSystemObjectId, Is.Null, "the change stays on the queue, its account cleared");
            Assert.That(stored.MetaverseObjectId, Is.EqualTo(seeded.IdentityId), "its identity is untouched");
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Parked), "and the save landed");
        }
    }

    [Test]
    public async Task DeleteMetaverseObjectsAsync_ExchangeChangeForTheIdentityTrackedThenSaved_SavesWithTheReferenceClearedAsync()
    {
        var seeded = await SeedAccountWithExchangeChangeAsync();

        await using var ctx = NewWorkerContext();
        var repository = new PostgresDataRepository(ctx);
        var identity = await ctx.MetaverseObjects.SingleAsync(m => m.Id == seeded.IdentityId);
        var change = await ctx.PendingExchangeChanges.SingleAsync(c => c.Id == seeded.ChangeId);

        await repository.Sync.DeleteMetaverseObjectsAsync([identity]);

        change.Status = PendingExchangeChangeStatus.Parked;
        ctx.Entry(change).State = EntityState.Modified;
        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.Nothing,
            "saving the change does not write the deleted identity's id back (23503)");

        await using var verify = NewContext();
        var stored = await verify.PendingExchangeChanges.SingleAsync(c => c.Id == seeded.ChangeId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.MetaverseObjectId, Is.Null, "the tracked change's identity is cleared, as the database cleared it");
            Assert.That(stored.MetaverseObjectId, Is.Null, "the change stays on the queue, its identity cleared");
            Assert.That(stored.ConnectedSystemObjectId, Is.EqualTo(seeded.AccountId), "its account is untouched");
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Parked), "and the save landed");
        }
    }

    private sealed record Seeded(Guid AccountId, Guid IdentityId, Guid ChangeId);

    /// <summary>
    /// One account joined to an identity, with a mailbox change owed for it on the Exchange queue.
    /// </summary>
    private async Task<Seeded> SeedAccountWithExchangeChangeAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband Directory", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var externalId = new ConnectedSystemObjectTypeAttribute
        {
            Name = "objectGUID", ConnectedSystemObjectType = csType, Type = AttributeDataType.Guid,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(externalId);
        var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        seed.AddRange(connectorDefinition, system, csType, mvType);
        await seed.SaveChangesAsync();

        var identity = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType, Created = DateTime.UtcNow };
        var account = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystem = system, Type = csType, ExternalIdAttributeId = externalId.Id,
            Status = ConnectedSystemObjectStatus.Obsolete, MetaverseObject = identity,
            JoinType = ConnectedSystemObjectJoinType.Provisioned, DateJoined = DateTime.UtcNow
        };
        seed.AddRange(identity, account);
        await seed.SaveChangesAsync();

        var change = new PendingExchangeChange
        {
            Id = Guid.NewGuid(), ConnectedSystemId = system.Id, ConnectedSystemObjectId = account.Id, MetaverseObjectId = identity.Id,
            RecipientType = "UserMailbox", Alias = "bmayer", Database = "Mailbox Database 01", ActivityId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow
        };
        seed.PendingExchangeChanges.Add(change);
        await seed.SaveChangesAsync();

        return new Seeded(account.Id, identity.Id, change.Id);
    }
}
