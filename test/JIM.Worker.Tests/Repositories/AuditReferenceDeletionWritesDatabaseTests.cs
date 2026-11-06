// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Every way JIM deletes a Connected System Object or a Metaverse Object leaves the history that names it alone (#2669):
/// no change record, change value, Run Profile Execution Item or Activity is updated or deleted, whether by a statement
/// of the deleter's own or by a foreign key's action in the database.
/// <para>
/// Each entry point runs inside a transaction the test holds, against objects with history of every kind naming them,
/// and the test reads <c>pg_stat_xact_user_tables</c> on that transaction's connection at its start and before rolling
/// it back. That view counts the backend's own row writes per table, trigger-driven ones included, so a rewrite cannot
/// hide in a cascade; and being the backend's own, it cannot be disturbed by another connection's statistics. It also
/// carries earlier transactions' writes until the backend flushes them, which it never does inside a transaction, so the
/// difference across the transaction is exactly what the deletion wrote.
/// </para>
/// <para>
/// The schema-level counterpart is <see cref="JIM.Worker.Tests.Servers.AuditReferenceCatalogueTests"/>, which holds
/// the history columns to plain ids. Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class AuditReferenceDeletionWritesDatabaseTests
{
    /// <summary>
    /// The tables whose rows name a Connected System Object or a Metaverse Object as history.
    /// </summary>
    private static readonly string[] AuditTables =
    [
        "ConnectedSystemObjectChanges", "ConnectedSystemObjectChangeAttributeValues", "ActivityRunProfileExecutionItems",
        "MetaverseObjectChanges", "MetaverseObjectChangeAttributeValues", "Activities"
    ];

    public enum DeletionEntryPoint
    {
        /// <summary><c>ConnectedSystemRepository.DeleteConnectedSystemObjectsAsync</c>.</summary>
        ConnectedSystemRepository,

        /// <summary><c>SyncServer.DeleteConnectedSystemObjectsQuietlyAsync</c>, deletion records persisted.</summary>
        QuietDeletion,

        /// <summary><c>SyncServer.DeleteConnectedSystemObjectsAsync</c>, deletion records linked to execution items.</summary>
        ExecutionItems,

        /// <summary><c>SyncRepository.DeleteConnectedSystemObjectsByIdsAsync</c>, the export path's.</summary>
        SyncRepositoryByIds,

        /// <summary><c>ConnectedSystemRepository.ClearConnectorSpaceAsync</c>, keeping change history.</summary>
        ConnectorSpaceClearKeepingHistory,

        /// <summary><c>ConnectedSystemRepository.DeleteConnectedSystemAsync</c>, keeping change history.</summary>
        ConnectedSystemDeletionKeepingHistory,

        /// <summary><c>SyncRepository.DeleteMetaverseObjectsAsync</c>, the synchronisation flush's.</summary>
        MetaverseObjectsBulk,

        /// <summary><c>MetaverseRepository.DeleteMetaverseObjectAsync</c>, the portal's and housekeeping's.</summary>
        MetaverseObjectSingular
    }

    private sealed record Seeded(
        int SystemId, Guid LeaverCsoId, Guid SurvivorCsoId, Guid LeaverMvoId, Guid SurvivorMvoId, int SystemChangeRecordCount);

    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL audit reference deletion tests.");
        _connectionString = connectionString!;

        using var ctx = NewContext();
        // The repository sets Npgsql's timestamp switch, as the Worker does, before anything is written.
        _ = new PostgresDataRepository(ctx);
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp() => await PostgresTestDatabase.ResetAsync(_connectionString);

    /// <summary>
    /// A tracking context, as the Worker's: the deleters must also leave its tracker able to save afterwards.
    /// </summary>
    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning, RelationalEventId.MultipleCollectionIncludeWarning))
        .Options);

    [TestCase(DeletionEntryPoint.ConnectedSystemRepository)]
    [TestCase(DeletionEntryPoint.QuietDeletion)]
    [TestCase(DeletionEntryPoint.ExecutionItems)]
    [TestCase(DeletionEntryPoint.SyncRepositoryByIds)]
    [TestCase(DeletionEntryPoint.ConnectorSpaceClearKeepingHistory)]
    [TestCase(DeletionEntryPoint.ConnectedSystemDeletionKeepingHistory)]
    [TestCase(DeletionEntryPoint.MetaverseObjectsBulk)]
    [TestCase(DeletionEntryPoint.MetaverseObjectSingular)]
    public async Task Deletion_LeavesEveryAuditRowNamingTheObjectUnwrittenAsync(DeletionEntryPoint entryPoint)
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        using var jim = new JimApplication(repository, syncRepository: repository.Sync);
        await using var transaction = await ctx.Database.BeginTransactionAsync();

        // The view also holds what earlier transactions on this pooled backend did and the backend has not yet flushed,
        // so the transaction's own writes are the difference across it. Nothing flushes inside a transaction.
        var before = await ReadAuditWritesAsync(ctx);
        await RunAsync(entryPoint, ctx, repository, jim, seeded);
        var after = await ReadAuditWritesAsync(ctx);
        await transaction.RollbackAsync();
        var writes = after.ToDictionary(a => a.Key, a => (Updated: a.Value.Updated - before[a.Key].Updated, Deleted: a.Value.Deleted - before[a.Key].Deleted));

        // Deleting a Connected System clears its Object Types from the change records it keeps (step 2d), one write per
        // record of the system: a reference to configuration, which is outside this guard's concern. Nothing else may
        // touch an audit row.
        var expected = AuditTables.ToDictionary(t => t, _ => (Updated: 0L, Deleted: 0L));
        if (entryPoint == DeletionEntryPoint.ConnectedSystemDeletionKeepingHistory)
            expected["ConnectedSystemObjectChanges"] = (seeded.SystemChangeRecordCount, 0L);

        Assert.That(writes, Is.EquivalentTo(expected),
            $"{entryPoint} wrote to history naming the deleted objects (table: updated, deleted): " +
            string.Join("; ", writes.Select(w => $"{w.Key}: {w.Value.Updated}, {w.Value.Deleted}")));
    }

    private static async Task RunAsync(DeletionEntryPoint entryPoint, JimDbContext ctx, PostgresDataRepository repository,
        JimApplication jim, Seeded seeded)
    {
        switch (entryPoint)
        {
            case DeletionEntryPoint.ConnectedSystemRepository:
                await repository.ConnectedSystems.DeleteConnectedSystemObjectsAsync(await LoadLeaverCsosAsync(ctx, seeded));
                break;
            case DeletionEntryPoint.QuietDeletion:
            {
                var activity = await ctx.Activities.FirstAsync();
                await new SyncServer(jim).DeleteConnectedSystemObjectsQuietlyAsync(await LoadLeaverCsosAsync(ctx, seeded), activity);
                break;
            }
            case DeletionEntryPoint.ExecutionItems:
            {
                var activity = await ctx.Activities.FirstAsync();
                var leavers = await LoadLeaverCsosAsync(ctx, seeded);
                var items = leavers.Select(cso =>
                {
                    var item = activity.PrepareRunProfileExecutionItem();
                    item.ConnectedSystemObject = cso;
                    item.ConnectedSystemObjectId = cso.Id;
                    item.ObjectChangeType = ObjectChangeType.Deleted;
                    return item;
                }).ToList();
                await new SyncServer(jim).DeleteConnectedSystemObjectsAsync(leavers, items);
                break;
            }
            case DeletionEntryPoint.SyncRepositoryByIds:
                await repository.Sync.DeleteConnectedSystemObjectsByIdsAsync([seeded.LeaverCsoId]);
                break;
            case DeletionEntryPoint.ConnectorSpaceClearKeepingHistory:
                await repository.ConnectedSystems.ClearConnectorSpaceAsync(seeded.SystemId, deleteChangeHistory: false, persistedConnectorDataAfterClear: null);
                break;
            case DeletionEntryPoint.ConnectedSystemDeletionKeepingHistory:
                await repository.ConnectedSystems.DeleteConnectedSystemAsync(seeded.SystemId, deleteChangeHistory: false);
                break;
            case DeletionEntryPoint.MetaverseObjectsBulk:
            {
                var leaver = await ctx.MetaverseObjects.AsNoTracking().SingleAsync(m => m.Id == seeded.LeaverMvoId);
                await repository.Sync.DeleteMetaverseObjectsAsync([leaver]);
                break;
            }
            case DeletionEntryPoint.MetaverseObjectSingular:
            {
                var leaver = await ctx.MetaverseObjects
                    .Include(m => m.Type)
                    .Include(m => m.ConnectedSystemObjects)
                    .SingleAsync(m => m.Id == seeded.LeaverMvoId);
                await repository.Metaverse.DeleteMetaverseObjectAsync(leaver);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(entryPoint), entryPoint, null);
        }

        // Whatever the deleter left tracked must save cleanly: the Worker carries on with the same context.
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// The departed account, tracked with what a deletion record is built from, as a synchronisation page holds it.
    /// </summary>
    private static Task<List<ConnectedSystemObject>> LoadLeaverCsosAsync(JimDbContext ctx, Seeded seeded) => ctx.ConnectedSystemObjects
        .Include(c => c.Type)
        .Include(c => c.AttributeValues).ThenInclude(v => v.Attribute)
        .Where(c => c.Id == seeded.LeaverCsoId)
        .ToListAsync();

    /// <summary>
    /// This transaction's updated and deleted row counts for each audit table, read on its own connection.
    /// </summary>
    private static async Task<Dictionary<string, (long Updated, long Deleted)>> ReadAuditWritesAsync(JimDbContext ctx)
    {
        var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)ctx.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand(
            """
            SELECT "relname", "n_tup_upd", "n_tup_del" FROM "pg_stat_xact_user_tables"
            WHERE "schemaname" = 'public' AND "relname" = ANY(@tables)
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("tables", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = AuditTables });

        var writes = AuditTables.ToDictionary(t => t, _ => (Updated: 0L, Deleted: 0L));
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            writes[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));
        return writes;
    }

    /// <summary>
    /// One Connected System with two accounts, the leaver and a survivor whose live Manager value names the leaver; two
    /// Metaverse Objects, likewise. History of every kind names each leaver: its own change record, a change value on
    /// the survivor's change record, a Run Profile Execution Item, and (for the Metaverse Object) an Activity. Written
    /// with plain SQL, so the seed does not depend on how the model maps any of it.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        int systemId, typeId, externalIdAttributeId, managerAttributeId, mvoTypeId, mvoManagerAttributeId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Panoply Directory", ConnectorDefinition = connectorDefinition };
            var type = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            var externalId = new ConnectedSystemObjectTypeAttribute
            {
                Name = "employeeId", ConnectedSystemObjectType = type, Type = AttributeDataType.Text, IsExternalId = true, Selected = true
            };
            var manager = new ConnectedSystemObjectTypeAttribute
            {
                Name = "manager", ConnectedSystemObjectType = type, Type = AttributeDataType.Reference, Selected = true
            };
            type.Attributes.AddRange([externalId, manager]);
            var mvoType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
            var mvoManager = new MetaverseAttribute { Name = "Manager", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.SingleValued };
            seed.AddRange(connectorDefinition, system, type, mvoType, mvoManager);
            seed.Activities.Add(new Activity
            {
                TargetName = "Earlier run",
                TargetType = ActivityTargetType.ConnectedSystemRunProfile,
                InitiatedByType = ActivityInitiatorType.System,
                Created = DateTime.UtcNow.AddDays(-1)
            });
            await seed.SaveChangesAsync();
            (systemId, typeId, externalIdAttributeId, managerAttributeId) = (system.Id, type.Id, externalId.Id, manager.Id);
            (mvoTypeId, mvoManagerAttributeId) = (mvoType.Id, mvoManager.Id);
        }

        var (leaverCso, survivorCso, leaverMvo, survivorMvo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (leaverCsoChange, survivorCsoChange, survivorCsoChangeAttribute) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (leaverMvoChange, survivorMvoChange, survivorMvoChangeAttribute) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var earlier = DateTime.UtcNow.AddDays(-1);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO "MetaverseObjects" ("Id", "Created", "TypeId", "Status", "DeletionInitiatedByType", "Origin")
            VALUES (@leaverMvo, @earlier, @mvoType, 0, 0, 0), (@survivorMvo, @earlier, @mvoType, 0, 0, 0);
            INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "ReferenceValueId")
            VALUES (gen_random_uuid(), @survivorMvo, @mvoManager, @leaverMvo);

            INSERT INTO "ConnectedSystemObjects" ("Id", "Created", "TypeId", "ConnectedSystemId", "ExternalIdAttributeId", "Status", "JoinType", "MetaverseObjectId")
            VALUES (@leaverCso, @earlier, @type, @system, @externalId, 0, 0, @leaverMvo), (@survivorCso, @earlier, @type, @system, @externalId, 0, 0, @survivorMvo);
            INSERT INTO "ConnectedSystemObjectAttributeValues" ("Id", "ConnectedSystemObjectId", "AttributeId", "StringValue", "ReferenceValueId")
            VALUES (gen_random_uuid(), @leaverCso, @externalId, 'E1001', NULL),
                   (gen_random_uuid(), @survivorCso, @externalId, 'E1002', NULL),
                   (gen_random_uuid(), @survivorCso, @manager, NULL, @leaverCso);

            INSERT INTO "ActivityRunProfileExecutionItems" ("Id", "ActivityId", "ObjectChangeType", "ConnectedSystemObjectId")
            SELECT gen_random_uuid(), "Id", 1, @leaverCso FROM "Activities";
            UPDATE "Activities" SET "MetaverseObjectId" = @leaverMvo;

            INSERT INTO "ConnectedSystemObjectChanges" ("Id", "ConnectedSystemId", "ChangeTime", "ChangeType", "InitiatedByType", "ConnectedSystemObjectId")
            VALUES (@leaverCsoChange, @system, @earlier, 1, 0, @leaverCso), (@survivorCsoChange, @system, @earlier, 1, 0, @survivorCso);
            INSERT INTO "ConnectedSystemObjectChangeAttributes" ("Id", "ConnectedSystemChangeId", "AttributeName", "AttributeType")
            VALUES (@survivorCsoChangeAttribute, @survivorCsoChange, 'manager', @reference);
            INSERT INTO "ConnectedSystemObjectChangeAttributeValues" ("Id", "ConnectedSystemObjectChangeAttributeId", "ValueChangeType", "IsPendingExportStub", "StringValue", "ReferenceValueId")
            VALUES (gen_random_uuid(), @survivorCsoChangeAttribute, 1, false, 'CN=Leaver', @leaverCso);

            INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "ChangeTime", "InitiatedByType", "ChangeInitiatorType", "ChangeType")
            VALUES (@leaverMvoChange, @leaverMvo, @earlier, 0, 0, 1), (@survivorMvoChange, @survivorMvo, @earlier, 0, 0, 1);
            INSERT INTO "MetaverseObjectChangeAttributes" ("Id", "MetaverseObjectChangeId", "AttributeId", "AttributeName", "AttributeType")
            VALUES (@survivorMvoChangeAttribute, @survivorMvoChange, @mvoManager, 'Manager', @reference);
            INSERT INTO "MetaverseObjectChangeAttributeValues" ("Id", "MetaverseObjectChangeAttributeId", "ValueChangeType", "ReferenceValueId")
            VALUES (gen_random_uuid(), @survivorMvoChangeAttribute, 1, @leaverMvo);
            """, connection);
        void Add(string name, object value, NpgsqlDbType type) => command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
        Add("leaverMvo", leaverMvo, NpgsqlDbType.Uuid);
        Add("survivorMvo", survivorMvo, NpgsqlDbType.Uuid);
        Add("leaverCso", leaverCso, NpgsqlDbType.Uuid);
        Add("survivorCso", survivorCso, NpgsqlDbType.Uuid);
        Add("leaverCsoChange", leaverCsoChange, NpgsqlDbType.Uuid);
        Add("survivorCsoChange", survivorCsoChange, NpgsqlDbType.Uuid);
        Add("survivorCsoChangeAttribute", survivorCsoChangeAttribute, NpgsqlDbType.Uuid);
        Add("leaverMvoChange", leaverMvoChange, NpgsqlDbType.Uuid);
        Add("survivorMvoChange", survivorMvoChange, NpgsqlDbType.Uuid);
        Add("survivorMvoChangeAttribute", survivorMvoChangeAttribute, NpgsqlDbType.Uuid);
        Add("earlier", earlier, NpgsqlDbType.TimestampTz);
        Add("system", systemId, NpgsqlDbType.Integer);
        Add("type", typeId, NpgsqlDbType.Integer);
        Add("externalId", externalIdAttributeId, NpgsqlDbType.Integer);
        Add("manager", managerAttributeId, NpgsqlDbType.Integer);
        Add("mvoType", mvoTypeId, NpgsqlDbType.Integer);
        Add("mvoManager", mvoManagerAttributeId, NpgsqlDbType.Integer);
        Add("reference", (int)AttributeDataType.Reference, NpgsqlDbType.Integer);
        await command.ExecuteNonQueryAsync();

        return new Seeded(systemId, leaverCso, survivorCso, leaverMvo, survivorMvo, SystemChangeRecordCount: 2);
    }
}
