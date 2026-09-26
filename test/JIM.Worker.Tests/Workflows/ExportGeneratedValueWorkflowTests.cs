// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// End-to-end workflow tests for export-mode Unique Value Generation (#242, Phase 2 work package H), driving
/// the real Full Synchronisation pipeline (engine, worker, repositories) against an in-memory database, exactly
/// as <see cref="UniqueValueGenerationWorkflowTests"/> does for import mode. Topology throughout: an "HR"
/// Connected System whose import Synchronisation Rule projects a "Person" Metaverse Object Type (Employee Id
/// and an unrelated Note attribute, used to force export re-evaluation without changing Employee Id itself),
/// and a "Ticketing" Connected System whose export Synchronisation Rule provisions and carries a generated
/// "loginName" Attribute Flow (base <c>Lower(mv["EmployeeId"])</c>, only-if-taken). Scenario 9 of the PRD:
/// export-mode generation keys on the Connected System Object and never touches the Metaverse.
/// </summary>
[TestFixture]
public class ExportGeneratedValueWorkflowTests : WorkflowTestBase
{
    #region Provisioning, stability, reassertion

    [Test]
    public async Task FullSync_ProvisioningNewObject_GeneratesLoginNameCommitsAssignmentAndRecordsOutcomeAsync()
    {
        var ctx = await SetUpExportGenerationAsync();
        await SeedHrCsoAsync(ctx, "E1", note: null);

        var activity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            var ticketingCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Ticketing.Id);
            Assert.That(ticketingCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning));

            var pendingExport = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemObjectId == ticketingCso.Id);
            Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create));
            var loginNameChange = pendingExport.AttributeValueChanges.Single(c => c.AttributeId == ctx.TicketingLoginNameAttribute.Id);
            Assert.That(loginNameChange.PendingGeneration, Is.Null, "the marker must be resolved before the Pending Export is persisted");
            Assert.That(loginNameChange.StringValue, Is.EqualTo("e1"));

            Assert.That(SyncRepo.GeneratedValueAssignments, Has.Count.EqualTo(1));
            var assignment = SyncRepo.GeneratedValueAssignments.Values.Single();
            Assert.That(assignment.ConnectedSystemObjectId, Is.EqualTo(ticketingCso.Id), "export-mode assignments key on the Connected System Object");
            Assert.That(assignment.MetaverseObjectId, Is.Null, "export-mode assignments never touch the Metaverse");
            Assert.That(assignment.Value, Is.EqualTo("e1"));

            var assignedOutcomes = activity.RunProfileExecutionItems
                .SelectMany(r => r.SyncOutcomes)
                .Where(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned)
                .ToList();
            Assert.That(assignedOutcomes, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task FullSync_TwoObjectsSameBaseInOnePage_GetDistinctLoginNamesAsync()
    {
        var ctx = await SetUpExportGenerationAsync();
        await SeedHrCsoAsync(ctx, "E1", note: null);
        await SeedHrCsoAsync(ctx, "E1", note: null); // same base on purpose: both objects generate "e1"

        await RunFullSyncReturningActivityAsync(ctx.Hr);

        var loginNames = SyncRepo.PendingExports.Values
            .Where(pe => pe.ConnectedSystemId == ctx.Ticketing.Id)
            .SelectMany(pe => pe.AttributeValueChanges)
            .Where(c => c.AttributeId == ctx.TicketingLoginNameAttribute.Id)
            .Select(c => c.StringValue)
            .ToList();

        Assert.That(loginNames, Is.EquivalentTo(new[] { "e1", "e11" }));
    }

    [Test]
    public async Task FullSync_ReEvaluationWithNoDrift_IsStableAndStagesNoPendingExportAsync()
    {
        var ctx = await SetUpExportGenerationAsync();
        await SeedHrCsoAsync(ctx, "E1", note: null);
        await RunFullSyncReturningActivityAsync(ctx.Hr);

        var assignmentIdBefore = SyncRepo.GeneratedValueAssignments.Keys.Single();
        var ticketingCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Ticketing.Id);

        // A confirming import (out of scope here) is what would normally write the exported value back onto
        // the Connected System Object; simulate it directly so the next evaluation sees the target already
        // holding what was generated.
        ticketingCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = ticketingCso,
            Attribute = ctx.TicketingLoginNameAttribute,
            AttributeId = ctx.TicketingLoginNameAttribute.Id,
            StringValue = "e1"
        });
        SyncRepo.ClearAllPendingExports();

        var hrCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Hr.Id);
        AddOrSetNote(hrCso, ctx, "second"); // forces re-evaluation without touching Employee Id
        await ModifyCsoAsync(hrCso);

        var secondActivity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.GeneratedValueAssignments.Keys.Single(), Is.EqualTo(assignmentIdBefore), "no new assignment");
            Assert.That(SyncRepo.PendingExports.Values.Any(pe => pe.ConnectedSystemId == ctx.Ticketing.Id), Is.False,
                "the Connected System Object already holds the sticky value, so no net-change Update is staged");
            Assert.That(secondActivity.RunProfileExecutionItems.SelectMany(r => r.SyncOutcomes)
                .Any(o => o.OutcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned
                    or ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAdopted), Is.False,
                "a stable re-evaluation records no new outcome");
        }
    }

    [Test]
    public async Task FullSync_TargetValueChangedOutsideJim_ReassertsTheAssignmentValueAsync()
    {
        var ctx = await SetUpExportGenerationAsync();
        var mvo = await SeedPreExistingMvoAsync(ctx, employeeId: "E1", matchKey: "P1");
        var ticketingCso = await SeedTicketingCsoAsync(ctx, mvo.Id, loginName: "somethingelse"); // drifted
        SeedGeneratedValueAssignmentFor(ctx, ticketingCso.Id, "e1");

        ConfigureHrToJoinByMatchKey(ctx);
        await SeedHrCsoAsync(ctx, "E1", note: "first", matchKey: "P1");

        await RunFullSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.GeneratedValueAssignments, Has.Count.EqualTo(1), "no new assignment is created for a reassertion");
            var pendingExport = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemObjectId == ticketingCso.Id);
            var change = pendingExport.AttributeValueChanges.Single(c => c.AttributeId == ctx.TicketingLoginNameAttribute.Id);
            Assert.That(change.StringValue, Is.EqualTo("e1"), "JIM reasserts the value it owns rather than accepting the drifted one");
        }
    }

    #endregion

    #region Adoption removed: generation always overwrites what the target already holds

    /// <summary>
    /// Formerly <c>FullSync_ExistingJoinedTargetAlreadyHoldingAValue_AdoptsItAndStagesNoChangeAsync</c>: before
    /// the product-owner decision to remove connector-space adoption, a joined target already holding the
    /// value a generation would have produced was adopted and no change was staged. Adoption is gone in export
    /// mode entirely (#242): with no assignment, JIM always generates and exports, overwriting whatever the
    /// target currently holds, exactly like any other export Attribute Flow.
    /// </summary>
    [Test]
    public async Task FullSync_ExistingJoinedTargetAlreadyHoldingADifferentValue_GeneratesAndExportsOverwritingItAsync()
    {
        var ctx = await SetUpExportGenerationAsync();
        var mvo = await SeedPreExistingMvoAsync(ctx, employeeId: "E1", matchKey: "P1");
        var ticketingCso = await SeedTicketingCsoAsync(ctx, mvo.Id, loginName: "somethingelse"); // no assignment recorded for it

        ConfigureHrToJoinByMatchKey(ctx);
        await SeedHrCsoAsync(ctx, "E1", note: "first", matchKey: "P1");

        var activity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            var pendingExport = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemObjectId == ticketingCso.Id);
            var change = pendingExport.AttributeValueChanges.Single(c => c.AttributeId == ctx.TicketingLoginNameAttribute.Id);
            Assert.That(change.StringValue, Is.EqualTo("e1"), "the value is generated from the base expression, never adopted from the target's existing value");

            Assert.That(SyncRepo.GeneratedValueAssignments, Has.Count.EqualTo(1));
            var assignment = SyncRepo.GeneratedValueAssignments.Values.Single();
            Assert.That(assignment.Adopted, Is.False, "connector-space adoption has been removed");
            Assert.That(assignment.Value, Is.EqualTo("e1"));

            Assert.That(activity.RunProfileExecutionItems.SelectMany(r => r.SyncOutcomes)
                .Any(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned), Is.True);
            Assert.That(activity.RunProfileExecutionItems.SelectMany(r => r.SyncOutcomes)
                .Any(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAdopted), Is.False);
        }
    }

    #endregion

    #region Initial Export Only

    [Test]
    public async Task FullSync_InitialExportOnly_ExistingJoinedCsoGetsNoExportNoAssignmentAndConsumesNoSequenceNumberAsync()
    {
        var ctx = await SetUpSequenceExportGenerationAsync(sequenceStart: 1000);
        var mapping = SyncRepo.SyncRules[ctx.TicketingExportRuleId].AttributeFlowRules.Single(m => m.Generation != null);
        mapping.InitialExportOnly = true;
        await DbContext.SaveChangesAsync();

        // A brownfield, already-joined Ticketing Connected System Object (Update, not Create): InitialExportOnly
        // means JIM never manages this attribute on an object it did not itself provision.
        var mvo = await SeedPreExistingMvoAsync(ctx, employeeId: "E1", matchKey: "P1");
        var ticketingCso = await SeedTicketingCsoAsync(ctx, mvo.Id, loginName: null);

        ConfigureHrToJoinByMatchKey(ctx);
        await SeedHrCsoAsync(ctx, "E1", note: "first", matchKey: "P1");

        await RunFullSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            var pendingExport = SyncRepo.PendingExports.Values.SingleOrDefault(pe => pe.ConnectedSystemObjectId == ticketingCso.Id);
            var accountNumberFlowed = pendingExport?.AttributeValueChanges.Any(c => c.AttributeId == ctx.TicketingAccountNumberAttribute!.Id) ?? false;
            Assert.That(accountNumberFlowed, Is.False, "InitialExportOnly must not flow on an Update to an already-existing joined Connected System Object");
            Assert.That(SyncRepo.GeneratedValueAssignments, Is.Empty, "a mapping that never evaluates must never record an assignment");
            Assert.That(SyncRepo.GeneratedValueSequences, Is.Empty, "a mapping that never evaluates must never allocate a sequence number");
        }
    }

    [Test]
    public async Task FullSync_InitialExportOnly_ProvisionedCsoStillGetsAGeneratedValueAsync()
    {
        var ctx = await SetUpSequenceExportGenerationAsync(sequenceStart: 1000);
        var mapping = SyncRepo.SyncRules[ctx.TicketingExportRuleId].AttributeFlowRules.Single(m => m.Generation != null);
        mapping.InitialExportOnly = true;
        await DbContext.SaveChangesAsync();

        await SeedHrCsoAsync(ctx, "E1", note: null); // no prior join: Ticketing is provisioned (Create)

        await RunFullSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            var ticketingCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Ticketing.Id);
            Assert.That(ticketingCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning));

            var pendingExport = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemObjectId == ticketingCso.Id);
            var change = pendingExport.AttributeValueChanges.Single(c => c.AttributeId == ctx.TicketingAccountNumberAttribute!.Id);
            Assert.That(change.IntValue, Is.EqualTo(1000), "InitialExportOnly still flows on the provisioning Create");

            Assert.That(SyncRepo.GeneratedValueAssignments, Has.Count.EqualTo(1));
            // A sequence row now exists (the mapping evaluated and allocated), unlike the Update case above.
            // Its exact NextValue depends on the resolve options' block size (numbers are reserved in blocks,
            // not one at a time), so this asserts existence rather than a specific number.
            Assert.That(SyncRepo.GeneratedValueSequences, Has.Count.EqualTo(1));
        }
    }

    #endregion

    #region Sequence token

    [Test]
    public async Task FullSync_SequenceTokenOnNumberTarget_AssignsSequentialNumbersAsync()
    {
        var ctx = await SetUpSequenceExportGenerationAsync(sequenceStart: 1000);
        await SeedHrCsoAsync(ctx, "E1", note: null);
        await SeedHrCsoAsync(ctx, "E2", note: null);

        await RunFullSyncReturningActivityAsync(ctx.Hr);

        var accountNumbers = SyncRepo.PendingExports.Values
            .Where(pe => pe.ConnectedSystemId == ctx.Ticketing.Id)
            .SelectMany(pe => pe.AttributeValueChanges)
            .Where(c => c.AttributeId == ctx.TicketingAccountNumberAttribute!.Id)
            .Select(c => c.IntValue)
            .ToList();

        Assert.That(accountNumbers, Is.EquivalentTo(new int?[] { 1000, 1001 }));
    }

    #endregion

    #region Sync Preview (outbound)

    [Test]
    public async Task PreviewSyncForMvo_GeneratedExportMapping_ShowsCandidateAndWritesNothingAsync()
    {
        var ctx = await SetUpExportGenerationAsync();
        var mvo = await SeedPreExistingMvoAsync(ctx, employeeId: "E1", matchKey: "P1");

        var previewResult = await Jim.SyncPreview.PreviewSyncForMvoAsync(mvo.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.PendingExports, Is.Empty, "a preview must write nothing");
            Assert.That(SyncRepo.GeneratedValueAssignments, Is.Empty, "a preview must write nothing");

            var proposedExport = previewResult.Outbound.ProposedExports.Single(pe => pe.ConnectedSystemId == ctx.Ticketing.Id);
            Assert.That(proposedExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create));
            var loginNameChange = proposedExport.AttributeValueChanges.Single(c => c.AttributeId == ctx.TicketingLoginNameAttribute.Id);
            Assert.That(loginNameChange.StringValue, Is.EqualTo("e1"), "the preview shows the candidate value");
            Assert.That(loginNameChange.PendingGeneration, Is.Null, "the preview resolves the marker for display, never leaving it unresolved");
        }
    }

    #endregion

    #region Exhaustion, Waiting

    [Test]
    public async Task FullSync_Exhaustion_RecordsErrorAndStillProvisionsWithoutTheAttributeAsync()
    {
        var ctx = await SetUpExportGenerationAsync(attemptLimit: 1);

        // Pre-claim "e1" for a different, unrelated Connected System Object so the one and only attempt this
        // object gets (AttemptLimit 1, Only-if-taken attempt 0) is already taken.
        SyncRepo.SeedGeneratedValueAssignment(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObjectId = Guid.NewGuid(),
            ConnectedSystemObjectTypeAttributeId = ctx.TicketingLoginNameAttribute.Id,
            Value = "e1",
            NormalisedValue = "e1",
            State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = 0,
            Created = DateTime.UtcNow,
            LastUpdated = DateTime.UtcNow
        });

        await SeedHrCsoAsync(ctx, "E1", note: null);

        var activity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            var ticketingCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Ticketing.Id);
            Assert.That(ticketingCso, Is.Not.Null, "provisioning still goes ahead despite the failure");

            var pendingExport = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemObjectId == ticketingCso.Id);
            Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create));
            Assert.That(pendingExport.AttributeValueChanges.Any(c => c.AttributeId == ctx.TicketingLoginNameAttribute.Id), Is.False,
                "the generated attribute is simply absent");

            var errorItem = activity.RunProfileExecutionItems.Single(r => r.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet);
            Assert.That(errorItem.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.GeneratedValueExhausted));
        }
    }

    [Test]
    public async Task FullSync_WaitingUpdateWithNoStickyAssignment_StagesNothingAsync()
    {
        var ctx = await SetUpExportGenerationAsync();
        // A brownfield Ticketing object (Update case, not Create) with nothing generated for it before, and
        // no Employee Id: the base expression's input is missing from the only evaluation this test drives.
        var mvo = await SeedPreExistingMvoAsync(ctx, employeeId: null, matchKey: "P1");
        await SeedTicketingCsoAsync(ctx, mvo.Id, loginName: null);

        ConfigureHrToJoinByMatchKey(ctx);
        await SeedHrCsoAsync(ctx, employeeId: null, note: "first", matchKey: "P1");

        await RunFullSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.PendingExports.Values.Any(pe => pe.ConnectedSystemId == ctx.Ticketing.Id), Is.False,
                "a Waiting Update with nothing else to export stages no Pending Export at all");
            Assert.That(SyncRepo.GeneratedValueAssignments, Is.Empty);
        }
    }

    #endregion

    #region Integrity guard

    [Test]
    public async Task FlushPendingExportOperationsAsync_UnresolvedMarkerLeftOnAChange_ThrowsAsync()
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var connectedSystem = await CreateConnectedSystemAsync("Ticketing");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "TicketingUser");
        var exportRule = await CreateExportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "Ticketing Export");
        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Ticketing Export", ConnectedSystemRunType.Export);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.Export);

        var loginNameAttr = csoType.Attributes.First(a => a.Name == "DisplayName");
        var mapping = new SyncRuleMapping
        {
            SyncRule = exportRule,
            SyncRuleId = exportRule.Id,
            TargetConnectedSystemAttribute = loginNameAttr,
            TargetConnectedSystemAttributeId = loginNameAttr.Id,
            Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.OnlyIfTaken }
        };

        var leftoverChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            Attribute = loginNameAttr,
            AttributeId = loginNameAttr.Id,
            ChangeType = PendingExportAttributeChangeType.Update,
            PendingGeneration = new JIM.Models.Sync.PendingGeneratedExportValue { Mapping = mapping, BaseValue = "leftover", BaseUnavailable = false }
        };
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystemObjectId = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            AttributeValueChanges = { leftoverChange }
        };

        var processor = new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, connectedSystem, runProfile, activity, new CancellationTokenSource());
        processor.QueuePendingExportForTests(pendingExport);

        Assert.That(async () => await processor.FlushPendingExportOperationsForTestsAsync(),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("unresolved generated value marker"));
    }

    #endregion

    #region Drift merge integrity

    /// <summary>
    /// Reproduces a bug found by Scenario 23 at runtime (not part of #242's original scope, fixed alongside it):
    /// drift detection stages a corrective Pending Export for a Connected System Object earlier in the same
    /// page (added straight to the worker's <c>_pendingExportsToCreate</c> batch); export evaluation for the
    /// SAME Connected System Object later in the same page then merges its own changes into that already-staged
    /// row (<c>ExportEvaluationServer.CreateOrUpdatePendingExportWithNoNetChangeAsync</c>'s in-memory merge
    /// branch) rather than creating a new one. When one of those merged-in changes carries an unresolved
    /// generated value marker, <see cref="SyncTaskProcessorBase.ResolveExportGeneratedValuesAsync"/> used to scan
    /// only <c>result.PendingExports</c>, which never included the merged-into row, so the marker survived
    /// unresolved into <c>FlushPendingExportOperationsAsync</c>'s integrity guard and the whole page threw. The
    /// fix (<c>ExportEvaluationResult.MergedExistingPendingExports</c>) is what this test proves.
    /// <para>
    /// Simulates the "drift already staged a Pending Export" half directly via <see cref="SyncTaskProcessorBase.QueuePendingExportForTests"/>
    /// (real drift detection needs EnforceState plus a genuinely drifted target value, which is unrelated
    /// machinery this test does not need to exercise): the state it leaves behind - an existing Update Pending
    /// Export for the target Connected System Object, sitting in the worker's batch before export evaluation
    /// runs - is exactly what matters here, however it got there.
    /// </para>
    /// </summary>
    [Test]
    public async Task FullSync_GeneratedChangeMergesIntoAnAlreadyStagedPendingExport_ResolvesTheMarkerWithNoExceptionAsync()
    {
        var ctx = await SetUpExportGenerationAsync();
        var mvo = await SeedPreExistingMvoAsync(ctx, employeeId: null, matchKey: "P1"); // no Employee Id yet
        var ticketingCso = await SeedTicketingCsoAsync(ctx, mvo.Id, loginName: null);
        var accountNumberAttr = SyncRepo.ObjectTypes[ctx.TicketingCsoTypeId].Attributes.Single(a => a.Name == "accountNumber");

        ConfigureHrToJoinByMatchKey(ctx);
        await SeedHrCsoAsync(ctx, "E1", note: "first", matchKey: "P1");

        var reloaded = await ReloadEntityAsync(ctx.Hr);
        var profile = await CreateRunProfileAsync(reloaded.Id, "HR Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        var processor = new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource());

        // Stand in for a drift-detected corrective Pending Export already staged for this same Connected
        // System Object earlier in the page: an ordinary Update change, unrelated to the generated attribute.
        var driftPendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ctx.Ticketing.Id,
            ConnectedSystemObjectId = ticketingCso.Id,
            ChangeType = PendingExportChangeType.Update,
            AttributeValueChanges =
            {
                new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(),
                    Attribute = accountNumberAttr,
                    AttributeId = accountNumberAttr.Id,
                    ChangeType = PendingExportAttributeChangeType.Update,
                    IntValue = 99
                }
            }
        };
        processor.QueuePendingExportForTests(driftPendingExport);

        Assert.That(async () => await processor.PerformFullSyncAsync(), Throws.Nothing,
            "a generated change merged into an already-staged Pending Export must resolve, not throw");

        using (Assert.EnterMultipleScope())
        {
            var pendingExport = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemObjectId == ticketingCso.Id);
            var loginNameChange = pendingExport.AttributeValueChanges.Single(c => c.AttributeId == ctx.TicketingLoginNameAttribute.Id);
            Assert.That(loginNameChange.StringValue, Is.EqualTo("e1"));
            Assert.That(loginNameChange.PendingGeneration, Is.Null, "the marker must be resolved before persistence");
            Assert.That(pendingExport.AttributeValueChanges.Any(c => c.AttributeId == accountNumberAttr.Id), Is.True,
                "the pre-staged (drift-standing-in) change survives the merge alongside the resolved generated one");

            Assert.That(SyncRepo.GeneratedValueAssignments, Has.Count.EqualTo(1));
            var assignedOutcomes = activity.RunProfileExecutionItems.SelectMany(r => r.SyncOutcomes)
                .Where(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned)
                .ToList();
            Assert.That(assignedOutcomes, Has.Count.EqualTo(1));
        }
    }

    #endregion

    #region Helpers

    private sealed record ExportGenerationContext(
        ConnectedSystem Hr,
        ConnectedSystem Ticketing,
        MetaverseObjectType MvType,
        MetaverseAttribute MvEmployeeIdAttribute,
        MetaverseAttribute MvDisplayNameAttribute,
        MetaverseAttribute MvNoteAttribute,
        int HrCsoTypeId,
        int HrImportRuleId,
        int TicketingCsoTypeId,
        ConnectedSystemObjectTypeAttribute TicketingLoginNameAttribute,
        int TicketingExportRuleId,
        ConnectedSystemObjectTypeAttribute? TicketingAccountNumberAttribute = null);

    /// <summary>
    /// Builds the basic topology: a Person Metaverse Object Type (Employee Id), an HR Connected System whose
    /// import Synchronisation Rule projects it (Employee Id plus an unrelated Note attribute used to force
    /// re-evaluation), and a Ticketing Connected System whose export Synchronisation Rule provisions and
    /// carries a generated only-if-taken loginName Attribute Flow (base <c>Lower(mv["EmployeeId"])</c>).
    /// </summary>
    private async Task<ExportGenerationContext> SetUpExportGenerationAsync(int attemptLimit = 1000)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName"); // used as the join/match key
        var mvNoteAttr = mvType.Attributes.First(a => a.Name == "Type"); // repurposed as the unrelated trigger attribute

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "HrUser", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "employeeId", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "name", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "note", Type = AttributeDataType.Text, Selected = true }
        });
        var hrEmployeeIdAttr = hrType.Attributes.Single(a => a.Name == "employeeId");
        var hrNameAttr = hrType.Attributes.Single(a => a.Name == "name");
        var hrNoteAttr = hrType.Attributes.Single(a => a.Name == "note");

        var importRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            SyncRuleId = importRule.Id,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrEmployeeIdAttr, ConnectedSystemAttributeId = hrEmployeeIdAttr.Id } }
        });
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            SyncRuleId = importRule.Id,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrNameAttr, ConnectedSystemAttributeId = hrNameAttr.Id } }
        });
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            SyncRuleId = importRule.Id,
            TargetMetaverseAttribute = mvNoteAttr,
            TargetMetaverseAttributeId = mvNoteAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrNoteAttr, ConnectedSystemAttributeId = hrNoteAttr.Id } }
        });

        var ticketing = await CreateConnectedSystemAsync("Ticketing");
        var ticketingType = await CreateCsoTypeAsync(ticketing.Id, "TicketingUser", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "loginName", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "accountNumber", Type = AttributeDataType.Number, Selected = true }
        });
        var loginNameAttr = ticketingType.Attributes.Single(a => a.Name == "loginName");

        var exportRule = await CreateExportSyncRuleAsync(ticketing.Id, ticketingType, mvType, "Ticketing Export", enableProvisioning: true);
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            SyncRuleId = exportRule.Id,
            TargetConnectedSystemAttribute = loginNameAttr,
            TargetConnectedSystemAttributeId = loginNameAttr.Id,
            Generation = new SyncRuleMappingGeneration
            {
                TokenKind = GeneratedValueTokenKind.OnlyIfTaken,
                SuffixStyle = GeneratedValueSuffixStyle.Number,
                SuffixStart = 1,
                AttemptLimit = attemptLimit,
                NeverReuse = true
            },
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = "Lower(mv[\"EmployeeId\"])" } }
        });
        await DbContext.SaveChangesAsync();

        return new ExportGenerationContext(
            hr, ticketing, mvType, mvEmployeeIdAttr, mvDisplayNameAttr, mvNoteAttr,
            hrType.Id, importRule.Id, ticketingType.Id, loginNameAttr, exportRule.Id);
    }

    /// <summary>
    /// Builds the same topology as <see cref="SetUpExportGenerationAsync"/>, except the generated mapping
    /// targets accountNumber (Number) with a Sequence token instead of Only-if-taken.
    /// </summary>
    private async Task<ExportGenerationContext> SetUpSequenceExportGenerationAsync(long sequenceStart)
    {
        var ctx = await SetUpExportGenerationAsync();

        var accountNumberAttr = SyncRepo.ObjectTypes[ctx.TicketingCsoTypeId].Attributes.Single(a => a.Name == "accountNumber");
        var exportRule = SyncRepo.SyncRules[ctx.TicketingExportRuleId];
        var mapping = exportRule.AttributeFlowRules.Single(m => m.Generation != null);
        mapping.Sources.Clear();
        mapping.TargetConnectedSystemAttribute = accountNumberAttr;
        mapping.TargetConnectedSystemAttributeId = accountNumberAttr.Id;
        mapping.Generation = new SyncRuleMappingGeneration
        {
            TokenKind = GeneratedValueTokenKind.Sequence,
            SequenceStart = sequenceStart,
            SequenceIncrement = 1,
            AttemptLimit = 1000,
            NeverReuse = true
        };
        await DbContext.SaveChangesAsync();

        return ctx with { TicketingAccountNumberAttribute = accountNumberAttr };
    }

    private async Task<ConnectedSystemObject> SeedHrCsoAsync(ExportGenerationContext ctx, string? employeeId, string? note, string? matchKey = null)
    {
        var hrType = SyncRepo.ObjectTypes[ctx.HrCsoTypeId];
        var externalIdAttr = hrType.Attributes.Single(a => a.IsExternalId);
        var employeeIdAttr = hrType.Attributes.Single(a => a.Name == "employeeId");
        var nameAttr = hrType.Attributes.Single(a => a.Name == "name");
        var noteAttr = hrType.Attributes.Single(a => a.Name == "note");

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ctx.Hr.Id,
            TypeId = hrType.Id,
            Type = hrType,
            ConnectedSystem = SyncRepo.ConnectedSystems[ctx.Hr.Id],
            Created = DateTime.UtcNow
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = externalIdAttr.Id, Attribute = externalIdAttr, GuidValue = Guid.NewGuid() });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = nameAttr.Id, Attribute = nameAttr, StringValue = matchKey ?? Guid.NewGuid().ToString() });
        if (employeeId != null)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = employeeIdAttr.Id, Attribute = employeeIdAttr, StringValue = employeeId });
        if (note != null)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = noteAttr.Id, Attribute = noteAttr, StringValue = note });

        SyncRepo.SeedConnectedSystemObject(cso);
        await Task.CompletedTask;
        return cso;
    }

    private static void AddOrSetNote(ConnectedSystemObject hrCso, ExportGenerationContext ctx, string note)
    {
        var noteAttr = hrCso.Type!.Attributes.Single(a => a.Name == "note");
        var existing = hrCso.AttributeValues.SingleOrDefault(av => av.AttributeId == noteAttr.Id);
        if (existing != null)
        {
            existing.StringValue = note;
        }
        else
        {
            hrCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = noteAttr.Id,
                Attribute = noteAttr,
                StringValue = note
            });
        }
    }

    /// <summary>
    /// Seeds a Metaverse Object directly, bypassing any Connected System's projection: used to establish a
    /// brownfield object (with a Ticketing Connected System Object already joined to it, seeded separately)
    /// before HR's own sync ever runs, so HR's first relevant evaluation sees an Update, never a Create.
    /// </summary>
    private async Task<MetaverseObject> SeedPreExistingMvoAsync(ExportGenerationContext ctx, string? employeeId, string matchKey)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = ctx.MvType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), MetaverseObject = mvo, Attribute = ctx.MvDisplayNameAttribute, AttributeId = ctx.MvDisplayNameAttribute.Id, StringValue = matchKey
        });
        if (employeeId != null)
        {
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(), MetaverseObject = mvo, Attribute = ctx.MvEmployeeIdAttribute, AttributeId = ctx.MvEmployeeIdAttribute.Id, StringValue = employeeId
            });
        }

        DbContext.MetaverseObjects.Add(mvo);
        await DbContext.SaveChangesAsync();
        SyncRepo.SeedMetaverseObject(mvo);
        return mvo;
    }

    /// <summary>
    /// Reconfigures HR's import rule to join an existing Metaverse Object by the "name"/DisplayName match key
    /// instead of projecting a new one, mirroring the import-side adoption tests' brownfield-join topology.
    /// </summary>
    private void ConfigureHrToJoinByMatchKey(ExportGenerationContext ctx)
    {
        var hrType = SyncRepo.ObjectTypes[ctx.HrCsoTypeId];
        var nameAttr = hrType.Attributes.Single(a => a.Name == "name");
        var importRule = SyncRepo.SyncRules[ctx.HrImportRuleId];
        importRule.ProjectToMetaverse = false;
        importRule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = importRule,
            SyncRuleId = importRule.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = ctx.MvDisplayNameAttribute,
            TargetMetaverseAttributeId = ctx.MvDisplayNameAttribute.Id,
            Sources = { new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = nameAttr, ConnectedSystemAttributeId = nameAttr.Id } }
        });
    }

    private void SeedGeneratedValueAssignmentFor(ExportGenerationContext ctx, Guid connectedSystemObjectId, string value)
    {
        var generation = SyncRepo.SyncRules[ctx.TicketingExportRuleId].AttributeFlowRules.Single(m => m.Generation != null).Generation!;
        SyncRepo.SeedGeneratedValueAssignment(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObjectId = connectedSystemObjectId,
            ConnectedSystemObjectTypeAttributeId = ctx.TicketingLoginNameAttribute.Id,
            Value = value,
            NormalisedValue = value.ToLowerInvariant(),
            State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = generation.Id,
            Created = DateTime.UtcNow,
            LastUpdated = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Seeds a brownfield, already-joined Ticketing Connected System Object (Status Normal, not
    /// PendingProvisioning), so export staging evaluates it as an Update rather than provisioning a new one.
    /// </summary>
    private async Task<ConnectedSystemObject> SeedTicketingCsoAsync(ExportGenerationContext ctx, Guid metaverseObjectId, string? loginName)
    {
        var ticketingType = SyncRepo.ObjectTypes[ctx.TicketingCsoTypeId];
        var externalIdAttr = ticketingType.Attributes.Single(a => a.IsExternalId);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ctx.Ticketing.Id,
            TypeId = ticketingType.Id,
            Type = ticketingType,
            ConnectedSystem = SyncRepo.ConnectedSystems[ctx.Ticketing.Id],
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            MetaverseObjectId = metaverseObjectId,
            DateJoined = DateTime.UtcNow,
            Created = DateTime.UtcNow
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = externalIdAttr.Id, Attribute = externalIdAttr, GuidValue = Guid.NewGuid() });
        if (loginName != null)
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = ctx.TicketingLoginNameAttribute.Id,
                Attribute = ctx.TicketingLoginNameAttribute,
                StringValue = loginName
            });
        }

        SyncRepo.SeedConnectedSystemObject(cso);

        // Register the join on the Metaverse Object side too (the projected navigation), so the export
        // evaluation cache's target CSO lookup finds it as an existing object for this Metaverse Object.
        if (SyncRepo.MetaverseObjects.TryGetValue(metaverseObjectId, out var mvo))
            mvo.ConnectedSystemObjects.Add(cso);

        await Task.CompletedTask;
        return cso;
    }

    private async Task<Activity> RunFullSyncReturningActivityAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    #endregion
}
