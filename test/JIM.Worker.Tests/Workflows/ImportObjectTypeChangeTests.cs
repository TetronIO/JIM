// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Worker.Processors;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// An object that changes Object Type (#2049). JIM recognises a Connected System Object by its External ID within one
/// Object Type, so an object arriving under another type used to look like a new object of that type plus, at the next
/// Full Import, a deletion of the old one: the old object was retired, which could delete its Metaverse Object and
/// deprovision it everywhere. Where the Connector vouches that an External ID names one object across every Object
/// Type, the import now refuses the change by default, naming both types, and leaves the existing object as it is.
/// </summary>
[TestFixture]
public class ImportObjectTypeChangeTests : WorkflowTestBase
{
    [Test]
    public async Task FullImport_ObjectHeldUnderAnotherObjectType_IsRefusedAndTheExistingObjectKeptAsync()
    {
        var context = await ArrangeAsync();
        var held = SeedCso(context, context.UserType, Guid.NewGuid());

        await RunImportAsync(context, ConnectedSystemRunType.FullImport, [ImportObject(context.ComputerType, held.ExternalId)]);

        var errored = SingleErroredRpei(context);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(errored.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ObjectTypeChanged));
            Assert.That(errored.ErrorMessage, Does.Contain("user").And.Contain("computer"), "The message names both Object Types");
            Assert.That(errored.ConnectedSystemObjectId, Is.Null, "Nothing was created, and the existing object is not this run's to report against");
        }

        Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(context.ConnectedSystem.Id), Is.EqualTo(1), "No second object is created");
        Assert.That(held.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal),
            "Deletion detection must not retire an object it saw this run under another Object Type");
    }

    [Test]
    public async Task DeltaImport_ObjectHeldUnderAnotherObjectType_IsRefusedAndTheExistingObjectKeptAsync()
    {
        var context = await ArrangeAsync();
        var held = SeedCso(context, context.UserType, Guid.NewGuid());

        await RunImportAsync(context, ConnectedSystemRunType.DeltaImport, [ImportObject(context.ComputerType, held.ExternalId, ObjectChangeType.Updated)]);

        Assert.That(SingleErroredRpei(context).ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ObjectTypeChanged));
        Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(context.ConnectedSystem.Id), Is.EqualTo(1));
        Assert.That(held.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
    }

    [Test]
    public async Task FullImport_ObjectHeldUnderAnotherObjectTypeAlreadyObsolete_IsStillRefusedAsync()
    {
        // Fail-safe: the old object is still in JIM, so creating a second one for the same directory object is still wrong.
        var context = await ArrangeAsync();
        var held = SeedCso(context, context.UserType, Guid.NewGuid(), ConnectedSystemObjectStatus.Obsolete);

        await RunImportAsync(context, ConnectedSystemRunType.FullImport, [ImportObject(context.ComputerType, held.ExternalId)]);

        Assert.That(SingleErroredRpei(context).ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ObjectTypeChanged));
        Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(context.ConnectedSystem.Id), Is.EqualTo(1));
    }

    [Test]
    public async Task FullImport_ObjectHeldUnderADeselectedObjectType_IsRefusedAndTheExistingObjectKeptAsync()
    {
        // Deselecting computer while user still matches the same entries re-types them rather than taking them out of
        // management; deletion detection walks the deselected type, so without the guard every computer was retired.
        var context = await ArrangeAsync();
        context.UserType.Selected = false;
        var held = SeedCso(context, context.UserType, Guid.NewGuid());

        await RunImportAsync(context, ConnectedSystemRunType.FullImport, [ImportObject(context.ComputerType, held.ExternalId)]);

        Assert.That(SingleErroredRpei(context).ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ObjectTypeChanged));
        Assert.That(held.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
    }

    [Test]
    public async Task DeltaImport_AllowObjectTypeChangesSetOnADeltaRunProfile_IsStillRefusedAsync()
    {
        // Validation keeps the setting off Delta Import Run Profiles; the engine does not rely on it. A Delta Import
        // never runs deletion detection, so carrying the change out there would leave both objects in place.
        var context = await ArrangeAsync();
        var held = SeedCso(context, context.UserType, Guid.NewGuid());

        await RunImportAsync(context, ConnectedSystemRunType.DeltaImport, [ImportObject(context.ComputerType, held.ExternalId, ObjectChangeType.Updated)],
            forceAllowObjectTypeChanges: true);

        Assert.That(SingleErroredRpei(context).ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ObjectTypeChanged));
        Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(context.ConnectedSystem.Id), Is.EqualTo(1));
    }

    [Test]
    public async Task FullImport_AllowObjectTypeChanges_CreatesTheNewObjectAndRetiresTheOldAsync()
    {
        var context = await ArrangeAsync(allowObjectTypeChanges: true);
        var held = SeedCso(context, context.UserType, Guid.NewGuid());

        await RunImportAsync(context, ConnectedSystemRunType.FullImport, [ImportObject(context.ComputerType, held.ExternalId)]);

        Assert.That(ErroredRpeis(context), Is.Empty);
        Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(context.ConnectedSystem.Id), Is.EqualTo(2));
        Assert.That(held.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete), "The deliberate change retires the old object, as deletion detection always has");
    }

    [Test]
    public async Task FullImport_AllowObjectTypeChangesUnderADeletionLimit_StillRetiresTheHeldObjectAsync()
    {
        // The retirement is half of a change the administrator asked for, not a missing object: a deletion limit
        // that withheld it would leave both objects in place for one directory object, the new one unable to join.
        var context = await ArrangeAsync(allowObjectTypeChanges: true);
        var held = SeedCso(context, context.UserType, Guid.NewGuid());

        await RunImportAsync(context, ConnectedSystemRunType.FullImport, [ImportObject(context.ComputerType, held.ExternalId)],
            configure: runProfile => runProfile.MaxDetectedDeletions = 0);

        Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(context.ConnectedSystem.Id), Is.EqualTo(2));
        Assert.That(held.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
    }

    [Test]
    public async Task FullImport_AllowObjectTypeChangesUnderADeletionLimit_StillWithholdsGenuineDeletionsAsync()
    {
        var context = await ArrangeAsync(allowObjectTypeChanges: true);
        var held = SeedCso(context, context.UserType, Guid.NewGuid());
        var missing = SeedCso(context, context.UserType, Guid.NewGuid());

        await RunImportAsync(context, ConnectedSystemRunType.FullImport, [ImportObject(context.ComputerType, held.ExternalId)],
            configure: runProfile => runProfile.MaxDetectedDeletions = 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete), "The type change is carried out");
            Assert.That(missing.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal), "A genuinely missing object is still held back by the limit");
            Assert.That(context.Activity.DetectedDeletionsWithheld, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task FullImport_AllowObjectTypeChanges_CountsOnlyTheGenuinelyMissingObjectAsNoLongerFoundAsync()
    {
        // The import returned the re-typed object, so it is counted once, as created (#2129). The object it replaces
        // was not missing, so only the object genuinely gone is reported as no longer found.
        var context = await ArrangeAsync(allowObjectTypeChanges: true);
        var held = SeedCso(context, context.UserType, Guid.NewGuid());
        SeedCso(context, context.UserType, Guid.NewGuid());

        await RunImportAsync(context, ConnectedSystemRunType.FullImport, [ImportObject(context.ComputerType, held.ExternalId)]);

        Assert.That(context.Activity.Message, Does.Contain("1 object (1 created, 0 updated); 1 no longer found and marked as deleted"));
    }

    [Test]
    public async Task FullImport_AllowObjectTypeChangesUnderADeletionLimit_ReportsNothingNoLongerFoundAsync()
    {
        // A refused deletion detection marks nothing as deleted, and its warning says so; the message must not
        // contradict it by reporting the retired object as no longer found.
        var context = await ArrangeAsync(allowObjectTypeChanges: true);
        var held = SeedCso(context, context.UserType, Guid.NewGuid());
        SeedCso(context, context.UserType, Guid.NewGuid());

        await RunImportAsync(context, ConnectedSystemRunType.FullImport, [ImportObject(context.ComputerType, held.ExternalId)],
            configure: runProfile => runProfile.MaxDetectedDeletions = 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Activity.Message, Does.Contain("1 object (1 created, 0 updated)"));
            Assert.That(context.Activity.Message, Does.Not.Contain("no longer found"));
        }
    }

    [Test]
    public async Task FullImport_AllowObjectTypeChangesWhenTheHeldObjectCannotBeRetired_DoesNotCreateTheReplacementAsync()
    {
        // A deselected Object Type with an enabled Synchronisation Rule still bound to it is left out of deletion
        // detection, so the held object would stay beside its replacement. The replacement is withdrawn instead.
        var context = await ArrangeAsync(allowObjectTypeChanges: true);
        context.UserType.Selected = false;
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(context.ConnectedSystem.Id, context.UserType, mvType, "Users In");
        var held = SeedCso(context, context.UserType, Guid.NewGuid());

        await RunImportAsync(context, ConnectedSystemRunType.FullImport, [ImportObject(context.ComputerType, held.ExternalId)]);

        Assert.That(SingleErroredRpei(context).ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ObjectTypeChanged));
        Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(context.ConnectedSystem.Id), Is.EqualTo(1));
        Assert.That(held.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
    }

    [Test]
    public async Task FullImport_AllowObjectTypeChangesWhenTheReplacementCannotBeCreated_KeepsTheHeldObjectAsync()
    {
        // An attribute the new Object Type does not define stops the replacement being created; retiring the held
        // object anyway would remove the directory object from JIM altogether.
        var context = await ArrangeAsync(allowObjectTypeChanges: true);
        var held = SeedCso(context, context.UserType, Guid.NewGuid());
        var importObject = ImportObject(context.ComputerType, held.ExternalId);
        importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "NotInTheSchema", Type = AttributeDataType.Text, StringValues = ["x"] });

        await RunImportAsync(context, ConnectedSystemRunType.FullImport, [importObject]);

        Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(context.ConnectedSystem.Id), Is.EqualTo(1));
        Assert.That(held.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
    }

    [Test]
    public async Task FullImport_AllowObjectTypeChangesWithTheObjectReturnedTwice_RejectsBothAndKeepsTheHeldObjectAsync()
    {
        // Both copies are rejected (#2063), so nothing replaces the held object and retiring it would remove the
        // directory object from JIM altogether.
        var context = await ArrangeAsync(allowObjectTypeChanges: true);
        var held = SeedCso(context, context.UserType, Guid.NewGuid());

        await RunImportAsync(context, ConnectedSystemRunType.FullImport,
            [ImportObject(context.ComputerType, held.ExternalId), ImportObject(context.ComputerType, held.ExternalId)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ErroredRpeis(context).Select(r => r.ErrorType),
                Is.EqualTo(new[] { ActivityRunProfileExecutionItemErrorType.DuplicateObject, ActivityRunProfileExecutionItemErrorType.DuplicateObject }));
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(context.ConnectedSystem.Id), Is.EqualTo(1), "No replacement is created");
            Assert.That(held.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
        }
    }

    [Test]
    public async Task FullImport_ObjectReturnedTwiceUnderItsNewObjectType_IsRejectedAndTheHeldObjectKeptAsync()
    {
        // Refusal is the default, but the copies are duplicates first: neither is reported as an Object Type change,
        // and the held object is kept as it would be for a refusal.
        var context = await ArrangeAsync();
        var held = SeedCso(context, context.UserType, Guid.NewGuid());

        await RunImportAsync(context, ConnectedSystemRunType.FullImport,
            [ImportObject(context.ComputerType, held.ExternalId), ImportObject(context.ComputerType, held.ExternalId)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ErroredRpeis(context).Select(r => r.ErrorType),
                Is.EqualTo(new[] { ActivityRunProfileExecutionItemErrorType.DuplicateObject, ActivityRunProfileExecutionItemErrorType.DuplicateObject }));
            Assert.That(held.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
        }
    }

    [Test]
    public async Task FullImport_ObjectHeldUnderTwoOtherObjectTypes_IsRefusedAndBothHeldObjectsKeptAsync()
    {
        var context = await ArrangeAsync();
        var groupType = await CreateCsoTypeAsync(context.ConnectedSystem.Id, "group");
        var externalId = Guid.NewGuid();
        var heldAsUser = SeedCso(context, context.UserType, externalId);
        var heldAsGroup = SeedCso(context, groupType, externalId);

        await RunImportAsync(context, ConnectedSystemRunType.FullImport, [ImportObject(context.ComputerType, externalId)]);

        Assert.That(SingleErroredRpei(context).ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ObjectTypeChanged));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(heldAsUser.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
            Assert.That(heldAsGroup.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
        }
    }

    [Test]
    public async Task FullImport_ConnectorWithoutGloballyUniqueExternalIds_TreatsTheValueAsADifferentObjectAsync()
    {
        // A SQL view shares its table's keys (#1285): the same value under two Object Types is two objects there.
        var context = await ArrangeAsync(externalIdsUniqueAcrossObjectTypes: false);
        var held = SeedCso(context, context.UserType, Guid.NewGuid());

        await RunImportAsync(context, ConnectedSystemRunType.FullImport,
            [ImportObject(context.UserType, held.ExternalId), ImportObject(context.ComputerType, held.ExternalId)]);

        Assert.That(ErroredRpeis(context), Is.Empty);
        Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(context.ConnectedSystem.Id), Is.EqualTo(2));
        Assert.That(held.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
    }

    [Test]
    public async Task FullImport_NewObjectWithAnExternalIdNobodyHolds_ImportsNormallyAsync()
    {
        var context = await ArrangeAsync();
        var held = SeedCso(context, context.UserType, Guid.NewGuid());

        await RunImportAsync(context, ConnectedSystemRunType.FullImport,
            [ImportObject(context.UserType, held.ExternalId), ImportObject(context.ComputerType, Guid.NewGuid())]);

        Assert.That(ErroredRpeis(context), Is.Empty);
        Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(context.ConnectedSystem.Id), Is.EqualTo(2));
        Assert.That(held.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
    }

    [Test]
    public async Task DeltaImport_DeleteForAnObjectHeldUnderAnotherObjectType_RetiresTheExistingObjectAsync()
    {
        // The Connector resolves a deleted entry's type from its classes and the current selection, which can differ
        // from the type JIM holds it under; the delete used to find nothing and be dropped silently.
        var context = await ArrangeAsync();
        var held = SeedCso(context, context.UserType, Guid.NewGuid());

        await RunImportAsync(context, ConnectedSystemRunType.DeltaImport, [ImportObject(context.ComputerType, held.ExternalId, ObjectChangeType.Deleted)]);

        Assert.That(held.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
        var deleted = context.Activity.RunProfileExecutionItems.SingleOrDefault(r => r.ObjectChangeType == ObjectChangeType.Deleted);
        Assert.That(deleted, Is.Not.Null, "The deletion is recorded against the object it retired");
        Assert.That(deleted!.ConnectedSystemObjectId, Is.EqualTo(held.Cso.Id));
    }

    [Test]
    public async Task DeltaImport_DeleteForAnObjectHeldUnderAnotherObjectTypeWithoutGloballyUniqueExternalIds_IsIgnoredAsync()
    {
        // Where values are not unique across Object Types, a delete of one type's object must never reach another's.
        var context = await ArrangeAsync(externalIdsUniqueAcrossObjectTypes: false);
        var held = SeedCso(context, context.UserType, Guid.NewGuid());

        await RunImportAsync(context, ConnectedSystemRunType.DeltaImport, [ImportObject(context.ComputerType, held.ExternalId, ObjectChangeType.Deleted)]);

        Assert.That(held.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
    }

    #region Helpers

    private sealed record ImportContext(
        ConnectedSystem ConnectedSystem,
        ConnectedSystemObjectType UserType,
        ConnectedSystemObjectType ComputerType,
        bool AllowObjectTypeChanges)
    {
        public Activity Activity { get; set; } = null!;
    }

    private sealed record HeldObject(ConnectedSystemObject Cso, Guid ExternalId);

    private async Task<ImportContext> ArrangeAsync(bool externalIdsUniqueAcrossObjectTypes = true, bool allowObjectTypeChanges = false)
    {
        var connectedSystem = await CreateConnectedSystemAsync("Directory");
        connectedSystem.ConnectorDefinition.ExternalIdsUniqueAcrossObjectTypes = externalIdsUniqueAcrossObjectTypes;
        var userType = await CreateCsoTypeAsync(connectedSystem.Id, "user");
        var computerType = await CreateCsoTypeAsync(connectedSystem.Id, "computer");
        return new ImportContext(connectedSystem, userType, computerType, allowObjectTypeChanges);
    }

    private HeldObject SeedCso(ImportContext context, ConnectedSystemObjectType type, Guid externalId,
        ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal)
    {
        var externalIdAttribute = type.Attributes.Single(a => a.IsExternalId);
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = context.ConnectedSystem.Id,
            ConnectedSystem = context.ConnectedSystem,
            TypeId = type.Id,
            Type = type,
            ExternalIdAttributeId = externalIdAttribute.Id,
            Status = status,
            Created = DateTime.UtcNow
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = externalIdAttribute.Id,
            Attribute = externalIdAttribute,
            GuidValue = externalId,
            ConnectedSystemObject = cso
        });
        SyncRepo.SeedConnectedSystemObject(cso);
        return new HeldObject(cso, externalId);
    }

    private async Task RunImportAsync(ImportContext context, ConnectedSystemRunType runType, List<ConnectedSystemImportObject> importObjects,
        bool forceAllowObjectTypeChanges = false, Action<ConnectedSystemRunProfile>? configure = null)
    {
        var runProfile = await CreateRunProfileAsync(context.ConnectedSystem.Id, runType.ToString(), runType);
        runProfile.AllowObjectTypeChanges = forceAllowObjectTypeChanges || (context.AllowObjectTypeChanges && runType == ConnectedSystemRunType.FullImport);
        configure?.Invoke(runProfile);
        context.Activity = await CreateActivityAsync(context.ConnectedSystem.Id, runProfile, runType);

        var workerTask = new SynchronisationWorkerTask(context.ConnectedSystem.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = context.Activity
        };

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            new SinglePageConnector(importObjects), context.ConnectedSystem, runProfile, workerTask,
            new CancellationTokenSource());

        await processor.PerformImportAsync();
    }

    private static List<ActivityRunProfileExecutionItem> ErroredRpeis(ImportContext context) =>
        context.Activity.RunProfileExecutionItems
            .Where(r => r.ErrorType != null && r.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
            .ToList();

    private static ActivityRunProfileExecutionItem SingleErroredRpei(ImportContext context)
    {
        var errored = ErroredRpeis(context);
        Assert.That(errored, Has.Count.EqualTo(1), "Exactly one object changed Object Type");
        return errored[0];
    }

    private static ConnectedSystemImportObject ImportObject(ConnectedSystemObjectType type, Guid externalId,
        ObjectChangeType changeType = ObjectChangeType.NotSet)
    {
        var externalIdAttribute = type.Attributes.Single(a => a.IsExternalId);
        return new ConnectedSystemImportObject
        {
            ObjectType = type.Name,
            ChangeType = changeType,
            Attributes =
            [
                new ConnectedSystemImportObjectAttribute
                {
                    Name = externalIdAttribute.Name,
                    Type = AttributeDataType.Guid,
                    GuidValues = [externalId]
                }
            ]
        };
    }

    /// <summary>
    /// Returns one page of caller-supplied import objects, as a Connector reading a directory does.
    /// </summary>
    private sealed class SinglePageConnector(List<ConnectedSystemImportObject> importObjects) : IConnector, IConnectorImportUsingCalls
    {
        public string Name => "MockConnector";
        public string? Description => null;
        public string? Url => null;

        public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, string? persistedConnectorData, ILogger logger) { }

        public string? CloseImportConnection() => null;

        public Task<ConnectedSystemImportResult> ImportAsync(
            ConnectedSystem connectedSystem,
            ConnectedSystemRunProfile runProfile,
            List<ConnectedSystemPaginationToken> paginationTokens,
            string? persistedConnectorData,
            ILogger logger,
            CancellationToken cancellationToken,
            IConnectorProgress progress) =>
            Task.FromResult(new ConnectedSystemImportResult { ImportObjects = importObjects });
    }

    #endregion
}
