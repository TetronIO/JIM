// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Workflow tests for the stranded-value sweep (#1549): clearing a Connector Space hard-deletes Connected
/// System Objects without obsoletion, so a departed source object's contributed Metaverse attribute values
/// survive with live provenance and no joined Connected System Object of that system. The next Full
/// Synchronisation of the cleared system reads StrandedValueSweepPending and, when set, sweeps every
/// import Synchronisation Rule (enabled and disabled alike) for stranded candidates, skips rules whose type
/// retains contributed attributes by policy, and recalls the rest through the shipped #1537/#809 recall
/// engine under the #1570 last-known-state preservation gate.
/// </summary>
[TestFixture]
public class StrandedValueSweepWorkflowTests : WorkflowTestBase
{
    private const string StrandedValue = "Stranded value";

    /// <summary>
    /// In-memory sync repository that records which Synchronisation Rule ids the stranded selector and the
    /// joined-systems lookup were called for, so a test can assert "never called" rather than only
    /// inferring it from the absence of a side effect.
    /// </summary>
    private sealed class SpyingSyncRepository : JIM.InMemoryData.SyncRepository
    {
        public List<int> StrandedSelectorCalledForRuleIds { get; } = new();
        public List<Guid> JoinedSystemsLookupCalledForMvoIds { get; } = new();

        public override Task<List<Guid>> GetMetaverseObjectIdsWithStrandedValuesContributedBySyncRuleAsync(int syncRuleId, int connectedSystemId)
        {
            StrandedSelectorCalledForRuleIds.Add(syncRuleId);
            return base.GetMetaverseObjectIdsWithStrandedValuesContributedBySyncRuleAsync(syncRuleId, connectedSystemId);
        }

        public override Task<List<int>> GetJoinedConnectedSystemIdsByMetaverseObjectIdAsync(Guid metaverseObjectId)
        {
            JoinedSystemsLookupCalledForMvoIds.Add(metaverseObjectId);
            return base.GetJoinedConnectedSystemIdsByMetaverseObjectIdAsync(metaverseObjectId);
        }
    }

    private SpyingSyncRepository _spySyncRepo = null!;

    [SetUp]
    public void SetUpSpySyncRepo()
    {
        // Replace the base harness's sync repository with the spying twin BEFORE any seeding, so every test
        // (not just the ones that inspect the spy) runs against the same repository instance the helpers seed.
        _spySyncRepo = new SpyingSyncRepository();
        _spySyncRepo.SetSyncOutcomeTrackingLevel(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        SyncRepo = _spySyncRepo;
        Jim = new JimApplication(Repository, syncRepository: SyncRepo);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Guard
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ExecuteStrandedValueSweepAsync_NotArmed_ThrowsAsync()
    {
        var system = await CreateConnectedSystemAsync("HR Source");
        Assert.That(system.StrandedValueSweepPending, Is.False, "precondition: a freshly created system is not armed");
        var activity = await BuildActivityAsync(system.Id);

        Assert.That(async () => await Jim.ConnectedSystems.ExecuteStrandedValueSweepAsync(system, activity),
            Throws.TypeOf<InvalidDataException>());
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Policy skip
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ExecuteStrandedValueSweepAsync_RecallDisabledObjectType_SkipsRuleAsync()
    {
        var ctx = await SetUpImportRuleAsync(removeContributedAttributesOnObsoletion: false);
        SeedStrandedMetaverseObject(ctx, StrandedValue);
        var system = await ArmSweepAsync(ctx.System);
        var activity = await BuildActivityAsync(system.Id);

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepAsync(system, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_spySyncRepo.StrandedSelectorCalledForRuleIds, Does.Not.Contain(ctx.ImportRule.Id),
                "a rule whose Connected System Object Type has RemoveContributedAttributesOnObsoletion disabled must never be evaluated for stranded values");
            Assert.That(result.SyncRulesSwept, Is.Zero);
            Assert.That(result.MetaverseObjectsProcessed, Is.Zero);
            Assert.That(system.StrandedValueSweepPending, Is.False, "the flag must still clear even when nothing was swept");
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Zero-findings and happy path
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ExecuteStrandedValueSweepAsync_NoStrandedValues_ClearsFlagAndReportsZeroAsync()
    {
        var ctx = await SetUpImportRuleAsync();
        // No stranded Metaverse Object seeded: nothing for the selector to find.
        var system = await ArmSweepAsync(ctx.System);
        var activity = await BuildActivityAsync(system.Id);

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepAsync(system, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SyncRulesSwept, Is.Zero);
            Assert.That(result.MetaverseObjectsProcessed, Is.Zero);
            Assert.That(result.ValuesRecalled, Is.Zero);
            Assert.That(result.PendingExportsStaged, Is.Zero);
            Assert.That(system.StrandedValueSweepPending, Is.False, "the flag must clear even on a zero-findings sweep");
        }
    }

    [Test]
    public async Task ExecuteStrandedValueSweepAsync_StrandedValues_RecallsAndClearsFlagAsync()
    {
        // A second import system remains joined to the stranded object, so the #1570 gate finds a
        // remaining import source and lets the recall proceed (see the preservation-gate tests below for
        // the opposite case).
        var ctx = await SetUpImportRuleAsync();
        var secondSystem = await AddSecondImportSourceAsync(ctx);
        var strandedMvo = SeedStrandedMetaverseObject(ctx, StrandedValue);
        JoinMvoToSystem(strandedMvo, secondSystem.System);
        var system = await ArmSweepAsync(ctx.System);
        var activity = await BuildActivityAsync(system.Id);

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepAsync(system, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SyncRulesSwept, Is.EqualTo(1));
            Assert.That(result.MetaverseObjectsProcessed, Is.EqualTo(1));
            Assert.That(result.ValuesRecalled, Is.EqualTo(1));
            Assert.That(result.AttributesCleared, Is.EqualTo(1), "the sole contributor's value has no survivor and must be genuinely cleared");
            Assert.That(result.MetaverseObjectsPreserved, Is.Zero);
            Assert.That(GetAttributeValue(strandedMvo, ctx.MvDescriptionAttr.Id), Is.Null,
                "the stranded value must be recalled (cleared, no surviving contributor)");
            Assert.That(system.StrandedValueSweepPending, Is.False);
            Assert.That(activity.RunProfileExecutionItems.Count, Is.EqualTo(1), "one Run Profile Execution Item per processed Metaverse Object");
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Disabled-rule coverage
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ExecuteStrandedValueSweepAsync_DisabledImportRule_StillSweepsAsync()
    {
        // Obsoletion recalls by system regardless of rule enablement, and the sweep substitutes for the
        // obsoletion that never ran; a disabled rule's stranded values must still be swept. A second import
        // source keeps the #1570 gate out of the way so the recall (not preservation) path is exercised.
        var ctx = await SetUpImportRuleAsync();
        ctx.ImportRule.Enabled = false;
        await DbContext.SaveChangesAsync();
        var secondSystem = await AddSecondImportSourceAsync(ctx);
        var strandedMvo = SeedStrandedMetaverseObject(ctx, StrandedValue);
        JoinMvoToSystem(strandedMvo, secondSystem.System);
        var system = await ArmSweepAsync(ctx.System);
        var activity = await BuildActivityAsync(system.Id);

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepAsync(system, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_spySyncRepo.StrandedSelectorCalledForRuleIds, Does.Contain(ctx.ImportRule.Id),
                "the disabled rule's stranded values must still be selected");
            Assert.That(result.SyncRulesSwept, Is.EqualTo(1));
            Assert.That(result.MetaverseObjectsProcessed, Is.EqualTo(1));
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // #1570 preservation gate
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ExecuteStrandedValueSweepAsync_NoRemainingImportSource_PreservesValuesAsync()
    {
        // The stranded object's only remaining connection is a provisioned export target, which carries no
        // import Synchronisation Rule for the type: no remaining import source, so the gate must preserve
        // the values as last known state rather than recall them.
        var ctx = await SetUpImportRuleAsync();
        var target = await AddExportTargetAsync(ctx);
        var strandedMvo = SeedStrandedMetaverseObject(ctx, StrandedValue);
        JoinMvoToSystem(strandedMvo, target);
        var system = await ArmSweepAsync(ctx.System);
        var activity = await BuildActivityAsync(system.Id);

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepAsync(system, activity);

        var preservedRpei = activity.RunProfileExecutionItems.SingleOrDefault();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.MetaverseObjectsProcessed, Is.Zero, "a preserved object is not counted as processed (recalled)");
            Assert.That(result.ValuesRecalled, Is.Zero);
            Assert.That(result.MetaverseObjectsPreserved, Is.EqualTo(1));
            Assert.That(result.ValuesPreserved, Is.EqualTo(1));
            Assert.That(GetAttributeValue(strandedMvo, ctx.MvDescriptionAttr.Id), Is.Not.Null,
                "no removals may be staged; the value must survive untouched");
            Assert.That(preservedRpei, Is.Not.Null);
            Assert.That(preservedRpei!.SyncOutcomes.Any(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.ValuesPreserved),
                Is.True, "a ValuesPreserved outcome must be recorded so the preservation is auditable");
            Assert.That(system.StrandedValueSweepPending, Is.False, "the flag must still clear even when values were preserved rather than recalled");
        }
    }

    [Test]
    public async Task ExecuteStrandedValueSweepAsync_RemainingImportSource_RecallsValuesAsync()
    {
        // The inverse of the preservation test: the remaining connection carries an enabled import
        // Synchronisation Rule for the type, so the gate must let the recall proceed.
        var ctx = await SetUpImportRuleAsync();
        var secondSystem = await AddSecondImportSourceAsync(ctx);
        var strandedMvo = SeedStrandedMetaverseObject(ctx, StrandedValue);
        JoinMvoToSystem(strandedMvo, secondSystem.System);
        var system = await ArmSweepAsync(ctx.System);
        var activity = await BuildActivityAsync(system.Id);

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepAsync(system, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.MetaverseObjectsPreserved, Is.Zero);
            Assert.That(result.ValuesPreserved, Is.Zero);
            Assert.That(result.MetaverseObjectsProcessed, Is.EqualTo(1));
            Assert.That(result.ValuesRecalled, Is.EqualTo(1));
            Assert.That(GetAttributeValue(strandedMvo, ctx.MvDescriptionAttr.Id), Is.Null);
        }
    }

    [Test]
    public async Task RecallSyncRuleContributedValuesAsync_DeliberateScope_NeverConsultsTheEvaluatorAsync()
    {
        // ForDeletedSyncRule (#1537) is a deliberate withdrawal (IsDeliberateWithdrawal true); the gate must
        // never consult the evaluator for it, proven directly by asserting the joined-systems lookup (the
        // gate's own first step, ahead of calling the evaluator) is never invoked, and the value is
        // recalled unconditionally even though its only remaining connection carries no import source.
        var ctx = await SetUpImportRuleAsync();
        var target = await AddExportTargetAsync(ctx);
        var strandedMvo = SeedStrandedMetaverseObject(ctx, StrandedValue);
        JoinMvoToSystem(strandedMvo, target);
        var activity = await BuildActivityAsync(ctx.System.Id);

        var allSyncRules = await Jim.SyncRepo.GetAllSyncRulesAsync();
        var priorityContext = new AttributePriorityContext(allSyncRules, honourNullAssertions: true);
        var exportEvaluationCache = await Jim.ExportEvaluation.BuildExportEvaluationCacheAsync(allSyncRules);
        var evaluator = new RemainingImportSourceEvaluator(Jim.SyncRepo);

        var result = await Jim.ConnectedSystems.RecallSyncRuleContributedValuesAsync(
            ctx.ImportRule.Id,
            ContributorRecallScope.ForDeletedSyncRule(ctx.ImportRule.Id),
            priorityContext,
            new JIM.Application.Servers.SyncEngine(),
            new JIM.Application.Expressions.DynamicExpressoEvaluator(),
            exportEvaluationCache,
            activity,
            reElectedDetailMessage: "re-elected",
            clearedDetailMessage: "cleared",
            trackActivityProgress: false,
            affectedMetaverseObjectIds: new List<Guid> { strandedMvo.Id },
            remainingImportSourceEvaluator: evaluator,
            preservedDetailMessage: "preserved");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_spySyncRepo.JoinedSystemsLookupCalledForMvoIds, Does.Not.Contain(strandedMvo.Id),
                "a deliberate-withdrawal scope must never consult the #1570 gate");
            Assert.That(result.MetaverseObjectsPreserved, Is.Zero);
            Assert.That(result.ValuesRecalled, Is.EqualTo(1), "the value must be recalled unconditionally under a deliberate scope");
            Assert.That(GetAttributeValue(strandedMvo, ctx.MvDescriptionAttr.Id), Is.Null);
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Topology builders
    // -----------------------------------------------------------------------------------------------------------------

    private sealed record ImportRuleContext(
        ConnectedSystem System, SyncRule ImportRule, MetaverseObjectType MvType, MetaverseAttribute MvDescriptionAttr);

    private sealed record SecondImportSource(ConnectedSystem System, SyncRule ImportRule);

    private static MetaverseObjectAttributeValue? GetAttributeValue(MetaverseObject mvo, int attributeId) =>
        mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == attributeId && !av.NullValue);

    private async Task<ImportRuleContext> SetUpImportRuleAsync(bool removeContributedAttributesOnObsoletion = true)
    {
        var system = await CreateConnectedSystemAsync("HR Source");
        var csoType = await CreateCsoTypeAsync(system.Id, "HrUser");
        csoType.RemoveContributedAttributesOnObsoletion = removeContributedAttributesOnObsoletion;

        var mvType = await CreateMvObjectTypeAsync("Person");
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

        var importRule = await CreateImportSyncRuleAsync(system.Id, csoType, mvType, "HR Import");

        return new ImportRuleContext(system, importRule, mvType, mvDescriptionAttr);
    }

    /// <summary>
    /// A second Connected System with its own enabled import Synchronisation Rule for the same Metaverse
    /// Object Type: a genuine remaining import source for the #1570 gate.
    /// </summary>
    private async Task<SecondImportSource> AddSecondImportSourceAsync(ImportRuleContext ctx)
    {
        var secondSystem = await CreateConnectedSystemAsync("Training Source");
        var secondCsoType = await CreateCsoTypeAsync(secondSystem.Id, "TrainingRecord");
        var secondImportRule = await CreateImportSyncRuleAsync(secondSystem.Id, secondCsoType, ctx.MvType, "Training Import", enableProjection: false);
        return new SecondImportSource(secondSystem, secondImportRule);
    }

    /// <summary>
    /// An export-only target system (provisioned account, no import rule for the type): the "only
    /// provisioned targets remain" case the #1570 preservation gate exists for.
    /// </summary>
    private async Task<ConnectedSystem> AddExportTargetAsync(ImportRuleContext ctx)
    {
        var target = await CreateConnectedSystemAsync("AD Target");
        var targetCsoType = await CreateCsoTypeAsync(target.Id, "TargetUser");
        await CreateExportSyncRuleAsync(target.Id, targetCsoType, ctx.MvType, "AD Export");
        return target;
    }

    private MetaverseObject SeedStrandedMetaverseObject(ImportRuleContext ctx, string value)
    {
        var strandedMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = ctx.MvType, Created = DateTime.UtcNow };
        strandedMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = strandedMvo,
            Attribute = ctx.MvDescriptionAttr,
            AttributeId = ctx.MvDescriptionAttr.Id,
            StringValue = value,
            ContributedBySyncRuleId = ctx.ImportRule.Id,
            ContributedBySystemId = ctx.System.Id
        });
        SyncRepo.SeedMetaverseObject(strandedMvo);
        return strandedMvo;
    }

    /// <summary>
    /// Joins a Connected System Object of the given system to the Metaverse Object, WITHOUT running an
    /// actual synchronisation: enough for the #1570 gate's joined-systems lookup to see the connection.
    /// </summary>
    private void JoinMvoToSystem(MetaverseObject mvo, ConnectedSystem system)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            MetaverseObjectId = mvo.Id,
            MetaverseObject = mvo,
            Status = ConnectedSystemObjectStatus.Normal
        };
        SyncRepo.SeedConnectedSystemObject(cso);
    }

    private async Task<ConnectedSystem> ArmSweepAsync(ConnectedSystem system)
    {
        system.StrandedValueSweepPending = true;
        await DbContext.SaveChangesAsync();
        return system;
    }

    private async Task<Activity> BuildActivityAsync(int connectedSystemId)
    {
        var profile = await CreateRunProfileAsync(connectedSystemId, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        return await CreateActivityAsync(connectedSystemId, profile, ConnectedSystemRunType.FullSynchronisation);
    }
}
