// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Workflow tests for Connected System Synchronised Deprovisioning (#809, Phase 2). Choosing "Deprovision
/// through synchronisation" fences the system (Status = Deleting) and ALWAYS queues a
/// <see cref="DeleteConnectedSystemWorkerTask"/> with <c>SynchronisedDeprovisioning</c> set; the executor
/// processes every Connected System Object through the Phase 1 obsoletion core (recall, surviving-contributor
/// re-election, Metaverse Object deletion rules, Pending Export staging), runs a by-provenance residue pass
/// per import Synchronisation Rule, and finishes with the existing deletion. The run checkpoints per batch so
/// a worker restart resumes without double-staging exports. Immediate deletion (the flag off) keeps today's
/// behaviour bit-for-bit.
/// </summary>
[TestFixture]
public class SynchronisedDeprovisioningWorkflowTests : WorkflowTestBase
{
    private const string HrDescription = "HR Description";
    private const string TrainingDescription = "Training Description";
    private const string SharedEmployeeId = "EMP001";

    private FailingSyncRepository _failingSyncRepo = null!;

    /// <summary>
    /// In-memory sync repository whose Metaverse Object batch update can be made to throw, simulating a
    /// database failure partway through the deprovisioning run.
    /// </summary>
    private sealed class FailingSyncRepository : JIM.InMemoryData.SyncRepository
    {
        public bool ThrowOnUpdateMetaverseObjects { get; set; }

        public override Task UpdateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
        {
            if (ThrowOnUpdateMetaverseObjects)
                throw new InvalidOperationException("Simulated database failure during the deprovisioning batch.");
            return base.UpdateMetaverseObjectsAsync(metaverseObjects);
        }
    }

    [SetUp]
    public void SetUpFailableSyncRepo()
    {
        // Replace the base harness's sync repository with the failable twin BEFORE any seeding, so every
        // test (not just the failure one) runs against the same repository instance the helpers seed.
        // The base Jim instance is NOT disposed here: disposing it would dispose the shared repository and
        // DbContext this replacement wraps; base tear-down disposes the replacement, which owns them both.
        _failingSyncRepo = new FailingSyncRepository();
        _failingSyncRepo.SetSyncOutcomeTrackingLevel(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        SyncRepo = _failingSyncRepo;
        Jim = new JimApplication(Repository, syncRepository: SyncRepo);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Queue path (DeleteAsync with synchronisedDeprovisioning: true) and the scheduling fence
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task DeleteAsync_SynchronisedDeprovisioning_FencesSystemAndQueuesTaskAsync()
    {
        var system = await CreateConnectedSystemAsync("HR Source");
        var user = await CreateAdministratorAsync();

        var result = await Jim.ConnectedSystems.DeleteAsync(system.Id, user, deleteChangeHistory: false,
            changeReason: "decommissioning", synchronisedDeprovisioning: true);

        var persistedSystem = await DbContext.ConnectedSystems.FindAsync(system.Id);
        var queuedTask = DbContext.DeleteConnectedSystemWorkerTasks.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedSystem, Is.Not.Null);
            Assert.That(persistedSystem!.Status, Is.EqualTo(ConnectedSystemStatus.Deleting),
                "the system must be fenced at queue time");
            Assert.That(queuedTask.ConnectedSystemId, Is.EqualTo(system.Id));
            Assert.That(queuedTask.SynchronisedDeprovisioning, Is.True,
                "the queued task must carry the deprovisioning mode flag");
            Assert.That(queuedTask.EvaluateMvoDeletionRules, Is.True);
            Assert.That(result.Outcome, Is.EqualTo(DeletionOutcome.QueuedAsBackgroundJob),
                "deprovisioning must ALWAYS queue; there is no synchronous small-system path");
            Assert.That(queuedTask.Activity, Is.Not.Null);
            Assert.That(queuedTask.Activity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Deprovision),
                "the Activity must record that the deprovisioning mode ran, distinct from an immediate Delete");
            Assert.That(queuedTask.Activity!.ChangeReason, Is.EqualTo("decommissioning"));
        }
    }

    [Test]
    public async Task CreateWorkerTaskAsync_RunProfileForDeletingSystem_RefusesAsync()
    {
        // The fence must exclude a Deleting system from synchronisation: a run profile execution queued
        // against it mid-deprovisioning would race the run.
        var system = await CreateConnectedSystemAsync("HR Source");
        var runProfile = await CreateRunProfileAsync(system.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var persistedSystem = await DbContext.ConnectedSystems.FindAsync(system.Id);
        persistedSystem!.Status = ConnectedSystemStatus.Deleting;
        await DbContext.SaveChangesAsync();

        var result = await Jim.Tasking.CreateWorkerTaskAsync(new SynchronisationWorkerTask
        {
            ConnectedSystemId = system.Id,
            ConnectedSystemRunProfileId = runProfile.Id,
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedById = Guid.NewGuid(),
            InitiatedByName = "Test Administrator"
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False, "a Deleting system must refuse run profile executions");
            Assert.That(result.ErrorMessage, Does.Contain("being deleted"));
            Assert.That(DbContext.SynchronisationWorkerTasks.Any(), Is.False, "nothing must be queued");
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Executor: Pass A per-object semantics
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ExecuteSynchronisedDeprovisioningAsync_SurvivingContributor_ReElectsAndStagesExportAsync()
    {
        var ctx = await SetUpTwoContributorsWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training!);
        var targetCso = SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId)?.StringValue, Is.EqualTo(HrDescription),
            "precondition: HR (priority 1) contributes Description");

        var (task, activity) = await FenceSystemAndBuildTaskAsync(ctx.Hr);
        var result = await Jim.ConnectedSystems.ExecuteSynchronisedDeprovisioningAsync(task);

        mvo = SyncRepo.MetaverseObjects.Values.Single();
        var reElected = GetAttributeValue(mvo, ctx.MvDescriptionAttributeId);
        var stagedPendingExport = SyncRepo.PendingExports.Values
            .SingleOrDefault(pe => pe.ConnectedSystemObjectId == targetCso.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reElected, Is.Not.Null,
                "Description must not blank: the surviving Training contributor must be re-elected");
            Assert.That(reElected!.StringValue, Is.EqualTo(TrainingDescription));
            Assert.That(reElected!.ContributedBySyncRuleId, Is.EqualTo(ctx.TrainingImportRuleId),
                "the re-elected value must carry the surviving Training rule's provenance");
            Assert.That(reElected!.ContributedBySystemId, Is.EqualTo(ctx.Training!.Id),
                "the re-elected value's contributing system must move to the survivor");
            Assert.That(GetAttributeValue(mvo, ctx.MvDisplayNameAttributeId), Is.Null,
                "DisplayName had no surviving contributor and must be cleared");

            Assert.That(stagedPendingExport, Is.Not.Null,
                "the re-election and clears must stage a Pending Export for the mapped target system");
            Assert.That(stagedPendingExport!.AttributeValueChanges
                    .Any(c => c.AttributeId == ctx.TargetDescriptionAttribute.Id && c.StringValue == TrainingDescription),
                Is.True, "the target's Description must be staged with the surviving contributor's value");

            Assert.That(SyncRepo.ConnectedSystemObjects.Values.Any(c => c.ConnectedSystemId == ctx.Hr.Id),
                Is.False, "every HR Connected System Object must be deleted");
            Assert.That(await DbContext.ConnectedSystems.FindAsync(ctx.Hr.Id), Is.Null,
                "the Connected System must be deleted as the run's final step");

            Assert.That(activity.ObjectsToProcess, Is.EqualTo(1));
            Assert.That(activity.ObjectsProcessed, Is.EqualTo(1));
            Assert.That(DbContext.ActivityRunProfileExecutionItems.Count(rpei => rpei.ActivityId == activity.Id),
                Is.EqualTo(1), "one RPEI per processed Connected System Object");
            Assert.That(activity.Message, Is.Not.Null.And.Contain("re-elected"),
                "the Activity must complete with summary statistics");

            Assert.That(result.ConnectedSystemObjectsProcessed, Is.EqualTo(1));
            Assert.That(result.AttributesReElected, Is.EqualTo(1), "Description re-elected to Training");
            Assert.That(result.PendingExportsStaged, Is.GreaterThanOrEqualTo(1));
        }
    }

    [Test]
    public async Task ExecuteSynchronisedDeprovisioningAsync_SoleContributor_ClearsValuesAndStagesRemovalAsync()
    {
        var ctx = await SetUpSoleContributorWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        var targetCso = SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);

        var (task, activity) = await FenceSystemAndBuildTaskAsync(ctx.Hr);
        var result = await Jim.ConnectedSystems.ExecuteSynchronisedDeprovisioningAsync(task);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var stagedPendingExport = SyncRepo.PendingExports.Values
            .SingleOrDefault(pe => pe.ConnectedSystemObjectId == targetCso.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId), Is.Null,
                "the sole-contributor Description must be cleared (No Contributor)");
            Assert.That(GetAttributeValue(mvo, ctx.MvDisplayNameAttributeId), Is.Null,
                "the sole-contributor DisplayName must be cleared (No Contributor)");

            Assert.That(stagedPendingExport, Is.Not.Null,
                "the clears must stage a Pending Export removing the values from the mapped target");
            Assert.That(stagedPendingExport!.AttributeValueChanges
                    .Any(c => c.AttributeId == ctx.TargetDescriptionAttribute.Id && c.StringValue == null),
                Is.True, "the target's Description must be staged as a null-clearing update");

            Assert.That(await DbContext.ConnectedSystems.FindAsync(ctx.Hr.Id), Is.Null,
                "the Connected System must be deleted as the run's final step");

            Assert.That(activity.Message, Is.Not.Null.And.Contain("cleared"),
                "the Activity must complete with summary statistics");
            Assert.That(result.ConnectedSystemObjectsProcessed, Is.EqualTo(1));
            Assert.That(result.AttributesReElected, Is.Zero);
            Assert.That(result.AttributesCleared, Is.GreaterThanOrEqualTo(3),
                "DisplayName, EmployeeId and Description all had no surviving contributor");
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Executor: Metaverse Object deletion rules
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ExecuteSynchronisedDeprovisioningAsync_LastConnectorImmediateRule_DeletesMetaverseObjectAsync()
    {
        // Sole HR contributor, NO export target: disconnecting the HR object leaves no remaining connector,
        // and the type has no grace period, so the Metaverse Object must be deleted immediately, exactly as
        // a synchronisation disconnect would do.
        var (hrSystem, _, _, _, _) = await SetUpHrContributorAsync();
        await RunFullSyncAsync(hrSystem);
        Assert.That(SyncRepo.MetaverseObjects, Has.Count.EqualTo(1), "precondition: the HR object projected");

        var (task, _) = await FenceSystemAndBuildTaskAsync(hrSystem);
        var result = await Jim.ConnectedSystems.ExecuteSynchronisedDeprovisioningAsync(task);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.MetaverseObjects, Is.Empty,
                "the last-connector Metaverse Object must be deleted immediately (no grace period)");
            Assert.That(await DbContext.ConnectedSystems.FindAsync(hrSystem.Id), Is.Null,
                "the Connected System must be deleted as the run's final step");
            Assert.That(result.MetaverseObjectsDeleted, Is.EqualTo(1));
            Assert.That(result.MetaverseObjectsMarkedForDeletion, Is.Zero);
        }
    }

    [Test]
    public async Task ExecuteSynchronisedDeprovisioningAsync_GracePeriodRule_MarksMetaverseObjectNotDeletedAsync()
    {
        var (hrSystem, _, mvType, mvDescriptionAttr, mvDisplayNameAttr) = await SetUpHrContributorAsync();
        await RunFullSyncAsync(hrSystem);
        // The executor evaluates the grace period off the Metaverse Object's Type instance; the harness
        // shares one instance between the DbContext and the sync store, so mutating it in place suffices.
        mvType.DeletionGracePeriod = TimeSpan.FromDays(7);

        var (task, _) = await FenceSystemAndBuildTaskAsync(hrSystem);
        var result = await Jim.ConnectedSystems.ExecuteSynchronisedDeprovisioningAsync(task);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.LastConnectorDisconnectedDate, Is.Not.Null,
                "the grace-period Metaverse Object must be marked for deferred deletion, not deleted");
            Assert.That(mvo.DeletionTriggeredBySystemId, Is.EqualTo(hrSystem.Id),
                "the deleted system must be recorded as the deletion trigger");
            Assert.That(GetAttributeValue(mvo, mvDisplayNameAttr.Id), Is.Not.Null,
                "single-source values must be frozen (preserved) for the grace window, not cleared");
            Assert.That(GetAttributeValue(mvo, mvDescriptionAttr.Id), Is.Not.Null,
                "single-source values must be frozen (preserved) for the grace window, not cleared");
            Assert.That(await DbContext.ConnectedSystems.FindAsync(hrSystem.Id), Is.Null,
                "the Connected System must still be deleted as the run's final step");
            Assert.That(result.MetaverseObjectsDeleted, Is.Zero);
            Assert.That(result.MetaverseObjectsMarkedForDeletion, Is.EqualTo(1));
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Executor: Pass B residue recall
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ExecuteSynchronisedDeprovisioningAsync_ResidueValueWithNoBackingCso_RecallsByProvenanceAsync()
    {
        // A value stranded by an earlier connector-space clear: its Metaverse Object holds no Connected
        // System Object of the deleted system, so the per-object pass cannot reach it; the residue pass
        // must recall it by provenance BEFORE the system's Synchronisation Rules are deleted (deletion's
        // ON DELETE SET NULL would sever the provenance the recall selects on).
        var ctx = await SetUpSoleContributorWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);

        var mvType = SyncRepo.MetaverseObjects.Values.Single().Type!;
        // First, not Single: EF relationship fix-up can add the manually-appended Description attribute to
        // the type's collection a second time in the harness.
        var descriptionAttribute = mvType.Attributes.First(a => a.Id == ctx.MvDescriptionAttributeId);
        var strandedMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType, Created = DateTime.UtcNow };
        strandedMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = strandedMvo,
            Attribute = descriptionAttribute,
            AttributeId = descriptionAttribute.Id,
            StringValue = "Stranded value",
            ContributedBySyncRuleId = ctx.HrImportRule.Id,
            ContributedBySystemId = ctx.Hr.Id
        });
        SyncRepo.SeedMetaverseObject(strandedMvo);

        var (task, _) = await FenceSystemAndBuildTaskAsync(ctx.Hr);
        var result = await Jim.ConnectedSystems.ExecuteSynchronisedDeprovisioningAsync(task);

        var survivingStranded = SyncRepo.MetaverseObjects.Values.SingleOrDefault(m => m.Id == strandedMvo.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(survivingStranded, Is.Not.Null, "the stranded Metaverse Object itself survives");
            Assert.That(GetAttributeValue(survivingStranded!, ctx.MvDescriptionAttributeId), Is.Null,
                "the residue pass must withdraw the stranded value by provenance");
            Assert.That(result.ResidueValuesRecalled, Is.GreaterThanOrEqualTo(1),
                "the run must account for the residue recall");
            Assert.That(await DbContext.ConnectedSystems.FindAsync(ctx.Hr.Id), Is.Null,
                "the Connected System must be deleted as the run's final step");
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Executor: failure and resumability
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ExecuteSynchronisedDeprovisioningAsync_FailurePartway_SystemStaysFencedAndConsistentAsync()
    {
        var ctx = await SetUpSoleContributorWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);

        var (task, activity) = await FenceSystemAndBuildTaskAsync(ctx.Hr);
        _failingSyncRepo.ThrowOnUpdateMetaverseObjects = true;

        // The executor must fail fast and hard; the Worker's dispatch boundary then fails the Activity,
        // mirrored here (the same contract Worker.cs applies to every queued task type).
        InvalidOperationException? thrown = null;
        try
        {
            await Jim.ConnectedSystems.ExecuteSynchronisedDeprovisioningAsync(task);
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }
        Assert.That(thrown, Is.Not.Null, "a mid-batch persistence failure must propagate, never be swallowed");
        await Jim.Activities.FailActivityWithErrorAsync(activity, thrown!);

        var persistedSystem = await DbContext.ConnectedSystems.FindAsync(ctx.Hr.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedSystem, Is.Not.Null, "the Connected System must survive a failed run");
            Assert.That(persistedSystem!.Status, Is.EqualTo(ConnectedSystemStatus.Deleting),
                "the fence must hold after a failure so nothing synchronises a half-deprovisioned system");
            Assert.That(SyncRepo.ConnectedSystemObjects.Values.Any(c => c.ConnectedSystemId == ctx.Hr.Id),
                Is.True, "the unflushed batch's Connected System Objects must survive for the retry");
            Assert.That(task.CheckpointPhase, Is.Null,
                "no checkpoint may be recorded for a batch that did not complete");
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(activity.ErrorMessage, Does.Contain("Simulated database failure"));
        }
    }

    [Test]
    public async Task ExecuteSynchronisedDeprovisioningAsync_ResumeFromCheckpoint_SkipsCompletedWorkWithoutDoubleStagingAsync()
    {
        // Two HR objects, each projecting its own Metaverse Object, both provisioned to the target system.
        // Simulate a worker crash after the first object's batch completed: its Connected System Object is
        // deleted, its values are recalled, its export is staged, and the checkpoint records it. The resumed
        // run must process ONLY the second object, and the first object's target must not gain a second
        // staged export.
        var ctx = await SetUpTwoObjectsWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        SimulateAllTargetExportsExecuted(ctx.Target);

        var hrCsos = SyncRepo.ConnectedSystemObjects.Values
            .Where(c => c.ConnectedSystemId == ctx.Hr.Id)
            .OrderBy(c => c.Id)
            .ToList();
        Assert.That(hrCsos, Has.Count.EqualTo(2), "precondition: two HR Connected System Objects");
        var processedCso = hrCsos[0];
        var processedMvo = processedCso.MetaverseObject!;
        var processedTargetCso = SyncRepo.ConnectedSystemObjects.Values
            .Single(c => c.ConnectedSystemId == ctx.Target.Id && c.MetaverseObjectId == processedMvo.Id);

        // Simulate the first object's completed batch: values recalled, join broken, CSO deleted,
        // export staged, checkpoint persisted.
        foreach (var recalled in processedMvo.AttributeValues.Where(av => av.ContributedBySystemId == ctx.Hr.Id).ToList())
            processedMvo.AttributeValues.Remove(recalled);
        processedMvo.ConnectedSystemObjects.Remove(processedCso);
        processedCso.MetaverseObject = null;
        processedCso.MetaverseObjectId = null;
        await SyncRepo.DeleteConnectedSystemObjectsAsync(new List<ConnectedSystemObject> { processedCso });
        var preStagedExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ctx.Target.Id,
            ConnectedSystemObjectId = processedTargetCso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending
        };
        await SyncRepo.CreatePendingExportsAsync(new List<PendingExport> { preStagedExport });

        var (task, activity) = await FenceSystemAndBuildTaskAsync(ctx.Hr);
        task.CheckpointPhase = SynchronisedDeprovisioningPhase.ObjectPass;
        task.CheckpointConnectedSystemObjectId = processedCso.Id;

        var result = await Jim.ConnectedSystems.ExecuteSynchronisedDeprovisioningAsync(task);

        var exportsForProcessedTarget = SyncRepo.PendingExports.Values
            .Where(pe => pe.ConnectedSystemObjectId == processedTargetCso.Id)
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ConnectedSystemObjectsProcessed, Is.EqualTo(1),
                "the resumed run must process only the object after the checkpoint");
            Assert.That(exportsForProcessedTarget, Has.Count.EqualTo(1),
                "the already-processed object's target must not gain a second staged export");
            Assert.That(DbContext.ActivityRunProfileExecutionItems.Count(rpei => rpei.ActivityId == activity.Id),
                Is.EqualTo(1), "only the resumed object gets a per-object result");
            Assert.That(await DbContext.ConnectedSystems.FindAsync(ctx.Hr.Id), Is.Null,
                "the resumed run must still complete the deletion");
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Immediate mode characterisation
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task DeleteAsync_ImmediateMode_DoesNotQueueDeprovisioningTaskAsync()
    {
        // Characterisation: the flag left at its default keeps today's decision shape. The in-memory
        // harness cannot execute the full raw-SQL bulk delete, so this pins the decision plumbing: no
        // worker task carries the deprovisioning flag, and small systems stay on the synchronous path
        // (asserted via the absence of any queued task).
        var system = await CreateConnectedSystemAsync("HR Source");
        var user = await CreateAdministratorAsync();

        await Jim.ConnectedSystems.DeleteAsync(system.Id, user);

        Assert.That(DbContext.DeleteConnectedSystemWorkerTasks.Any(t => t.SynchronisedDeprovisioning), Is.False,
            "immediate mode must never queue a deprovisioning task");
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Topology builders
    // -----------------------------------------------------------------------------------------------------------------

    private sealed record DeprovisioningContext(
        ConnectedSystem Hr,
        ConnectedSystem? Training,
        SyncRule HrImportRule,
        int TrainingImportRuleId,
        int MvDescriptionAttributeId,
        int MvDisplayNameAttributeId,
        ConnectedSystem Target,
        ConnectedSystemObjectTypeAttribute TargetDescriptionAttribute,
        ConnectedSystemObjectTypeAttribute TargetDisplayNameAttribute);

    private static MetaverseObjectAttributeValue? GetAttributeValue(MetaverseObject mvo, int attributeId) =>
        mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == attributeId && !av.NullValue);

    private async Task<MetaverseObject> CreateAdministratorAsync()
    {
        var mvType = await CreateMvObjectTypeAsync("Administrator");
        var user = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = mvType,
            Created = DateTime.UtcNow,
            CachedDisplayName = "Test Administrator",
            Origin = MetaverseObjectOrigin.Internal
        };
        DbContext.MetaverseObjects.Add(user);
        await DbContext.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// Fences the system with the Deleting status (as the queue step does) and builds the worker task with
    /// its Activity, mirroring what TaskingServer records at queue time.
    /// </summary>
    private async Task<(DeleteConnectedSystemWorkerTask Task, Activity Activity)> FenceSystemAndBuildTaskAsync(ConnectedSystem system)
    {
        // Detach processor-modified entities first (the same guard the base harness's helpers apply): the
        // full syncs above leave tracked entities in states the in-memory store no longer recognises.
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Modified).ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;

        var persistedSystem = await DbContext.ConnectedSystems.FindAsync(system.Id);
        persistedSystem!.Status = ConnectedSystemStatus.Deleting;
        await DbContext.SaveChangesAsync();

        var activity = new Activity
        {
            TargetName = system.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Deprovision,
            Status = ActivityStatus.InProgress,
            Executed = DateTime.UtcNow
            // ConnectedSystemId deliberately not set: the system is deleted before the Activity completes.
        };
        DbContext.Activities.Add(activity);
        await DbContext.SaveChangesAsync();

        var task = new DeleteConnectedSystemWorkerTask(system.Id, evaluateMvoDeletionRules: true, deleteChangeHistory: false)
        {
            SynchronisedDeprovisioning = true,
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedById = Guid.NewGuid(),
            InitiatedByName = "Test Administrator",
            Activity = activity
        };
        return (task, activity);
    }

    /// <summary>
    /// Sole-contributor topology plus a downstream export target: HR projects and flows DisplayName,
    /// EmployeeId and Description; a target system maps DisplayName and Description outbound.
    /// </summary>
    private async Task<DeprovisioningContext> SetUpSoleContributorWithExportTargetAsync()
    {
        var (hrSystem, hrImportRule, mvType, mvDescriptionAttr, mvDisplayNameAttr) = await SetUpHrContributorAsync();
        var target = await AddExportTargetAsync(mvType, mvDisplayNameAttr, mvDescriptionAttr);
        return new DeprovisioningContext(hrSystem, null, hrImportRule, 0, mvDescriptionAttr.Id,
            mvDisplayNameAttr.Id, target.System, target.DescriptionAttribute, target.DisplayNameAttribute);
    }

    /// <summary>
    /// Two-contributor topology plus a downstream export target: HR (Description priority 1, projects) and
    /// Training (Description priority 2, joins on EmployeeId).
    /// </summary>
    private async Task<DeprovisioningContext> SetUpTwoContributorsWithExportTargetAsync()
    {
        var (hrSystem, hrImportRule, mvType, mvDescriptionAttr, mvDisplayNameAttr) = await SetUpHrContributorAsync();
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var trainingSystem = await CreateConnectedSystemAsync("Training Source");
        var trainingExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var trainingEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var trainingDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "TrainingDescription", Type = AttributeDataType.Text, Selected = true };
        var trainingType = await CreateCsoTypeAsync(trainingSystem.Id, "TrainingRecord",
            new List<ConnectedSystemObjectTypeAttribute> { trainingExternalIdAttr, trainingEmployeeIdAttr, trainingDescriptionAttr });

        var trainingImportRule = await CreateImportSyncRuleAsync(trainingSystem.Id, trainingType, mvType, "Training Import", enableProjection: false);
        trainingImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(trainingImportRule, mvDescriptionAttr, trainingDescriptionAttr, priority: 2));
        trainingImportRule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = trainingImportRule,
            SyncRuleId = trainingImportRule.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new() { Order = 0, ConnectedSystemAttribute = trainingEmployeeIdAttr, ConnectedSystemAttributeId = trainingEmployeeIdAttr.Id }
            }
        });
        await DbContext.SaveChangesAsync();

        var trainingCso = await CreateCsoAsync(trainingSystem.Id, trainingType, "unused", SharedEmployeeId);
        trainingCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = trainingDescriptionAttr.Id, Attribute = trainingDescriptionAttr, StringValue = TrainingDescription, ConnectedSystemObject = trainingCso
        });

        var target = await AddExportTargetAsync(mvType, mvDisplayNameAttr, mvDescriptionAttr);
        return new DeprovisioningContext(hrSystem, trainingSystem, hrImportRule, trainingImportRule.Id,
            mvDescriptionAttr.Id, mvDisplayNameAttr.Id, target.System, target.DescriptionAttribute, target.DisplayNameAttribute);
    }

    /// <summary>
    /// Sole-contributor topology with TWO HR objects (distinct Metaverse Objects) and an export target, for
    /// the resume-from-checkpoint test.
    /// </summary>
    private async Task<DeprovisioningContext> SetUpTwoObjectsWithExportTargetAsync()
    {
        var (hrSystem, hrImportRule, mvType, mvDescriptionAttr, mvDisplayNameAttr) = await SetUpHrContributorAsync();

        var hrType = SyncRepo.ConnectedSystemObjects.Values
            .First(c => c.ConnectedSystemId == hrSystem.Id).Type;
        var hrDescriptionAttr = hrType.Attributes.First(a => a.Name == "HrDescription");
        var secondCso = await CreateCsoAsync(hrSystem.Id, hrType, "Jane Doe", "EMP002");
        secondCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = hrDescriptionAttr.Id, Attribute = hrDescriptionAttr, StringValue = HrDescription, ConnectedSystemObject = secondCso
        });

        var target = await AddExportTargetAsync(mvType, mvDisplayNameAttr, mvDescriptionAttr);
        return new DeprovisioningContext(hrSystem, null, hrImportRule, 0, mvDescriptionAttr.Id,
            mvDisplayNameAttr.Id, target.System, target.DescriptionAttribute, target.DisplayNameAttribute);
    }

    private async Task<(ConnectedSystem HrSystem, SyncRule HrImportRule, MetaverseObjectType MvType, MetaverseAttribute MvDescriptionAttr, MetaverseAttribute MvDisplayNameAttr)> SetUpHrContributorAsync()
    {
        var hrSystem = await CreateConnectedSystemAsync("HR Source");
        var hrExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var hrDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var hrEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var hrDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "HrDescription", Type = AttributeDataType.Text, Selected = true };
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "HrUser",
            new List<ConnectedSystemObjectTypeAttribute> { hrExternalIdAttr, hrDisplayNameAttr, hrEmployeeIdAttr, hrDescriptionAttr });

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvDescriptionAttr = new MetaverseAttribute
        {
            Name = "Description",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(mvDescriptionAttr);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(mvDescriptionAttr);

        var hrImportRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvDisplayNameAttr, hrDisplayNameAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvEmployeeIdAttr, hrEmployeeIdAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvDescriptionAttr, hrDescriptionAttr, priority: 1));
        await DbContext.SaveChangesAsync();

        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", SharedEmployeeId);
        hrCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = hrDescriptionAttr.Id, Attribute = hrDescriptionAttr, StringValue = HrDescription, ConnectedSystemObject = hrCso
        });

        return (hrSystem, hrImportRule, mvType, mvDescriptionAttr, mvDisplayNameAttr);
    }

    private sealed record ExportTarget(
        ConnectedSystem System,
        ConnectedSystemObjectTypeAttribute DescriptionAttribute,
        ConnectedSystemObjectTypeAttribute DisplayNameAttribute);

    private async Task<ExportTarget> AddExportTargetAsync(
        MetaverseObjectType mvType, MetaverseAttribute mvDisplayNameAttr, MetaverseAttribute mvDescriptionAttr)
    {
        var targetSystem = await CreateConnectedSystemAsync("AD Target");
        var targetExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var targetDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var targetDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "Description", Type = AttributeDataType.Text, Selected = true };
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "TargetUser",
            new List<ConnectedSystemObjectTypeAttribute> { targetExternalIdAttr, targetDisplayNameAttr, targetDescriptionAttr });

        var exportRule = new SyncRule
        {
            ConnectedSystemId = targetSystem.Id,
            Name = "AD Export",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemObjectTypeId = targetType.Id,
            ConnectedSystemObjectType = targetType,
            MetaverseObjectTypeId = mvType.Id,
            MetaverseObjectType = mvType,
            ProvisionToConnectedSystem = true
        };
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvDisplayNameAttr, MetaverseAttributeId = mvDisplayNameAttr.Id } }
        });
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDescriptionAttr,
            TargetConnectedSystemAttributeId = targetDescriptionAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvDescriptionAttr, MetaverseAttributeId = mvDescriptionAttr.Id } }
        });

        DbContext.SyncRules.Add(exportRule);
        await DbContext.SaveChangesAsync();
        SyncRepo.SeedSyncRule(exportRule);

        return new ExportTarget(targetSystem, targetDescriptionAttr, targetDisplayNameAttr);
    }

    /// <summary>
    /// Simulates the provisioning export having been executed against the target Connected System: marks the
    /// provisioned target CSO Normal, writes the exported values onto it, and clears all Pending Exports so
    /// assertions only see exports staged by the deprovisioning run under test.
    /// </summary>
    private ConnectedSystemObject SimulateTargetExportExecuted(DeprovisioningContext ctx, string displayName, string description)
    {
        var targetCso = SyncRepo.ConnectedSystemObjects.Values.First(c => c.ConnectedSystemId == ctx.Target.Id);
        targetCso.Status = ConnectedSystemObjectStatus.Normal;
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = ctx.TargetDisplayNameAttribute.Id,
            Attribute = ctx.TargetDisplayNameAttribute,
            StringValue = displayName,
            ConnectedSystemObject = targetCso
        });
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = ctx.TargetDescriptionAttribute.Id,
            Attribute = ctx.TargetDescriptionAttribute,
            StringValue = description,
            ConnectedSystemObject = targetCso
        });
        SyncRepo.ClearAllPendingExports();
        return targetCso;
    }

    /// <summary>
    /// Marks every provisioned CSO of the target system Normal and clears all Pending Exports, simulating
    /// their provisioning exports having executed.
    /// </summary>
    private void SimulateAllTargetExportsExecuted(ConnectedSystem targetSystem)
    {
        foreach (var targetCso in SyncRepo.ConnectedSystemObjects.Values.Where(c => c.ConnectedSystemId == targetSystem.Id))
            targetCso.Status = ConnectedSystemObjectStatus.Normal;
        SyncRepo.ClearAllPendingExports();
    }

    private static SyncRuleMapping BuildDirectImportMapping(SyncRule rule, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source, int priority = int.MaxValue)
    {
        return new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            Priority = priority,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        };
    }

    private async Task RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new JIM.Application.Servers.SyncEngine(), new JIM.Application.Servers.SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
    }
}
