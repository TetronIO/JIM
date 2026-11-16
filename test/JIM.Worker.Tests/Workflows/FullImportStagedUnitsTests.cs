// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A Full Import saves what it reads a unit at a time (#2521, Phase 5). Every case here crosses units (a unit is a page or
/// two, by a small <see cref="SyncImportTaskProcessor.ImportBatchSize"/>), so a reference can name an object a later unit
/// saves: it is saved by its identifier, and the end-of-run sweep resolves it, or reports it on the execution item the
/// unit already saved. The outcomes are a single save phase's: the same objects, references, reports, deletions and
/// retries. A run that stops part-way keeps the units it saved.
/// </summary>
[TestFixture]
public class FullImportStagedUnitsTests : WorkflowTestBase
{
    [Test]
    public async Task FullImport_GroupReferencingAUserOnALaterPage_ResolvesTheReferenceAsync()
    {
        var schema = await ArrangeAsync();
        var users = Ids(5);
        var groupId = Guid.NewGuid();

        var (processor, activity) = await RunAsync(schema,
        [
            [Group(schema, groupId, users[2]), User(schema, users[0])],
            [User(schema, users[1]), User(schema, users[3])],
            [User(schema, users[2]), User(schema, users[4])]
        ], importBatchSize: 2);

        var group = Held(schema.Group, groupId);
        var member = group.AttributeValues.Single(v => v.Attribute?.Name == "member");
        var item = ItemFor(activity, group);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(processor.StagedUnitCount, Is.EqualTo(3), "each page is a unit of its own");
            Assert.That(Referenced(member), Is.EqualTo(Held(schema.User, users[2]).Id), "the group on page 1 names the user on page 3");
            Assert.That(item.ErrorType, Is.Null.Or.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet));
            Assert.That(item.UnresolvedReferenceCount, Is.Null);
            Assert.That(processor.ObjectsWithOutstandingReferences, Is.Zero, "the sweep's notes are released once it is over");
        }
    }

    [TestCase(UnresolvedReferenceHandling.Error)]
    [TestCase(UnresolvedReferenceHandling.Warn)]
    [TestCase(UnresolvedReferenceHandling.Ignore)]
    public async Task FullImport_ReferenceStillUnresolvedAfterTheLastUnit_IsReportedOnTheSavedItemAsync(UnresolvedReferenceHandling handling)
    {
        var schema = await ArrangeAsync();
        schema.ConnectedSystem.UnresolvedReferenceHandling = handling;
        var users = Ids(3);
        var groupId = Guid.NewGuid();
        var nobody = Guid.NewGuid();

        var (processor, activity) = await RunAsync(schema,
        [
            [Group(schema, groupId, users[2], nobody), User(schema, users[0])],
            [User(schema, users[1]), User(schema, users[2])]
        ], importBatchSize: 2);

        var group = Held(schema.Group, groupId);
        var item = ItemFor(activity, group);
        var members = group.AttributeValues.Where(v => v.Attribute?.Name == "member").ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(processor.StagedUnitCount, Is.EqualTo(2));
            Assert.That(item.UnresolvedReferenceCount, Is.EqualTo(1), "the count goes on the item whatever the handling (#959)");
            Assert.That(Referenced(members.Single(v => v.UnresolvedReferenceValue == users[2].ToString())), Is.EqualTo(Held(schema.User, users[2]).Id));
            Assert.That(Referenced(members.Single(v => v.UnresolvedReferenceValue == nobody.ToString())), Is.Null);
            if (handling == UnresolvedReferenceHandling.Error)
            {
                Assert.That(item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.UnresolvedReference));
                Assert.That(item.ErrorMessage, Does.Contain(nobody.ToString()));
            }
            else
            {
                Assert.That(item.ErrorType, Is.Null.Or.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet));
            }

            if (handling == UnresolvedReferenceHandling.Warn)
                Assert.That(activity.WarningMessage, Does.Contain("1 reference value(s) could not be resolved"));
            else
                Assert.That(activity.WarningMessage ?? string.Empty, Does.Not.Contain("could not be resolved"));
        }
    }

    [Test]
    public async Task FullImport_ReferenceToALaterUnitWithADeclaredTargetType_ResolvesToThatTypeAloneAsync()
    {
        // A user and a contact share the anchor value; the member attribute declares users as its target (#1285), and the
        // reference names the user in another case, which the anchor ignores (#2051).
        var schema = await ArrangeTextAnchoredAsync(caseExact: false, declareUserAsTarget: true);

        var (processor, activity) = await RunAsync(schema,
        [
            [TextGroup("g1", "U9")],
            [TextObject(schema.Contact!, "u9")],
            [TextObject(schema.User, "u9")]
        ], importBatchSize: 1);

        var group = HeldByText(schema.Group, "g1");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(processor.StagedUnitCount, Is.EqualTo(3));
            Assert.That(Referenced(group.AttributeValues.Single(v => v.Attribute?.Name == "member")), Is.EqualTo(HeldByText(schema.User, "u9").Id));
            Assert.That(ItemFor(activity, group).UnresolvedReferenceCount, Is.Null);
        }
    }

    [Test]
    public async Task FullImport_ReferenceToALaterUnitMatchingTwoObjectTypes_IsReportedAsAmbiguousAsync()
    {
        // Undeclared, the value names a user and a contact alike: reported, never guessed (#1285).
        var schema = await ArrangeTextAnchoredAsync(caseExact: false, declareUserAsTarget: false);
        schema.ConnectedSystem.UnresolvedReferenceHandling = UnresolvedReferenceHandling.Error;

        var (processor, activity) = await RunAsync(schema,
        [
            [TextGroup("g1", "u9")],
            [TextObject(schema.Contact!, "u9")],
            [TextObject(schema.User, "u9")]
        ], importBatchSize: 1);

        var group = HeldByText(schema.Group, "g1");
        var item = ItemFor(activity, group);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(processor.StagedUnitCount, Is.EqualTo(3));
            Assert.That(Referenced(group.AttributeValues.Single(v => v.Attribute?.Name == "member")), Is.Null);
            Assert.That(item.UnresolvedReferenceCount, Is.EqualTo(1));
            Assert.That(item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.UnresolvedReference));
            Assert.That(item.ErrorMessage, Does.Contain("contact").And.Contain("user"));
        }
    }

    [Test]
    public async Task FullImport_ReferenceToALaterUnitInAnotherCaseWhereTheAnchorIsCaseExact_StaysUnresolvedAsync()
    {
        var schema = await ArrangeTextAnchoredAsync(caseExact: true, declareUserAsTarget: true);

        var (processor, activity) = await RunAsync(schema,
        [
            [TextGroup("g1", "U9")],
            [TextObject(schema.User, "u9")]
        ], importBatchSize: 1);

        var group = HeldByText(schema.Group, "g1");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(processor.StagedUnitCount, Is.EqualTo(2));
            Assert.That(Referenced(group.AttributeValues.Single(v => v.Attribute?.Name == "member")), Is.Null);
            Assert.That(ItemFor(activity, group).UnresolvedReferenceCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task FullImport_StoppedAfterTheSecondUnit_KeepsWhatItSavedAndDetectsNoDeletionsAsync()
    {
        var schema = await ArrangeAsync();
        var gone = SeedUser(schema, Guid.NewGuid());
        var users = Ids(8);
        var pages = Enumerable.Range(0, 4).Select(page => new List<ConnectedSystemImportObject> { User(schema, users[page * 2]), User(schema, users[page * 2 + 1]) }).ToList();

        // Cancelled as the third page is asked for: the third page is read and processed, the second unit is saved when
        // the third begins, and the third, still being built, is not.
        var cancellation = new CancellationTokenSource();
        var (stopped, stoppedActivity) = await RunAsync(schema, pages, importBatchSize: 2, cancellation: cancellation,
            beforePage: page => { if (page == 2) cancellation.Cancel(); }, watermark: "w1");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stopped.StagedUnitCount, Is.EqualTo(2));
            Assert.That(users.Take(4).Select(id => HeldOrNull(schema.User, id)), Has.All.Not.Null, "the saved units stay");
            Assert.That(users.Skip(4).Select(id => HeldOrNull(schema.User, id)), Has.All.Null, "the unit being built is discarded");
            Assert.That(gone.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal), "a stopped run detects nothing as deleted");
            Assert.That(schema.ConnectedSystem.PersistedConnectorData, Is.Null, "the watermark is not recorded");
            Assert.That(schema.ConnectedSystem.ImportIncompleteSince, Is.Not.Null, "synchronisation is held back");
            Assert.That(stoppedActivity.RunProfileExecutionItems.Where(i => i.ObjectChangeType == ObjectChangeType.Deleted), Is.Empty);
        }

        var (completed, completedActivity) = await RunAsync(schema, pages, importBatchSize: 2, watermark: "w1");

        var created = stoppedActivity.RunProfileExecutionItems.Concat(completedActivity.RunProfileExecutionItems)
            .Where(i => i.ObjectChangeType == ObjectChangeType.Added)
            .Select(i => i.ConnectedSystemObjectId)
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed.StagedUnitCount, Is.GreaterThan(0));
            Assert.That(users.Select(id => HeldOrNull(schema.User, id)), Has.All.Not.Null);
            Assert.That(created, Has.Count.EqualTo(8).And.Unique, "each object is recorded as created once, across both runs");
            Assert.That(completedActivity.RunProfileExecutionItems.Where(i => i.ObjectChangeType == ObjectChangeType.Updated), Is.Empty,
                "what the stopped run saved is unchanged, so nothing is recorded again");
            Assert.That(gone.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
            Assert.That(schema.ConnectedSystem.PersistedConnectorData, Is.EqualTo("w1"));
            Assert.That(schema.ConnectedSystem.ImportIncompleteSince, Is.Null);
        }
    }

    [Test]
    public async Task FullImport_AfterStagedUnits_DetectsDeletionsAndRetriesUnconfirmedCreatesAsBeforeAsync()
    {
        var schema = await ArrangeAsync();
        var gone = SeedUser(schema, Guid.NewGuid());
        var unconfirmed = SeedUser(schema, Guid.NewGuid(), ConnectedSystemObjectStatus.PendingProvisioning);
        var unconfirmedCreate = SeedExportedCreate(schema, unconfirmed);
        var users = Ids(4);

        var (processor, activity) = await RunAsync(schema,
        [
            [User(schema, users[0]), User(schema, users[1])],
            [User(schema, users[2]), User(schema, users[3])]
        ], importBatchSize: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processor.StagedUnitCount, Is.GreaterThanOrEqualTo(2));
            Assert.That(gone.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
            Assert.That(activity.RunProfileExecutionItems.Where(i => i.ObjectChangeType == ObjectChangeType.Deleted).Select(i => i.ConnectedSystemObjectId),
                Is.EqualTo(new[] { gone.Id }));
            Assert.That(SyncRepo.PendingExports[unconfirmedCreate.Id].Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed));
            Assert.That(activity.RunProfileExecutionItems.Where(i => i.ErrorType == ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed)
                .Select(i => i.ConnectedSystemObjectId), Is.EqualTo(new[] { unconfirmed.Id }));
        }
    }

    [Test]
    public async Task FullImport_ReferenceToAnObjectTypeChangeReplacementWhoseHeldObjectIsRetiredThisRun_IsAmbiguousAsBeforeAsync()
    {
        // The computer is read first, as a dependency-ordered read reads what is referenced first (#2521, decision 7), and its
        // replacement is held apart until deletion detection decides the change (#2049). The group's unit is saved before
        // then, while the database holds only the user the replacement retires under the same External ID: the reference is
        // not resolved to the user. The single save phase resolved it with the replacement and the user it retires both in
        // memory, so an undeclared reference was ambiguous between them; the sweep reports it the same way.
        var schema = await ArrangeAsync(allowObjectTypeChanges: true);
        schema.ConnectedSystem.UnresolvedReferenceHandling = UnresolvedReferenceHandling.Error;
        var held = SeedUser(schema, Guid.NewGuid());
        var heldExternalId = held.AttributeValues.Single(v => v.GuidValue.HasValue).GuidValue!.Value;
        var groupId = Guid.NewGuid();

        var (processor, activity) = await RunAsync(schema,
        [
            [Computer(schema, heldExternalId)],
            [Group(schema, groupId, heldExternalId)]
        ], importBatchSize: 1);

        var group = Held(schema.Group, groupId);
        var item = ItemFor(activity, group);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(processor.StagedUnitCount, Is.GreaterThanOrEqualTo(2));
            Assert.That(held.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
            Assert.That(Referenced(group.AttributeValues.Single(v => v.Attribute?.Name == "member")), Is.Null, "never the user being retired");
            Assert.That(item.UnresolvedReferenceCount, Is.EqualTo(1));
            Assert.That(item.ErrorMessage, Does.Contain("more than one Object Type (computer, user)"));
        }
    }

    [Test]
    public async Task FullImport_ReferenceToAnObjectTypeChangeReplacementOfAnAlreadyObsoleteObject_ResolvesToTheReplacementAsync()
    {
        // The held user was already Obsolete, so the single save phase held only the replacement in memory and resolved the
        // reference to it; the sweep does too, though the database still holds the user under the same External ID.
        var schema = await ArrangeAsync(allowObjectTypeChanges: true);
        schema.ConnectedSystem.UnresolvedReferenceHandling = UnresolvedReferenceHandling.Error;
        var held = SeedUser(schema, Guid.NewGuid(), ConnectedSystemObjectStatus.Obsolete);
        var heldExternalId = held.AttributeValues.Single(v => v.GuidValue.HasValue).GuidValue!.Value;
        var groupId = Guid.NewGuid();

        var (processor, activity) = await RunAsync(schema,
        [
            [Computer(schema, heldExternalId)],
            [Group(schema, groupId, heldExternalId)]
        ], importBatchSize: 1);

        var replacement = Held(schema.Computer, heldExternalId);
        var group = Held(schema.Group, groupId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(processor.StagedUnitCount, Is.GreaterThanOrEqualTo(2));
            Assert.That(Referenced(group.AttributeValues.Single(v => v.Attribute?.Name == "member")), Is.EqualTo(replacement.Id));
            Assert.That(ItemFor(activity, group).UnresolvedReferenceCount, Is.Null);
        }
    }

    [Test]
    public async Task FullImport_ReferenceReadBeforeAnObjectTypeChangeRetiresItsObjectThisRun_IsAmbiguousAsBeforeAsync()
    {
        // The group is read before the computer, so its unit resolves the reference through the database while the user is
        // the only object holding the External ID, and nothing yet marks a change. The computer's change then stands and
        // deletion detection retires the user. The single save phase resolved the reference with the replacement and the
        // user both in memory, so an undeclared reference was ambiguous between them; the sweep rechecks it and says so,
        // rather than leaving it naming the user being retired.
        var schema = await ArrangeAsync(allowObjectTypeChanges: true);
        schema.ConnectedSystem.UnresolvedReferenceHandling = UnresolvedReferenceHandling.Error;
        var held = SeedUser(schema, Guid.NewGuid());
        var heldExternalId = held.AttributeValues.Single(v => v.GuidValue.HasValue).GuidValue!.Value;
        var groupId = Guid.NewGuid();

        var (processor, activity) = await RunAsync(schema,
        [
            [Group(schema, groupId, heldExternalId)],
            [Computer(schema, heldExternalId)]
        ], importBatchSize: 1);

        var group = Held(schema.Group, groupId);
        var item = ItemFor(activity, group);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(processor.StagedUnitCount, Is.GreaterThanOrEqualTo(2));
            Assert.That(held.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete), "the change stood");
            Assert.That(Referenced(group.AttributeValues.Single(v => v.Attribute?.Name == "member")), Is.Null, "never the user being retired");
            Assert.That(item.UnresolvedReferenceCount, Is.EqualTo(1));
            Assert.That(item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.UnresolvedReference));
            Assert.That(item.ErrorMessage, Does.Contain("more than one Object Type (computer, user)"));
            Assert.That(group.ImportStateHash, Is.Null, "an item that ends the run errored keeps no hash, so the next run compares it again");
        }
    }

    [Test]
    public async Task FullImport_ReferenceReadBeforeAnObjectTypeChangeOfAnAlreadyObsoleteObject_ResolvesToTheReplacementAsync()
    {
        // As above, but the user was already Obsolete: the single save phase held only the replacement in memory and resolved
        // the reference to it. The sweep's recheck re-points it from the user to the replacement.
        var schema = await ArrangeAsync(allowObjectTypeChanges: true);
        schema.ConnectedSystem.UnresolvedReferenceHandling = UnresolvedReferenceHandling.Error;
        var held = SeedUser(schema, Guid.NewGuid(), ConnectedSystemObjectStatus.Obsolete);
        var heldExternalId = held.AttributeValues.Single(v => v.GuidValue.HasValue).GuidValue!.Value;
        var groupId = Guid.NewGuid();

        var (_, activity) = await RunAsync(schema,
        [
            [Group(schema, groupId, heldExternalId)],
            [Computer(schema, heldExternalId)]
        ], importBatchSize: 1);

        var replacement = Held(schema.Computer, heldExternalId);
        var group = Held(schema.Group, groupId);
        var item = ItemFor(activity, group);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Referenced(group.AttributeValues.Single(v => v.Attribute?.Name == "member")), Is.EqualTo(replacement.Id));
            Assert.That(item.UnresolvedReferenceCount, Is.Null);
            Assert.That(item.ErrorType, Is.Null.Or.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet));
        }
    }

    [TestCase(false, TestName = "FullImport_ReferencesResolvedThroughTheDatabase_KeepNothingForARecheckWhereTypeChangesAreNotAllowedAsync")]
    [TestCase(true, TestName = "FullImport_ReferencesToObjectsAlreadyReadThisRun_KeepNothingForARecheckEvenWhereTypeChangesAreAllowedAsync")]
    public async Task FullImport_ReferencesResolvedThroughTheDatabase_KeepNothingForARecheckAsync(bool allowObjectTypeChanges)
    {
        // A recheck costs memory only where an Object Type change could retire what a reference names: a run that allows
        // them, and an object it has not read itself (one read this run under its own type is never retired).
        var schema = await ArrangeAsync(allowObjectTypeChanges);
        var users = Ids(3);
        var groupId = Guid.NewGuid();

        var (processor, _) = await RunAsync(schema,
        [
            [.. users.Select(id => User(schema, id))],
            [Group(schema, groupId, [.. users])]
        ], importBatchSize: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processor.StagedUnitCount, Is.EqualTo(2), "the group's unit resolves its members through the database");
            Assert.That(Held(schema.Group, groupId).AttributeValues.Where(v => v.Attribute?.Name == "member").Select(Referenced),
                Is.EquivalentTo(users.Select(id => (Guid?)Held(schema.User, id).Id)));
            Assert.That(processor.ReferencesKeptForObjectTypeChangeRecheck, Is.Zero);
        }
    }

    [Test]
    public async Task FullImport_PageOfLargeGroupsOverTheValueBudget_IsSplitIntoUnitsAsync()
    {
        var schema = await ArrangeAsync();
        var users = Ids(30);
        var groups = Ids(3);

        // A page of users, then one page of three groups of 30 members (32 values each) against a budget of 40 values: each
        // group is a unit of its own.
        var (processor, _) = await RunAsync(schema,
        [
            [.. users.Select(id => User(schema, id))],
            [.. groups.Select(id => Group(schema, id, [.. users]))]
        ], importBatchSize: 1000, valueBudget: 40, measure: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processor.StagedUnitCount, Is.GreaterThanOrEqualTo(groups.Count + 1));
            Assert.That(processor.PeakHeldAttributeValues, Is.LessThanOrEqualTo(40), "no unit holds more than the budget, as no single object does");
            foreach (var group in groups.Select(id => Held(schema.Group, id)))
                Assert.That(group.AttributeValues.Where(v => v.Attribute?.Name == "member").Select(Referenced),
                    Is.EquivalentTo(users.Select(id => (Guid?)Held(schema.User, id).Id)));
        }
    }

    #region Harness

    private sealed record Schema(
        ConnectedSystem ConnectedSystem,
        ConnectedSystemObjectType User,
        ConnectedSystemObjectType Group,
        ConnectedSystemObjectType Computer,
        ConnectedSystemObjectType? Contact,
        bool AllowObjectTypeChanges);

    private async Task<Schema> ArrangeAsync(bool allowObjectTypeChanges = false)
    {
        var connectedSystem = await CreateConnectedSystemAsync("Directory");
        connectedSystem.ConnectorDefinition.ExternalIdsUniqueAcrossObjectTypes = true;
        var user = await CreateCsoTypeAsync(connectedSystem.Id, "user");
        var computer = await CreateCsoTypeAsync(connectedSystem.Id, "computer");
        var group = await CreateCsoTypeAsync(connectedSystem.Id, "group",
        [
            new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued, Selected = true }
        ]);
        return new Schema(connectedSystem, user, group, computer, null, allowObjectTypeChanges);
    }

    /// <summary>Users, contacts and groups whose External ID is text, ignoring case unless <paramref name="caseExact"/>.</summary>
    private async Task<Schema> ArrangeTextAnchoredAsync(bool caseExact, bool declareUserAsTarget)
    {
        var connectedSystem = await CreateConnectedSystemAsync("Database");
        List<ConnectedSystemObjectTypeAttribute> Attributes(bool withMember) =>
        [
            new() { Name = "id", Type = AttributeDataType.Text, IsExternalId = true, CaseExact = caseExact, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            .. withMember
                ? new[] { new ConnectedSystemObjectTypeAttribute { Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued, Selected = true } }
                : []
        ];

        var user = await CreateCsoTypeAsync(connectedSystem.Id, "user", Attributes(withMember: false));
        var contact = await CreateCsoTypeAsync(connectedSystem.Id, "contact", Attributes(withMember: false));
        var group = await CreateCsoTypeAsync(connectedSystem.Id, "group", Attributes(withMember: true));
        if (declareUserAsTarget)
            group.Attributes.Single(a => a.Name == "member").ReferencedObjectTypeId = user.Id;
        return new Schema(connectedSystem, user, group, group, contact, AllowObjectTypeChanges: false);
    }

    private async Task<(SyncImportTaskProcessor Processor, Activity Activity)> RunAsync(Schema schema, List<List<ConnectedSystemImportObject>> pages,
        int importBatchSize, int valueBudget = SyncImportTaskProcessor.DefaultImportUnitValueBudget, bool measure = false,
        CancellationTokenSource? cancellation = null, Action<int>? beforePage = null, string? watermark = null)
    {
        var runProfile = await CreateRunProfileAsync(schema.ConnectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        runProfile.AllowObjectTypeChanges = schema.AllowObjectTypeChanges;
        var activity = await CreateActivityAsync(schema.ConnectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);
        var workerTask = new SynchronisationWorkerTask(schema.ConnectedSystem.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };

        // Each run is handed its own copy of the pages: processing a page changes its import objects.
        var copies = pages.Select(page => page.Select(Copy).ToList()).ToList();
        var processor = new SyncImportTaskProcessor(Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            new PagedImportConnector(copies, firstPageWatermark: watermark, beforePage: beforePage), schema.ConnectedSystem, runProfile, workerTask,
            cancellation ?? new CancellationTokenSource())
        {
            ImportBatchSize = importBatchSize,
            ImportUnitValueBudget = valueBudget,
            MeasureHeldAttributeValues = measure
        };

        await processor.PerformImportAsync();
        return (processor, activity);
    }

    private static ConnectedSystemImportObject Copy(ConnectedSystemImportObject importObject) => new()
    {
        ObjectType = importObject.ObjectType,
        ChangeType = importObject.ChangeType,
        Attributes = importObject.Attributes.Select(attribute => new ConnectedSystemImportObjectAttribute
        {
            Name = attribute.Name,
            Type = attribute.Type,
            GuidValues = attribute.HasGuidValues ? [.. attribute.GuidValues] : [],
            StringValues = attribute.HasStringValues ? [.. attribute.StringValues] : [],
            ReferenceValues = attribute.HasReferenceValues ? [.. attribute.ReferenceValues] : []
        }).ToList()
    };

    private static List<Guid> Ids(int count) => Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();

    private static ConnectedSystemImportObject User(Schema schema, Guid externalId) => Guided(schema.User, externalId, $"User {externalId}");

    private static ConnectedSystemImportObject Computer(Schema schema, Guid externalId) => Guided(schema.Computer, externalId, $"Computer {externalId}");

    private static ConnectedSystemImportObject Guided(ConnectedSystemObjectType type, Guid externalId, string displayName) => new()
    {
        ObjectType = type.Name,
        Attributes =
        [
            new ConnectedSystemImportObjectAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, GuidValues = [externalId] },
            new ConnectedSystemImportObjectAttribute { Name = "DisplayName", Type = AttributeDataType.Text, StringValues = [displayName] }
        ]
    };

    private static ConnectedSystemImportObject Group(Schema schema, Guid externalId, params Guid[] members) => new()
    {
        ObjectType = schema.Group.Name,
        Attributes =
        [
            new ConnectedSystemImportObjectAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, GuidValues = [externalId] },
            new ConnectedSystemImportObjectAttribute { Name = "DisplayName", Type = AttributeDataType.Text, StringValues = [$"Group {externalId}"] },
            new ConnectedSystemImportObjectAttribute { Name = "member", Type = AttributeDataType.Reference, ReferenceValues = [.. members.Select(m => m.ToString())] }
        ]
    };

    private static ConnectedSystemImportObject TextObject(ConnectedSystemObjectType type, string id) => new()
    {
        ObjectType = type.Name,
        Attributes =
        [
            new ConnectedSystemImportObjectAttribute { Name = "id", Type = AttributeDataType.Text, StringValues = [id] },
            new ConnectedSystemImportObjectAttribute { Name = "DisplayName", Type = AttributeDataType.Text, StringValues = [$"{type.Name} {id}"] }
        ]
    };

    private static ConnectedSystemImportObject TextGroup(string id, params string[] members) => new()
    {
        ObjectType = "group",
        Attributes =
        [
            new ConnectedSystemImportObjectAttribute { Name = "id", Type = AttributeDataType.Text, StringValues = [id] },
            new ConnectedSystemImportObjectAttribute { Name = "DisplayName", Type = AttributeDataType.Text, StringValues = [$"group {id}"] },
            new ConnectedSystemImportObjectAttribute { Name = "member", Type = AttributeDataType.Reference, ReferenceValues = [.. members] }
        ]
    };

    private ConnectedSystemObject Held(ConnectedSystemObjectType type, Guid externalId) =>
        HeldOrNull(type, externalId) ?? throw new AssertionException($"No {type.Name} with External ID {externalId} is held.");

    private ConnectedSystemObject? HeldOrNull(ConnectedSystemObjectType type, Guid externalId) =>
        SyncRepo.ConnectedSystemObjects.Values.SingleOrDefault(cso => cso.TypeId == type.Id &&
            cso.AttributeValues.Any(v => v.AttributeId == cso.ExternalIdAttributeId && v.GuidValue == externalId));

    private ConnectedSystemObject HeldByText(ConnectedSystemObjectType type, string externalId) =>
        SyncRepo.ConnectedSystemObjects.Values.Single(cso => cso.TypeId == type.Id &&
            cso.AttributeValues.Any(v => v.AttributeId == cso.ExternalIdAttributeId && v.StringValue == externalId));

    /// <summary>
    /// The object a reference value names: its id, or, where the in-memory store has not been given it, the object it was
    /// resolved to in memory (the database writer takes the id from it).
    /// </summary>
    private static Guid? Referenced(ConnectedSystemObjectAttributeValue value) => value.ReferenceValueId ?? value.ReferenceValue?.Id;

    private static ActivityRunProfileExecutionItem ItemFor(Activity activity, ConnectedSystemObject cso) =>
        activity.RunProfileExecutionItems.Single(i => i.ConnectedSystemObjectId == cso.Id);

    private ConnectedSystemObject SeedUser(Schema schema, Guid externalId, ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = schema.ConnectedSystem.Id,
            ConnectedSystem = schema.ConnectedSystem,
            TypeId = schema.User.Id,
            Type = schema.User,
            ExternalIdAttributeId = schema.User.Attributes.Single(a => a.IsExternalId).Id,
            Status = status,
            Created = DateTime.UtcNow
        };
        foreach (var (attribute, guidValue, stringValue) in new[]
                 {
                     (schema.User.Attributes.Single(a => a.IsExternalId), (Guid?)externalId, (string?)null),
                     (schema.User.Attributes.Single(a => a.Name == "DisplayName"), null, $"User {externalId}")
                 })
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), AttributeId = attribute.Id, Attribute = attribute, GuidValue = guidValue, StringValue = stringValue, ConnectedSystemObject = cso
            });
        }

        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    /// <summary>A Create exported to the Connected System that no import has yet confirmed, with its one exported value.</summary>
    private PendingExport SeedExportedCreate(Schema schema, ConnectedSystemObject cso)
    {
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = schema.ConnectedSystem.Id,
            ConnectedSystemObjectId = cso.Id,
            ConnectedSystemObject = cso,
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Exported,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(),
                    AttributeId = schema.User.Attributes.Single(a => a.Name == "DisplayName").Id,
                    ChangeType = PendingExportAttributeChangeType.Add,
                    Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
                    StringValue = $"User {cso.AttributeValues.Single(v => v.GuidValue.HasValue).GuidValue}"
                }
            ]
        };
        SyncRepo.SeedPendingExport(pendingExport);
        return pendingExport;
    }

    #endregion
}
