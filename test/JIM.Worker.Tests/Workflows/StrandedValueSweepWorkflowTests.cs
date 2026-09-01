// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
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
    // ExecuteStrandedValueSweepIfArmedAsync: the caller-facing armed-check wrapper (#1549)
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ExecuteStrandedValueSweepIfArmedAsync_NotArmed_ReturnsNullAndTouchesNothingAsync()
    {
        var system = await CreateConnectedSystemAsync("HR Source");
        Assert.That(system.StrandedValueSweepPending, Is.False, "precondition: a freshly created system is not armed");
        var activity = await BuildActivityAsync(system.Id);
        var originalMessage = activity.Message;

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepIfArmedAsync(system, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null, "an unarmed system must not run the sweep");
            Assert.That(activity.Message, Is.EqualTo(originalMessage), "the Activity Message must be untouched when the sweep does not run");
            Assert.That(_spySyncRepo.StrandedSelectorCalledForRuleIds, Is.Empty, "the sweep's support set must not be constructed when unarmed");
        }
    }

    [Test]
    public async Task ExecuteStrandedValueSweepIfArmedAsync_Armed_RunsSweepAppendsMessageAndReturnsResultAsync()
    {
        var ctx = await SetUpImportRuleAsync();
        var secondSystem = await AddSecondImportSourceAsync(ctx);
        var strandedMvo = SeedStrandedMetaverseObject(ctx, StrandedValue);
        JoinMvoToSystem(strandedMvo, secondSystem.System);
        var system = await ArmSweepAsync(ctx.System);
        var activity = await BuildActivityAsync(system.Id);
        activity.Message = "Sync complete: 1 objects processed.";

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepIfArmedAsync(system, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.ValuesRecalled, Is.EqualTo(1));
            Assert.That(system.StrandedValueSweepPending, Is.False);
            Assert.That(activity.Message, Does.StartWith("Sync complete: 1 objects processed."),
                "the sweep's summary must be appended, not replace, the existing message");
            Assert.That(activity.Message, Does.Contain("Stranded-value sweep executed (armed by a Connector Space clear)"));
            Assert.That(activity.Message, Does.Contain(ConnectedSystemServer.BuildSweepActivityMessage(result)),
                "the appended text must match BuildSweepActivityMessage's composition exactly");
        }
    }

    [Test]
    public async Task ExecuteStrandedValueSweepIfArmedAsync_ArmedNoFindings_AppendsZeroFindingsMessageAsync()
    {
        var ctx = await SetUpImportRuleAsync();
        // No stranded Metaverse Object seeded: nothing for the selector to find.
        var system = await ArmSweepAsync(ctx.System);
        var activity = await BuildActivityAsync(system.Id);
        activity.Message = null;

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepIfArmedAsync(system, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(activity.Message, Is.EqualTo(
                "Stranded-value sweep executed (armed by a Connector Space clear): no stranded values were found."),
                "with no prior message, the sweep's sentence becomes the whole message");
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // BuildSweepActivityMessage: message composition (#1549 Functional Requirement 11)
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public void BuildSweepActivityMessage_ZeroFindings_StatesNoStrandedValuesFound()
    {
        var result = new StrandedValueSweepResult();

        var message = ConnectedSystemServer.BuildSweepActivityMessage(result);

        Assert.That(message, Is.EqualTo(
            "Stranded-value sweep executed (armed by a Connector Space clear): no stranded values were found."));
    }

    [Test]
    public void BuildSweepActivityMessage_ZeroFindings_MetaverseObjectsPreservedAlsoZero_IsRequiredForZeroWording()
    {
        // MetaverseObjectsProcessed is zero but MetaverseObjectsPreserved is not: this is NOT the zero-findings
        // case (values were found and preserved), so the full wording must be used, not the short form.
        var result = new StrandedValueSweepResult
        {
            MetaverseObjectsProcessed = 0,
            MetaverseObjectsPreserved = 1,
            ValuesPreserved = 1
        };

        var message = ConnectedSystemServer.BuildSweepActivityMessage(result);

        Assert.That(message, Does.Not.Contain("no stranded values were found"));
        Assert.That(message, Does.Contain("1 Metaverse Object(s) preserved as last known state (1 value(s))"));
    }

    [Test]
    public void BuildSweepActivityMessage_WithFindings_StatesAllCountersInWords()
    {
        var result = new StrandedValueSweepResult
        {
            SyncRulesSwept = 2,
            MetaverseObjectsProcessed = 3,
            ValuesRecalled = 4,
            AttributesReElected = 1,
            AttributesCleared = 3,
            MetaverseObjectsPreserved = 5,
            ValuesPreserved = 6,
            PendingExportsStaged = 7
        };

        var message = ConnectedSystemServer.BuildSweepActivityMessage(result);

        Assert.That(message, Is.EqualTo(
            "Stranded-value sweep executed (armed by a Connector Space clear): " +
            "4 stranded value(s) recalled across 3 Metaverse Object(s) " +
            "(1 re-elected to a surviving contributor, 3 cleared with no remaining contributor); " +
            "5 Metaverse Object(s) preserved as last known state (6 value(s)); " +
            "7 Pending Export(s) staged."));
    }

    [Test]
    public void BuildSweepActivityMessage_LargeCounters_UsesThousandsSeparators()
    {
        var result = new StrandedValueSweepResult
        {
            MetaverseObjectsProcessed = 12345,
            ValuesRecalled = 12345,
            AttributesCleared = 12345
        };

        var message = ConnectedSystemServer.BuildSweepActivityMessage(result);

        Assert.That(message, Does.Contain("12,345 stranded value(s) recalled across 12,345 Metaverse Object(s)"));
        Assert.That(message, Does.Contain("12,345 cleared with no remaining contributor"));
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
