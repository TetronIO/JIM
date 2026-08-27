// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Workflow tests for the Synchronisation Rule deletion recall choice (#1537, Phase 2). Deleting a rule that
/// still contributes Metaverse attribute values must offer recall-or-keep: recall (the default) disables the
/// rule and queues a <see cref="DeleteSyncRuleWorkerTask"/> whose executor withdraws the values by provenance
/// (re-electing surviving contributors, staging Pending Exports) and deletes the rule as its final step; keep,
/// or a rule with no contributed values, deletes synchronously exactly as before.
/// </summary>
[TestFixture]
public class SyncRuleDeletionRecallWorkflowTests : WorkflowTestBase
{
    private const string HrDescription = "HR Description";
    private const string TrainingDescription = "Training Description";
    private const string SharedEmployeeId = "EMP001";

    private FailingSyncRepository _failingSyncRepo = null!;

    /// <summary>
    /// In-memory sync repository whose Metaverse Object batch update can be made to throw, simulating a
    /// database failure partway through the recall task.
    /// </summary>
    private sealed class FailingSyncRepository : JIM.InMemoryData.SyncRepository
    {
        public bool ThrowOnUpdateMetaverseObjects { get; set; }

        public override Task UpdateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
        {
            if (ThrowOnUpdateMetaverseObjects)
                throw new InvalidOperationException("Simulated database failure during the recall batch.");
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
    // Deletion choice (DeleteSyncRuleAsync)
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task DeleteSyncRuleAsync_RecallChosenWithContributedValues_DisablesRuleAndQueuesRecallTaskAsync()
    {
        var ctx = await SetUpChoiceTopologyAsync(seedContributedValues: true);

        var result = await Jim.ConnectedSystems.DeleteSyncRuleAsync(ctx.Rule, ctx.User, "decommissioning HR");

        var persistedRule = await DbContext.SyncRules.FindAsync(ctx.Rule.Id);
        var queuedTask = DbContext.DeleteSyncRuleWorkerTasks.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedRule, Is.Not.Null, "the rule must NOT be deleted synchronously when a recall queues");
            Assert.That(persistedRule!.Enabled, Is.False, "the rule must be disabled the moment the recall queues");
            Assert.That(persistedRule!.DisabledReason,
                Is.EqualTo("Deletion in progress: contributed attribute values are being recalled."));
            Assert.That(queuedTask.SyncRuleId, Is.EqualTo(ctx.Rule.Id));
            Assert.That(queuedTask.RecallContributedValues, Is.True);
            Assert.That(result.RecallQueued, Is.True);
            Assert.That(result.RecallActivityId, Is.EqualTo(queuedTask.Activity.Id),
                "the result must carry the queued task's Activity id so callers can link to it");
            Assert.That(result.AffectedValueCount, Is.EqualTo(3), "two Description values plus one Mobile value");
            Assert.That(result.AffectedObjectCount, Is.EqualTo(2), "two distinct Metaverse Objects hold the values");
        }
    }

    [Test]
    public async Task DeleteSyncRuleAsync_RecallWithNoInitiator_QueuesSystemAttributedTaskAsync()
    {
        // An internal caller with no principal must not queue a NotSet-initiated task: the worker's dispatch
        // refuses those, which would leave the task stuck, the Activity never completed, and the rule
        // disabled forever.
        var ctx = await SetUpChoiceTopologyAsync(seedContributedValues: true);

        var result = await Jim.ConnectedSystems.DeleteSyncRuleAsync(ctx.Rule, initiatedBy: null);

        var queuedTask = DbContext.DeleteSyncRuleWorkerTasks.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RecallQueued, Is.True);
            Assert.That(queuedTask.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
            Assert.That(queuedTask.InitiatedByName, Is.EqualTo("System"));
        }
    }

    [Test]
    public async Task DeleteSyncRuleAsync_KeepChosenWithContributedValues_DeletesSynchronouslyAndRecordsChoiceAsync()
    {
        var ctx = await SetUpChoiceTopologyAsync(seedContributedValues: true);

        var result = await Jim.ConnectedSystems.DeleteSyncRuleAsync(ctx.Rule, ctx.User, "migrating authority",
            recallContributedValues: false);

        var persistedRule = await DbContext.SyncRules.FindAsync(ctx.Rule.Id);
        var survivingValues = DbContext.MetaverseObjectAttributeValues.ToList();
        var deletionActivity = DbContext.Activities.Single(a =>
            a.TargetType == ActivityTargetType.SynchronisationRule &&
            a.TargetOperationType == ActivityTargetOperationType.Delete);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedRule, Is.Null, "keep deletes the rule synchronously, exactly as today");
            Assert.That(DbContext.DeleteSyncRuleWorkerTasks.Any(), Is.False, "keep must queue nothing");
            Assert.That(result.RecallQueued, Is.False);
            Assert.That(result.RecallActivityId, Is.Null);
            Assert.That(survivingValues, Has.Count.EqualTo(3), "the contributed values must remain in place");
            Assert.That(survivingValues.Select(v => v.ContributedBySyncRuleId), Has.All.Null,
                "provenance must be nulled by the rule deletion (the ON DELETE SET NULL end state)");
            Assert.That(deletionActivity.Message, Does.Contain("kept"),
                "the deletion Activity must record that keep was chosen so the choice is auditable");
        }
    }

    [Test]
    public async Task DeleteSyncRuleAsync_RecallChosenWithNoContributedValues_DeletesSynchronouslyAsync()
    {
        var ctx = await SetUpChoiceTopologyAsync(seedContributedValues: false);

        var result = await Jim.ConnectedSystems.DeleteSyncRuleAsync(ctx.Rule, ctx.User, "never used");

        var persistedRule = await DbContext.SyncRules.FindAsync(ctx.Rule.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedRule, Is.Null, "no contributed values means the synchronous delete path, as today");
            Assert.That(DbContext.DeleteSyncRuleWorkerTasks.Any(), Is.False, "nothing must be queued");
            Assert.That(result.RecallQueued, Is.False);
            Assert.That(result.RecallActivityId, Is.Null);
            Assert.That(result.AffectedValueCount, Is.Zero);
            Assert.That(result.AffectedObjectCount, Is.Zero);
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Recall task execution (ExecuteSyncRuleDeletionRecallAsync)
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ExecuteSyncRuleDeletionRecallAsync_SurvivingContributor_ReElectsStagesExportAndDeletesRuleAsync()
    {
        var ctx = await SetUpTwoContributorsWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training!);
        var targetCso = SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId)?.StringValue, Is.EqualTo(HrDescription),
            "precondition: HR (priority 1) contributes Description");

        var (task, activity) = await DisableRuleAndBuildTaskAsync(ctx.HrImportRule);
        var recallResult = await Jim.ConnectedSystems.ExecuteSyncRuleDeletionRecallAsync(task);

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
            Assert.That(GetAttributeValue(mvo, ctx.MvDisplayNameAttributeId), Is.Null,
                "DisplayName had no surviving contributor and must be cleared");

            Assert.That(stagedPendingExport, Is.Not.Null,
                "the re-election and clears must stage a Pending Export for the mapped target system");
            Assert.That(stagedPendingExport!.AttributeValueChanges
                    .Any(c => c.AttributeId == ctx.TargetDescriptionAttribute.Id && c.StringValue == TrainingDescription),
                Is.True, "the target's Description must be staged with the surviving contributor's value");

            Assert.That(await DbContext.SyncRules.FindAsync(ctx.HrImportRule.Id), Is.Null,
                "the rule must be deleted as the task's final step");

            Assert.That(activity.ObjectsToProcess, Is.EqualTo(1));
            Assert.That(activity.ObjectsProcessed, Is.EqualTo(1));
            Assert.That(activity.Message, Is.Not.Null.And.Contain("re-elected"),
                "the Activity must complete with summary statistics");
            Assert.That(DbContext.ActivityRunProfileExecutionItems.Count(rpei => rpei.ActivityId == activity.Id),
                Is.EqualTo(1), "one RPEI per affected Metaverse Object");

            Assert.That(recallResult.MetaverseObjectsProcessed, Is.EqualTo(1));
            Assert.That(recallResult.AttributesReElected, Is.EqualTo(1), "Description re-elected to Training");
            Assert.That(recallResult.AttributesCleared, Is.EqualTo(2), "DisplayName and EmployeeId had no survivor");
            Assert.That(recallResult.PendingExportsStaged, Is.GreaterThanOrEqualTo(1));
        }
    }

    [Test]
    public async Task ExecuteSyncRuleDeletionRecallAsync_SoleContributor_ClearsValuesStagesRemovalAndDeletesRuleAsync()
    {
        var ctx = await SetUpSoleContributorWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        var targetCso = SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);

        var (task, activity) = await DisableRuleAndBuildTaskAsync(ctx.HrImportRule);
        var recallResult = await Jim.ConnectedSystems.ExecuteSyncRuleDeletionRecallAsync(task);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var stagedPendingExport = SyncRepo.PendingExports.Values
            .SingleOrDefault(pe => pe.ConnectedSystemObjectId == targetCso.Id);
        var rpeis = DbContext.ActivityRunProfileExecutionItems.Where(rpei => rpei.ActivityId == activity.Id).ToList();
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

            Assert.That(await DbContext.SyncRules.FindAsync(ctx.HrImportRule.Id), Is.Null,
                "the rule must be deleted as the task's final step");

            Assert.That(rpeis, Has.Count.EqualTo(1), "one RPEI per affected Metaverse Object");
            Assert.That(activity.Message, Is.Not.Null.And.Contain("cleared"),
                "the Activity must complete with summary statistics");

            Assert.That(recallResult.MetaverseObjectsProcessed, Is.EqualTo(1));
            Assert.That(recallResult.AttributesReElected, Is.Zero);
            Assert.That(recallResult.AttributesCleared, Is.EqualTo(3),
                "DisplayName, EmployeeId and Description all had no surviving contributor");
        }
    }

    [Test]
    public async Task ExecuteSyncRuleDeletionRecallAsync_FailurePartway_RuleSurvivesDisabledAndActivityFailsAsync()
    {
        var ctx = await SetUpSoleContributorWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);

        var (task, activity) = await DisableRuleAndBuildTaskAsync(ctx.HrImportRule);
        _failingSyncRepo.ThrowOnUpdateMetaverseObjects = true;

        // The executor must fail fast and hard; the Worker's dispatch boundary then fails the Activity,
        // mirrored here (the same contract Worker.cs applies to every queued task type).
        InvalidOperationException? thrown = null;
        try
        {
            await Jim.ConnectedSystems.ExecuteSyncRuleDeletionRecallAsync(task);
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }
        Assert.That(thrown, Is.Not.Null, "a mid-batch persistence failure must propagate, never be swallowed");
        await Jim.Activities.FailActivityWithErrorAsync(activity, thrown!);

        var persistedRule = await DbContext.SyncRules.FindAsync(ctx.HrImportRule.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedRule, Is.Not.Null, "the rule must survive a failed recall so it can be retried");
            Assert.That(persistedRule!.Enabled, Is.False, "the rule must remain disabled");
            Assert.That(persistedRule!.DisabledReason,
                Is.EqualTo("Deletion in progress: contributed attribute values are being recalled."),
                "the disabled reason must survive the failure");
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(activity.ErrorMessage, Does.Contain("Simulated database failure"));
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Topology builders
    // -----------------------------------------------------------------------------------------------------------------

    private sealed record ChoiceContext(SyncRule Rule, MetaverseObject User);

    private sealed record RecallExecutionContext(
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

    /// <summary>
    /// A minimal topology for the deletion-choice tests: one import rule, and (optionally) contributed values
    /// seeded straight into the store the choice's impact summary reads: three values across two Metaverse
    /// Objects, plus a control value contributed by nothing.
    /// </summary>
    private async Task<ChoiceContext> SetUpChoiceTopologyAsync(bool seedContributedValues)
    {
        var system = await CreateConnectedSystemAsync("HR Source");
        var csoType = await CreateCsoTypeAsync(system.Id, "HrUser");
        var mvType = await CreateMvObjectTypeAsync("Person");
        var rule = await CreateImportSyncRuleAsync(system.Id, csoType, mvType, "HR Import");

        var displayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var employeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var user = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType, Created = DateTime.UtcNow, CachedDisplayName = "Test Administrator" };
        var mvo1 = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType, Created = DateTime.UtcNow };
        var mvo2 = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType, Created = DateTime.UtcNow };
        DbContext.MetaverseObjects.AddRange(user, mvo1, mvo2);
        await DbContext.SaveChangesAsync();

        if (seedContributedValues)
        {
            DbContext.MetaverseObjectAttributeValues.AddRange(
                NewContributedValue(mvo1, displayNameAttr, "Alice", rule.Id, system.Id),
                NewContributedValue(mvo2, displayNameAttr, "Bob", rule.Id, system.Id),
                NewContributedValue(mvo1, employeeIdAttr, "EMP001", rule.Id, system.Id));
            await DbContext.SaveChangesAsync();
        }

        return new ChoiceContext(rule, user);
    }

    private static MetaverseObjectAttributeValue NewContributedValue(
        MetaverseObject mvo, MetaverseAttribute attribute, string value, int? syncRuleId, int? systemId)
    {
        return new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = attribute,
            AttributeId = attribute.Id,
            StringValue = value,
            ContributedBySyncRuleId = syncRuleId,
            ContributedBySystemId = systemId
        };
    }

    /// <summary>
    /// Disables the rule with the deletion-in-progress reason (as the queue step does) and builds the worker
    /// task with its Activity, mirroring what TaskingServer records at queue time.
    /// </summary>
    private async Task<(DeleteSyncRuleWorkerTask Task, Activity Activity)> DisableRuleAndBuildTaskAsync(SyncRule rule)
    {
        // Detach processor-modified entities first (the same guard the base harness's helpers apply): the
        // full syncs above leave tracked entities in states the in-memory store no longer recognises.
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Modified).ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;

        rule.Enabled = false;
        rule.DisabledReason = "Deletion in progress: contributed attribute values are being recalled.";
        DbContext.Entry(rule).State = Microsoft.EntityFrameworkCore.EntityState.Modified;
        await DbContext.SaveChangesAsync();

        var activity = new Activity
        {
            TargetName = rule.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            TargetOperationType = ActivityTargetOperationType.RecallAttributeValues,
            Status = ActivityStatus.InProgress,
            ConnectedSystemId = rule.ConnectedSystemId,
            Executed = DateTime.UtcNow
        };
        DbContext.Activities.Add(activity);
        await DbContext.SaveChangesAsync();

        var task = new DeleteSyncRuleWorkerTask(rule.Id, recallContributedValues: true)
        {
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
    private async Task<RecallExecutionContext> SetUpSoleContributorWithExportTargetAsync()
    {
        var (hrSystem, hrImportRule, mvType, mvDescriptionAttr, mvDisplayNameAttr) = await SetUpHrContributorAsync();
        var target = await AddExportTargetAsync(mvType, mvDisplayNameAttr, mvDescriptionAttr);
        return new RecallExecutionContext(hrSystem, null, hrImportRule, 0, mvDescriptionAttr.Id,
            mvDisplayNameAttr.Id, target.System, target.DescriptionAttribute, target.DisplayNameAttribute);
    }

    /// <summary>
    /// Two-contributor topology plus a downstream export target: HR (Description priority 1, projects) and
    /// Training (Description priority 2, joins on EmployeeId).
    /// </summary>
    private async Task<RecallExecutionContext> SetUpTwoContributorsWithExportTargetAsync()
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
        return new RecallExecutionContext(hrSystem, trainingSystem, hrImportRule, trainingImportRule.Id,
            mvDescriptionAttr.Id, mvDisplayNameAttr.Id, target.System, target.DescriptionAttribute, target.DisplayNameAttribute);
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
    /// assertions only see exports staged by the recall under test.
    /// </summary>
    private ConnectedSystemObject SimulateTargetExportExecuted(RecallExecutionContext ctx, string displayName, string description)
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
