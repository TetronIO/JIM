// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;
using JimActivity = JIM.Models.Activities.Activity;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL coverage for deleting a batch of Connected System Objects (#2669): one raw delete by id behind both
/// repositories' deletion methods, with the change tracker brought into line with what the database's cascades did.
/// </summary>
/// <remarks>
/// <para>
/// The deletion used to remove a tracked graph with EF Core, which wrote one statement per object and per tracked
/// value: 13,002 statements for a page of 500 departed accounts of 25 values each, most of a leaver cohort's
/// synchronisation time. The statement test below holds it to a fixed few however many objects go.
/// </para>
/// <para>
/// A raw delete leaves the tracker to the code issuing it (<c>src/CLAUDE.md</c>, "Raw SQL Writes Must Fix Up or Detach
/// Tracked Instances"), and the in-memory provider has no foreign keys or row counts to show a stale instance, so the
/// round trip here loads every kind of row that refers to a deleted object, tracked as the Worker tracks a page, and
/// requires the database and the tracker to agree afterwards, with nothing left for a later save to write.
/// </para>
/// <para>
/// Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables, as the other database-backed tests; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemObjectBatchDeletionDatabaseTests
{
    private const int ValuesPerObject = 25;

    // Two updates clearing references to the objects and the delete itself; the generated-value retirement is a raw
    // command of its own, which the counter does not see. Anything above this is a statement per object or per value.
    private const int MaxStatements = 3;

    private string _connectionString = null!;

    public enum DeletionEntryPoint
    {
        /// <summary><c>ConnectedSystemRepository.DeleteConnectedSystemObjectsAsync</c>: the synchronisation flush's.</summary>
        ConnectedSystemObjects,

        /// <summary><c>SyncRepository.DeleteConnectedSystemObjectsByIdsAsync</c>: the export path's.</summary>
        ConnectedSystemObjectIds
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Connected System Object batch deletion tests.");
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

    private JimDbContext NewContext(SqlStatementCounter? counter = null)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning, RelationalEventId.MultipleCollectionIncludeWarning));
        if (counter != null)
            builder.AddInterceptors(counter);
        return new JimDbContext(builder.Options);
    }

    /// <summary>
    /// A tracking context with change detection off, as the Worker holds it from a page's load to its tracker clear.
    /// </summary>
    private JimDbContext NewPageContext(SqlStatementCounter? counter = null)
    {
        var context = NewContext(counter);
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        return context;
    }

    #region Statements

    [Test]
    public async Task DeleteConnectedSystemObjectsAsync_FiftyThenFiveHundredTrackedObjects_IssuesTheSameFewStatementsAsync()
    {
        var schema = await SeedSchemaAsync();
        var fifty = await SeedObjectsWithValuesAsync(schema, 50);
        var fiveHundred = await SeedObjectsWithValuesAsync(schema, 500);

        var fiftyCounter = await DeleteTrackedAsync(fifty);
        var fiveHundredCounter = await DeleteTrackedAsync(fiveHundred);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fiveHundredCounter.StatementCount, Is.LessThanOrEqualTo(MaxStatements),
                $"deleting 500 objects of {ValuesPerObject} values each: {fiveHundredCounter.Describe()}");
            Assert.That(fiveHundredCounter.Statements, Is.EquivalentTo(fiftyCounter.Statements),
                $"the same statements for 500 objects as for 50; 50 took {fiftyCounter.Describe()}, 500 took {fiveHundredCounter.Describe()}");
            Assert.That(fiveHundredCounter.Commands, Is.EqualTo(fiftyCounter.Commands), "the same round trips for 500 objects as for 50");
        }

        await using var verify = NewContext();
        var deletedIds = fifty.Concat(fiveHundred).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.ConnectedSystemObjects.CountAsync(c => deletedIds.Contains(c.Id)), Is.Zero, "every object is deleted");
            Assert.That(await verify.ConnectedSystemObjectAttributeValues.CountAsync(), Is.Zero, "and every value with it");
        }
    }

    /// <summary>
    /// Loads the objects with their values on a page context, as the Worker holds them, and deletes them through the
    /// synchronisation flush's entry point, counting what EF Core sends for the deletion alone.
    /// </summary>
    private async Task<SqlStatementCounter> DeleteTrackedAsync(List<Guid> objectIds)
    {
        var counter = new SqlStatementCounter();
        await using var ctx = NewPageContext(counter);
        var repository = new PostgresDataRepository(ctx);
        var ids = objectIds.ToArray();
        var objects = await ctx.ConnectedSystemObjects
            .Include(c => c.Type)
            .Include(c => c.AttributeValues).ThenInclude(v => v.Attribute)
            .Where(c => ids.Contains(c.Id))
            .ToListAsync();
        Assert.That(objects.Sum(c => c.AttributeValues.Count), Is.EqualTo(objectIds.Count * ValuesPerObject), "the objects are tracked with their values");

        counter.Counting = true;
        await repository.ConnectedSystems.DeleteConnectedSystemObjectsAsync(objects);
        counter.Counting = false;
        return counter;
    }

    #endregion

    #region Round trip

    [Test]
    public async Task DeleteConnectedSystemObjectsAsync_EveryKindOfReferenceTracked_DatabaseAndTrackerAgreeAndASaveWritesNothingAsync(
        [Values] DeletionEntryPoint entryPoint, [Values] bool detectChangesForSave)
    {
        var seeded = await SeedObjectWithEveryKindOfReferenceAsync();

        var counter = new SqlStatementCounter();
        await using var ctx = NewPageContext(counter);
        var repository = new PostgresDataRepository(ctx);
        var tracked = await LoadTrackedAsync(ctx, seeded);

        if (entryPoint == DeletionEntryPoint.ConnectedSystemObjects)
            await repository.ConnectedSystems.DeleteConnectedSystemObjectsAsync([tracked.Deleted]);
        else
            await repository.Sync.DeleteConnectedSystemObjectsByIdsAsync([seeded.DeletedId]);

        // The tracker, read with detection still off as the page leaves it.
        var stillTracked = ctx.ChangeTracker.Entries().Select(e => e.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stillTracked.Contains(tracked.Deleted), Is.False, "the deleted object is no longer tracked");
            Assert.That(tracked.Deleted.AttributeValues.Where(stillTracked.Contains), Is.Empty, "nor are its values");
            Assert.That(stillTracked.Contains(tracked.DeferredReference), Is.False, "nor its deferred reference, which the database deleted with it");
            Assert.That(stillTracked.Contains(tracked.Assignment), Is.False, "nor its generated value assignment, which the database deleted with it");

            Assert.That(tracked.ReferencingValue.ReferenceValueId, Is.Null, "the other object's reference to it is cleared");
            Assert.That(tracked.ReferencingValue.ReferenceValue, Is.Null);
            Assert.That(tracked.MetaverseValue.UnresolvedReferenceValueId, Is.Null, "the Metaverse Object value's unresolved reference to it is cleared");
            Assert.That(tracked.MetaverseValue.UnresolvedReferenceValue, Is.Null);
            Assert.That(tracked.PendingExport.ConnectedSystemObjectId, Is.Null, "the Pending Export's reference is cleared, as the database cleared it");
            Assert.That(tracked.PendingExport.ConnectedSystemObject, Is.Null);
            Assert.That(tracked.ExecutionItem.ConnectedSystemObjectId, Is.Null, "the execution item's reference is cleared");
            Assert.That(tracked.ExecutionItem.ConnectedSystemObject, Is.Null);
            Assert.That(tracked.Change.ConnectedSystemObjectId, Is.Null, "the change record's reference is cleared");
            Assert.That(tracked.Change.ConnectedSystemObject, Is.Null);
            Assert.That(ctx.Entry(tracked.ChangeValue).Property("ReferenceValueId").CurrentValue, Is.Null, "the change value's reference is cleared");
            Assert.That(tracked.ChangeValue.ReferenceValue, Is.Null);
            Assert.That(tracked.PasswordChange.ConnectedSystemObjectId, Is.Null, "the Pending Password Change's account is cleared");
            Assert.That(tracked.PasswordChange.ProvisionedConnectedSystemObjectId, Is.Null, "and the account it owes a first password");
        }

        // What a later save on the same context writes: nothing, whether or not it detects changes first.
        counter.Reset();
        counter.Counting = true;
        ctx.ChangeTracker.AutoDetectChangesEnabled = detectChangesForSave;
        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.Nothing, "a later save on the context succeeds");
        counter.Counting = false;
        Assert.That(counter.StatementCount, Is.Zero, $"a later save writes nothing: {counter.Describe()}");

        await AssertDatabaseAfterDeletionAsync(seeded);
    }

    private async Task AssertDatabaseAfterDeletionAsync(SeededReferences seeded)
    {
        await using var verify = NewContext();
        var id = seeded.DeletedId;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.ConnectedSystemObjects.AnyAsync(c => c.Id == id), Is.False, "the object is deleted");
            Assert.That(await verify.ConnectedSystemObjectAttributeValues.CountAsync(v => EF.Property<Guid>(v, "ConnectedSystemObjectId") == id),
                Is.Zero, "its values cascade with it");
            Assert.That(await verify.DeferredReferences.AnyAsync(d => d.Id == seeded.DeferredReferenceId), Is.False, "its deferred reference cascades with it");
            Assert.That(await verify.GeneratedValueAssignments.AnyAsync(a => a.Id == seeded.AssignmentId), Is.False, "its generated value assignment cascades with it");

            var referencingValue = await verify.ConnectedSystemObjectAttributeValues.SingleAsync(v => v.Id == seeded.ReferencingValueId);
            Assert.That(referencingValue.ReferenceValueId, Is.Null, "the other object keeps its value, without the reference");
            var metaverseValue = await verify.MetaverseObjectAttributeValues.SingleAsync(v => v.Id == seeded.MetaverseValueId);
            Assert.That(metaverseValue.UnresolvedReferenceValueId, Is.Null, "the Metaverse Object keeps its value, without the reference");
            var pendingExport = await verify.PendingExports.SingleAsync(p => p.Id == seeded.PendingExportId);
            Assert.That(pendingExport.ConnectedSystemObjectId, Is.Null, "the Pending Export stays, its reference set to null");
            var executionItem = await verify.ActivityRunProfileExecutionItems.SingleAsync(i => i.Id == seeded.ExecutionItemId);
            Assert.That(executionItem.ConnectedSystemObjectId, Is.Null, "the execution item stays, its reference set to null");
            var change = await verify.ConnectedSystemObjectChanges.SingleAsync(c => c.Id == seeded.ChangeId);
            Assert.That(change.ConnectedSystemObjectId, Is.Null, "the change record stays, its reference set to null");
            Assert.That(await verify.ConnectedSystemObjectChangeAttributeValues
                    .Where(v => v.Id == seeded.ChangeValueId)
                    .Select(v => EF.Property<Guid?>(v, "ReferenceValueId"))
                    .SingleAsync(),
                Is.Null, "the change value stays, its reference set to null");
            var passwordChange = await verify.PendingPasswordChanges.SingleAsync(p => p.Id == seeded.PasswordChangeId);
            Assert.That(passwordChange.ConnectedSystemObjectId, Is.Null, "the Pending Password Change stays, its account set to null");
            Assert.That(passwordChange.ProvisionedConnectedSystemObjectId, Is.Null, "and the account it owes a first password");
        }
    }

    private sealed record TrackedReferences(
        ConnectedSystemObject Deleted,
        ConnectedSystemObjectAttributeValue ReferencingValue,
        MetaverseObjectAttributeValue MetaverseValue,
        PendingExport PendingExport,
        ActivityRunProfileExecutionItem ExecutionItem,
        ConnectedSystemObjectChange Change,
        ConnectedSystemObjectChangeAttributeValue ChangeValue,
        PendingPasswordChange PasswordChange,
        DeferredReference DeferredReference,
        GeneratedValueAssignment Assignment);

    /// <summary>
    /// Every seeded row, loaded tracked one query at a time as different parts of a page would load them, so EF Core's
    /// fix-up joins each to the tracked object it refers to.
    /// </summary>
    private static async Task<TrackedReferences> LoadTrackedAsync(JimDbContext ctx, SeededReferences seeded)
    {
        var deleted = await ctx.ConnectedSystemObjects
            .Include(c => c.Type)
            .Include(c => c.AttributeValues).ThenInclude(v => v.Attribute)
            .SingleAsync(c => c.Id == seeded.DeletedId);
        var referencing = await ctx.ConnectedSystemObjects
            .Include(c => c.AttributeValues)
            .SingleAsync(c => c.Id == seeded.ReferencingId);
        var tracked = new TrackedReferences(
            deleted,
            referencing.AttributeValues.Single(v => v.Id == seeded.ReferencingValueId),
            await ctx.MetaverseObjectAttributeValues.SingleAsync(v => v.Id == seeded.MetaverseValueId),
            await ctx.PendingExports.SingleAsync(p => p.Id == seeded.PendingExportId),
            await ctx.ActivityRunProfileExecutionItems.SingleAsync(i => i.Id == seeded.ExecutionItemId),
            await ctx.ConnectedSystemObjectChanges.SingleAsync(c => c.Id == seeded.ChangeId),
            await ctx.ConnectedSystemObjectChangeAttributeValues.SingleAsync(v => v.Id == seeded.ChangeValueId),
            await ctx.PendingPasswordChanges.SingleAsync(p => p.Id == seeded.PasswordChangeId),
            await ctx.DeferredReferences.SingleAsync(d => d.Id == seeded.DeferredReferenceId),
            await ctx.GeneratedValueAssignments.SingleAsync(a => a.Id == seeded.AssignmentId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracked.ReferencingValue.ReferenceValue, Is.SameAs(deleted), "fix-up joined the referencing value to the tracked object");
            Assert.That(tracked.PendingExport.ConnectedSystemObject, Is.SameAs(deleted), "and the Pending Export");
            Assert.That(tracked.ChangeValue.ReferenceValue, Is.SameAs(deleted), "and the change value");
        }

        return tracked;
    }

    #endregion

    #region Callers

    [Test]
    public async Task DeleteConnectedSystemObjectsAsync_ExecutionItemPath_DeletesTheObjectsAndPersistsTheirRecordsWithTheirItemsAsync()
    {
        var schema = await SeedSchemaAsync();
        var objectIds = await SeedObjectsWithValuesAsync(schema, 3);
        Guid activityId;
        await using (var seed = NewContext())
        {
            var activity = new JimActivity { TargetName = "Target Delta Synchronisation", InitiatedByType = ActivityInitiatorType.System, InitiatedByName = "Scheduler" };
            seed.Activities.Add(activity);
            await seed.SaveChangesAsync();
            activityId = activity.Id;
        }

        await using (var ctx = NewPageContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var ids = objectIds.ToArray();
            var objects = await ctx.ConnectedSystemObjects
                .Include(c => c.Type)
                .Include(c => c.AttributeValues).ThenInclude(v => v.Attribute)
                .Where(c => ids.Contains(c.Id))
                .ToListAsync();
            var activity = new JimActivity { Id = activityId, InitiatedByType = ActivityInitiatorType.System, InitiatedByName = "Scheduler" };
            // As the obsoletion handling queues them: one execution item per object, naming it.
            var items = objects.Select(cso =>
            {
                var item = activity.PrepareRunProfileExecutionItem();
                item.ConnectedSystemObject = cso;
                item.ConnectedSystemObjectId = cso.Id;
                item.ObjectChangeType = ObjectChangeType.Deleted;
                item.SnapshotCsoDisplayFields(cso);
                return item;
            }).ToList();

            using var jim = new JimApplication(repository, syncRepository: repository.Sync);
            await new SyncServer(jim).DeleteConnectedSystemObjectsAsync(objects, items);

            // Then the page's execution item flush, as FlushRpeisAsync writes them: the items, then each record under the
            // id its item was given.
            await repository.Sync.BulkInsertRpeisAsync(items);
            foreach (var item in items)
                item.ConnectedSystemObjectChange!.ActivityRunProfileExecutionItemId = item.Id;
            await repository.Sync.PersistRpeiCsoChangesAsync(items);
        }

        await using var verify = NewContext();
        var deletedIds = objectIds.ToArray();
        var records = await verify.ConnectedSystemObjectChanges
            .AsNoTracking()
            .Include(c => c.AttributeChanges)
            .Where(c => c.DeletedConnectedSystemObjectId.HasValue && deletedIds.Contains(c.DeletedConnectedSystemObjectId.Value))
            .ToListAsync();
        var persistedItems = await verify.ActivityRunProfileExecutionItems.AsNoTracking().Where(i => i.ActivityId == activityId).ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.ConnectedSystemObjects.CountAsync(c => deletedIds.Contains(c.Id)), Is.Zero, "the objects are deleted");
            Assert.That(persistedItems, Has.Count.EqualTo(objectIds.Count), "one execution item each");
            Assert.That(persistedItems.Select(i => i.ConnectedSystemObjectId), Is.All.Null, "none names a deleted object");
            Assert.That(records, Has.Count.EqualTo(objectIds.Count), "one deletion record each");
            Assert.That(records.Select(r => r.ChangeType).Distinct(), Is.EqualTo(new[] { ObjectChangeType.Deleted }));
            Assert.That(records.Select(r => r.ActivityRunProfileExecutionItemId), Is.EquivalentTo(persistedItems.Select(i => (Guid?)i.Id)),
                "each record belongs to its execution item");
            Assert.That(records.Select(r => r.AttributeChanges.Count), Is.All.EqualTo(ValuesPerObject), "each records every final value as removed");
        }
    }

    [Test]
    public async Task DeleteConnectedSystemObjectsAsync_UntrackedProvisioningObjectOnTrackedSchema_DeletesItAndTracksNothingNewAsync()
    {
        // Cancelled provisioning (ExportEvaluationServer): the object was written by the bulk create, untracked, and
        // refers to the run's tracked schema; its never-sent Pending Export has already been deleted.
        var schema = await SeedSchemaAsync();
        var objectIds = await SeedObjectsWithValuesAsync(schema, 1, ConnectedSystemObjectStatus.PendingProvisioning);

        var counter = new SqlStatementCounter();
        await using var ctx = NewPageContext(counter);
        var repository = new PostgresDataRepository(ctx);
        var type = await ctx.ConnectedSystemObjectTypes.Include(t => t.Attributes).SingleAsync(t => t.Id == schema.TypeId);
        var provisioned = new ConnectedSystemObject
        {
            Id = objectIds[0], ConnectedSystemId = schema.SystemId, Type = type, TypeId = type.Id, ExternalIdAttributeId = schema.ExternalIdAttributeId,
            Status = ConnectedSystemObjectStatus.PendingProvisioning, JoinType = ConnectedSystemObjectJoinType.Provisioned
        };
        await using (var read = NewContext())
        {
            var storedValues = await read.ConnectedSystemObjectAttributeValues
                .Where(v => EF.Property<Guid>(v, "ConnectedSystemObjectId") == provisioned.Id)
                .Select(v => new { v.Id, v.AttributeId, v.StringValue })
                .ToListAsync();
            foreach (var value in storedValues)
            {
                provisioned.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                {
                    Id = value.Id, ConnectedSystemObject = provisioned, AttributeId = value.AttributeId,
                    Attribute = type.Attributes.Single(a => a.Id == value.AttributeId), StringValue = value.StringValue
                });
            }
        }
        var trackedBefore = ctx.ChangeTracker.Entries().Count();

        await repository.Sync.DeleteConnectedSystemObjectsAsync([provisioned]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.ChangeTracker.Entries().Count(), Is.EqualTo(trackedBefore), "the deletion tracks nothing it was handed");
            Assert.That(ctx.ChangeTracker.Entries().Any(e => ReferenceEquals(e.Entity, provisioned)), Is.False);
        }

        counter.Reset();
        counter.Counting = true;
        ctx.ChangeTracker.AutoDetectChangesEnabled = true;
        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.Nothing, "a later save on the context succeeds");
        counter.Counting = false;
        Assert.That(counter.StatementCount, Is.Zero, $"a later save writes nothing: {counter.Describe()}");

        await using var verify = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.ConnectedSystemObjects.AnyAsync(c => c.Id == provisioned.Id), Is.False, "the object is deleted");
            Assert.That(await verify.ConnectedSystemObjectAttributeValues.CountAsync(), Is.Zero, "and its values with it");
        }
    }

    #endregion

    #region Seeding

    private sealed record Schema(int SystemId, int TypeId, int ExternalIdAttributeId, List<int> TextAttributeIds, int ReferenceAttributeId);

    /// <summary>
    /// A Connected System with one Object Type: an external id, <see cref="ValuesPerObject"/> - 1 further text attributes
    /// and a multi-valued reference.
    /// </summary>
    private async Task<Schema> SeedSchemaAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Yellowstone Directory", ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "inetOrgPerson", ConnectedSystem = system, Selected = true };
        var externalId = Attribute(type, "distinguishedName", AttributeDataType.Text, AttributePlurality.SingleValued);
        var text = Enumerable.Range(1, ValuesPerObject - 1)
            .Select(i => Attribute(type, $"attribute{i:D2}", AttributeDataType.Text, AttributePlurality.SingleValued))
            .ToList();
        var reference = Attribute(type, "member", AttributeDataType.Reference, AttributePlurality.MultiValued);
        seed.AddRange(connectorDefinition, system, type);
        await seed.SaveChangesAsync();
        return new Schema(system.Id, type.Id, externalId.Id, text.Select(a => a.Id).ToList(), reference.Id);
    }

    /// <summary>
    /// <paramref name="count"/> objects of <see cref="ValuesPerObject"/> values each, written with COPY.
    /// </summary>
    private async Task<List<Guid>> SeedObjectsWithValuesAsync(Schema schema, int count,
        ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Obsolete)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using (var writer = await connection.BeginBinaryImportAsync(
            @"COPY ""ConnectedSystemObjects"" (""Id"", ""Created"", ""TypeId"", ""ConnectedSystemId"", ""ExternalIdAttributeId"", ""Status"", ""JoinType"") FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var id in ids)
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(id, NpgsqlDbType.Uuid);
                await writer.WriteAsync(DateTime.UtcNow, NpgsqlDbType.TimestampTz);
                await writer.WriteAsync(schema.TypeId, NpgsqlDbType.Integer);
                await writer.WriteAsync(schema.SystemId, NpgsqlDbType.Integer);
                await writer.WriteAsync(schema.ExternalIdAttributeId, NpgsqlDbType.Integer);
                await writer.WriteAsync((int)status, NpgsqlDbType.Integer);
                await writer.WriteAsync((int)ConnectedSystemObjectJoinType.NotJoined, NpgsqlDbType.Integer);
            }
            await writer.CompleteAsync();
        }

        await using (var writer = await connection.BeginBinaryImportAsync(
            @"COPY ""ConnectedSystemObjectAttributeValues"" (""Id"", ""ConnectedSystemObjectId"", ""AttributeId"", ""StringValue"") FROM STDIN (FORMAT BINARY)"))
        {
            var attributeIds = schema.TextAttributeIds.Prepend(schema.ExternalIdAttributeId).ToList();
            foreach (var (id, attributeId) in ids.SelectMany(id => attributeIds.Select(attributeId => (id, attributeId))))
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(Guid.NewGuid(), NpgsqlDbType.Uuid);
                await writer.WriteAsync(id, NpgsqlDbType.Uuid);
                await writer.WriteAsync(attributeId, NpgsqlDbType.Integer);
                await writer.WriteAsync($"value {attributeId} of {id:N}", NpgsqlDbType.Text);
            }
            await writer.CompleteAsync();
        }

        return ids;
    }

    private sealed record SeededReferences(
        Guid DeletedId, Guid ReferencingId, Guid ReferencingValueId, Guid MetaverseValueId, Guid PendingExportId, Guid ExecutionItemId,
        Guid ChangeId, Guid ChangeValueId, Guid PasswordChangeId, Guid DeferredReferenceId, Guid AssignmentId);

    /// <summary>
    /// One departed account, and a row of every kind that refers to it: another object's member value, a Metaverse
    /// Object value's unresolved reference, a Pending Export, an execution item, a change record and a change value, a
    /// Pending Password Change naming it twice, a deferred reference and a generated value assignment.
    /// </summary>
    private async Task<SeededReferences> SeedObjectWithEveryKindOfReferenceAsync()
    {
        var schema = await SeedSchemaAsync();
        await using var seed = NewContext();

        var personType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var manager = new MetaverseAttribute { Name = "Manager", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.SingleValued };
        var person = new MetaverseObject { Id = Guid.NewGuid(), Type = personType, Created = DateTime.UtcNow };
        seed.AddRange(personType, manager, person);
        await seed.SaveChangesAsync();

        var exportRule = new SyncRule
        {
            Name = "Yellowstone Directory export", Direction = SyncRuleDirection.Export, ConnectedSystemId = schema.SystemId,
            ConnectedSystemObjectTypeId = schema.TypeId, MetaverseObjectTypeId = personType.Id
        };
        seed.SyncRules.Add(exportRule);
        await seed.SaveChangesAsync();
        var mapping = new SyncRuleMapping { SyncRuleId = exportRule.Id, TargetConnectedSystemAttributeId = schema.TextAttributeIds[0] };
        seed.SyncRuleMappings.Add(mapping);
        await seed.SaveChangesAsync();
        var generation = new SyncRuleMappingGeneration { SyncRuleMappingId = mapping.Id, TokenKind = GeneratedValueTokenKind.OnlyIfTaken };
        seed.SyncRuleMappingGenerations.Add(generation);
        await seed.SaveChangesAsync();

        var deleted = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = schema.SystemId, TypeId = schema.TypeId, ExternalIdAttributeId = schema.ExternalIdAttributeId,
            Status = ConnectedSystemObjectStatus.Obsolete, JoinType = ConnectedSystemObjectJoinType.NotJoined
        };
        deleted.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = schema.ExternalIdAttributeId, StringValue = "uid=tina.adams,ou=People,dc=yellowstone,dc=local" });
        deleted.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = schema.TextAttributeIds[0], StringValue = "tadams" });
        var referencing = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = schema.SystemId, TypeId = schema.TypeId, ExternalIdAttributeId = schema.ExternalIdAttributeId,
            Status = ConnectedSystemObjectStatus.Normal, JoinType = ConnectedSystemObjectJoinType.NotJoined
        };
        var referencingValue = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = schema.ReferenceAttributeId, ReferenceValue = deleted };
        referencing.AttributeValues.Add(referencingValue);
        seed.ConnectedSystemObjects.AddRange(deleted, referencing);

        var metaverseValue = new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = person, Attribute = manager, UnresolvedReferenceValue = deleted };
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemId = schema.SystemId, ConnectedSystemObject = deleted, ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending, CreatedAt = DateTime.UtcNow
        };
        var activity = new JimActivity { TargetName = "Earlier run", InitiatedByType = ActivityInitiatorType.System };
        var executionItem = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), Activity = activity, ConnectedSystemObject = deleted, ObjectChangeType = ObjectChangeType.Updated };
        var change = new ConnectedSystemObjectChange
        {
            Id = Guid.NewGuid(), ConnectedSystemId = schema.SystemId, ConnectedSystemObject = deleted, ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Updated, InitiatedByType = ActivityInitiatorType.System
        };
        // A membership change recorded on the other object, naming the departed account as a member.
        var referencingChange = new ConnectedSystemObjectChange
        {
            Id = Guid.NewGuid(), ConnectedSystemId = schema.SystemId, ConnectedSystemObject = referencing, ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Updated, InitiatedByType = ActivityInitiatorType.System
        };
        var memberChange = new ConnectedSystemObjectChangeAttribute
        {
            Id = Guid.NewGuid(), ConnectedSystemChange = referencingChange, AttributeName = "member", AttributeType = AttributeDataType.Reference
        };
        var changeValue = new ConnectedSystemObjectChangeAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObjectChangeAttribute = memberChange, ValueChangeType = ValueChangeType.Add, ReferenceValue = deleted
        };
        memberChange.ValueChanges.Add(changeValue);
        referencingChange.AttributeChanges.Add(memberChange);
        var passwordChange = new PendingPasswordChange
        {
            Id = Guid.NewGuid(), MetaverseObjectId = person.Id, ConnectedSystemId = schema.SystemId, EncryptedPassword = "$JIMPW$v1$ciphertext",
            CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(7), ActivityId = Guid.NewGuid(),
            ConnectedSystemObjectId = deleted.Id, ProvisionedConnectedSystemObjectId = deleted.Id
        };
        var deferredReference = new DeferredReference
        {
            Id = Guid.NewGuid(), SourceCso = deleted, AttributeName = "member", TargetMvoId = person.Id, TargetSystemId = schema.SystemId
        };
        var assignment = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = deleted, ConnectedSystemObjectTypeAttributeId = schema.TextAttributeIds[0],
            Value = "tadams", NormalisedValue = "tadams", State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = generation.Id, Created = DateTime.UtcNow, LastUpdated = DateTime.UtcNow
        };
        seed.AddRange(metaverseValue, pendingExport, activity, executionItem, change, referencingChange, passwordChange, deferredReference, assignment);
        await seed.SaveChangesAsync();

        return new SeededReferences(deleted.Id, referencing.Id, referencingValue.Id, metaverseValue.Id, pendingExport.Id, executionItem.Id,
            change.Id, changeValue.Id, passwordChange.Id, deferredReference.Id, assignment.Id);
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystemObjectType type, string name, AttributeDataType dataType, AttributePlurality plurality)
    {
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = name, ConnectedSystemObjectType = type, Type = dataType, AttributePlurality = plurality, Selected = true
        };
        type.Attributes.Add(attribute);
        return attribute;
    }

    #endregion
}
