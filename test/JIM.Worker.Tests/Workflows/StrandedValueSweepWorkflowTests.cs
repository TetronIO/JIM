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
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Workflow tests for the stranded-value sweep (#1549): clearing a Connector Space hard-deletes Connected
/// System Objects without obsoletion, so a departed source object's contributed Metaverse attribute values
/// survive with live provenance and no joined Connected System Object of that system. The next Full
/// Synchronisation of the cleared system reads StrandedValueSweepArmedAt and, once the #1605 Full Import
/// gate is open (a Full Import of the system has completed successfully later than the arming), sweeps
/// every import Synchronisation Rule (enabled and disabled alike) for stranded candidates, skips rules whose
/// type retains contributed attributes by policy, and recalls the rest through the shipped #1537/#809
/// recall engine under the #1570 last-known-state preservation gate. Most fixtures here call
/// <see cref="ExecuteStrandedValueSweepAsync"/> directly, which does not itself consult the gate (only its
/// caller-facing wrapper <see cref="ExecuteStrandedValueSweepIfArmedAsync"/> does; see the dedicated gate
/// region below), so <see cref="ArmSweepAsync"/> arms with the gate already open by default.
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
        Assert.That(system.StrandedValueSweepArmedAt, Is.Null, "precondition: a freshly created system is not armed");
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
            Assert.That(system.StrandedValueSweepArmedAt, Is.Null, "the arming must still clear even when nothing was swept");
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
            Assert.That(system.StrandedValueSweepArmedAt, Is.Null, "the arming must clear even on a zero-findings sweep");
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
            Assert.That(system.StrandedValueSweepArmedAt, Is.Null);
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
            Assert.That(system.StrandedValueSweepArmedAt, Is.Null, "the arming must still clear even when values were preserved rather than recalled");
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
        Assert.That(system.StrandedValueSweepArmedAt, Is.Null, "precondition: a freshly created system is not armed");
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
            Assert.That(system.StrandedValueSweepArmedAt, Is.Null);
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
                "Stranded-value sweep executed (armed by a Connector Space clear): no stranded values were found; " +
                "0 Metaverse Object(s) evaluated against their Deletion Rules: 0 marked for deletion, 0 deleted; " +
                "0 object(s) with no connector remaining marked for deletion; 0 Pending Export(s) staged."),
                "with no prior message, the sweep's sentence becomes the whole message");
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // #1605 Full Import gate: ExecuteStrandedValueSweepIfArmedAsync skips while the gate is closed
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ExecuteStrandedValueSweepIfArmedAsync_ArmedNoFullImportYet_SkipsAndKeepsArmingAsync()
    {
        var ctx = await SetUpImportRuleAsync();
        var secondSystem = await AddSecondImportSourceAsync(ctx);
        var strandedMvo = SeedStrandedMetaverseObject(ctx, StrandedValue);
        JoinMvoToSystem(strandedMvo, secondSystem.System);
        var system = await ArmSweepWithGateClosedAsync(ctx.System, lastSuccessfulFullImportCompletedAt: null);
        var armedAt = system.StrandedValueSweepArmedAt!.Value;
        var activity = await BuildActivityAsync(system.Id);
        activity.Message = "Sync complete: 1 objects processed.";

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepIfArmedAsync(system, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Skipped, Is.True);
            Assert.That(result.MetaverseObjectsProcessed, Is.Zero, "a gated run must not process anything");
            Assert.That(result.ValuesRecalled, Is.Zero);
            Assert.That(system.StrandedValueSweepArmedAt, Is.EqualTo(armedAt), "the arming must be left exactly as it was");
            Assert.That(_spySyncRepo.StrandedSelectorCalledForRuleIds, Is.Empty, "a gated run must never construct the sweep's support set");
            Assert.That(GetAttributeValue(strandedMvo, ctx.MvDescriptionAttr.Id), Is.Not.Null, "no value may be recalled while the gate is closed");
            Assert.That(activity.Message, Does.StartWith("Sync complete: 1 objects processed."),
                "the skipped sentence must be appended, not replace, the existing message");
            Assert.That(activity.Message, Does.Contain(ConnectedSystemServer.BuildSweepSkippedMessage(armedAt)),
                "the appended text must match BuildSweepSkippedMessage's composition exactly");
        }
    }

    [Test]
    public async Task ExecuteStrandedValueSweepIfArmedAsync_LastImportBeforeArming_SkipsAsync()
    {
        var ctx = await SetUpImportRuleAsync();
        var system = await ArmSweepWithGateClosedAsync(ctx.System);
        // An import that completed BEFORE this clear armed the sweep: stale evidence, not proof of a genuine
        // re-import since the clear, so the gate must still treat it as closed.
        system.LastSuccessfulFullImportCompletedAt = system.StrandedValueSweepArmedAt!.Value.AddMinutes(-1);
        await DbContext.SaveChangesAsync();
        var activity = await BuildActivityAsync(system.Id);

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepIfArmedAsync(system, activity);

        Assert.That(result!.Skipped, Is.True, "an import older than the arming must not open the gate");
    }

    [Test]
    public async Task ExecuteStrandedValueSweepIfArmedAsync_LastImportExactlyAtArming_SkipsAsync()
    {
        var ctx = await SetUpImportRuleAsync();
        var system = await ArmSweepWithGateClosedAsync(ctx.System);
        // Equal, not later: the gate requires strictly later, so this boundary case must still be closed.
        system.LastSuccessfulFullImportCompletedAt = system.StrandedValueSweepArmedAt!.Value;
        await DbContext.SaveChangesAsync();
        var activity = await BuildActivityAsync(system.Id);

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepIfArmedAsync(system, activity);

        Assert.That(result!.Skipped, Is.True, "an import at exactly the arming time must not open the gate");
    }

    // -----------------------------------------------------------------------------------------------------------------
    // IsSweepGateOpen: the #1605 gate predicate, tested directly
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public void IsSweepGateOpen_NullArmedAt_ReturnsFalse()
    {
        Assert.That(ConnectedSystemServer.IsSweepGateOpen(null, DateTime.UtcNow), Is.False);
    }

    [Test]
    public void IsSweepGateOpen_ArmedWithNoSuccessfulImport_ReturnsFalse()
    {
        Assert.That(ConnectedSystemServer.IsSweepGateOpen(DateTime.UtcNow, null), Is.False);
    }

    [Test]
    public void IsSweepGateOpen_ImportBeforeArming_ReturnsFalse()
    {
        var armedAt = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        var lastImport = armedAt.AddMinutes(-1);

        Assert.That(ConnectedSystemServer.IsSweepGateOpen(armedAt, lastImport), Is.False);
    }

    [Test]
    public void IsSweepGateOpen_ImportEqualToArming_ReturnsFalse()
    {
        var armedAt = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);

        Assert.That(ConnectedSystemServer.IsSweepGateOpen(armedAt, armedAt), Is.False);
    }

    [Test]
    public void IsSweepGateOpen_ImportAfterArming_ReturnsTrue()
    {
        var armedAt = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        var lastImport = armedAt.AddMinutes(1);

        Assert.That(ConnectedSystemServer.IsSweepGateOpen(armedAt, lastImport), Is.True);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // BuildSweepSkippedMessage: message composition (#1605)
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public void BuildSweepSkippedMessage_StatesArmedAtAndRemedy()
    {
        var armedAt = new DateTime(2026, 9, 3, 8, 30, 15, DateTimeKind.Utc);

        var message = ConnectedSystemServer.BuildSweepSkippedMessage(armedAt);

        Assert.That(message, Is.EqualTo(
            "Stranded-value sweep armed by a Connector Space clear on 2026-09-03 08:30:15 UTC; skipped: " +
            "no Full Import of this Connected System has completed successfully since. Run a Full Import, " +
            "then a Full Synchronisation, to reconcile objects that did not return."));
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
            "Stranded-value sweep executed (armed by a Connector Space clear): no stranded values were found; " +
            "0 Metaverse Object(s) evaluated against their Deletion Rules: 0 marked for deletion, 0 deleted; " +
            "0 object(s) with no connector remaining marked for deletion; 0 Pending Export(s) staged."));
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
            "0 Metaverse Object(s) evaluated against their Deletion Rules: 0 marked for deletion, 0 deleted; " +
            "0 object(s) with no connector remaining marked for deletion; " +
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
    // RecordSuccessfulFullImportAsync (#1605): the worker's stamp on a genuinely successful Full Import
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task RecordSuccessfulFullImportAsync_PersistsTimestampAndUpdatesInMemoryInstanceAsync()
    {
        var system = await CreateConnectedSystemAsync("HR Source");
        Assert.That(system.LastSuccessfulFullImportCompletedAt, Is.Null, "precondition: a freshly created system has never imported");
        var completedAt = DateTime.UtcNow;

        await Jim.ConnectedSystems.RecordSuccessfulFullImportAsync(system, completedAt);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(system.LastSuccessfulFullImportCompletedAt, Is.EqualTo(completedAt),
                "the caller's in-memory instance must observe the change without a re-fetch, matching the sweep's own arming/clearing convention");

            var reloaded = await DbContext.ConnectedSystems.AsNoTracking().SingleAsync(cs => cs.Id == system.Id);
            Assert.That(reloaded.LastSuccessfulFullImportCompletedAt, Is.EqualTo(completedAt), "the change must be persisted");
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // IsReconciliationRefused: the #1605 Functional Requirement 9 shortfall predicate, tested directly
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public void IsReconciliationRefused_ZeroRecorded_ReturnsFalse()
    {
        Assert.That(ConnectedSystemServer.IsReconciliationRefused(recordedCount: 0, missingCount: 0, maxMissingPercentThreshold: 10), Is.False);
    }

    [Test]
    public void IsReconciliationRefused_MissingExactlyAtThreshold_ReturnsFalse()
    {
        // 10 of 100 missing is exactly the 10% threshold; the check is strictly greater, so this must not refuse.
        Assert.That(ConnectedSystemServer.IsReconciliationRefused(recordedCount: 100, missingCount: 10, maxMissingPercentThreshold: 10), Is.False);
    }

    [Test]
    public void IsReconciliationRefused_OneAboveThreshold_ReturnsTrue()
    {
        Assert.That(ConnectedSystemServer.IsReconciliationRefused(recordedCount: 100, missingCount: 11, maxMissingPercentThreshold: 10), Is.True);
    }

    [Test]
    public void IsReconciliationRefused_AllMissing_ReturnsTrue()
    {
        Assert.That(ConnectedSystemServer.IsReconciliationRefused(recordedCount: 3, missingCount: 3, maxMissingPercentThreshold: 10), Is.True);
    }

    [Test]
    public void IsReconciliationRefused_NoneMissing_ReturnsFalse()
    {
        Assert.That(ConnectedSystemServer.IsReconciliationRefused(recordedCount: 100, missingCount: 0, maxMissingPercentThreshold: 10), Is.False);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // BuildSweepRefusedMessage: message composition (#1605 Functional Requirement 9)
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public void BuildSweepRefusedMessage_StatesCountsPercentThresholdAndRemedy()
    {
        var armedAt = new DateTime(2026, 9, 3, 8, 30, 15, DateTimeKind.Utc);

        var message = ConnectedSystemServer.BuildSweepRefusedMessage(missingCount: 34, recordedCount: 100, armedAt: armedAt, thresholdPercent: 10);

        Assert.That(message, Is.EqualTo(
            "Stranded-value sweep refused: 34 of 100 objects joined before the clear on 2026-09-03 08:30:15 UTC " +
            "have not returned (34%), above the 10% allowed by the 'Sync.PostClearReconciliation.MaxMissingPercent' " +
            "setting. Re-import the Connected System, or raise the setting, then run a Full Synchronisation."));
    }

    // -----------------------------------------------------------------------------------------------------------------
    // AppendSweepSentence: the punctuation fix (observed at runtime: "Sync complete: 2 objects Stranded-value...")
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public void AppendSweepSentence_NoPriorMessage_ReturnsSentenceAlone()
    {
        Assert.That(ConnectedSystemServer.AppendSweepSentence(null, "Stranded-value sweep executed."), Is.EqualTo("Stranded-value sweep executed."));
        Assert.That(ConnectedSystemServer.AppendSweepSentence(string.Empty, "Stranded-value sweep executed."), Is.EqualTo("Stranded-value sweep executed."));
    }

    [Test]
    public void AppendSweepSentence_PriorMessageMissingTerminatingPunctuation_InsertsFullStop()
    {
        var result = ConnectedSystemServer.AppendSweepSentence("Sync complete: 2 objects", "Stranded-value sweep executed (armed by a Connector Space clear): ...");

        Assert.That(result, Is.EqualTo("Sync complete: 2 objects. Stranded-value sweep executed (armed by a Connector Space clear): ..."));
    }

    [Test]
    public void AppendSweepSentence_PriorMessageEndsWithFullStop_JoinsWithSingleSpace()
    {
        var result = ConnectedSystemServer.AppendSweepSentence("Sync complete: 2 objects.", "Stranded-value sweep executed.");

        Assert.That(result, Is.EqualTo("Sync complete: 2 objects. Stranded-value sweep executed."));
    }

    [Test]
    public void AppendSweepSentence_PriorMessageEndsWithExclamationOrQuestionMark_JoinsWithSingleSpace()
    {
        Assert.That(ConnectedSystemServer.AppendSweepSentence("Sync complete!", "Next."), Is.EqualTo("Sync complete! Next."));
        Assert.That(ConnectedSystemServer.AppendSweepSentence("Really?", "Next."), Is.EqualTo("Really? Next."));
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Re-join shortfall check, end to end (#1605 Functional Requirement 9)
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ExecuteStrandedValueSweepAsync_ShortfallAboveThreshold_RefusesAndTouchesNothingAsync()
    {
        // Ten objects recorded at the clear; only one has rejoined. 90% missing is far above the default 10%
        // threshold (no ServiceSetting row seeded, so the default applies).
        var ctx = await SetUpImportRuleAsync();
        var recordedMvos = new List<MetaverseObject>();
        for (var i = 0; i < 10; i++)
        {
            var mvo = SeedPlainMetaverseObject(ctx);
            recordedMvos.Add(mvo);
            await SeedJoinRecordAsync(ctx.System, mvo.Id);
        }
        // Rejoin exactly one of them.
        JoinMvoToSystem(recordedMvos[0], ctx.System);

        var system = await ArmSweepAsync(ctx.System);
        var armedAt = system.StrandedValueSweepArmedAt!.Value;
        var activity = await BuildActivityAsync(system.Id);

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepAsync(system, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Refused, Is.True);
            Assert.That(result.RefuseReason, Is.EqualTo(ConnectedSystemServer.BuildSweepRefusedMessage(9, 10, armedAt, 10)));
            Assert.That(result.MetaverseObjectsProcessed, Is.Zero, "no value recall may run while refused");
            Assert.That(result.MetaverseObjectsEvaluatedForDeletion, Is.Zero, "no Deletion Rule evaluation may run while refused");
            Assert.That(system.StrandedValueSweepArmedAt, Is.EqualTo(armedAt), "the arming must be left exactly as it was");
            Assert.That((await DbContext.ConnectorSpaceClearJoinRecords.Where(r => r.ConnectedSystemId == ctx.System.Id).ToListAsync()).Count,
                Is.EqualTo(10), "the join records must be left in place for a later retry");
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Deletion Rule evaluation for recorded objects that did not return (#1605 Functional Requirement 7)
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ExecuteStrandedValueSweepAsync_RecordedObjectManualRule_NotEvaluatedForDeletionAsync()
    {
        var ctx = await SetUpImportRuleAsync();
        ctx.MvType.DeletionRule = MetaverseObjectDeletionRule.Manual;
        await DbContext.SaveChangesAsync();
        var mvo = SeedPlainMetaverseObject(ctx);
        await SeedJoinRecordAsync(ctx.System, mvo.Id);
        await SeedMaxMissingPercentAsync(100);
        var system = await ArmSweepAsync(ctx.System);
        var activity = await BuildActivityAsync(system.Id);

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepAsync(system, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Refused, Is.False);
            Assert.That(result.MetaverseObjectsMarkedForDeletion, Is.Zero);
            Assert.That(result.MetaverseObjectsDeleted, Is.Zero);
            Assert.That(mvo.LastConnectorDisconnectedDate, Is.Null, "a Manual rule must never mark the object for deletion");
        }
    }

    [Test]
    public async Task ExecuteStrandedValueSweepAsync_LastConnectorRuleWithRemainingJoin_NotEvaluatedForDeletionAsync()
    {
        // The recorded object never rejoined the CLEARED system, but it does hold a CURRENT join to a
        // different, still-connected system: WhenLastConnectorDisconnected must not fire while any connector
        // remains, regardless of which one.
        var ctx = await SetUpImportRuleAsync();
        var secondSystem = await AddSecondImportSourceAsync(ctx);
        var mvo = SeedPlainMetaverseObject(ctx);
        JoinMvoToSystem(mvo, secondSystem.System);
        await SeedJoinRecordAsync(ctx.System, mvo.Id);
        await SeedMaxMissingPercentAsync(100);
        var system = await ArmSweepAsync(ctx.System);
        var activity = await BuildActivityAsync(system.Id);

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepAsync(system, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.MetaverseObjectsEvaluatedForDeletion, Is.EqualTo(1));
            Assert.That(result.MetaverseObjectsMarkedForDeletion, Is.Zero);
            Assert.That(result.MetaverseObjectsDeleted, Is.Zero);
            Assert.That(mvo.LastConnectorDisconnectedDate, Is.Null, "a remaining connector must prevent the marking");
        }
    }

    [Test]
    public async Task ExecuteStrandedValueSweepAsync_LastConnectorRuleNoRemainingJoinWithGrace_MarkedWithTriggerAndSnapshotAsync()
    {
        var ctx = await SetUpImportRuleAsync();
        ctx.MvType.DeletionGracePeriod = TimeSpan.FromDays(7);
        await DbContext.SaveChangesAsync();
        var mvo = SeedPlainMetaverseObject(ctx);
        await SeedJoinRecordAsync(ctx.System, mvo.Id);
        await SeedMaxMissingPercentAsync(100);
        var system = await ArmSweepAsync(ctx.System);
        var activity = await BuildActivityAsync(system.Id);

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepAsync(system, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.MetaverseObjectsMarkedForDeletion, Is.EqualTo(1));
            Assert.That(result.MetaverseObjectsDeleted, Is.Zero);
            Assert.That(mvo.LastConnectorDisconnectedDate, Is.Not.Null);
            Assert.That(mvo.DeletionTriggeredBySystemId, Is.EqualTo(ctx.System.Id), "the cleared system is the trigger");
            Assert.That(mvo.DeletionTriggeredBySystemName, Is.EqualTo(ctx.System.Name));
            Assert.That(mvo.DeletionPolicySnapshotJson, Is.Not.Null.And.Not.Empty, "the decision-time policy snapshot must be recorded");
            var item = activity.RunProfileExecutionItems.SingleOrDefault(i => i.SyncOutcomes.Any(o => o.TargetEntityId == mvo.Id));
            Assert.That(item, Is.Not.Null, "an execution item must be recorded for the marked object");
            Assert.That(item!.SyncOutcomes.Any(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled), Is.True);
        }
    }

    [Test]
    public async Task ExecuteStrandedValueSweepAsync_LastConnectorRuleNoGrace_DeletedImmediatelyWithMvoDeletedItemAsync()
    {
        var ctx = await SetUpImportRuleAsync();
        ctx.MvType.DeletionGracePeriod = null;
        await DbContext.SaveChangesAsync();
        var mvo = SeedPlainMetaverseObject(ctx);
        var mvoId = mvo.Id;
        await SeedJoinRecordAsync(ctx.System, mvoId);
        await SeedMaxMissingPercentAsync(100);
        var system = await ArmSweepAsync(ctx.System);
        var activity = await BuildActivityAsync(system.Id);

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepAsync(system, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.MetaverseObjectsDeleted, Is.EqualTo(1));
            Assert.That(result.MetaverseObjectsMarkedForDeletion, Is.Zero);
            Assert.That((await Jim.SyncRepo.GetMetaverseObjectsByIdsForUpdateAsync(new[] { mvoId })).Count, Is.Zero, "a no-grace fate must delete the object immediately");
            var item = activity.RunProfileExecutionItems.SingleOrDefault(i => i.SyncOutcomes.Any(o => o.TargetEntityId == mvoId));
            Assert.That(item, Is.Not.Null);
            Assert.That(item!.SyncOutcomes.Any(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted), Is.True);
        }
    }

    [Test]
    public async Task ExecuteStrandedValueSweepAsync_AuthoritativeSourceSpecificModeClearedSystemListed_FiresDespiteRemainingJoinAsync()
    {
        // Specific-sources mode fires on ANY listed source's disconnection, even with other connectors
        // (including non-listed ones) still joined.
        var ctx = await SetUpImportRuleAsync();
        var secondSystem = await AddSecondImportSourceAsync(ctx);
        ctx.MvType.DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected;
        ctx.MvType.DeletionTriggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect;
        ctx.MvType.DeletionTriggerConnectedSystemIds = new List<int> { ctx.System.Id };
        ctx.MvType.DeletionGracePeriod = null;
        await DbContext.SaveChangesAsync();
        var mvo = SeedPlainMetaverseObject(ctx);
        JoinMvoToSystem(mvo, secondSystem.System);
        var mvoId = mvo.Id;
        await SeedJoinRecordAsync(ctx.System, mvoId);
        await SeedMaxMissingPercentAsync(100);
        var system = await ArmSweepAsync(ctx.System);
        var activity = await BuildActivityAsync(system.Id);

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepAsync(system, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.MetaverseObjectsDeleted, Is.EqualTo(1), "Specific mode fires even though the second system's connector remains");
            Assert.That((await Jim.SyncRepo.GetMetaverseObjectsByIdsForUpdateAsync(new[] { mvoId })).Count, Is.Zero);
        }
    }

    [Test]
    public async Task ExecuteStrandedValueSweepAsync_RejoinBeforeSweep_SkippedAsync()
    {
        // Recorded at the clear, but the object rejoined the cleared system before the sweep ran: nothing to
        // evaluate.
        var ctx = await SetUpImportRuleAsync();
        ctx.MvType.DeletionGracePeriod = null;
        await DbContext.SaveChangesAsync();
        var mvo = SeedPlainMetaverseObject(ctx);
        JoinMvoToSystem(mvo, ctx.System);
        await SeedJoinRecordAsync(ctx.System, mvo.Id);
        var system = await ArmSweepAsync(ctx.System);
        var activity = await BuildActivityAsync(system.Id);

        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepAsync(system, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Refused, Is.False, "a fully rejoined recorded set has nothing missing");
            Assert.That(result.MetaverseObjectsEvaluatedForDeletion, Is.Zero, "a rejoined object must never be evaluated for deletion");
            Assert.That(mvo.LastConnectorDisconnectedDate, Is.Null);
        }
    }

    // Note: the state-convergent zero-join pass (#1605 Functional Requirement 10) is covered by
    // MetaverseZeroJoinPassDatabaseTests (RequiresPostgres), not here: it queries
    // Repository.Database.MetaverseObjects directly (a real EF/SQL query, by design - it has to scan the
    // whole Metaverse for historical strays), which this fixture's fake, dictionary-backed SyncRepository
    // cannot see. Metaverse Objects created via SeedPlainMetaverseObject/SeedStrandedMetaverseObject in this
    // file exist only in that fake, so a zero-join-pass assertion here would either silently find nothing or
    // require a second, parallel EF-backed seed of the same data - real-PostgreSQL coverage is more honest
    // and is what the query needs anyway (RequiresPostgres per src/CLAUDE.md's EF In-Memory Database
    // Limitation and the #1605 PRD's own coverage constraint).

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
    /// A plain Projected Metaverse Object of the context's type with no attribute values and no joins, for
    /// the Deletion Rule evaluation and zero-join pass tests, which care about marking/deletion outcomes
    /// rather than attribute recall.
    /// </summary>
    private MetaverseObject SeedPlainMetaverseObject(ImportRuleContext ctx)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = ctx.MvType, Created = DateTime.UtcNow };
        SyncRepo.SeedMetaverseObject(mvo);
        return mvo;
    }

    /// <summary>
    /// Records that the given Metaverse Object was joined to the system at the moment of a (simulated)
    /// clear, exactly as the clear's own transaction would have written via
    /// <c>ConnectedSystemRepository.DeleteAllConnectedSystemObjectsAndDependenciesAsync</c>'s step zero.
    /// </summary>
    private async Task SeedJoinRecordAsync(ConnectedSystem system, Guid metaverseObjectId)
    {
        DbContext.ConnectorSpaceClearJoinRecords.Add(new ConnectorSpaceClearJoinRecord
        {
            ConnectedSystemId = system.Id,
            MetaverseObjectId = metaverseObjectId,
            ClearedAt = DateTime.UtcNow.AddMinutes(-10)
        });
        await DbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Raises the re-join shortfall threshold so a test's small, deliberately mostly-missing recorded set
    /// does not itself trip the #1605 Functional Requirement 9 refusal; the shortfall check has its own
    /// dedicated tests above.
    /// </summary>
    private async Task SeedMaxMissingPercentAsync(int percent)
    {
        DbContext.ServiceSettingItems.Add(new JIM.Models.Core.ServiceSetting
        {
            Key = JIM.Models.Core.Constants.SettingKeys.PostClearReconciliationMaxMissingPercent,
            DisplayName = "Post-clear reconciliation: maximum missing share",
            Description = "test",
            Category = JIM.Models.Core.ServiceSettingCategory.Synchronisation,
            ValueType = JIM.Models.Core.ServiceSettingValueType.Integer,
            DefaultValue = percent.ToString()
        });
        await DbContext.SaveChangesAsync();
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

    /// <summary>
    /// Arms the sweep with the #1605 gate already open: a clear ten minutes ago, followed by a successful
    /// Full Import five minutes ago. Every existing test in this fixture predates the gate and calls
    /// <see cref="ExecuteStrandedValueSweepAsync"/> directly (which does not itself consult the gate), except
    /// the <c>ExecuteStrandedValueSweepIfArmedAsync</c> tests, which need the gate open to exercise the "runs
    /// the sweep" path; see <see cref="ArmSweepWithGateClosedAsync"/> for the skipped path.
    /// </summary>
    private async Task<ConnectedSystem> ArmSweepAsync(ConnectedSystem system)
    {
        system.StrandedValueSweepArmedAt = DateTime.UtcNow.AddMinutes(-10);
        system.LastSuccessfulFullImportCompletedAt = DateTime.UtcNow.AddMinutes(-5);
        await DbContext.SaveChangesAsync();
        return system;
    }

    /// <summary>
    /// Arms the sweep with the #1605 gate closed: no Full Import has completed since the arming (either
    /// because none ever has, or because the most recent one predates the clear).
    /// </summary>
    private async Task<ConnectedSystem> ArmSweepWithGateClosedAsync(ConnectedSystem system, DateTime? lastSuccessfulFullImportCompletedAt = null)
    {
        system.StrandedValueSweepArmedAt = DateTime.UtcNow.AddMinutes(-5);
        system.LastSuccessfulFullImportCompletedAt = lastSuccessfulFullImportCompletedAt;
        await DbContext.SaveChangesAsync();
        return system;
    }

    private async Task<Activity> BuildActivityAsync(int connectedSystemId)
    {
        var profile = await CreateRunProfileAsync(connectedSystemId, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        return await CreateActivityAsync(connectedSystemId, profile, ConnectedSystemRunType.FullSynchronisation);
    }
}
