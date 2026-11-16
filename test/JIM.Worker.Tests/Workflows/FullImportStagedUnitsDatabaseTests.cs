// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A Full Import that saves each unit as it goes (#2521, Phase 5), end to end on real PostgreSQL, on one long-lived
/// tracking context as the Worker runs it, holding tracked instances of rows the units and the sweep write by raw SQL. The
/// in-memory store neither writes ids the way the database writers do nor checks a tracked instance against its row, so
/// these are the cases it cannot see: references between units written by id, the sweep's writes to the values, the change
/// records and the execution items saved before it, and a second run that finds nothing to record.
/// Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; <c>SetUp</c> TRUNCATEs every table.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class FullImportStagedUnitsDatabaseTests
{
    private string _connectionString = null!;
    private int _systemId;
    private int _userTypeId;
    private int _groupTypeId;
    private int _userIdAttributeId;
    private int _userNameAttributeId;
    private int _groupIdAttributeId;
    private int _groupNameAttributeId;
    private int _memberAttributeId;
    private int _contactIdAttributeId;

    private static readonly Guid Kept = Guid.Parse("00000000-0000-0000-0000-00000000a001");
    private static readonly Guid Gone = Guid.Parse("00000000-0000-0000-0000-00000000a002");
    private static readonly Guid Unconfirmed = Guid.Parse("00000000-0000-0000-0000-00000000a003");
    private static readonly Guid UnconfirmedExport = Guid.Parse("00000000-0000-0000-0000-00000000a004");
    private static readonly Guid Moving = Guid.Parse("00000000-0000-0000-0000-00000000a005");
    private static readonly Guid Moved = Guid.Parse("00000000-0000-0000-0000-00000000a006");

    private JimDbContext NewContext(bool trackAll = false)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        // The Worker tracks by default (src/JIM.Worker/Program.cs); an import run must too.
        if (trackAll)
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL staged Full Import tests.");
        _connectionString = connectionString;

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    [Test]
    public async Task FullImport_ReferencesAcrossUnitsOnATrackingContext_ResolvesReportsAndRecordsAsASingleSaveAsync()
    {
        await SeedAsync();

        var first = await RunFullImportAsync(trackHeldObjectsFirst: true);

        await using var ctx = NewContext();
        var g1 = await ObjectIdAsync(ctx, _groupIdAttributeId, "g1");
        var u3 = await ObjectIdAsync(ctx, _userIdAttributeId, "u3");
        var u4 = await ObjectIdAsync(ctx, _userIdAttributeId, "U4");
        var members = await ctx.ConnectedSystemObjectAttributeValues.AsNoTracking()
            .Where(v => v.ConnectedSystemObject.Id == g1 && v.AttributeId == _memberAttributeId)
            .ToDictionaryAsync(v => v.UnresolvedReferenceValue!, v => v.ReferenceValueId);
        var changeValues = await ctx.ConnectedSystemObjectChangeAttributeValues.AsNoTracking()
            .Where(v => v.ConnectedSystemObjectChangeAttribute.AttributeId == _memberAttributeId &&
                        v.ConnectedSystemObjectChangeAttribute.ConnectedSystemChange.ConnectedSystemObjectId == g1)
            .ToDictionaryAsync(v => v.StringValue!, v => (v.ReferenceValueId, v.IsUnresolvedReference));
        var g1Item = await ctx.ActivityRunProfileExecutionItems.AsNoTracking().SingleAsync(i => i.ActivityId == first.ActivityId && i.ConnectedSystemObjectId == g1);
        var activity = await ctx.Activities.AsNoTracking().SingleAsync(a => a.Id == first.ActivityId);
        var gone = await ctx.ConnectedSystemObjects.AsNoTracking().SingleAsync(o => o.Id == Gone);
        var export = await ctx.PendingExports.AsNoTracking().SingleAsync(p => p.Id == UnconfirmedExport);
        var system = await ctx.ConnectedSystems.AsNoTracking().SingleAsync(s => s.Id == _systemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.StagedUnitCount, Is.GreaterThanOrEqualTo(3), "the run crosses units");
            Assert.That(members["u3"], Is.EqualTo(u3), "a reference to a later unit is resolved by the sweep");
            Assert.That(members["u4"], Is.EqualTo(u4), "a reference to a later unit in another case, which the anchor ignores (#2051)");
            Assert.That(members["kept"], Is.EqualTo(Kept), "a reference to an object saved before the run");
            Assert.That(members["nobody"], Is.Null);
            Assert.That(changeValues["u3"], Is.EqualTo(((Guid?)u3, false)), "the change record names the object the sweep resolved");
            Assert.That(changeValues["nobody"], Is.EqualTo(((Guid?)null, true)));
            Assert.That(g1Item.UnresolvedReferenceCount, Is.EqualTo(1));
            Assert.That(g1Item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.UnresolvedReference));
            Assert.That(g1Item.ErrorMessage, Does.Contain("nobody"));
            Assert.That(activity.TotalErrors, Is.EqualTo(2), "the unresolved reference and the unconfirmed Create are counted");
            Assert.That(gone.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
            Assert.That(export.Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed));
            Assert.That(system.ImportIncompleteSince, Is.Null);
            Assert.That(first.ChangesSavedOnTheRunContext, Is.True, "nothing tracked on the run's context is left stale");
        }

        var changeRecordsAfterFirst = await ctx.ConnectedSystemObjectChanges.CountAsync();
        var second = await RunFullImportAsync(trackHeldObjectsFirst: false);

        await using var after = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await after.ConnectedSystemObjectChanges.CountAsync(), Is.EqualTo(changeRecordsAfterFirst), "a second run records nothing");
            Assert.That(await after.ActivityRunProfileExecutionItems.CountAsync(i => i.ActivityId == second.ActivityId &&
                                                                                     i.ObjectChangeType != ObjectChangeType.NotSet &&
                                                                                     i.ErrorType != ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed),
                Is.Zero);
            Assert.That(await after.ConnectedSystemObjects.CountAsync(o => o.ConnectedSystemId == _systemId), Is.EqualTo(10));
        }
    }

    [Test]
    public async Task FullImport_ReferencesReadBeforeObjectTypeChangesRetireWhatTheyNamed_AreRecheckedAsASingleSaveAsync()
    {
        // The group is read before the two contacts that change the Object Type of users JIM holds (#2049), so its unit
        // resolves both references through the database to the users. Once both changes stand, the sweep re-opens them: the
        // user that was already Obsolete gives way to its replacement, and the one deletion detection retires this run is
        // ambiguous between the two, as the single save phase found with both in memory. The run's context tracks the users
        // and the group's values throughout.
        await SeedAsync(withObjectTypeChanges: true);

        var run = await RunFullImportAsync(trackHeldObjectsFirst: true, ObjectTypeChangePages());

        await using var ctx = NewContext();
        var g9 = await ObjectIdAsync(ctx, _groupIdAttributeId, "g9");
        var movingReplacement = await ObjectIdAsync(ctx, _contactIdAttributeId, "moving");
        var members = await ctx.ConnectedSystemObjectAttributeValues.AsNoTracking()
            .Where(v => v.ConnectedSystemObject.Id == g9 && v.AttributeId == _memberAttributeId)
            .ToDictionaryAsync(v => v.UnresolvedReferenceValue!, v => v.ReferenceValueId);
        var changeValues = await ctx.ConnectedSystemObjectChangeAttributeValues.AsNoTracking()
            .Where(v => v.ConnectedSystemObjectChangeAttribute.AttributeId == _memberAttributeId &&
                        v.ConnectedSystemObjectChangeAttribute.ConnectedSystemChange.ConnectedSystemObjectId == g9)
            .ToDictionaryAsync(v => v.StringValue!, v => (v.ReferenceValueId, v.IsUnresolvedReference));
        var g9Item = await ctx.ActivityRunProfileExecutionItems.AsNoTracking().SingleAsync(i => i.ActivityId == run.ActivityId && i.ConnectedSystemObjectId == g9);
        var g9Object = await ctx.ConnectedSystemObjects.AsNoTracking().SingleAsync(o => o.Id == g9);
        var moved = await ctx.ConnectedSystemObjects.AsNoTracking().SingleAsync(o => o.Id == Moved);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.StagedUnitCount, Is.GreaterThanOrEqualTo(2), "the group's unit is saved before the changes are read");
            Assert.That(moved.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete), "the change stood");
            Assert.That(members["moving"], Is.EqualTo(movingReplacement), "re-pointed from the Obsolete user to its replacement");
            Assert.That(members["moved"], Is.Null, "never the user being retired");
            Assert.That(changeValues["moving"], Is.EqualTo(((Guid?)movingReplacement, false)), "the change record names the replacement too");
            Assert.That(changeValues["moved"], Is.EqualTo(((Guid?)null, true)), "and is left marked where the value is left unresolved");
            Assert.That(g9Item.UnresolvedReferenceCount, Is.EqualTo(1));
            Assert.That(g9Item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.UnresolvedReference));
            Assert.That(g9Item.ErrorMessage, Does.Contain("more than one Object Type (contact, user)"));
            Assert.That(g9Object.ImportStateHash, Is.Null, "an object whose item ends the run errored is not stamped");
            Assert.That(run.ChangesSavedOnTheRunContext, Is.True, "nothing tracked on the run's context is left stale");
        }
    }

    #region Harness

    private sealed record RunResult(Guid ActivityId, int StagedUnitCount, bool ChangesSavedOnTheRunContext);

    private async Task SeedAsync(bool withObjectTypeChanges = false)
    {
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true, ExternalIdsUniqueAcrossObjectTypes = withObjectTypeChanges };
            var system = new ConnectedSystem
            {
                Name = "Database", ConnectorDefinition = connectorDefinition, UnresolvedReferenceHandling = UnresolvedReferenceHandling.Error
            };

            ConnectedSystemObjectType ObjectType(string name, bool withMember)
            {
                var objectType = new ConnectedSystemObjectType { Name = name, ConnectedSystem = system, Selected = true };
                objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
                {
                    Name = "id", ConnectedSystemObjectType = objectType, Type = AttributeDataType.Text,
                    AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
                });
                objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
                {
                    Name = "displayName", ConnectedSystemObjectType = objectType, Type = AttributeDataType.Text,
                    AttributePlurality = AttributePlurality.SingleValued, Selected = true
                });
                if (withMember)
                {
                    objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
                    {
                        Name = "member", ConnectedSystemObjectType = objectType, Type = AttributeDataType.Reference,
                        AttributePlurality = AttributePlurality.MultiValued, Selected = true
                    });
                }

                return objectType;
            }

            var user = ObjectType("user", withMember: false);
            var group = ObjectType("group", withMember: true);
            var contact = ObjectType("contact", withMember: false);
            seed.AddRange(connectorDefinition, system, user, group, contact);
            await seed.SaveChangesAsync();
            seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
            {
                ConnectedSystemId = system.Id, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport,
                AllowObjectTypeChanges = withObjectTypeChanges
            });
            await seed.SaveChangesAsync();

            _systemId = system.Id;
            _userTypeId = user.Id;
            _groupTypeId = group.Id;
            _userIdAttributeId = user.Attributes.Single(a => a.Name == "id").Id;
            _userNameAttributeId = user.Attributes.Single(a => a.Name == "displayName").Id;
            _groupIdAttributeId = group.Attributes.Single(a => a.Name == "id").Id;
            _groupNameAttributeId = group.Attributes.Single(a => a.Name == "displayName").Id;
            _memberAttributeId = group.Attributes.Single(a => a.Name == "member").Id;
            _contactIdAttributeId = contact.Attributes.Single(a => a.Name == "id").Id;
        }

        await using var write = NewContext();
        var sync = new PostgresDataRepository(write).Sync;
        await sync.CreateConnectedSystemObjectsAsync(
        [
            User(Kept, "kept", "Kept"),
            User(Gone, "gone", "Gone"),
            User(Unconfirmed, "unconfirmed", "Unconfirmed", ConnectedSystemObjectStatus.PendingProvisioning)
        ]);
        if (withObjectTypeChanges)
            await sync.CreateConnectedSystemObjectsAsync([User(Moving, "moving", "Moving", ConnectedSystemObjectStatus.Obsolete), User(Moved, "moved", "Moved")]);
        await sync.CreatePendingExportsAsync(
        [
            new PendingExport
            {
                Id = UnconfirmedExport, ConnectedSystemId = _systemId, ConnectedSystemObjectId = Unconfirmed, ChangeType = PendingExportChangeType.Create,
                Status = PendingExportStatus.Exported, CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                AttributeValueChanges =
                [
                    new PendingExportAttributeValueChange
                    {
                        Id = Guid.NewGuid(), AttributeId = _userNameAttributeId, ChangeType = PendingExportAttributeChangeType.Add,
                        Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation, StringValue = "Unconfirmed"
                    }
                ]
            }
        ]);
    }

    private ConnectedSystemObject User(Guid id, string externalId, string name, ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal)
    {
        var cso = new ConnectedSystemObject
        {
            Id = id, ConnectedSystemId = _systemId, TypeId = _userTypeId, ExternalIdAttributeId = _userIdAttributeId, Status = status,
            Created = DateTime.UtcNow.AddHours(-2)
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _userIdAttributeId, StringValue = externalId });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _userNameAttributeId, StringValue = name });
        return cso;
    }

    /// <summary>
    /// Four pages of two objects, a unit each: the group on the first names a user on the third, one on the fourth in
    /// another case, the user JIM already holds, and one nobody holds. The held user comes back renamed.
    /// </summary>
    private static List<List<ConnectedSystemImportObject>> DirectoryPages()
    {
        static ConnectedSystemImportObjectAttribute Text(string name, string value) => new() { Name = name, Type = AttributeDataType.Text, StringValues = [value] };
        static ConnectedSystemImportObject Object(string type, string id, string name) => new() { ObjectType = type, Attributes = [Text("id", id), Text("displayName", name)] };

        var g1 = Object("group", "g1", "Group 1");
        g1.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "member", Type = AttributeDataType.Reference, ReferenceValues = ["u3", "u4", "kept", "nobody"] });
        return
        [
            [g1, Object("user", "u1", "User 1")],
            [Object("user", "kept", "Kept (renamed)"), Object("user", "u2", "User 2")],
            [Object("user", "u3", "User 3"), Object("user", "u5", "User 5")],
            [Object("user", "U4", "User 4"), Object("user", "u6", "User 6")]
        ];
    }

    /// <summary>
    /// A group naming two users JIM holds, then those users returned as contacts: Object Type changes (#2049) the group's
    /// unit, saved first, knows nothing of.
    /// </summary>
    private static List<List<ConnectedSystemImportObject>> ObjectTypeChangePages()
    {
        static ConnectedSystemImportObjectAttribute Text(string name, string value) => new() { Name = name, Type = AttributeDataType.Text, StringValues = [value] };
        static ConnectedSystemImportObject Object(string type, string id, string name) => new() { ObjectType = type, Attributes = [Text("id", id), Text("displayName", name)] };

        var g9 = Object("group", "g9", "Group 9");
        g9.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "member", Type = AttributeDataType.Reference, ReferenceValues = ["moving", "moved"] });
        return
        [
            [g9, Object("user", "kept", "Kept")],
            [Object("contact", "moving", "Moving"), Object("contact", "moved", "Moved")]
        ];
    }

    private async Task<RunResult> RunFullImportAsync(bool trackHeldObjectsFirst, List<List<ConnectedSystemImportObject>>? pages = null)
    {
        await using var ctx = NewContext(trackAll: true);
        var repository = new PostgresDataRepository(ctx);
        using var jim = new JimApplication(repository, syncRepository: repository.Sync);
        var system = await jim.ConnectedSystems.GetConnectedSystemAsync(_systemId, withChangeTracking: true)
            ?? throw new InvalidOperationException("Connected System not found.");
        var runProfile = system.RunProfiles!.Single(rp => rp.RunType == ConnectedSystemRunType.FullImport);
        var activity = new Activity
        {
            TargetName = runProfile.Name, Status = ActivityStatus.InProgress, ConnectedSystemId = system.Id,
            ConnectedSystemRunProfileId = runProfile.Id, ConnectedSystemRunType = ConnectedSystemRunType.FullImport
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();

        // Tracked instances of rows the run writes by raw SQL: the held objects and their values.
        if (trackHeldObjectsFirst)
            await ctx.ConnectedSystemObjects.Include(o => o.AttributeValues).Where(o => o.ConnectedSystemId == _systemId).ToListAsync();

        var workerTask = new SynchronisationWorkerTask(system.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(), Status = WorkerTaskStatus.Processing, Activity = activity
        };
        var processor = new SyncImportTaskProcessor(jim, jim.SyncRepository, new SyncServer(jim), new SyncEngine(),
            new PagedImportConnector(pages ?? DirectoryPages()), system, runProfile, workerTask, new CancellationTokenSource())
        {
            ImportBatchSize = 2
        };

        await processor.PerformImportAsync();
        await jim.SyncRepository.UpdateActivityAsync(activity);

        // A later save on the run's context must neither fail on nor write back anything the run wrote by raw SQL.
        bool saved;
        try
        {
            await ctx.SaveChangesAsync();
            saved = true;
        }
        catch (DbUpdateException)
        {
            saved = false;
        }

        return new RunResult(activity.Id, processor.StagedUnitCount, saved);
    }

    private static async Task<Guid> ObjectIdAsync(JimDbContext ctx, int externalIdAttributeId, string externalId) =>
        await ctx.ConnectedSystemObjectAttributeValues.AsNoTracking()
            .Where(v => v.AttributeId == externalIdAttributeId && v.StringValue == externalId)
            .Select(v => v.ConnectedSystemObject.Id)
            .SingleAsync();

    #endregion
}
