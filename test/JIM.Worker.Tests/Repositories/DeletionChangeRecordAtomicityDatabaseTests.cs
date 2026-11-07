// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Tests.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// A deleted object and the record of its deletion land together or not at all (#2669): every deleter that writes a
/// Deleted change record writes it in the same database transaction as the delete, so a failure between the two can
/// never leave an object gone with no record of its going.
/// </summary>
/// <remarks>
/// Each entry point runs with a failure injected into the change record write, the step after the delete, and the test
/// then reads the database on a connection of its own: the object must still be there and no deletion record may name
/// it. The same entry points without the failure must leave the object gone and its deletion record written. Opt-in via
/// the <c>JIM_TEST_RESET_*</c> environment variables; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class DeletionChangeRecordAtomicityDatabaseTests
{
    /// <summary>
    /// The deleters that write a Deleted change record, each named with the repository write that records it.
    /// </summary>
    public enum DeletionEntryPoint
    {
        /// <summary><c>SyncServer.DeleteMetaverseObjectsAsync</c>, the synchronisation flush's; records by <c>PersistPendingMvoChangesAsync</c>.</summary>
        MetaverseObjectsBulk,

        /// <summary><c>SyncServer.DeleteMetaverseObjectAsync</c>, the synchronisation fallback's; records by <c>CreateMetaverseObjectChangeDirectAsync</c>.</summary>
        MetaverseObjectSynchronisation,

        /// <summary><c>MetaverseServer.DeleteMetaverseObjectAsync</c>, the portal's and housekeeping's; records by <c>CreateMetaverseObjectChangeDirectAsync</c>.</summary>
        MetaverseObjectPortal,

        /// <summary><c>SyncServer.DeleteConnectedSystemObjectsQuietlyAsync</c>, a leaver cohort's; records by <c>PersistCsoChangesAsync</c>.</summary>
        ConnectedSystemObjectsQuiet,

        /// <summary>
        /// <c>SyncServer.DeleteConnectedSystemObjectsAsync</c>, the synchronisation flush's for objects that left their
        /// Connected System, each with an execution item; records by <c>PersistRpeiCsoChangesAsync</c> (#2807).
        /// </summary>
        ConnectedSystemObjectsWithExecutionItems
    }

    private sealed record Seeded(Guid MvoId, Guid CsoId);

    private sealed class InjectedFailureException() : Exception("A failure injected between the delete and its change record.");

    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL deletion change record atomicity tests.");
        _connectionString = connectionString!;

        using var ctx = NewContext();
        // The repository sets Npgsql's timestamp switch, as the Worker does, before anything is written.
        _ = new PostgresDataRepository(ctx);
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    /// <summary>
    /// A tracking context, as the Worker's.
    /// </summary>
    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning, RelationalEventId.MultipleCollectionIncludeWarning))
        .Options);

    [TestCase(DeletionEntryPoint.MetaverseObjectsBulk)]
    [TestCase(DeletionEntryPoint.MetaverseObjectSynchronisation)]
    [TestCase(DeletionEntryPoint.MetaverseObjectPortal)]
    [TestCase(DeletionEntryPoint.ConnectedSystemObjectsQuiet)]
    [TestCase(DeletionEntryPoint.ConnectedSystemObjectsWithExecutionItems)]
    public async Task Delete_ChangeRecordWriteFails_ObjectIsNotDeletedAndNoDeletionRecordIsWrittenAsync(DeletionEntryPoint entryPoint)
    {
        var seeded = await SeedAsync();

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var failure = new InjectedFailureException();
            var (jimRepository, syncRepository) = WithFailureInjected(entryPoint, repository, failure);
            using var jim = new JimApplication(jimRepository, syncRepository: syncRepository);

            var thrown = Assert.ThrowsAsync<InjectedFailureException>(() => RunAsync(entryPoint, ctx, jim, seeded));
            Assert.That(thrown, Is.SameAs(failure), "the injected failure reaches the caller");
        }

        var (objectExists, deletionRecords) = await ReadStateAsync(entryPoint, seeded);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objectExists, Is.True, $"{entryPoint}: the delete rolls back with the record that failed, so the object is still there");
            Assert.That(deletionRecords, Is.Zero, $"{entryPoint}: no deletion record names an object that is still there");
        }
    }

    [TestCase(DeletionEntryPoint.MetaverseObjectsBulk)]
    [TestCase(DeletionEntryPoint.MetaverseObjectSynchronisation)]
    [TestCase(DeletionEntryPoint.MetaverseObjectPortal)]
    [TestCase(DeletionEntryPoint.ConnectedSystemObjectsQuiet)]
    [TestCase(DeletionEntryPoint.ConnectedSystemObjectsWithExecutionItems)]
    public async Task Delete_NoFailure_ObjectIsDeletedAndItsDeletionRecordIsWrittenAsync(DeletionEntryPoint entryPoint)
    {
        var seeded = await SeedAsync();

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repository, syncRepository: repository.Sync);
            await RunAsync(entryPoint, ctx, jim, seeded);
        }

        var (objectExists, deletionRecords) = await ReadStateAsync(entryPoint, seeded);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objectExists, Is.False, $"{entryPoint}: the object is deleted");
            Assert.That(deletionRecords, Is.EqualTo(1), $"{entryPoint}: one deletion record names it");
        }
    }

    /// <summary>
    /// A synchronisation's deletion record belongs to the execution item reporting the deletion, so the item is written
    /// with the delete as well (#2807): it rolls back with a failed record, and lands with a successful one, the record
    /// naming it.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task DeleteConnectedSystemObjectsAsync_WithExecutionItems_ItemIsWrittenWithTheDeleteOrNotAtAllAsync(bool recordWriteFails)
    {
        var seeded = await SeedAsync();
        Guid itemId;

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var failure = new InjectedFailureException();
            var (jimRepository, syncRepository) = recordWriteFails
                ? WithFailureInjected(DeletionEntryPoint.ConnectedSystemObjectsWithExecutionItems, repository, failure)
                : (repository, repository.Sync);
            using var jim = new JimApplication(jimRepository, syncRepository: syncRepository);
            var activity = await ctx.Activities.FirstAsync();
            var leavers = await LoadConnectedSystemObjectsAsync(ctx, seeded);
            var items = DeletionItems(activity, leavers);

            if (recordWriteFails)
                Assert.ThrowsAsync<InjectedFailureException>(() => new SyncServer(jim).DeleteConnectedSystemObjectsAsync(leavers, items));
            else
                await new SyncServer(jim).DeleteConnectedSystemObjectsAsync(leavers, items);

            itemId = items.Single().Id;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(itemId, Is.Not.EqualTo(Guid.Empty), "the item is given its id before its record is written, so the record can name it");
            Assert.That(itemId.Version, Is.EqualTo(7), "the item's id is time-ordered, as the change records' are");
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT (SELECT COUNT(*) FROM "ActivityRunProfileExecutionItems" WHERE "Id" = @item),
                   (SELECT COUNT(*) FROM "ConnectedSystemObjectChanges" WHERE "ActivityRunProfileExecutionItemId" = @item AND "ChangeType" = @deleted)
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("item", NpgsqlDbType.Uuid) { Value = itemId });
        command.Parameters.Add(new NpgsqlParameter("deleted", NpgsqlDbType.Integer) { Value = (int)ObjectChangeType.Deleted });
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var (itemRows, records) = (reader.GetInt64(0), reader.GetInt64(1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(itemRows, Is.EqualTo(recordWriteFails ? 0 : 1),
                recordWriteFails ? "the item rolls back with the delete and its record" : "the item is written with the delete");
            Assert.That(records, Is.EqualTo(recordWriteFails ? 0 : 1),
                recordWriteFails ? "no record is written" : "the deletion record belongs to the item");
        }
    }

    /// <summary>
    /// The repositories the application is built over, with the entry point's change record write made to throw.
    /// </summary>
    private static (IRepository Repository, ISyncRepository SyncRepository) WithFailureInjected(
        DeletionEntryPoint entryPoint, PostgresDataRepository repository, Exception failure)
    {
        ISyncRepository ThrowFromSync(string methodName) =>
            InterceptingSyncRepositoryProxy.Create(repository.Sync, methodName, () => throw failure);

        return entryPoint switch
        {
            DeletionEntryPoint.MetaverseObjectsBulk =>
                (repository, ThrowFromSync(nameof(ISyncRepository.PersistPendingMvoChangesAsync))),
            DeletionEntryPoint.MetaverseObjectSynchronisation =>
                (repository, ThrowFromSync(nameof(ISyncRepository.CreateMetaverseObjectChangeDirectAsync))),
            DeletionEntryPoint.MetaverseObjectPortal =>
                (ThrowingMetaverseRepositoryProxy.Create(repository, nameof(IMetaverseRepository.CreateMetaverseObjectChangeDirectAsync), failure), repository.Sync),
            DeletionEntryPoint.ConnectedSystemObjectsQuiet =>
                (repository, ThrowFromSync(nameof(ISyncRepository.PersistCsoChangesAsync))),
            DeletionEntryPoint.ConnectedSystemObjectsWithExecutionItems =>
                (repository, ThrowFromSync(nameof(ISyncRepository.PersistRpeiCsoChangesAsync))),
            _ => throw new ArgumentOutOfRangeException(nameof(entryPoint), entryPoint, null)
        };
    }

    private static async Task RunAsync(DeletionEntryPoint entryPoint, JimDbContext ctx, JimApplication jim, Seeded seeded)
    {
        var activity = await ctx.Activities.FirstAsync();
        switch (entryPoint)
        {
            case DeletionEntryPoint.MetaverseObjectsBulk:
            {
                var leaver = await LoadMetaverseObjectAsync(ctx, seeded);
                await new SyncServer(jim).DeleteMetaverseObjectsAsync([(leaver, leaver.AttributeValues.ToList())], activity);
                break;
            }
            case DeletionEntryPoint.MetaverseObjectSynchronisation:
            {
                var leaver = await LoadMetaverseObjectAsync(ctx, seeded);
                await new SyncServer(jim).DeleteMetaverseObjectAsync(leaver, activity, leaver.AttributeValues.ToList());
                break;
            }
            case DeletionEntryPoint.MetaverseObjectPortal:
            {
                var leaver = await LoadMetaverseObjectAsync(ctx, seeded);
                await jim.Metaverse.DeleteMetaverseObjectAsync(leaver, ActivityInitiatorType.System, activityId: activity.Id);
                break;
            }
            case DeletionEntryPoint.ConnectedSystemObjectsQuiet:
            {
                var leavers = await LoadConnectedSystemObjectsAsync(ctx, seeded);
                await new SyncServer(jim).DeleteConnectedSystemObjectsQuietlyAsync(leavers, activity);
                break;
            }
            case DeletionEntryPoint.ConnectedSystemObjectsWithExecutionItems:
            {
                var leavers = await LoadConnectedSystemObjectsAsync(ctx, seeded);
                await new SyncServer(jim).DeleteConnectedSystemObjectsAsync(leavers, DeletionItems(activity, leavers));
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(entryPoint), entryPoint, null);
        }
    }

    private static Task<List<ConnectedSystemObject>> LoadConnectedSystemObjectsAsync(JimDbContext ctx, Seeded seeded) => ctx.ConnectedSystemObjects
        .Include(c => c.Type)
        .Include(c => c.AttributeValues).ThenInclude(v => v.Attribute)
        .Where(c => c.Id == seeded.CsoId)
        .ToListAsync();

    /// <summary>
    /// One execution item per object, as the synchronisation's obsoletion handling prepares them.
    /// </summary>
    private static List<ActivityRunProfileExecutionItem> DeletionItems(Activity activity, List<ConnectedSystemObject> leavers) =>
        leavers.Select(cso =>
        {
            var item = activity.PrepareRunProfileExecutionItem();
            item.ConnectedSystemObject = cso;
            item.ConnectedSystemObjectId = cso.Id;
            item.ObjectChangeType = ObjectChangeType.Deleted;
            item.SnapshotCsoDisplayFields(cso);
            return item;
        }).ToList();

    private static Task<MetaverseObject> LoadMetaverseObjectAsync(JimDbContext ctx, Seeded seeded) => ctx.MetaverseObjects
        .Include(m => m.Type)
        .Include(m => m.AttributeValues).ThenInclude(v => v.Attribute)
        .SingleAsync(m => m.Id == seeded.MvoId);

    /// <summary>
    /// Whether the entry point's object is still there, and how many deletion records name it, read on a connection of
    /// the test's own.
    /// </summary>
    private async Task<(bool ObjectExists, long DeletionRecords)> ReadStateAsync(DeletionEntryPoint entryPoint, Seeded seeded)
    {
        var (sql, id) = entryPoint is DeletionEntryPoint.ConnectedSystemObjectsQuiet or DeletionEntryPoint.ConnectedSystemObjectsWithExecutionItems
            ? ("""
               SELECT EXISTS (SELECT 1 FROM "ConnectedSystemObjects" WHERE "Id" = @id),
                      (SELECT COUNT(*) FROM "ConnectedSystemObjectChanges" WHERE "DeletedConnectedSystemObjectId" = @id AND "ChangeType" = @deleted)
               """, seeded.CsoId)
            : ("""
               SELECT EXISTS (SELECT 1 FROM "MetaverseObjects" WHERE "Id" = @id),
                      (SELECT COUNT(*) FROM "MetaverseObjectChanges" WHERE "DeletedMetaverseObjectId" = @id AND "ChangeType" = @deleted)
               """, seeded.MvoId);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });
        command.Parameters.Add(new NpgsqlParameter("deleted", NpgsqlDbType.Integer) { Value = (int)ObjectChangeType.Deleted });
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetBoolean(0), reader.GetInt64(1));
    }

    /// <summary>
    /// One Metaverse Object and one Connected System Object, each with a value its deletion record captures, and the
    /// Activity the deletions are made in.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        int systemId, typeId, externalIdAttributeId, mvoTypeId, displayNameAttributeId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Panoply Directory", ConnectorDefinition = connectorDefinition };
            var type = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            var externalId = new ConnectedSystemObjectTypeAttribute
            {
                Name = "employeeId", ConnectedSystemObjectType = type, Type = AttributeDataType.Text, IsExternalId = true, Selected = true
            };
            type.Attributes.Add(externalId);
            var mvoType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
            var displayName = new MetaverseAttribute { Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
            seed.AddRange(connectorDefinition, system, type, mvoType, displayName);
            seed.Activities.Add(new Activity
            {
                TargetName = "Leaver run",
                TargetType = ActivityTargetType.ConnectedSystemRunProfile,
                InitiatedByType = ActivityInitiatorType.System,
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
            (systemId, typeId, externalIdAttributeId) = (system.Id, type.Id, externalId.Id);
            (mvoTypeId, displayNameAttributeId) = (mvoType.Id, displayName.Id);
        }

        var (mvoId, csoId) = (Guid.NewGuid(), Guid.NewGuid());
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO "MetaverseObjects" ("Id", "Created", "TypeId", "Status", "DeletionInitiatedByType", "Origin")
            VALUES (@mvo, now(), @mvoType, 0, 0, 0);
            INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "StringValue")
            VALUES (gen_random_uuid(), @mvo, @displayName, 'Joan Leaver');

            INSERT INTO "ConnectedSystemObjects" ("Id", "Created", "TypeId", "ConnectedSystemId", "ExternalIdAttributeId", "Status", "JoinType")
            VALUES (@cso, now(), @type, @system, @externalId, 0, 0);
            INSERT INTO "ConnectedSystemObjectAttributeValues" ("Id", "ConnectedSystemObjectId", "AttributeId", "StringValue")
            VALUES (gen_random_uuid(), @cso, @externalId, 'E1001');
            """, connection);
        void Add(string name, object value, NpgsqlDbType type) => command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
        Add("mvo", mvoId, NpgsqlDbType.Uuid);
        Add("cso", csoId, NpgsqlDbType.Uuid);
        Add("mvoType", mvoTypeId, NpgsqlDbType.Integer);
        Add("displayName", displayNameAttributeId, NpgsqlDbType.Integer);
        Add("system", systemId, NpgsqlDbType.Integer);
        Add("type", typeId, NpgsqlDbType.Integer);
        Add("externalId", externalIdAttributeId, NpgsqlDbType.Integer);
        await command.ExecuteNonQueryAsync();

        return new Seeded(mvoId, csoId);
    }
}
