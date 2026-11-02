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
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL checks of which change record reference values the cross-batch fix-up reads, and of what it does
/// with them (#2690): only a reference written without its referenced object's id, and so still holding just a DN,
/// is resolved; a reference whose id was written, and later cleared by its object's deletion, keeps the name it was
/// recorded with.
/// </summary>
/// <remarks>
/// Values are written through the repository's own change record writer, so the tests see the state a run leaves
/// rather than a hand-built one. Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ChangeRecordUnresolvedReferenceDatabaseTests
{
    private const string ManagerDn = "CN=Mo Manager,OU=People,DC=glitterband,DC=local";

    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL change record unresolved reference tests.");
        _connectionString = connectionString!;

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    private JimDbContext NewContext() => NewContext(_connectionString);

    private static JimDbContext NewContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    #region Writer

    /// <summary>
    /// An import writes its new objects in batches with their ids decided up front, so a reference can name an object a
    /// later batch has yet to write. Its id cannot be written, so the value keeps the DN and is marked for the fix-up.
    /// </summary>
    [Test]
    public async Task PersistCsoChangesAsync_ReferenceToAnObjectNotWrittenYet_MarksTheValueUnresolvedAsync()
    {
        var seed = await SeedSystemAsync();

        var valueId = await WriteValueAsync(seed, seed.Manager, ManagerDn, referencedObjectId: Guid.NewGuid());

        var stored = await ReadValueAsync(valueId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.ReferenceValueId, Is.Null, "the object has no row yet, so its id is not written");
            Assert.That(stored.IsUnresolvedReference, Is.True, "the reference still needs resolving");
        }
    }

    [Test]
    public async Task PersistCsoChangesAsync_ReferenceToAWrittenObject_WritesTheIdAndLeavesTheValueUnmarkedAsync()
    {
        var seed = await SeedSystemAsync();
        var managerId = await AddPersonAsync(seed, ManagerDn);

        var valueId = await WriteValueAsync(seed, seed.Manager, ManagerDn, managerId);

        var stored = await ReadValueAsync(valueId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.ReferenceValueId, Is.EqualTo(managerId));
            Assert.That(stored.IsUnresolvedReference, Is.False, "a reference written with its id is resolved");
        }
    }

    [Test]
    public async Task PersistCsoChangesAsync_TextValueHoldingADn_LeavesTheValueUnmarkedAsync()
    {
        var seed = await SeedSystemAsync();
        await AddPersonAsync(seed, ManagerDn);

        var valueId = await WriteValueAsync(seed, seed.Description, ManagerDn, referencedObjectId: null);

        Assert.That((await ReadValueAsync(valueId)).IsUnresolvedReference, Is.False,
            "a value of a Text attribute is never a reference, whatever it holds");
    }

    /// <summary>
    /// An import reference that matched no object (its target out of scope, or arriving in a later run) is recorded with
    /// its DN alone, and is marked so a later run can still resolve it.
    /// </summary>
    [Test]
    public async Task PersistCsoChangesAsync_ReferenceDnWithNoObject_MarksTheValueUnresolvedAsync()
    {
        var seed = await SeedSystemAsync();

        var valueId = await WriteValueAsync(seed, seed.Manager, ManagerDn, referencedObjectId: null);

        var stored = await ReadValueAsync(valueId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.ReferenceValueId, Is.Null);
            Assert.That(stored.IsUnresolvedReference, Is.True);
        }
    }

    /// <summary>
    /// The snapshots a synchronisation records on its outcomes (a staged export's values) are written by the execution
    /// item flush rather than by the change record writer's own entry point, so they need marking there too.
    /// </summary>
    [Test]
    public async Task BulkInsertRpeisAsync_OutcomeSnapshotReferenceDnWithNoObject_MarksTheValueUnresolvedAsync()
    {
        var seed = await SeedSystemAsync();
        Guid activityId;
        await using (var ctx = NewContext())
        {
            var activity = new Activity
            {
                Id = Guid.NewGuid(), TargetName = "Full Synchronisation", TargetOperationType = ActivityTargetOperationType.Execute,
                Status = ActivityStatus.InProgress, InitiatedByType = ActivityInitiatorType.System
            };
            ctx.Activities.Add(activity);
            await ctx.SaveChangesAsync();
            activityId = activity.Id;
        }

        var (change, value) = NewChange(seed, seed.Manager, ManagerDn, referencedObjectId: null, ObjectChangeType.PendingExport);
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = activityId, ObjectChangeType = ObjectChangeType.PendingExport };
        item.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            ConnectedSystemObjectChange = change
        });

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.BulkInsertRpeisAsync([item]);

        Assert.That((await ReadValueAsync(value.Id)).IsUnresolvedReference, Is.True);
    }

    #endregion

    #region Fix-up

    [Test]
    public async Task FixupCrossBatchChangeRecordReferenceIdsAsync_MarkedReferenceNamingAnObject_ResolvesItAndClearsTheMarkAsync()
    {
        var seed = await SeedSystemAsync();
        var managerId = await AddPersonAsync(seed, ManagerDn);
        // In another case: DNs are compared ignoring case unless the target's attribute is case-exact (#2051).
        var valueId = await WriteValueAsync(seed, seed.Manager, ManagerDn.ToLowerInvariant(), referencedObjectId: null);

        var resolved = await FixupAsync(seed);

        var stored = await ReadValueAsync(valueId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.EqualTo(1));
            Assert.That(stored.ReferenceValueId, Is.EqualTo(managerId));
            Assert.That(stored.IsUnresolvedReference, Is.False, "a resolved reference is no longer read by later runs");
        }
    }

    [Test]
    public async Task FixupCrossBatchChangeRecordReferenceIdsAsync_MarkedReferenceNamingNoObject_StaysMarkedAndUnresolvedAsync()
    {
        var seed = await SeedSystemAsync();
        await AddPersonAsync(seed, ManagerDn);
        var valueId = await WriteValueAsync(seed, seed.Manager, "CN=Nobody,OU=People,DC=glitterband,DC=local", referencedObjectId: null);

        var resolved = await FixupAsync(seed);

        var stored = await ReadValueAsync(valueId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.Zero);
            Assert.That(stored.ReferenceValueId, Is.Null);
            Assert.That(stored.IsUnresolvedReference, Is.True, "a later run may yet find the object it names");
        }
    }

    /// <summary>
    /// The fault the issue names: a reference written with its object's id keeps only the DN once that object is
    /// deleted, because the foreign key clears the id. Reading every such row as unresolved re-pointed the history at
    /// whichever later object took the same DN, so the record of who the manager was became a record of someone else.
    /// </summary>
    [Test]
    public async Task FixupCrossBatchChangeRecordReferenceIdsAsync_ReferenceWhoseTargetWasDeleted_IsNotRepointedAtALaterObjectWithTheSameDnAsync()
    {
        var seed = await SeedSystemAsync();
        var formerManagerId = await AddPersonAsync(seed, ManagerDn);
        var valueId = await WriteValueAsync(seed, seed.Manager, ManagerDn, formerManagerId);

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.DeleteConnectedSystemObjectsByIdsAsync([formerManagerId]);
        Assert.That((await ReadValueAsync(valueId)).ReferenceValueId, Is.Null,
            "precondition: deleting the referenced object clears the change record's id, leaving its DN");
        await AddPersonAsync(seed, ManagerDn);

        await FixupAsync(seed);

        var stored = await ReadValueAsync(valueId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.ReferenceValueId, Is.Null,
                "the history refers to the deleted manager and must not be re-pointed at a later object reusing the DN");
            Assert.That(stored.StringValue, Is.EqualTo(ManagerDn), "the DN the value was recorded with is kept");
            Assert.That(stored.IsUnresolvedReference, Is.False, "a reference once resolved is never read as unresolved again");
        }
    }

    /// <summary>
    /// The common case after most runs: nothing is marked, so the fix-up must not read the target side at all (#2690).
    /// Another session holds the target side's tables exclusively, so a statement that so much as names them waits,
    /// and the fix-up's short lock timeout turns that wait into a failure rather than a hang.
    /// </summary>
    [Test]
    public async Task FixupCrossBatchChangeRecordReferenceIdsAsync_NothingMarked_DoesNotTouchTheTargetSideAsync()
    {
        var seed = await SeedSystemAsync();
        var managerId = await AddPersonAsync(seed, ManagerDn);
        await WriteValueAsync(seed, seed.Manager, ManagerDn, managerId);
        await WriteValueAsync(seed, seed.Description, ManagerDn, referencedObjectId: null);

        await using var locker = new NpgsqlConnection(_connectionString);
        await locker.OpenAsync();
        await using var lockTransaction = await locker.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            """LOCK TABLE "ConnectedSystemObjects", "ConnectedSystemObjectAttributeValues", "ConnectedSystemAttributes" IN ACCESS EXCLUSIVE MODE""",
            locker, lockTransaction))
            await lockCommand.ExecuteNonQueryAsync();

        var shortLockTimeout = new NpgsqlConnectionStringBuilder(_connectionString) { Options = "-c lock_timeout=2s" };
        int resolved;
        await using (var ctx = NewContext(shortLockTimeout.ConnectionString))
            resolved = await new PostgresDataRepository(ctx).Sync.FixupCrossBatchChangeRecordReferenceIdsAsync(seed.SystemId);

        Assert.That(resolved, Is.Zero, "with nothing marked there is nothing to resolve, and nothing to read it from");
    }

    /// <summary>
    /// An import reference that named no object yet is written with its DN alone; when the object it names arrives in
    /// a later run, that run's fix-up resolves it.
    /// </summary>
    [Test]
    public async Task FixupCrossBatchChangeRecordReferenceIdsAsync_TargetArrivesInALaterRun_IsResolvedByThatRunAsync()
    {
        var seed = await SeedSystemAsync();
        var valueId = await WriteValueAsync(seed, seed.Manager, ManagerDn, referencedObjectId: null);

        var firstRun = await FixupAsync(seed);
        var managerId = await AddPersonAsync(seed, ManagerDn);
        var laterRun = await FixupAsync(seed);

        var stored = await ReadValueAsync(valueId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstRun, Is.Zero, "nothing names the DN yet");
            Assert.That(laterRun, Is.EqualTo(1), "the later run resolves it");
            Assert.That(stored.ReferenceValueId, Is.EqualTo(managerId));
            Assert.That(stored.IsUnresolvedReference, Is.False);
        }
    }

    #endregion

    #region seeding

    private sealed record SeededSystem(
        int SystemId,
        int ObjectTypeId,
        ConnectedSystemObjectTypeAttribute Dn,
        ConnectedSystemObjectTypeAttribute Manager,
        ConnectedSystemObjectTypeAttribute Description);

    private sealed record StoredValue(string? StringValue, Guid? ReferenceValueId, bool IsUnresolvedReference);

    /// <summary>
    /// A Connected System whose people carry their DN as the Secondary External ID, a Reference attribute naming a
    /// manager, and a Text attribute.
    /// </summary>
    private async Task<SeededSystem> SeedSystemAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = system, Selected = true };
        var dn = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName", ConnectedSystemObjectType = objectType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsSecondaryExternalId = true
        };
        var manager = new ConnectedSystemObjectTypeAttribute
        {
            Name = "manager", ConnectedSystemObjectType = objectType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var description = new ConnectedSystemObjectTypeAttribute
        {
            Name = "description", ConnectedSystemObjectType = objectType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        objectType.Attributes.AddRange([dn, manager, description]);
        seed.AddRange(connectorDefinition, system, objectType);
        await seed.SaveChangesAsync();

        return new SeededSystem(system.Id, objectType.Id, dn, manager, description);
    }

    /// <summary>
    /// A person in the seeded system whose Secondary External ID is the given DN.
    /// </summary>
    private async Task<Guid> AddPersonAsync(SeededSystem seed, string dn)
    {
        await using var ctx = NewContext();
        var person = new ConnectedSystemObject
        {
            ConnectedSystemId = seed.SystemId, TypeId = seed.ObjectTypeId, ExternalIdAttributeId = seed.Dn.Id,
            SecondaryExternalIdAttributeId = seed.Dn.Id, Status = ConnectedSystemObjectStatus.Normal
        };
        person.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = person, AttributeId = seed.Dn.Id, StringValue = dn });
        ctx.Add(person);
        await ctx.SaveChangesAsync();
        return person.Id;
    }

    /// <summary>
    /// Writes one change value through the repository's change record writer, as a run does, and returns its id.
    /// </summary>
    /// <param name="referencedObjectId">The object the value refers to, which need not have a row yet; null for a
    /// reference that named no object.</param>
    private async Task<Guid> WriteValueAsync(SeededSystem seed, ConnectedSystemObjectTypeAttribute attribute, string stringValue, Guid? referencedObjectId)
    {
        var (change, value) = NewChange(seed, attribute, stringValue, referencedObjectId, ObjectChangeType.Added);

        await using var ctx = NewContext();
        await new PostgresDataRepository(ctx).Sync.PersistCsoChangesAsync([change]);
        return value.Id;
    }

    /// <summary>
    /// A change record holding one value of the given attribute.
    /// </summary>
    private static (ConnectedSystemObjectChange Change, ConnectedSystemObjectChangeAttributeValue Value) NewChange(
        SeededSystem seed, ConnectedSystemObjectTypeAttribute attribute, string stringValue, Guid? referencedObjectId, ObjectChangeType changeType)
    {
        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = seed.SystemId,
            ChangeTime = DateTime.UtcNow,
            ChangeType = changeType,
            InitiatedByType = ActivityInitiatorType.System
        };
        var attributeChange = new ConnectedSystemObjectChangeAttribute
        {
            Attribute = attribute,
            AttributeName = attribute.Name,
            AttributeType = attribute.Type,
            ConnectedSystemChange = change
        };
        change.AttributeChanges.Add(attributeChange);
        var value = new ConnectedSystemObjectChangeAttributeValue(attributeChange, ValueChangeType.Add, stringValue);
        if (referencedObjectId.HasValue)
            value.ReferenceValue = new ConnectedSystemObject { Id = referencedObjectId.Value };
        attributeChange.ValueChanges.Add(value);
        return (change, value);
    }

    private async Task<int> FixupAsync(SeededSystem seed)
    {
        await using var ctx = NewContext();
        return await new PostgresDataRepository(ctx).Sync.FixupCrossBatchChangeRecordReferenceIdsAsync(seed.SystemId);
    }

    private async Task<StoredValue> ReadValueAsync(Guid valueId)
    {
        await using var ctx = NewContext();
        return await ctx.ConnectedSystemObjectChangeAttributeValues
            .AsNoTracking()
            .Where(v => v.Id == valueId)
            .Select(v => new StoredValue(v.StringValue, EF.Property<Guid?>(v, "ReferenceValueId"), v.IsUnresolvedReference))
            .SingleAsync();
    }

    #endregion
}
