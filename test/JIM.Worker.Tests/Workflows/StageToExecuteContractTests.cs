// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Contract test between Pending Export STAGING (<c>ExportEvaluationServer</c>, driven by the sync task
/// processors) and Pending Export EXECUTION (<c>ExportExecutionServer</c>, driven by
/// <see cref="SyncExportTaskProcessor"/>). Existing tests exercise each half in isolation: staging tests
/// assert what gets staged; execution tests feed hand-built Pending Exports. Nothing previously checked
/// that what staging actually produces is something execution can handle, nor what state is left behind.
/// Three real defects lived in exactly that gap (see the three "Defect" tests below, each a standalone,
/// sharply-asserted regression before the wider matrix runs the same shapes more broadly):
///
/// 1. A Metaverse Object deleted before its provisioning Create was ever exported: staging used to replace
///    the unsent Create with a Delete carrying no External ID; export then failed ("Delete export has no
///    External ID value") and the Pending Provisioning Connected System Object (CSO) was stranded. Fixed:
///    staging cancels the provisioning outright (<see cref="SyncEngine.IsProvisioningNeverExported"/>,
///    <c>ExportEvaluationServer.TryCancelNeverExportedProvisioningOnScopeOutAsync</c> /
///    <c>CancelNeverExportedProvisioningAsync</c>).
/// 2. A Pending Provisioning CSO with NO Pending Export was wrongly treated as "never exported", but a
///    file export with auto-confirm deletes the Pending Export immediately after sending, so the object
///    really did reach the target and would have been orphaned. Fixed: null Pending Export now means "was
///    exported", so a Delete is staged.
/// 3. Create exported but never confirmed by import, Metaverse Object then deleted: the Delete staged and
///    exported correctly, but the CSO stayed Pending Provisioning for ever (import deletion detection
///    excludes Pending Provisioning objects, so nothing else could ever remove it). Fixed:
///    <c>ExportExecutionServer.RemoveUnconfirmedProvisioningCsosAsync</c> removes the CSO once its Delete
///    export succeeds.
///
/// Every case drives the REAL sync task processors end to end with NO hand-built Pending Exports: source
/// import/sync provisions to a target (real staging) -&gt; optionally export (real execution) -&gt;
/// optionally a confirming import on the target -&gt; a change arrives at the source -&gt; sync (real
/// staging) -&gt; optionally export (real execution) -&gt; a final confirming import/sync where the target
/// object still exists to confirm. One shared helper (<see cref="AssertContract"/>) checks the contract
/// after every case:
///
/// 1. Every Delete Pending Export staging produces targets a CSO holding an External ID value (the shape
///    of defect 1), checked immediately after each staging step, before execution runs.
/// 2. Execution never throws and the export Activity carries no UnhandledError items, for anything staging
///    produced.
/// 3. End-state invariants after the final confirming import/sync (mirroring
///    <c>Assert-SyncStateInvariants</c> in test/integration/utils/Test-Helpers.ps1): no stranded Pending
///    Provisioning + NotJoined CSO (none at all, or only an unsent Create), and no Pending, no-attribute-
///    change Delete Pending Export whose CSO holds no attribute values either.
/// 4. The target connector received exactly the expected operations for the case (recorded per connector
///    instance via its own call log), derived from docs/configuration/synchronisation-rules.md and
///    engineering/notes/PENDING_EXPORT_LIFECYCLE.md, not from what the code happens to do.
///
/// Connector failure is deliberately OUT OF SCOPE for this fixture, per the brief, except that keeping
/// defect 1's regression meaningful requires the real "Delete export has no External ID value" failure
/// shape to still be reachable in principle - contract clause 1 exists precisely to catch a regression of
/// it before execution would ever see it.
/// </summary>
[TestFixture]
public class StageToExecuteContractTests : WorkflowTestBase
{
    #region Case matrix plumbing

    public enum CsoLifecycleState
    {
        /// <summary>Provisioned but the Create Pending Export has never been sent (unsent, zero attempts).</summary>
        NeverExported,

        /// <summary>The Create was exported successfully but no confirming import has run yet; the CSO is
        /// still Pending Provisioning. Depending on the export path, the Create Pending Export may have
        /// been deleted already (auto-confirming file export) or left in Exported status.</summary>
        ExportedUnconfirmed,

        /// <summary>The Create was exported and confirmed by a subsequent import; the CSO is Normal.</summary>
        Confirmed
    }

    public enum ChangeKind
    {
        /// <summary>An attribute the export rule flows changes at the source.</summary>
        AttributeUpdate,

        /// <summary>The source object no longer satisfies the export rule's scoping criteria.</summary>
        LeavesScope,

        /// <summary>The source object is removed, and an immediate (zero grace period) deletion rule
        /// deletes the owning Metaverse Object synchronously during Delta Sync.</summary>
        MvoDeleted
    }

    public enum ExportPath
    {
        /// <summary>Batched "calls" style (LDAP/SQL/SCIM-shaped): <see cref="MockCallConnector"/>.</summary>
        Call,

        /// <summary>File-shaped export with the connector's auto-confirm capability enabled.</summary>
        FileAutoConfirm,

        /// <summary>File-shaped export with auto-confirm unavailable (a Delete stays Exported, awaiting a
        /// confirming import, exactly like the batched-calls path).</summary>
        FileNoAutoConfirm
    }

    /// <summary>
    /// One matrix cell. <see cref="ExecuteFollowUpExport"/> is false for the cells where nothing should
    /// ever reach a connector (provisioning cancellation, and attribute updates on a still-unsent Create,
    /// where there is nothing to execute yet). Expected counts are for the WHOLE scenario (the initial
    /// provisioning export, when one runs, plus the follow-up export after the tested change).
    /// </summary>
    public sealed record ContractCase(
        string Name,
        CsoLifecycleState InitialState,
        ChangeKind Change,
        OutboundDeprovisionAction Action,
        ExportPath ExportPath,
        bool ExecuteFollowUpExport,
        int ExpectedCreates,
        int ExpectedUpdates,
        int ExpectedDeletes,
        bool ExpectedCsoRemoved,
        ConnectedSystemObjectStatus? ExpectedFinalStatus = null,
        ConnectedSystemObjectJoinType? ExpectedFinalJoinType = null,
        string? ExpectedFinalDisplayName = null)
    {
        public override string ToString() => Name;
    }

    #endregion

    #region The expectation table (derived from docs/configuration/synchronisation-rules.md and
    // engineering/notes/PENDING_EXPORT_LIFECYCLE.md, not from the implementation)
    //
    // Case                                                         | Expected connector calls  | Result
    // --------------------------------------------------------------------------------------------------------------------
    // NeverExported + (LeavesScope|MvoDeleted) x (Delete|Disconnect)| none                      | Provisioning cancelled: CSO
    //                                                                                              + unsent Create both removed,
    //                                                                                              nothing ever reaches the target
    //                                                                                              ("one case sits outside the
    //                                                                                              action altogether" - docs).
    // NeverExported + AttributeUpdate                               | none (nothing executes)   | The unsent Create's staged
    //                                                                                              value updates in place; still
    //                                                                                              exactly one unsent Create.
    // Confirmed + AttributeUpdate x {Call, FileAutoConfirm,          | 1 Create (provisioning)   | A single Update carries the
    //   FileNoAutoConfirm}                                          | + 1 Update (follow-up)    | latest value; CSO stays Normal.
    // ExportedUnconfirmed + AttributeUpdate x {Call,                 | 1 Create + 1 Update,      | The Update is not sent before
    //   FileAutoConfirm, FileNoAutoConfirm}                          | Create always first       | the Create is confirmed (auto-
    //   (dedicated test, two sync/export/confirm cycles)                                          | confirm counts as confirmation,
    //                                                                                              | so it may go on the very next
    //                                                                                              | export there); CSO ends Normal
    //                                                                                              | + Provisioned with the new value.
    // ExportedUnconfirmed + (LeavesScope|MvoDeleted) + Delete        | 1 Create + 1 Delete       | Delete export succeeds and
    //   x {Call, FileAutoConfirm, FileNoAutoConfirm}                                              | removes the CSO even though it
    //                                                                                              was never confirmed by import
    //                                                                                              (defects 2 and 3's shape).
    // ExportedUnconfirmed + (LeavesScope|MvoDeleted) + Disconnect    | 1 Create (provisioning)   | Nothing further is exported
    //   x {Call, FileAutoConfirm, FileNoAutoConfirm}                 | only, no follow-up export | ("Disconnect: ... Nothing is
    //                                                                                              exported" - docs); the object
    //                                                                                              is still present in the
    //                                                                                              target, so its own next
    //                                                                                              confirming import (unrelated
    //                                                                                              to any export) resolves the
    //                                                                                              CSO to Normal + NotJoined -
    //                                                                                              not stranded.
    // Confirmed + (LeavesScope|MvoDeleted) + Delete                  | 1 Create + 1 Delete       | CSO removed.
    //   x {Call, FileAutoConfirm, FileNoAutoConfirm}
    // Confirmed + (LeavesScope|MvoDeleted) + Disconnect               | 1 Create, no Delete       | CSO ends disconnected
    //                                                                                              (Normal, NotJoined), not
    //                                                                                              stranded as Pending
    //                                                                                              Provisioning (docs' own
    //                                                                                              worked example).
    #endregion

    #region Named regression cases (the three defects, first, sharply asserted)

    /// <summary>
    /// Defect 1: a Metaverse Object deleted before its provisioning Create was ever exported must cancel
    /// the provisioning outright, never stage a Delete (which would carry no External ID and fail export).
    /// </summary>
    [Test]
    public async Task Defect1_NeverExportedProvisioning_MvoDeleted_CancelsProvisioningNotDeleteExportAsync()
    {
        var topology = await BuildTopologyAsync(OutboundDeprovisionAction.Delete);
        var (sourceCso, _) = await CreateSourceCsoAsync(topology, "Never Exported User", "EMP0001");
        await RunFullSyncAsync(topology.Source, "Provisioning Full Sync");

        var targetCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == topology.Target.Id);
        Assert.That(targetCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning),
            "arrange: provisioning must stage a Pending Provisioning target CSO");
        AssertStagedDeletesHaveExternalId("Defect1", PendingExportsFor(targetCso.Id));

        sourceCso.Status = ConnectedSystemObjectStatus.Obsolete;
        sourceCso.LastUpdated = DateTime.UtcNow;
        var deletionActivity = await RunDeltaSyncAsync(topology.Source, "Deletion Delta Sync");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.ConnectedSystemObjects.ContainsKey(targetCso.Id), Is.False,
                "the never-exported CSO must be removed, not left to export a Delete with no External ID");
            Assert.That(PendingExportsFor(targetCso.Id), Is.Empty, "no Pending Export may remain for it");
            var cancelled = deletionActivity.RunProfileExecutionItems
                .SelectMany(r => r.SyncOutcomes)
                .SingleOrDefault(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled);
            Assert.That(cancelled, Is.Not.Null, "the cancellation must be reported on the Activity");
        }

        AssertContract("Defect1", stagedBeforeExecution: [], executedActivities: [], expectedCreates: 0, expectedUpdates: 0, expectedDeletes: 0,
            connectorCallLogs: []);
    }

    /// <summary>
    /// Defect 2: a Pending Provisioning CSO with NO Pending Export (an auto-confirming file export deletes
    /// the Create the moment it succeeds) is NOT "never exported" - it really reached the target. Deleting
    /// its owning Metaverse Object must stage AND execute a real Delete, not fall into the defect 1 path.
    /// Runs the full lifecycle: stage, export, and the contract assertions.
    /// </summary>
    [Test]
    public async Task Defect2_AutoConfirmedUnconfirmedProvisioning_MvoDeleted_StagesAndExportsDeleteAsync()
    {
        var topology = await BuildTopologyAsync(OutboundDeprovisionAction.Delete);
        var (sourceCso, _) = await CreateSourceCsoAsync(topology, "Auto Confirmed User", "EMP0002");
        await RunFullSyncAsync(topology.Source, "Provisioning Full Sync");
        var targetCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == topology.Target.Id);

        var provisioningConnector = new StubFileExportConnector(supportsAutoConfirmExport: true);
        var provisioningActivity = await RunExportAsync(topology.Target, provisioningConnector);
        AssertExportExecutedCleanly("Defect2 (provisioning)", provisioningActivity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(targetCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning),
                "arrange: export never transitions status by itself");
            Assert.That(PendingExportsFor(targetCso.Id), Is.Empty,
                "arrange: auto-confirm must have deleted the Create Pending Export immediately");
        }

        sourceCso.Status = ConnectedSystemObjectStatus.Obsolete;
        sourceCso.LastUpdated = DateTime.UtcNow;
        await RunDeltaSyncAsync(topology.Source, "Deletion Delta Sync");

        var stagedDelete = PendingExportsFor(targetCso.Id).SingleOrDefault();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stagedDelete, Is.Not.Null,
                "the no-Pending-Export Pending Provisioning CSO must get a real Delete staged, not be cancelled as never-exported");
            Assert.That(stagedDelete!.ChangeType, Is.EqualTo(PendingExportChangeType.Delete));
            Assert.That(SyncRepo.ConnectedSystemObjects.ContainsKey(targetCso.Id), Is.True,
                "unlike defect 1's cancellation, the CSO itself must survive staging - there IS something to delete");
        }
        AssertStagedDeletesHaveExternalId("Defect2", [stagedDelete!]);

        var deleteConnector = new StubFileExportConnector(supportsAutoConfirmExport: true);
        var deleteActivity = await RunExportAsync(topology.Target, deleteConnector);
        AssertExportExecutedCleanly("Defect2 (delete)", deleteActivity);

        Assert.That(SyncRepo.ConnectedSystemObjects.ContainsKey(targetCso.Id), Is.False,
            "the Delete export succeeding must remove the CSO");

        AssertContract("Defect2",
            stagedBeforeExecution: [],
            executedActivities: [provisioningActivity, deleteActivity],
            expectedCreates: 1, expectedUpdates: 0, expectedDeletes: 1,
            connectorCallLogs: [provisioningConnector.ExportedItems, deleteConnector.ExportedItems]);
    }

    /// <summary>
    /// Defect 3: a Create exported but never confirmed by import (Pending Provisioning, its Create Pending
    /// Export left in Exported status - the non-auto-confirming shape) must, once its Metaverse Object is
    /// deleted and the resulting Delete export succeeds, have its CSO removed: that Delete export is the
    /// only confirmation such an object can ever get, since import deletion detection excludes Pending
    /// Provisioning objects.
    /// </summary>
    [Test]
    public async Task Defect3_UnconfirmedProvisioningDeleteSucceeds_RemovesStrandedCsoAsync()
    {
        var topology = await BuildTopologyAsync(OutboundDeprovisionAction.Delete);
        var (sourceCso, _) = await CreateSourceCsoAsync(topology, "Unconfirmed User", "EMP0003");
        await RunFullSyncAsync(topology.Source, "Provisioning Full Sync");
        var targetCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == topology.Target.Id);

        var provisioningConnector = new MockCallConnector();
        var provisioningActivity = await RunExportAsync(topology.Target, provisioningConnector);
        AssertExportExecutedCleanly("Defect3 (provisioning)", provisioningActivity);
        Assert.That(targetCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning));
        var createPe = PendingExportsFor(targetCso.Id).Single();
        Assert.That(createPe.Status, Is.EqualTo(PendingExportStatus.Exported),
            "arrange: the calls-style connector never auto-confirms, so the Create stays Exported awaiting confirmation");

        sourceCso.Status = ConnectedSystemObjectStatus.Obsolete;
        sourceCso.LastUpdated = DateTime.UtcNow;
        await RunDeltaSyncAsync(topology.Source, "Deletion Delta Sync");
        var stagedDelete = PendingExportsFor(targetCso.Id).Single(pe => pe.ChangeType == PendingExportChangeType.Delete);
        AssertStagedDeletesHaveExternalId("Defect3", [stagedDelete]);

        var deleteConnector = new MockCallConnector();
        var deleteActivity = await RunExportAsync(topology.Target, deleteConnector);
        AssertExportExecutedCleanly("Defect3 (delete)", deleteActivity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.ConnectedSystemObjects.ContainsKey(targetCso.Id), Is.False,
                "the never-confirmed CSO must be removed once its Delete export succeeds - that success is the only " +
                "confirmation such an object can ever get");
            Assert.That(PendingExportsFor(targetCso.Id), Is.Empty);
            var rpei = deleteActivity.RunProfileExecutionItems.Single();
            Assert.That(rpei.ConnectedSystemObjectId, Is.Null, "the RPEI must not FK to the row it just deleted");
            Assert.That(rpei.DisplayNameSnapshot, Is.EqualTo("Unconfirmed User"), "the item must keep its display name snapshot");
        }

        AssertContract("Defect3",
            stagedBeforeExecution: [],
            executedActivities: [provisioningActivity, deleteActivity],
            expectedCreates: 1, expectedUpdates: 0, expectedDeletes: 1,
            connectorCallLogs: [provisioningConnector.ExportedItems, deleteConnector.ExportedItems]);
    }

    #endregion

    #region The wider matrix

    [Test]
    [TestCaseSource(nameof(Cases))]
    public async Task StageToExecute_Contract_HoldsAsync(ContractCase c)
    {
        var topology = await BuildTopologyAsync(c.Action);
        var (sourceCso, statusValue) = await CreateSourceCsoAsync(topology, $"{c.Name} User", "EMP1000");
        await RunFullSyncAsync(topology.Source, "Provisioning Full Sync");

        var targetCsoOrNull = SyncRepo.ConnectedSystemObjects.Values.SingleOrDefault(cso => cso.ConnectedSystemId == topology.Target.Id);
        Assert.That(targetCsoOrNull, Is.Not.Null, $"[{c.Name}] arrange: provisioning must stage a target CSO");
        // Bound once, non-null, and used for every dereference below: CodeQL's nullable-flow analysis does
        // not carry across the assertion above the way the compiler's does (src/CLAUDE.md's "bare
        // dereference after Is.Not.Null" rule), and this method dereferences it many times.
        var targetCso = targetCsoOrNull!;
        Assert.That(targetCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning));
        AssertStagedDeletesHaveExternalId(c.Name, PendingExportsFor(targetCso.Id));

        var callLogs = new List<IReadOnlyList<PendingExport>>();
        var activities = new List<Activity>();
        var anyExportHasExecuted = false;

        if (c.InitialState != CsoLifecycleState.NeverExported)
        {
            var provisioningConnector = CreateExportConnector(c.ExportPath);
            var provisioningActivity = await RunExportAsync(topology.Target, provisioningConnector);
            AssertExportExecutedCleanly(c.Name + " (provisioning)", provisioningActivity);
            callLogs.Add(GetExportedItems(provisioningConnector));
            activities.Add(provisioningActivity);
            anyExportHasExecuted = true;

            if (c.InitialState == CsoLifecycleState.Confirmed)
            {
                var importConnector = new MockCallConnector();
                importConnector.QueueImportObjects(BuildConfirmingImportObject(topology, sourceCso, targetCso));
                await RunConfirmingImportAsync(topology.Target, importConnector);
                Assert.That(targetCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal),
                    $"[{c.Name}] arrange: confirming import must transition Pending Provisioning to Normal");
            }
        }

        // Act: apply the tested change.
        switch (c.Change)
        {
            case ChangeKind.AttributeUpdate:
                ReplaceStringAttributeValue(sourceCso, topology.SourceDisplayNameAttr, $"{c.Name} Updated Name");
                await RunFullSyncAsync(topology.Source, "Attribute Update Full Sync");
                break;
            case ChangeKind.LeavesScope:
                ReplaceStringAttributeValue(sourceCso, topology.SourceStatusAttr, "Inactive");
                await RunFullSyncAsync(topology.Source, "Scope Out Full Sync");
                break;
            case ChangeKind.MvoDeleted:
                sourceCso.Status = ConnectedSystemObjectStatus.Obsolete;
                sourceCso.LastUpdated = DateTime.UtcNow;
                await RunDeltaSyncAsync(topology.Source, "Deletion Delta Sync");
                break;
        }

        var freshlyStaged = PendingExportsFor(targetCso.Id);
        AssertStagedDeletesHaveExternalId(c.Name, freshlyStaged);

        if (c.ExecuteFollowUpExport)
        {
            var followUpConnector = CreateExportConnector(c.ExportPath);
            var followUpActivity = await RunExportAsync(topology.Target, followUpConnector);
            AssertExportExecutedCleanly(c.Name + " (follow-up)", followUpActivity);
            callLogs.Add(GetExportedItems(followUpConnector));
            activities.Add(followUpActivity);
            anyExportHasExecuted = true;
        }

        // Final confirming import/sync representing the target's own next cycle - run whenever the
        // object has actually reached the target at least once (otherwise there is nothing there for an
        // import to report) and still exists. This runs REGARDLESS of whether an export just executed:
        // a Disconnect stages and executes nothing, but the object is still physically present in the
        // target, so its next import cycle happens anyway and is exactly what proves (or disproves)
        // whether an unconfirmed provisioning left dangling by Disconnect ever gets resolved.
        if (anyExportHasExecuted && SyncRepo.ConnectedSystemObjects.TryGetValue(targetCso.Id, out var stillThere))
        {
            var deleteJustExecuted = c.ExecuteFollowUpExport && freshlyStaged.Any(pe => pe.ChangeType == PendingExportChangeType.Delete);
            if (deleteJustExecuted)
            {
                // A Delete export against an already-Normal (confirmed) CSO does NOT itself remove it:
                // RemoveUnconfirmedProvisioningCsosAsync only special-cases a Pending Provisioning CSO
                // (defects 2/3's shape), because that status is excluded from ordinary import deletion
                // detection. A Normal CSO is instead removed the ordinary way: the target's own next
                // import confirms the (now-executed) Delete Pending Export and reports the object as
                // gone, deletion detection marks the CSO Obsolete, and a sync then removes it. A bare
                // "empty Full Import" against this harness's in-memory store does not itself run deletion
                // detection (a real-repository/query concern this fixture's WorkflowTestBase equivalent
                // does not model), so the two halves of that same event are represented directly here:
                // the Delete Pending Export is confirmed away (mirroring what
                // PendingExportReconciliationService does once an import reports the object gone), and
                // the CSO is marked Obsolete (mirroring MarkCsoAsObsoleteAsync elsewhere in this suite)
                // for the Delta Sync to remove.
                await SyncRepo.DeletePendingExportsByConnectedSystemObjectIdsAsync([stillThere.Id]);
                stillThere.Status = ConnectedSystemObjectStatus.Obsolete;
                stillThere.LastUpdated = DateTime.UtcNow;
                await RunDeltaSyncAsync(topology.Target, "Target Deletion Delta Sync");
            }
            else
            {
                // The object is still present in the target (Disconnect staged/executed nothing, or an
                // attribute change that only updated it): the target's own next import reports it back,
                // by its now-correctly-populated External Id (see BuildConfirmingImportObject).
                var finalImportConnector = new MockCallConnector();
                finalImportConnector.QueueImportObjects(BuildConfirmingImportObject(topology, sourceCso, stillThere));
                await RunConfirmingImportAsync(topology.Target, finalImportConnector);
            }
        }

        AssertContract(c.Name, stagedBeforeExecution: freshlyStaged, executedActivities: activities,
            expectedCreates: c.ExpectedCreates, expectedUpdates: c.ExpectedUpdates, expectedDeletes: c.ExpectedDeletes,
            connectorCallLogs: callLogs);

        if (c.ExpectedCsoRemoved)
        {
            Assert.That(SyncRepo.ConnectedSystemObjects.ContainsKey(targetCso.Id), Is.False,
                $"[{c.Name}] expected the target CSO to have been removed");
        }
        else if (c.ExpectedFinalStatus.HasValue)
        {
            var finalCso = SyncRepo.ConnectedSystemObjects.GetValueOrDefault(targetCso.Id);
            Assert.That(finalCso, Is.Not.Null, $"[{c.Name}] expected the target CSO to still exist");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(finalCso!.Status, Is.EqualTo(c.ExpectedFinalStatus.Value), $"[{c.Name}] final CSO status");
                if (c.ExpectedFinalJoinType.HasValue)
                    Assert.That(finalCso!.JoinType, Is.EqualTo(c.ExpectedFinalJoinType.Value), $"[{c.Name}] final CSO join type");
                if (c.ExpectedFinalDisplayName != null)
                {
                    var displayNameAttr = topology.TargetType.Attributes.Single(a => a.Name == "DisplayName");
                    var value = finalCso!.AttributeValues.SingleOrDefault(av => av.AttributeId == displayNameAttr.Id)?.StringValue;
                    Assert.That(value, Is.EqualTo(c.ExpectedFinalDisplayName), $"[{c.Name}] final CSO DisplayName");
                }
            }
        }
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        var changes = new[] { ChangeKind.LeavesScope, ChangeKind.MvoDeleted };
        var actions = new[] { OutboundDeprovisionAction.Delete, OutboundDeprovisionAction.Disconnect };
        var exportPaths = new[] { ExportPath.Call, ExportPath.FileAutoConfirm, ExportPath.FileNoAutoConfirm };

        // Group 1: NeverExported must cancel regardless of the export rule's Deprovisioning Action - "one
        // case sits outside the action altogether" (docs). Nothing ever reaches a connector.
        foreach (var change in changes)
        foreach (var action in actions)
        {
            yield return Case(new ContractCase(
                $"NeverExported_{change}_{action}_Cancels",
                CsoLifecycleState.NeverExported, change, action, ExportPath.Call, ExecuteFollowUpExport: false,
                ExpectedCreates: 0, ExpectedUpdates: 0, ExpectedDeletes: 0, ExpectedCsoRemoved: true));
        }

        // Group 2: attribute updates. NeverExported: the unsent Create's staged value simply updates in
        // place (nothing executes). Confirmed: a real Update export, proven clean across all three export
        // paths. ExportedUnconfirmed x AttributeUpdate (Create sent, not yet confirmed, attribute change
        // arrives before confirmation) is no longer excluded: the product owner has specified the
        // behaviour (an Update follows once the Create has been confirmed - auto-confirm counts as
        // confirmation), and it is covered by its own dedicated test below
        // (ExportedUnconfirmed_AttributeUpdate_UpdateFollowsOnceCreateConfirmedAsync), which needs a
        // two-cycle sync/export/confirm lifecycle the shared matrix driver does not model.
        yield return Case(new ContractCase(
            "NeverExported_AttributeUpdate_UpdatesUnsentCreateInPlace",
            CsoLifecycleState.NeverExported, ChangeKind.AttributeUpdate, OutboundDeprovisionAction.Disconnect, ExportPath.Call,
            ExecuteFollowUpExport: false, ExpectedCreates: 0, ExpectedUpdates: 0, ExpectedDeletes: 0, ExpectedCsoRemoved: false));

        foreach (var exportPath in exportPaths)
        {
            yield return Case(new ContractCase(
                $"Confirmed_AttributeUpdate_{exportPath}",
                CsoLifecycleState.Confirmed, ChangeKind.AttributeUpdate, OutboundDeprovisionAction.Disconnect, exportPath,
                ExecuteFollowUpExport: true, ExpectedCreates: 1, ExpectedUpdates: 1, ExpectedDeletes: 0, ExpectedCsoRemoved: false,
                ExpectedFinalStatus: ConnectedSystemObjectStatus.Normal,
                ExpectedFinalDisplayName: "Confirmed_AttributeUpdate_" + exportPath + " Updated Name"));
        }

        // Groups 3/4: ExportedUnconfirmed (Create sent, never confirmed) x Delete/Disconnect x every export
        // path. Disconnect leaves the object present in the target with no further export; its status only
        // resolves to Normal via the target's own next confirming import (see the "final confirming
        // import/sync" step in the test body below, which runs whenever the object has reached the target
        // at least once, regardless of whether a follow-up export executed).
        foreach (var change in changes)
        foreach (var exportPath in exportPaths)
        {
            yield return Case(new ContractCase(
                $"ExportedUnconfirmed_{change}_Delete_{exportPath}",
                CsoLifecycleState.ExportedUnconfirmed, change, OutboundDeprovisionAction.Delete, exportPath,
                ExecuteFollowUpExport: true, ExpectedCreates: 1, ExpectedUpdates: 0, ExpectedDeletes: 1, ExpectedCsoRemoved: true));

            yield return Case(new ContractCase(
                $"ExportedUnconfirmed_{change}_Disconnect_{exportPath}",
                CsoLifecycleState.ExportedUnconfirmed, change, OutboundDeprovisionAction.Disconnect, exportPath,
                ExecuteFollowUpExport: false, ExpectedCreates: 1, ExpectedUpdates: 0, ExpectedDeletes: 0, ExpectedCsoRemoved: false,
                ExpectedFinalStatus: ConnectedSystemObjectStatus.Normal, ExpectedFinalJoinType: ConnectedSystemObjectJoinType.NotJoined));
        }

        // Groups 5/6: Confirmed (Normal) x Delete/Disconnect x export path (Disconnect: path is irrelevant
        // since nothing exports afterward, so it is covered once per change rather than three times).
        foreach (var change in changes)
        {
            foreach (var exportPath in exportPaths)
            {
                yield return Case(new ContractCase(
                    $"Confirmed_{change}_Delete_{exportPath}",
                    CsoLifecycleState.Confirmed, change, OutboundDeprovisionAction.Delete, exportPath,
                    ExecuteFollowUpExport: true, ExpectedCreates: 1, ExpectedUpdates: 0, ExpectedDeletes: 1, ExpectedCsoRemoved: true));
            }

            yield return Case(new ContractCase(
                $"Confirmed_{change}_Disconnect",
                CsoLifecycleState.Confirmed, change, OutboundDeprovisionAction.Disconnect, ExportPath.Call,
                ExecuteFollowUpExport: false, ExpectedCreates: 1, ExpectedUpdates: 0, ExpectedDeletes: 0, ExpectedCsoRemoved: false,
                ExpectedFinalStatus: ConnectedSystemObjectStatus.Normal, ExpectedFinalJoinType: ConnectedSystemObjectJoinType.NotJoined));
        }
    }

    private static TestCaseData Case(ContractCase c, string? ignoreReason = null)
    {
        var testCase = new TestCaseData(c).SetName(c.Name);
        return ignoreReason == null ? testCase : testCase.Ignore(ignoreReason);
    }

    #endregion

    #region ExportedUnconfirmed + AttributeUpdate: Update sent only once the Create is confirmed

    /// <summary>
    /// An attribute change arriving for a PendingProvisioning CSO whose Create has already been sent (or
    /// auto-confirmed away) and is awaiting confirmation by import must never re-issue a second Create.
    /// <c>SyncEngine.ExportStaging.cs</c>'s <c>DecideOutboundStaging</c> tells a never-sent Create apart
    /// from one already sent via <c>SyncEngine.IsProvisioningNeverExported</c>: only a still-unsent Create
    /// (unattempted, zero errors) reuses the <c>ReusePendingProvisioningCso</c>/Create branch; a Create that
    /// has been sent (or auto-confirmed away, or attempted) stages an Update instead
    /// (<c>UpdateExportedProvisioningCso</c>). <c>ExportExecutionServer</c>'s exportability guard refuses to
    /// re-execute a Create with Status Exported, so that Update is never sent before the Create is
    /// confirmed (auto-confirm counts as confirmation); once confirmed,
    /// <c>SyncEngine.Reconciliation.ReconcileCsoAgainstPendingExport</c> flips the Pending Export's
    /// ChangeType from Create to Update so the queued change goes out on the next export.
    ///
    /// This test drives its own two-cycle sync/export/confirm/sync/export/confirm lifecycle (the shared
    /// matrix driver only models one export cycle after the tested change) so it is a dedicated test rather
    /// than a <see cref="ContractCase"/>.
    ///
    /// The connector is deliberately reused across every export call in this test (rather than a fresh
    /// instance per call, as elsewhere in this fixture) so its own <c>ExportedItems</c> log accumulates the
    /// full, ordered call history in one place, letting the assertions below check both the count AND the
    /// order of Create vs Update directly against it.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(AttributeUpdateBeforeConfirmationCases))]
    public async Task ExportedUnconfirmed_AttributeUpdate_UpdateFollowsOnceCreateConfirmedAsync(ExportPath exportPath)
    {
        var caseName = $"ExportedUnconfirmed_AttributeUpdate_{exportPath}";
        var topology = await BuildTopologyAsync(OutboundDeprovisionAction.Disconnect);
        var (sourceCso, _) = await CreateSourceCsoAsync(topology, $"{caseName} User", "EMP2000");
        await RunFullSyncAsync(topology.Source, "Provisioning Full Sync");

        var targetCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == topology.Target.Id);
        Assert.That(targetCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning),
            $"[{caseName}] arrange: provisioning must stage a Pending Provisioning target CSO");

        var connector = CreateExportConnector(exportPath);

        // The connector's own log holds live PendingExport references (both MockCallConnector and
        // StubFileExportConnector), and this test's whole point is that reconciliation genuinely mutates
        // the Create's ChangeType to Update in place, later, once it is confirmed (by design - see
        // SyncEngine.Reconciliation.cs). Reading the log live at the end would therefore see EVERY entry
        // sharing that one mutated object's CURRENT ChangeType, however many times it was logged, which
        // cannot tell "exactly one Create, never a second one" apart from "the one Create got renamed
        // in place". Snapshotting each newly logged call (ChangeType and a copy of its attribute changes
        // at that moment) into a list nothing mutates afterward is what makes that check - and the
        // shared AssertContract call below, which does the same counting - honest.
        var callHistory = new List<PendingExport>();
        void RecordNewCalls() => callHistory.AddRange(GetExportedItems(connector).Skip(callHistory.Count)
            .Select(pe => new PendingExport { Id = pe.Id, ChangeType = pe.ChangeType, AttributeValueChanges = pe.AttributeValueChanges.ToList() }));

        // Export the Create.
        var createActivity = await RunExportAsync(topology.Target, connector);
        AssertExportExecutedCleanly($"{caseName} (create)", createActivity);
        RecordNewCalls();
        Assert.That(callHistory.Count(pe => pe.ChangeType == PendingExportChangeType.Create), Is.EqualTo(1),
            $"[{caseName}] arrange: exactly one Create must have been sent");
        var callsAfterCreate = callHistory.Count;

        // The attribute update arrives at the source.
        ReplaceStringAttributeValue(sourceCso, topology.SourceDisplayNameAttr, $"{caseName} Updated Name");
        await RunFullSyncAsync(topology.Source, "Attribute Update Full Sync");

        // First post-change export attempt.
        var firstExportActivity = await RunExportAsync(topology.Target, connector);
        AssertExportExecutedCleanly($"{caseName} (export 1)", firstExportActivity);
        RecordNewCalls();
        var callsAfterFirstExport = callHistory.Count;

        if (exportPath != ExportPath.FileAutoConfirm)
        {
            // Auto-confirm is the only path where confirmation happens the moment the Create succeeds;
            // for the other two, nothing may be sent yet - the Create is still awaiting a real confirming
            // import, and clause 3 forbids sending the Update before that.
            Assert.That(callsAfterFirstExport, Is.EqualTo(callsAfterCreate),
                $"[{caseName}] the Update must not be sent before the Create has been confirmed by import");
        }
        // FileAutoConfirm: deliberately no assertion here - the specification says the Update "may" go on
        // this very next export (auto-confirm already counts as confirmation by this point), not that it
        // must; whichever export call actually carries it is checked by the ordering assertion below.

        // Confirming import: report back what the target actually, currently holds, derived from the
        // connector's own recorded call history rather than assumed - see BuildConfirmingImportObjectFromActualExport.
        var importConnector1 = new MockCallConnector();
        importConnector1.QueueImportObjects(BuildConfirmingImportObjectFromActualExport(topology, targetCso, GetExportedItems(connector)));
        await RunConfirmingImportAsync(topology.Target, importConnector1);

        // Sync again: with the Create now confirmed, staging should notice the source's updated
        // DisplayName no longer matches the target and stage the Update (if it has not already gone out).
        await RunFullSyncAsync(topology.Source, "Attribute Update Full Sync (post-confirm)");

        // Second export attempt.
        var secondExportActivity = await RunExportAsync(topology.Target, connector);
        AssertExportExecutedCleanly($"{caseName} (export 2)", secondExportActivity);
        RecordNewCalls();

        var allExported = GetExportedItems(connector);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(callHistory.Count(pe => pe.ChangeType == PendingExportChangeType.Create), Is.EqualTo(1),
                $"[{caseName}] exactly one Create, never a second one");
            Assert.That(callHistory.Count(pe => pe.ChangeType == PendingExportChangeType.Update), Is.EqualTo(1),
                $"[{caseName}] exactly one Update");
        }

        var createIndex = callHistory.FindIndex(pe => pe.ChangeType == PendingExportChangeType.Create);
        var updateIndex = callHistory.FindIndex(pe => pe.ChangeType == PendingExportChangeType.Update);
        Assert.That(createIndex, Is.LessThan(updateIndex),
            $"[{caseName}] the connector must receive the Create before the Update, never the reverse");

        // The Update must carry the new value, read from the frozen snapshot taken the moment it was sent.
        var targetDisplayNameAttr = topology.TargetType.Attributes.Single(a => a.Name == "DisplayName");
        var updatePe = callHistory[updateIndex];
        var updateChange = updatePe.AttributeValueChanges.SingleOrDefault(c => c.AttributeId == targetDisplayNameAttr.Id);
        Assert.That(updateChange?.StringValue, Is.EqualTo($"{caseName} Updated Name"),
            $"[{caseName}] the Update must carry the new DisplayName value");

        // Final confirming import (confirms the Update) and sync.
        var importConnector2 = new MockCallConnector();
        importConnector2.QueueImportObjects(BuildConfirmingImportObjectFromActualExport(topology, targetCso, allExported));
        await RunConfirmingImportAsync(topology.Target, importConnector2);
        await RunFullSyncAsync(topology.Source, "Final Full Sync");

        var finalTargetCso = SyncRepo.ConnectedSystemObjects.GetValueOrDefault(targetCso.Id);
        Assert.That(finalTargetCso, Is.Not.Null, $"[{caseName}] the target CSO must still exist");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finalTargetCso!.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal), $"[{caseName}] final CSO status");
            Assert.That(finalTargetCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Provisioned), $"[{caseName}] final CSO join type");
            var finalValue = finalTargetCso.AttributeValues.SingleOrDefault(av => av.AttributeId == targetDisplayNameAttr.Id)?.StringValue;
            Assert.That(finalValue, Is.EqualTo($"{caseName} Updated Name"), $"[{caseName}] final CSO DisplayName");
        }

        AssertContract(caseName,
            stagedBeforeExecution: [],
            executedActivities: [createActivity, firstExportActivity, secondExportActivity],
            expectedCreates: 1, expectedUpdates: 1, expectedDeletes: 0,
            connectorCallLogs: [callHistory]);
    }

    /// <summary>
    /// One case per export path: proves staging appends the change and stages an Update - never a second
    /// Create - for an attribute change arriving while the target CSO is still Pending Provisioning with
    /// its Create already sent (or auto-confirmed), and that the Update is not sent before the Create is
    /// confirmed.
    /// </summary>
    private static IEnumerable<TestCaseData> AttributeUpdateBeforeConfirmationCases()
    {
        foreach (var exportPath in new[] { ExportPath.Call, ExportPath.FileAutoConfirm, ExportPath.FileNoAutoConfirm })
        {
            yield return new TestCaseData(exportPath)
                .SetName($"ExportedUnconfirmed_AttributeUpdate_UpdateFollowsOnceCreateConfirmed_{exportPath}");
        }
    }

    /// <summary>
    /// Builds a confirming import object from what the target connector has ACTUALLY been sent so far
    /// (per its own recorded call history), rather than from source data or assumption: the latest
    /// AttributeValueChange for each attribute across every Pending Export the connector has received, in
    /// call order, is what the target genuinely, currently holds. This is what makes it possible to check
    /// "the Update is not sent before the Create is confirmed" honestly - a confirming import built from
    /// source data would silently report the new value as already present even on a run where it had not,
    /// in fact, been sent yet.
    /// </summary>
    private static ConnectedSystemImportObject BuildConfirmingImportObjectFromActualExport(
        Topology topology, ConnectedSystemObject targetCso, IReadOnlyList<PendingExport> exportedItemsSoFar)
    {
        var targetDisplayNameAttr = topology.TargetType.Attributes.Single(a => a.Name == "DisplayName");
        string? displayName = null;
        foreach (var pe in exportedItemsSoFar)
        {
            var change = pe.AttributeValueChanges.FirstOrDefault(c => c.AttributeId == targetDisplayNameAttr.Id);
            if (change != null)
                displayName = change.StringValue;
        }

        var targetExternalIdAttr = topology.TargetType.Attributes.Single(a => a.IsExternalId);
        var externalId = targetCso.AttributeValues.SingleOrDefault(av => av.AttributeId == targetExternalIdAttr.Id)?.StringValue;

        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = topology.TargetType.Name,
            ChangeType = ObjectChangeType.Updated
        };

        if (displayName != null)
            importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "DisplayName", StringValues = { displayName } });
        if (externalId != null)
            importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "ExternalId", StringValues = { externalId } });

        return importObject;
    }

    #endregion

    #region A Create whose confirming import does not confirm it: retry shape

    /// <summary>
    /// Sub-case A: the connector reports the Create's export as successful, and a confirming import
    /// matches the object by its (primary) External Id - so the CSO transitions Pending Provisioning to
    /// Normal, per the doctrine that the confirming import is what establishes the object exists - but one
    /// exported attribute (DisplayName) is reported back with a DIFFERENT value than what was sent (e.g.
    /// the target normalised it), so that one change stays unconfirmed and must retry.
    ///
    /// Expected, derived from the same doctrine: the retry travels as an UPDATE of the unconfirmed
    /// attribute(s), never a second Create; exactly one Create ever reaches the connector; the Update
    /// carries the originally-exported (still wanted) value; once a second confirming import reports that
    /// value back, the Pending Export is gone and the CSO is Normal.
    ///
    /// This topology has no Secondary External Id configured (see <see cref="BuildTopologyAsync"/> - the
    /// primary External Id is itself the flowed attribute), so the old, narrower
    /// <c>TransitionCreateToUpdateIfSecondaryExternalIdConfirmed</c> never fired for this shape: its first
    /// trigger (a confirmed Secondary External Id) could never fire here, and its second trigger required
    /// EVERY originally-exported change to be confirmed before transitioning, which the one still-
    /// unconfirmed DisplayName change prevented - so a second Create went out for an object reconciliation
    /// had already matched. Fixed by generalising the transition
    /// (<c>SyncEngine.Reconciliation.TransitionCreateToUpdateOnceObjectConfirmed</c>): reconciliation only
    /// ever runs for a CSO an import actually matched, so a Create reaching it already proves the object
    /// exists, and any changes still outstanding after confirmation now travel as an Update regardless of
    /// which attribute(s) confirmed.
    ///
    /// Does not cover FileAutoConfirm: auto-confirm deletes the Create Pending Export the moment the
    /// export succeeds, so there is no confirming step for the Create to NOT confirm - the axis this test
    /// exercises does not apply to that path.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(ObjectPresentUnconfirmedAttributeCases))]
    public async Task UnconfirmedCreate_ObjectPresentOneAttributeUnconfirmed_RetriesAsUpdateAsync(ExportPath exportPath)
    {
        var caseName = $"UnconfirmedCreate_ObjectPresent_{exportPath}";
        var topology = await BuildTopologyAsync(OutboundDeprovisionAction.Disconnect);
        var (sourceCso, _) = await CreateSourceCsoAsync(topology, $"{caseName} User", "EMP3000");
        await RunFullSyncAsync(topology.Source, "Provisioning Full Sync");

        var targetCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == topology.Target.Id);
        Assert.That(targetCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning),
            $"[{caseName}] arrange: provisioning must stage a Pending Provisioning target CSO");

        var connector = CreateExportConnector(exportPath);
        // Frozen call-history snapshot, same reason as ExportedUnconfirmed_AttributeUpdate_UpdateFollowsOnceCreateConfirmedAsync:
        // reconciliation genuinely mutates a Pending Export's ChangeType in place, so reading the
        // connector's live log at the end cannot tell "one Create, renamed in place" apart from "one
        // Create, then a second logged as Update" - both would show as one Create + one Update in a live
        // read. Snapshotting each newly logged call's ChangeType the moment it is sent is what makes
        // "exactly one Create ever reached the connector" an honest assertion.
        var callHistory = new List<PendingExport>();
        void RecordNewCalls() => callHistory.AddRange(GetExportedItems(connector).Skip(callHistory.Count)
            .Select(pe => new PendingExport { Id = pe.Id, ChangeType = pe.ChangeType, AttributeValueChanges = pe.AttributeValueChanges.ToList() }));

        // Export the Create; the connector reports success.
        var createActivity = await RunExportAsync(topology.Target, connector);
        AssertExportExecutedCleanly($"{caseName} (create)", createActivity);
        RecordNewCalls();
        Assert.That(callHistory.Count(pe => pe.ChangeType == PendingExportChangeType.Create), Is.EqualTo(1),
            $"[{caseName}] arrange: exactly one Create must have been sent");

        var targetDisplayNameAttr = topology.TargetType.Attributes.Single(a => a.Name == "DisplayName");
        var targetExternalIdAttr = topology.TargetType.Attributes.Single(a => a.IsExternalId);
        var externalId = targetCso.AttributeValues.SingleOrDefault(av => av.AttributeId == targetExternalIdAttr.Id)?.StringValue;
        Assert.That(externalId, Is.Not.Null, $"[{caseName}] arrange: the Create export must have assigned/echoed an External Id");
        var exportedDisplayName = $"{caseName} User";

        // Confirming import that does NOT confirm: the object is present (matched by External Id) but
        // reports a DIFFERENT DisplayName than what was exported.
        var mismatchedImportObject = new ConnectedSystemImportObject { ObjectType = topology.TargetType.Name, ChangeType = ObjectChangeType.Updated };
        mismatchedImportObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "ExternalId", StringValues = { externalId! } });
        mismatchedImportObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "DisplayName", StringValues = { $"{caseName} Normalised" } });

        var importConnector1 = new MockCallConnector();
        importConnector1.QueueImportObjects(mismatchedImportObject);
        await RunConfirmingImportAsync(topology.Target, importConnector1);

        Assert.That(targetCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal),
            $"[{caseName}] arrange: a confirming import matching the object by External Id must transition it to Normal " +
            "even though one exported attribute stays unconfirmed - the import is what establishes the object exists");

        // Sync: the ordinary next cycle, even though nothing new changed at the source.
        await RunFullSyncAsync(topology.Source, "Sync After Partial Confirmation");

        // Retry export attempt.
        var retryActivity = await RunExportAsync(topology.Target, connector);
        AssertExportExecutedCleanly($"{caseName} (retry)", retryActivity);
        RecordNewCalls();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(callHistory.Count(pe => pe.ChangeType == PendingExportChangeType.Create), Is.EqualTo(1),
                $"[{caseName}] exactly one Create must ever reach the connector - the retry must travel as an Update, " +
                "never a second Create, for an object the confirming import has already matched");
            Assert.That(callHistory.Count(pe => pe.ChangeType == PendingExportChangeType.Update), Is.EqualTo(1),
                $"[{caseName}] the retry must be exactly one Update");
        }

        var updatePe = callHistory.Last(pe => pe.ChangeType == PendingExportChangeType.Update);
        var updateChange = updatePe.AttributeValueChanges.SingleOrDefault(c => c.AttributeId == targetDisplayNameAttr.Id);
        Assert.That(updateChange?.StringValue, Is.EqualTo(exportedDisplayName),
            $"[{caseName}] the Update must carry the originally-exported (still wanted) DisplayName value, not the " +
            "target's mismatched/normalised one");

        // Final confirming import now returns the exported value.
        var confirmedImportObject = new ConnectedSystemImportObject { ObjectType = topology.TargetType.Name, ChangeType = ObjectChangeType.Updated };
        confirmedImportObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "ExternalId", StringValues = { externalId! } });
        confirmedImportObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "DisplayName", StringValues = { exportedDisplayName } });
        var importConnector2 = new MockCallConnector();
        importConnector2.QueueImportObjects(confirmedImportObject);
        await RunConfirmingImportAsync(topology.Target, importConnector2);
        await RunFullSyncAsync(topology.Source, "Final Full Sync");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PendingExportsFor(targetCso.Id), Is.Empty, $"[{caseName}] the Pending Export must be gone once confirmed");
            Assert.That(targetCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal), $"[{caseName}] final CSO status");
            Assert.That(targetCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Provisioned), $"[{caseName}] final CSO join type");
        }

        AssertContract(caseName,
            stagedBeforeExecution: [],
            executedActivities: [createActivity, retryActivity],
            expectedCreates: 1, expectedUpdates: 1, expectedDeletes: 0,
            connectorCallLogs: [callHistory]);
    }

    /// <summary>
    /// One case per export path that has a confirming step for the Create to not confirm (see the test
    /// method's own doc comment for why FileAutoConfirm is excluded).
    /// </summary>
    private static IEnumerable<TestCaseData> ObjectPresentUnconfirmedAttributeCases()
    {
        foreach (var exportPath in new[] { ExportPath.Call, ExportPath.FileNoAutoConfirm })
        {
            yield return new TestCaseData(exportPath)
                .SetName($"UnconfirmedCreate_ObjectPresentOneAttributeUnconfirmed_{exportPath}");
        }
    }

    /// <summary>
    /// Sub-case B: the connector reports the Create's export as successful, but a confirming import does
    /// not return the object AT ALL (absent from the Full Import payload entirely - e.g. it has not landed
    /// wherever the target's own read-back source looks yet). Expected: the Create is retried as a
    /// CREATE - a second Create is correct here, since as far as JIM can tell there is genuinely nothing in
    /// the target; the CSO stays Pending Provisioning until an import confirms it; no stranded state
    /// (contract clause 3's invariants, including the one added specifically for an unconfirmable Exported
    /// Pending Export against a NotJoined CSO, are asserted at the end).
    ///
    /// Fixed via <c>SyncEngine.IsExportedCreateUnseenByFullImport</c> +
    /// <c>SyncImportTaskProcessor</c>'s Full Import deletion-detection phase: when a Full Import completes
    /// without seeing a Pending Provisioning CSO whose Create Pending Export is Status Exported, that
    /// Create is marked for retry (Pending Export Status -&gt; ExportNotConfirmed, its
    /// ExportedPendingConfirmation/ExportedNotConfirmed attribute changes -&gt; ExportedNotConfirmed with
    /// the usual retry accounting), so the next export re-sends the Create. A Delta Import cannot prove
    /// absence and leaves it alone.
    ///
    /// Does not cover FileAutoConfirm, for the same reason as sub-case A above.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(ObjectAbsentCases))]
    public async Task UnconfirmedCreate_ObjectAbsent_RetriesAsCreateAsync(ExportPath exportPath)
    {
        var caseName = $"UnconfirmedCreate_ObjectAbsent_{exportPath}";
        var topology = await BuildTopologyAsync(OutboundDeprovisionAction.Disconnect);
        var (sourceCso, _) = await CreateSourceCsoAsync(topology, $"{caseName} User", "EMP4000");
        await RunFullSyncAsync(topology.Source, "Provisioning Full Sync");

        var targetCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == topology.Target.Id);
        Assert.That(targetCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning),
            $"[{caseName}] arrange: provisioning must stage a Pending Provisioning target CSO");

        var connector = CreateExportConnector(exportPath);
        var callHistory = new List<PendingExport>();
        void RecordNewCalls() => callHistory.AddRange(GetExportedItems(connector).Skip(callHistory.Count)
            .Select(pe => new PendingExport { Id = pe.Id, ChangeType = pe.ChangeType, AttributeValueChanges = pe.AttributeValueChanges.ToList() }));

        // Export the Create; the connector reports success.
        var createActivity = await RunExportAsync(topology.Target, connector);
        AssertExportExecutedCleanly($"{caseName} (create)", createActivity);
        RecordNewCalls();
        Assert.That(callHistory.Count(pe => pe.ChangeType == PendingExportChangeType.Create), Is.EqualTo(1),
            $"[{caseName}] arrange: exactly one Create must have been sent");

        // Confirming import that does not return our object at all - but the run must still read
        // something, unrelated noise, so it is not itself an empty Full Import (which the retry step
        // deliberately treats as "no objects imported means do nothing", mirroring the same guard
        // deletion detection applies: a Full Import that reads literally nothing may be the symptom of
        // a connector/configuration problem, not proof that everything outstanding is now absent).
        var importConnector1 = new MockCallConnector();
        var noiseImportObject = new ConnectedSystemImportObject { ObjectType = topology.TargetType.Name, ChangeType = ObjectChangeType.Updated };
        noiseImportObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "ExternalId", StringValues = { $"noise-{Guid.NewGuid()}" } });
        noiseImportObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "DisplayName", StringValues = { "Unrelated Noise Object" } });
        importConnector1.QueueImportObjects(noiseImportObject);
        await RunConfirmingImportAsync(topology.Target, importConnector1);

        Assert.That(targetCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning),
            $"[{caseName}] arrange: an import that never reports the object must leave it unconfirmed");

        await RunFullSyncAsync(topology.Source, "Sync After No Confirmation");

        var retryActivity = await RunExportAsync(topology.Target, connector);
        AssertExportExecutedCleanly($"{caseName} (retry)", retryActivity);
        RecordNewCalls();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(callHistory.Count(pe => pe.ChangeType == PendingExportChangeType.Create), Is.EqualTo(2),
                $"[{caseName}] the retry must travel as a second Create - there is nothing in the target for an Update " +
                "to address, and the CSO must not be left permanently stuck on the first, still-Exported Create");
            Assert.That(callHistory.Count(pe => pe.ChangeType == PendingExportChangeType.Update), Is.EqualTo(0),
                $"[{caseName}] no Update may be sent - nothing has ever been confirmed to exist");
        }

        // Confirming import that now returns the object.
        var targetExternalIdAttr = topology.TargetType.Attributes.Single(a => a.IsExternalId);
        var externalId = targetCso.AttributeValues.SingleOrDefault(av => av.AttributeId == targetExternalIdAttr.Id)?.StringValue;
        Assert.That(externalId, Is.Not.Null, $"[{caseName}] arrange: the retried Create must have assigned/echoed an External Id");
        var confirmedImportObject = BuildConfirmingImportObject(topology, sourceCso, targetCso);
        var importConnector2 = new MockCallConnector();
        importConnector2.QueueImportObjects(confirmedImportObject);
        await RunConfirmingImportAsync(topology.Target, importConnector2);
        await RunFullSyncAsync(topology.Source, "Final Full Sync");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(targetCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal), $"[{caseName}] final CSO status");
            Assert.That(targetCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Provisioned), $"[{caseName}] final CSO join type");
        }

        AssertContract(caseName,
            stagedBeforeExecution: [],
            executedActivities: [createActivity, retryActivity],
            expectedCreates: 2, expectedUpdates: 0, expectedDeletes: 0,
            connectorCallLogs: [callHistory]);
    }

    /// <summary>
    /// One case per export path that has a confirming step for the Create to not confirm (see the sub-case
    /// B test method's own doc comment for why FileAutoConfirm is excluded).
    ///
    /// Fixed the same way as sub-case A's doc comment describes for the general mechanism, but via a
    /// different code path: <c>ReconcileCsoAgainstPendingExport</c> is only ever invoked per imported
    /// object, so an object genuinely absent from every subsequent import is never reconciled by it.
    /// <c>SyncImportTaskProcessor</c>'s Full Import deletion-detection phase now runs a second, narrow
    /// step (<c>SyncEngine.IsExportedCreateUnseenByFullImport</c>) that finds exactly this shape - a
    /// Pending Provisioning CSO whose Create Pending Export is Status Exported but whose External Id
    /// never appeared in the run's own imported set - and marks it for retry, so the next export re-sends
    /// the Create.
    /// </summary>
    private static IEnumerable<TestCaseData> ObjectAbsentCases()
    {
        foreach (var exportPath in new[] { ExportPath.Call, ExportPath.FileNoAutoConfirm })
        {
            yield return new TestCaseData(exportPath)
                .SetName($"UnconfirmedCreate_ObjectAbsent_{exportPath}");
        }
    }

    #endregion

    #region Shared contract assertions

    /// <summary>
    /// Contract clause 1: every Delete Pending Export staging just produced must target a CSO holding an
    /// External ID value - checked between staging and execution, before any connector sees it. This is
    /// the exact shape of defect 1: a Delete with no External ID fails export ("Delete export has no
    /// External ID value") and strands the CSO forever.
    /// </summary>
    private static void AssertStagedDeletesHaveExternalId(string caseLabel, IEnumerable<PendingExport> stagedPendingExports)
    {
        foreach (var pe in stagedPendingExports.Where(p => p.ChangeType == PendingExportChangeType.Delete))
        {
            Assert.That(pe.ConnectedSystemObject, Is.Not.Null,
                $"[{caseLabel}] contract #1: staged Delete {pe.Id} carries no Connected System Object");
            var cso = pe.ConnectedSystemObject!;
            var externalIdAttr = cso.Type.Attributes.SingleOrDefault(a => a.IsExternalId);
            var externalIdValue = externalIdAttr != null
                ? cso.AttributeValues.SingleOrDefault(av => av.AttributeId == externalIdAttr.Id)
                : null;
            var hasExternalId = externalIdValue is { StringValue: not null } or { GuidValue: not null } or { IntValue: not null };
            Assert.That(hasExternalId, Is.True,
                $"[{caseLabel}] contract #1 violated: Delete Pending Export {pe.Id} targets CSO {cso.Id} with no External ID " +
                "value (this is exactly defect 1's shape - such a Delete cannot execute)");
        }
    }

    /// <summary>Contract clause 2: execution must not throw and must leave no UnhandledError RPEI.</summary>
    private static void AssertExportExecutedCleanly(string caseLabel, Activity activity)
    {
        var unhandled = activity.RunProfileExecutionItems
            .Where(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError)
            .ToList();
        Assert.That(unhandled, Is.Empty,
            $"[{caseLabel}] contract #2 violated: export Activity has UnhandledError item(s): " +
            string.Join("; ", unhandled.Select(r => r.ErrorMessage)));
    }

    /// <summary>
    /// Contract clause 3: end-state invariants. The first three mirror Assert-SyncStateInvariants in
    /// test/integration/utils/Test-Helpers.ps1 exactly - no stranded Pending Provisioning CSO (NotJoined,
    /// with either no Pending Export at all or only an unsent Create), no Pending Delete Pending Export
    /// that carries no attribute changes for a CSO holding no attribute values either (unexecutable), and
    /// no Pending Export left dangling against a CSO that no longer exists. The fourth (unconfirmable
    /// Exported Pending Export) goes BEYOND Assert-SyncStateInvariants - it is not one of its checks - and
    /// was added after this fixture's own matrix surfaced the shape it targets.
    /// </summary>
    private void AssertNoStragglingInvariantViolations(string caseLabel)
    {
        var violations = new List<string>();

        foreach (var cso in SyncRepo.ConnectedSystemObjects.Values
                     .Where(c => c.Status == ConnectedSystemObjectStatus.PendingProvisioning && c.JoinType == ConnectedSystemObjectJoinType.NotJoined))
        {
            var pendingExports = PendingExportsFor(cso.Id);
            var isOnlyAnUnsentCreate = pendingExports.Count == 1
                && pendingExports[0].ChangeType == PendingExportChangeType.Create
                && pendingExports[0].Status == PendingExportStatus.Pending
                && pendingExports[0].LastAttemptedAt == null;

            if (pendingExports.Count == 0 || isOnlyAnUnsentCreate)
            {
                violations.Add($"stranded Pending Provisioning CSO {cso.Id} (NotJoined, " +
                    $"{(pendingExports.Count == 0 ? "no Pending Export" : "only an unsent Create")})");
            }
        }

        foreach (var pe in SyncRepo.PendingExports.Values
                     .Where(p => p.ChangeType == PendingExportChangeType.Delete && p.Status == PendingExportStatus.Pending))
        {
            var hasAttributeChanges = pe.AttributeValueChanges.Count > 0;
            var cso = pe.ConnectedSystemObjectId.HasValue
                ? SyncRepo.ConnectedSystemObjects.GetValueOrDefault(pe.ConnectedSystemObjectId.Value)
                : null;
            var csoHasNoValues = cso == null || cso.AttributeValues.Count == 0;
            if (!hasAttributeChanges && csoHasNoValues)
                violations.Add($"unexecutable Delete Pending Export {pe.Id} (no attribute changes, and its CSO holds no values)");
        }

        foreach (var pe in SyncRepo.PendingExports.Values
                     .Where(p => p.ConnectedSystemObjectId.HasValue && !SyncRepo.ConnectedSystemObjects.ContainsKey(p.ConnectedSystemObjectId!.Value)))
        {
            violations.Add($"dangling Pending Export {pe.Id} references missing CSO {pe.ConnectedSystemObjectId!.Value}");
        }

        // Beyond Assert-SyncStateInvariants: a Pending Export left in Exported status (sent, awaiting
        // confirmation) whose CSO is NotJoined can never be confirmed - nothing routes a future confirming
        // import at a disconnected/unmanaged object - so it will sit Exported forever. This is the
        // FileNoAutoConfirm + Disconnect shape: the Create stays Exported (no auto-confirm to delete it),
        // then Disconnect breaks the join without ever addressing the Pending Export.
        foreach (var pe in SyncRepo.PendingExports.Values.Where(p => p.Status == PendingExportStatus.Exported))
        {
            var cso = pe.ConnectedSystemObjectId.HasValue
                ? SyncRepo.ConnectedSystemObjects.GetValueOrDefault(pe.ConnectedSystemObjectId.Value)
                : null;
            if (cso is { JoinType: ConnectedSystemObjectJoinType.NotJoined })
                violations.Add($"unconfirmable Exported Pending Export {pe.Id} (CSO {cso.Id} is NotJoined, so nothing will ever confirm it)");
        }

        Assert.That(violations, Is.Empty,
            $"[{caseLabel}] contract #3 violated (end-state invariants):\n{string.Join("\n", violations)}");
    }

    /// <summary>
    /// Contract clause 4: the target connector received exactly the expected operations for the case.
    /// Combined with clauses 1-3 above via the whole-scenario call.
    /// </summary>
    private void AssertContract(
        string caseLabel,
        IReadOnlyList<PendingExport> stagedBeforeExecution,
        IReadOnlyList<Activity> executedActivities,
        int expectedCreates,
        int expectedUpdates,
        int expectedDeletes,
        IReadOnlyList<IReadOnlyList<PendingExport>> connectorCallLogs)
    {
        AssertStagedDeletesHaveExternalId(caseLabel, stagedBeforeExecution);
        foreach (var activity in executedActivities)
            AssertExportExecutedCleanly(caseLabel, activity);
        AssertNoStragglingInvariantViolations(caseLabel);

        var allCalls = connectorCallLogs.SelectMany(log => log).ToList();
        var actualCreates = allCalls.Count(pe => pe.ChangeType == PendingExportChangeType.Create);
        var actualUpdates = allCalls.Count(pe => pe.ChangeType == PendingExportChangeType.Update);
        var actualDeletes = allCalls.Count(pe => pe.ChangeType == PendingExportChangeType.Delete);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actualCreates, Is.EqualTo(expectedCreates), $"[{caseLabel}] contract #4: expected Create call count");
            Assert.That(actualUpdates, Is.EqualTo(expectedUpdates), $"[{caseLabel}] contract #4: expected Update call count");
            Assert.That(actualDeletes, Is.EqualTo(expectedDeletes), $"[{caseLabel}] contract #4: expected Delete call count");
        }
    }

    #endregion

    #region Test-only file-style export connector (no export-capable file mock exists usable from
    // WorkflowTestBase: MockFileConnector only implements IConnectorImportUsingFiles). Modelled on the
    // Mock<IConnector>.As&lt;IConnectorExportUsingFiles&gt;() pattern in OutboundSync/ExportExecutionTests.cs,
    // but written as a small concrete class (rather than a Moq setup) so it can record its own call log the
    // same way MockCallConnector does.

    /// <summary>
    /// Minimal test-only connector for the "file" export path (e.g. the File Connector): records every
    /// Pending Export it was asked to export, and always succeeds without returning an External ID (file
    /// exports typically have none to return - see IConnectorExportUsingFiles.ExportAsync's own doc
    /// comment). <see cref="SupportsAutoConfirmExport"/> is configurable so both the auto-confirming and
    /// non-auto-confirming file shapes can be driven from the same class.
    /// </summary>
    private sealed class StubFileExportConnector(bool supportsAutoConfirmExport) : IConnector, IConnectorCapabilities, IConnectorExportUsingFiles
    {
        private readonly List<PendingExport> _exportedItems = [];

        public string Name => "Stub File Export Connector";
        public string? Description => null;
        public string? Url => null;

        public bool SupportsFullImport => false;
        public bool SupportsDeltaImport => false;
        public bool SupportsExport => true;
        public bool SupportsPartitions => false;
        public bool SupportsPartitionContainers => false;
        public bool SupportsSecondaryExternalId => false;
        public bool SupportsUserSelectedExternalId => true;
        public bool SupportsUserSelectedAttributeTypes => true;
        public bool SupportsAutoConfirmExport { get; } = supportsAutoConfirmExport;
        public bool SupportsParallelExport => false;
        public bool SupportsPaging => false;
        public bool SupportsFilePaths => true;
        public bool SupportsPasswordSet => false;
        public bool SupportsPasswordPolicyDiscovery => false;

        public IReadOnlyList<PendingExport> ExportedItems => _exportedItems;

        public Task<List<ConnectedSystemExportResult>> ExportAsync(
            IList<ConnectedSystemSettingValue> settings, IList<PendingExport> pendingExports,
            CancellationToken cancellationToken, IConnectorProgress progress)
        {
            _exportedItems.AddRange(pendingExports);
            var results = pendingExports.Select(BuildResult).ToList();
            return Task.FromResult(results);
        }

        /// <summary>
        /// Mirrors the real File Connector's own External Id resolution
        /// (<c>FileConnectorExport.GetExternalIdValue</c>): for Update/Delete, resolve from the CSO's
        /// existing <see cref="ConnectedSystemObject.ExternalIdAttributeValue"/>; for Create (or when the
        /// CSO does not have one yet), resolve from the Pending Export's own <c>AttributeValueChanges</c>
        /// - a file connector cannot assign identifiers, so the External Id has to be a flowed attribute,
        /// carried in the Create's changes and echoed straight back. A Delete that cannot resolve one
        /// fails with the exact message <c>FileConnectorExport.ProcessPendingExport</c> uses, so defect
        /// 1's failure mode stays detectable through this stub.
        /// </summary>
        private static ConnectedSystemExportResult BuildResult(PendingExport pendingExport)
        {
            var externalId = ResolveExternalId(pendingExport);

            if (pendingExport.ChangeType == PendingExportChangeType.Delete && string.IsNullOrEmpty(externalId))
                return ConnectedSystemExportResult.Failed("Delete export has no External ID value.");

            return ConnectedSystemExportResult.Succeeded(externalId);
        }

        private static string? ResolveExternalId(PendingExport pendingExport)
        {
            var existing = pendingExport.ConnectedSystemObject?.ExternalIdAttributeValue?.ToStringNoName();
            if (!string.IsNullOrEmpty(existing))
                return existing;

            var externalIdChange = pendingExport.AttributeValueChanges.FirstOrDefault(a => a.Attribute?.IsExternalId == true);
            if (externalIdChange == null)
                return null;

            return externalIdChange.StringValue
                ?? externalIdChange.GuidValue?.ToString()
                ?? externalIdChange.IntValue?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static IConnector CreateExportConnector(ExportPath path) => path switch
    {
        ExportPath.Call => new MockCallConnector(),
        ExportPath.FileAutoConfirm => new StubFileExportConnector(supportsAutoConfirmExport: true),
        ExportPath.FileNoAutoConfirm => new StubFileExportConnector(supportsAutoConfirmExport: false),
        _ => throw new ArgumentOutOfRangeException(nameof(path))
    };

    private static IReadOnlyList<PendingExport> GetExportedItems(IConnector connector) => connector switch
    {
        MockCallConnector c => c.ExportedItems,
        StubFileExportConnector c => c.ExportedItems,
        _ => throw new ArgumentOutOfRangeException(nameof(connector))
    };

    #endregion

    #region Topology and lifecycle plumbing

    private sealed record Topology(
        ConnectedSystem Source,
        ConnectedSystem Target,
        ConnectedSystemObjectType SourceType,
        ConnectedSystemObjectType TargetType,
        MetaverseObjectType MvType,
        SyncRule ImportRule,
        SyncRule ExportRule,
        MetaverseAttribute StatusMvAttr,
        ConnectedSystemObjectTypeAttribute SourceStatusAttr,
        ConnectedSystemObjectTypeAttribute SourceDisplayNameAttr,
        ConnectedSystemObjectTypeAttribute SourceEmployeeIdAttr);

    /// <summary>
    /// Builds a source -&gt; Metaverse -&gt; target provisioning topology: source flows DisplayName,
    /// EmployeeId and Status; the target's ExternalId is itself flowed from EmployeeId (a File-Connector-
    /// realistic shape, since file exports return no connector-assigned id - see
    /// IConnectorExportUsingFiles.ExportAsync's own doc comment), and DisplayName is flowed too. The
    /// export rule scopes on Status = Active, provisions, and carries the given Deprovisioning Action. The
    /// Metaverse Object Type's deletion rule is WhenAuthoritativeSourceDisconnected with a zero grace
    /// period and the source as the sole trigger, so deleting the source CSO deletes the Metaverse Object
    /// synchronously during Delta Sync without being masked by the target's own remaining connector (the
    /// same shape DeletionRuleWorkflowTests uses for its "export target present" scenarios).
    /// </summary>
    private async Task<Topology> BuildTopologyAsync(OutboundDeprovisionAction deprovisionAction)
    {
        var sourceSystem = await CreateConnectedSystemAsync($"Contract Source {Guid.NewGuid():N}");
        var targetSystem = await CreateConnectedSystemAsync($"Contract Target {Guid.NewGuid():N}");

        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "Status", Type = AttributeDataType.Text, Selected = true }
        });

        // The target's primary ExternalId is a FLOWED attribute (Text, sourced from EmployeeId), not a
        // connector-assigned one: a file connector cannot assign identifiers, so the real File Connector's
        // own External Id resolution (FileConnectorExport.GetExternalIdValue/FindExternalIdAttributeName)
        // requires the External Id attribute to be carried in the Create Pending Export's AttributeValueChanges
        // and echoes that value straight back (see StubFileExportConnector.ResolveExternalId below, which
        // mirrors it). The batched-calls path (MockCallConnector) instead fabricates its own connector-assigned
        // id on Create, same as it always has; BatchUpdateCsosAfterSuccessfulExportAsync writes back whichever
        // value the connector actually returned regardless of path, so both are handled uniformly downstream.
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "User", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Text, IsExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true }
        });

        var mvType = await CreateMvObjectTypeAsync("Contract Person");
        // AddMetaverseAttributeAsync detaches every Modified-state entity before its own SaveChangesAsync
        // (mirroring the pattern in NeverExportedProvisioningCancellationTests); mutating mvType's own
        // properties BEFORE calling it would mark mvType Modified, get it detached mid-call, and then have
        // Add() walk the still-attached MetaverseObjectTypes navigation on the new attribute and try to
        // re-insert the (already persisted) mvType as new - an EF in-memory "same key already added"
        // failure, not a product defect. Add the attribute first, mutate mvType's own columns after.
        var statusMvAttr = await AddMetaverseAttributeAsync(mvType, "Status");
        mvType.DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected;
        mvType.DeletionGracePeriod = TimeSpan.Zero;
        mvType.DeletionTriggerConnectedSystemIds = [sourceSystem.Id];
        mvType.DeletionTriggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect;

        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "Contract Source Import");
        var sourceStatusAttr = sourceType.Attributes.Single(a => a.Name == "Status");
        var sourceDisplayNameAttr = sourceType.Attributes.Single(a => a.Name == "DisplayName");
        var sourceEmployeeIdAttr = sourceType.Attributes.Single(a => a.Name == "EmployeeId");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");

        importRule.AttributeFlowRules.Add(DirectMapping(importRule, statusMvAttr, sourceStatusAttr));
        importRule.AttributeFlowRules.Add(DirectMapping(importRule, mvDisplayNameAttr, sourceDisplayNameAttr));
        importRule.AttributeFlowRules.Add(DirectMapping(importRule, mvEmployeeIdAttr, sourceEmployeeIdAttr));

        var exportRule = await CreateExportSyncRuleAsync(
            targetSystem.Id, targetType, mvType, "Contract Target Export",
            enableProvisioning: true, deprovisionAction: deprovisionAction);

        var targetDisplayNameAttr = targetType.Attributes.Single(a => a.Name == "DisplayName");
        var targetExternalIdAttr = targetType.Attributes.Single(a => a.IsExternalId);
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
            TargetConnectedSystemAttribute = targetExternalIdAttr,
            TargetConnectedSystemAttributeId = targetExternalIdAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvEmployeeIdAttr, MetaverseAttributeId = mvEmployeeIdAttr.Id } }
        });
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    MetaverseAttribute = statusMvAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "Active",
                    CaseSensitive = true
                }
            }
        });

        return new Topology(sourceSystem, targetSystem, sourceType, targetType, mvType, importRule, exportRule,
            statusMvAttr, sourceStatusAttr, sourceDisplayNameAttr, sourceEmployeeIdAttr);
    }

    private static SyncRuleMapping DirectMapping(SyncRule rule, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source) => new()
    {
        SyncRule = rule,
        TargetMetaverseAttribute = target,
        TargetMetaverseAttributeId = target.Id,
        Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
    };

    private async Task<(ConnectedSystemObject Cso, ConnectedSystemObjectAttributeValue StatusValue)> CreateSourceCsoAsync(
        Topology topology, string displayName, string employeeId)
    {
        var sourceCso = await CreateCsoAsync(topology.Source.Id, topology.SourceType, displayName, employeeId);
        var statusValue = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = topology.SourceStatusAttr.Id,
            Attribute = topology.SourceStatusAttr,
            StringValue = "Active",
            ConnectedSystemObject = sourceCso
        };
        sourceCso.AttributeValues.Add(statusValue);
        return (sourceCso, statusValue);
    }

    private static void ReplaceStringAttributeValue(ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute attribute, string newValue)
    {
        cso.AttributeValues.RemoveAll(av => av.AttributeId == attribute.Id);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = attribute.Id,
            Attribute = attribute,
            StringValue = newValue,
            ConnectedSystemObject = cso
        });
    }

    private List<PendingExport> PendingExportsFor(Guid connectedSystemObjectId) =>
        SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemObjectId == connectedSystemObjectId).ToList();

    /// <summary>
    /// Builds a confirming import object reporting what the target currently, actually holds:
    /// DisplayName comes from the SOURCE, because a file-style export never writes non-identifier
    /// attribute values back onto the CSO (<c>ExecuteUsingFilesWithBatchingAsync</c>'s own comment: "the
    /// confirming import re-materialises the CSO's attribute values as before") - the value nonetheless
    /// really is in the target (the export wrote it there), so it is exactly what a real confirming
    /// import would read back. ExternalId comes from the TARGET CSO's own current attribute value
    /// instead, because <c>BatchUpdateCsosAfterSuccessfulExportAsync</c> DOES apply
    /// <c>ConnectedSystemExportResult.ExternalId</c> onto the CSO after a successful Create on both
    /// export paths, and what gets applied differs by connector: the batched-calls path
    /// (<see cref="MockCallConnector"/>) fabricates its own connector-assigned id, while the file path
    /// (<see cref="StubFileExportConnector"/>) echoes back the flowed value - reading the CSO's actual
    /// state, rather than reconstructing from the source, keeps this correct for both without needing to
    /// know which path produced it.
    /// </summary>
    private static ConnectedSystemImportObject BuildConfirmingImportObject(Topology topology, ConnectedSystemObject sourceCso, ConnectedSystemObject targetCso)
    {
        var displayName = sourceCso.AttributeValues.SingleOrDefault(av => av.AttributeId == topology.SourceDisplayNameAttr.Id)?.StringValue;

        var targetExternalIdAttr = topology.TargetType.Attributes.Single(a => a.IsExternalId);
        var externalId = targetCso.AttributeValues.SingleOrDefault(av => av.AttributeId == targetExternalIdAttr.Id)?.StringValue;

        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = topology.TargetType.Name,
            ChangeType = ObjectChangeType.Updated
        };

        if (displayName != null)
            importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "DisplayName", StringValues = { displayName } });
        if (externalId != null)
            importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "ExternalId", StringValues = { externalId } });

        return importObject;
    }

    /// <summary>Adds a single-valued text Metaverse Attribute to the given type (pattern: NeverExportedProvisioningCancellationTests).</summary>
    private async Task<MetaverseAttribute> AddMetaverseAttributeAsync(MetaverseObjectType mvType, string name)
    {
        var attribute = new MetaverseAttribute
        {
            Name = name,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };

        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == EntityState.Modified).ToList())
            entry.State = EntityState.Detached;
        DbContext.MetaverseAttributes.Add(attribute);
        await DbContext.SaveChangesAsync();

        if (!mvType.Attributes.Contains(attribute))
            mvType.Attributes.Add(attribute);

        return attribute;
    }

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem system, string runProfileName)
    {
        var runProfile = await CreateRunProfileAsync(system.Id, runProfileName, ConnectedSystemRunType.FullSynchronisation);
        var reloaded = await ReloadEntityAsync(system);
        var activity = await CreateActivityAsync(reloaded.Id, runProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, runProfile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        DbContext.ChangeTracker.Clear();
        return activity;
    }

    private async Task<Activity> RunDeltaSyncAsync(ConnectedSystem system, string runProfileName)
    {
        var runProfile = await CreateRunProfileAsync(system.Id, runProfileName, ConnectedSystemRunType.DeltaSynchronisation);
        var reloaded = await ReloadEntityAsync(system);
        var activity = await CreateActivityAsync(reloaded.Id, runProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, runProfile, activity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();
        DbContext.ChangeTracker.Clear();
        return activity;
    }

    private async Task<Activity> RunExportAsync(ConnectedSystem system, IConnector connector)
    {
        var runProfile = await CreateRunProfileAsync(system.Id, "Export", ConnectedSystemRunType.Export);
        var reloaded = await ReloadEntityAsync(system);
        var activity = await CreateActivityAsync(reloaded.Id, runProfile, ConnectedSystemRunType.Export);
        var workerTask = CreateWorkerTask(reloaded.Id, runProfile.Id, activity);
        var processor = new SyncExportTaskProcessor(new SyncServer(Jim), SyncRepo, connector, reloaded, runProfile, workerTask, new CancellationTokenSource());
        await processor.PerformExportAsync();
        DbContext.ChangeTracker.Clear();
        return activity;
    }

    private async Task<Activity> RunConfirmingImportAsync(ConnectedSystem system, IConnector connector)
    {
        var runProfile = await CreateRunProfileAsync(system.Id, "Confirming Import", ConnectedSystemRunType.FullImport);
        var reloaded = await ReloadEntityAsync(system);
        var activity = await CreateActivityAsync(reloaded.Id, runProfile, ConnectedSystemRunType.FullImport);
        var workerTask = CreateWorkerTask(reloaded.Id, runProfile.Id, activity);
        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(), connector, reloaded, runProfile, workerTask, new CancellationTokenSource());
        await processor.PerformImportAsync();
        DbContext.ChangeTracker.Clear();
        return activity;
    }

    private static SynchronisationWorkerTask CreateWorkerTask(int connectedSystemId, int runProfileId, Activity activity) => new(connectedSystemId, runProfileId)
    {
        Id = Guid.NewGuid(),
        Status = WorkerTaskStatus.Processing,
        Activity = activity
    };

    #endregion
}
