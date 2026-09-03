// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Regression coverage for the grace-period deletion marker page-flush bug: when a Connected System
/// Object is obsoleted mid-page and its joined Metaverse Object's deletion rule schedules a deferred
/// deletion (grace period greater than zero), the marker fields (LastConnectorDisconnectedDate,
/// DeletionInitiatedBy*, DeletionTriggeredBy*, DeletionPolicySnapshotJson) must be queued onto the
/// ordinary page-flush Metaverse Object update batch, never persisted via an immediate, context-wide
/// <c>SaveChangesAsync</c> mid-page.
/// <para>
/// The reason is subtle enough to be worth stating in full: a page's disconnection Activity Run Profile
/// Execution Item (RPEI) is created and added to the tracked Activity's <c>RunProfileExecutionItems</c>
/// collection BEFORE the deletion rule is evaluated. Mid-page, <c>AutoDetectChangesEnabled</c> is still
/// true (the flush only disables it after both processing passes complete), so an immediate
/// <c>SaveChangesAsync</c> here runs a full <c>DetectChanges()</c> that scans EVERY tracked entity's
/// navigation properties, not just the Metaverse Object being updated. That discovers the Activity's
/// freshly-added RPEI as new and inserts it early. The page flush's own raw-SQL RPEI bulk insert (the
/// production fast path) then tries to insert the SAME RPEI Id a second time, and the whole Activity
/// fails on a duplicate-key violation. Verified against real PostgreSQL in integration Scenario 5.
/// </para>
/// <para>
/// The in-memory <see cref="JIM.InMemoryData.SyncRepository"/> used by workflow tests is a hand-rolled
/// dictionary store with no <c>DbContext</c>, no change tracker and no unique constraints, so it cannot
/// reproduce the duplicate-key failure itself (per test/CLAUDE.md's EF Core in-memory limitation notes,
/// which apply doubly here since this store is not even EF Core). The strongest test the harness can
/// support instead asserts the causative interaction directly: the single-entity
/// <c>UpdateMetaverseObjectAsync</c> path (the one that, in production, performs the dangerous immediate
/// graph-walking update) must never be called while a page is mid-flight, and the marker fields must
/// instead flow through the batch <c>UpdateMetaverseObjectsAsync</c> path that
/// <c>PersistPendingMetaverseObjectsAsync</c> drives at the page boundary. This is an interaction
/// assertion by necessity, not by preference: it is the one assertion this harness can make that
/// distinguishes the buggy code path from the fixed one, since both produce the same final dictionary
/// state (the fake persists either way). It is paired with an observable-state assertion (the marker
/// fields are correct after the run) to prove the fix does not merely move the write, but preserves it.
/// </para>
/// </summary>
[TestFixture]
public class GraceDeletionMarkerPageFlushTests : WorkflowTestBase
{
    private SpySyncRepository _spySyncRepo = null!;

    /// <summary>
    /// Replaces the base harness's sync repository with a spy BEFORE any seeding, so every helper method
    /// (which seeds via <c>SyncRepo</c>) writes into the spy instance. Mirrors the established pattern in
    /// <c>SynchronisedDeprovisioningWorkflowTests.SetUpFailableSyncRepo</c>.
    /// </summary>
    [SetUp]
    public void SetUpSpySyncRepo()
    {
        _spySyncRepo = new SpySyncRepository();
        _spySyncRepo.SetSyncOutcomeTrackingLevel(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        SyncRepo = _spySyncRepo;
        Jim = new JimApplication(Repository, syncRepository: SyncRepo);
    }

    [Test]
    public async Task DeltaSync_GracePeriodDeletionScheduledMidPage_QueuesMarkersOnTheBatchFlushInsteadOfMidPageSaveAsync()
    {
        // Arrange: a WhenLastConnectorDisconnected deletion rule with a grace period, same shape as
        // DeletionRuleWorkflowTests.WhenLastConnectorDisconnected_WithGracePeriod_MvoMarkedForDeletionAsync,
        // which proves the observable-state half of this contract already holds; this test adds the
        // interaction assertion that catches the mid-page double-persist regression that test cannot see.
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: TimeSpan.FromDays(30));
        await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "precondition: the CSO must be joined to an MVO after Full Sync");
        var mvoId = cso.MetaverseObjectId!.Value;
        await MarkCsoAsObsoleteAsync(cso);

        // Isolate the assertion to the delta sync run: reset the spy's counters so the projecting full sync
        // above (a different code path) cannot mask what the delta sync itself does.
        _spySyncRepo.SingleMvoUpdateCallCount = 0;
        _spySyncRepo.BatchMvoUpdateIds.Clear();

        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);

        // Act: process the now-Obsolete CSO. This drives ProcessObsoleteConnectedSystemObjectAsync ->
        // ProcessMvoDeletionRuleAsync -> MarkMvoForDeletionAsync's grace branch mid-page.
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, deltaSyncProfile, deltaSyncActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        using (Assert.EnterMultipleScope())
        {
            // The interaction assertion: this is what actually distinguishes the buggy behaviour (an
            // immediate, context-wide save mid-page) from the fix (queued onto the page-flush batch). Before
            // the fix, MarkMvoForDeletionAsync's grace branch calls the single-entity UpdateMetaverseObjectAsync
            // directly, so this assertion fails with a call count of 1.
            Assert.That(_spySyncRepo.SingleMvoUpdateCallCount, Is.Zero,
                "A grace-period deletion must never persist the Metaverse Object via the single-entity update " +
                "path mid-page. In production that path performs Database.Update() + an immediate " +
                "SaveChangesAsync while AutoDetectChangesEnabled is still true, which walks the tracked " +
                "Activity's RunProfileExecutionItems collection and inserts this page's RPEIs early; the page " +
                "flush's own raw-SQL RPEI insert then collides on the same Id.");
            Assert.That(_spySyncRepo.BatchMvoUpdateIds, Does.Contain(mvoId),
                "The Metaverse Object's deletion markers must instead be queued onto the ordinary page-flush " +
                "batch update (PersistPendingMetaverseObjectsAsync -> UpdateMetaverseObjectsAsync).");

            // The observable-state assertion: the fix must not merely avoid the dangerous call, it must still
            // land the marker fields correctly via the batch path.
            Assert.That(mvo, Is.Not.Null, "the MVO must still exist (grace period > 0 defers deletion to housekeeping)");
            Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Not.Null,
                "LastConnectorDisconnectedDate must be persisted by the batch flush");
            Assert.That(mvo.DeletionTriggeredBySystemId, Is.EqualTo(sourceSystem.Id),
                "the triggering system must be persisted by the batch flush");
            Assert.That(mvo.DeletionPolicySnapshotJson, Is.Not.Null.And.Not.Empty,
                "the decision-time policy snapshot must be persisted by the batch flush");
        }
    }

    /// <summary>
    /// Tripwire coverage for the SamePageJoinConflict identity-split shape (#1612): when a page's Pass 1
    /// schedules a grace-period deletion on a Metaverse Object (via the obsoleting CSO's own, already-loaded
    /// MVO instance) and Pass 2 joins a DIFFERENT CSO to that same Metaverse Object via matching (which, in
    /// production, performs a separate database load and so CAN return a distinct CLR instance of the same
    /// row), the page identity map (<c>MetaverseObjectPageIdentityMap</c>) must absorb the split before it
    /// ever reaches <c>QueueMvoForUpdate</c>. This test proves the absorption itself, not the state that
    /// results from it: see
    /// <see cref="DeltaSync_GracePeriodDeletionWithSamePageRejoin_CancelsScheduledDeletionAsync"/> for the
    /// observable-state assertions (which now assert cancellation, not survival - see that test's own
    /// remarks for why the original version of this test asserted the wrong outcome).
    /// <para>
    /// The in-memory <see cref="JIM.InMemoryData.SyncRepository"/>'s matching lookup returns the exact
    /// object reference it has stored, so it cannot reproduce the identity split on its own: Pass 1 and
    /// Pass 2 would operate on the SAME instance, masking the scenario entirely. <see cref="SpySyncRepository"/>
    /// overrides the matching lookup to return a clone instead, deterministically reproducing what a
    /// genuine separate EF load COULD produce, so this test can drive the real two-pass page pipeline
    /// (<see cref="SyncDeltaSyncTaskProcessor.PerformDeltaSyncAsync"/>) and still observe the map doing its
    /// job. On real PostgreSQL, JIM's single tracking DbContext already resolves both loads to one instance
    /// via EF's own identity map, so this scenario is latent there, not live; the map is defence in depth
    /// (see <c>docs/developer/diagrams/MVO_DELETION_AND_GRACE_PERIOD.md</c> > Same-Page Disconnect-Then-Rejoin
    /// Hardening).
    /// </para>
    /// </summary>
    [Test]
    public async Task DeltaSync_GracePeriodDeletionWithSamePageJoin_AbsorbsTheSplitInstanceViaIdentityMapAsync()
    {
        const string RekeyedDisplayName = "John Smith II";

        var (sourceSystem, sourceType, mvType, _) = await ArrangeSamePageRejoinFixtureAsync(
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);

        var csoA = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", employeeId: "E100");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        csoA = await ReloadEntityAsync(csoA);
        Assert.That(csoA.MetaverseObjectId, Is.Not.Null, "precondition: CSO A must be projected to an MVO after Full Sync");
        var mvoId = csoA.MetaverseObjectId!.Value;

        // Isolate the assertion to the delta sync run below.
        _spySyncRepo.SingleMvoUpdateCallCount = 0;
        _spySyncRepo.BatchMvoUpdateIds.Clear();

        // Obsolete CSO A (Pass 1 will schedule the grace-period deletion) and, in the SAME page, seed a
        // second CSO that rekeys the same identity via matching (Pass 2 will join it).
        await MarkCsoAsObsoleteAsync(csoA);
        var csoB = await CreateCsoAsync(sourceSystem.Id, sourceType, RekeyedDisplayName, employeeId: "E100", lastUpdated: DateTime.UtcNow);

        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);

        // Act: process both CSOs in one page. Pass 1 obsoletes CSO A and queues its own MVO instance for
        // the grace-period deletion markers; Pass 2 joins CSO B via matching, which (via the spy) returns a
        // distinct clone of the same MVO row rather than the stored instance - the SamePageJoinConflict shape.
        var deltaSyncProcessor = new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, deltaSyncProfile, deltaSyncActivity, new CancellationTokenSource());
        await deltaSyncProcessor.PerformDeltaSyncAsync();

        using (Assert.EnterMultipleScope())
        {
            // The tripwire itself: the spy's clone must be absorbed into the page's canonical instance by
            // the identity map, not silently accepted as a second, distinct load.
            Assert.That(deltaSyncProcessor.MvoIdentityMapAbsorbedCountForTests, Is.EqualTo(1),
                "the spy's cloned match must be absorbed by MetaverseObjectPageIdentityMap");

            // The observable proof that the clone never reached the page-flush batch as a second entry:
            // exactly one entry for this MVO Id must ever have reached the batch flush call.
            Assert.That(_spySyncRepo.BatchMvoUpdateIds.Count(id => id == mvoId), Is.EqualTo(1),
                "the absorbed clone must never reach the page-flush batch update as a distinct instance of " +
                "this MVO Id");
        }
    }

    /// <summary>
    /// Regression coverage for #1612 (hardening, not a live-bug fix - see the class-level identity map
    /// remarks): when a page's Pass 1 schedules a grace-period deletion on a Metaverse Object and Pass 2
    /// joins a DIFFERENT, rekeyed CSO of the SAME Connected System to that same Metaverse Object in the SAME
    /// page, the rejoin must CANCEL the scheduled deletion, exactly as a rejoin in a later, separate run
    /// already does. Under <see cref="MetaverseObjectDeletionRule.WhenLastConnectorDisconnected"/> any
    /// reconnection cancels ("a connector now exists, the condition no longer holds" -
    /// <see cref="ISyncEngine.ShouldCancelScheduledDeletion"/>), so the rekeyed CSO B counts exactly the same
    /// as any other rejoin.
    /// <para>
    /// The previous version of this test (before #1612) asserted the OPPOSITE: that the markers "survive
    /// consolidation". That was asserting the bug, not guarding against it - the old <c>QueueMvoForUpdate</c>
    /// consolidation path preserved Pass 1's markers onto Pass 2's join instance without ever routing that
    /// instance through <c>EstablishJoinAsync</c>'s cancellation check, so a rejoin that should have
    /// cancelled the deletion silently left it scheduled instead. With the page identity map in place, Pass
    /// 2's join operates on the SAME canonical instance Pass 1 marked, so the ordinary cancellation check
    /// applies and correctly clears every marker.
    /// </para>
    /// </summary>
    [Test]
    public async Task DeltaSync_GracePeriodDeletionWithSamePageRejoin_CancelsScheduledDeletionAsync()
    {
        const string RekeyedDisplayName = "John Smith II";

        var (sourceSystem, sourceType, mvType, mvDisplayNameAttr) = await ArrangeSamePageRejoinFixtureAsync(
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);

        var csoA = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", employeeId: "E100");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        csoA = await ReloadEntityAsync(csoA);
        Assert.That(csoA.MetaverseObjectId, Is.Not.Null, "precondition: CSO A must be projected to an MVO after Full Sync");
        var mvoId = csoA.MetaverseObjectId!.Value;

        _spySyncRepo.SingleMvoUpdateCallCount = 0;
        _spySyncRepo.BatchMvoUpdateIds.Clear();

        // Obsolete CSO A (Pass 1 schedules the grace-period deletion) and, in the SAME page, seed a second
        // CSO that rekeys the same identity via matching (Pass 2 joins it and must cancel the deletion).
        await MarkCsoAsObsoleteAsync(csoA);
        var csoB = await CreateCsoAsync(sourceSystem.Id, sourceType, RekeyedDisplayName, employeeId: "E100", lastUpdated: DateTime.UtcNow);

        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);

        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, deltaSyncProfile, deltaSyncActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        var csoBAfter = SyncRepo.ConnectedSystemObjects[csoB.Id];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo, Is.Not.Null, "the MVO must still exist (the rejoin cancels the deletion; it was never scheduled for immediate removal)");

            // All seven deletion markers must be cleared by the cancellation (ClearMvoDeletionMarkers).
            Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Null, "LastConnectorDisconnectedDate must be cleared by the same-page rejoin's cancellation");
            Assert.That(mvo.DeletionInitiatedByType, Is.EqualTo(ActivityInitiatorType.NotSet), "DeletionInitiatedByType must be cleared");
            Assert.That(mvo.DeletionInitiatedById, Is.Null, "DeletionInitiatedById must be cleared");
            Assert.That(mvo.DeletionInitiatedByName, Is.Null, "DeletionInitiatedByName must be cleared");
            Assert.That(mvo.DeletionTriggeredBySystemId, Is.Null, "DeletionTriggeredBySystemId must be cleared");
            Assert.That(mvo.DeletionTriggeredBySystemName, Is.Null, "DeletionTriggeredBySystemName must be cleared");
            Assert.That(mvo.DeletionPolicySnapshotJson, Is.Null, "DeletionPolicySnapshotJson must be cleared");

            // CSO B must be the one now joined.
            Assert.That(csoBAfter.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined), "CSO B must be joined");
            Assert.That(csoBAfter.MetaverseObjectId, Is.EqualTo(mvoId), "CSO B must be joined to the same MVO");

            var displayNameValue = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == mvDisplayNameAttr.Id);
            Assert.That(displayNameValue?.StringValue, Is.EqualTo(RekeyedDisplayName),
                "the MVO's display name must be CSO B's (the rejoiner's), proving the join's Attribute Flow landed on the canonical instance");

            // Exactly one batch entry for this MVO Id: Pass 1's mark-for-deletion queue and Pass 2's join
            // queue resolve to the SAME instance via the identity map, so QueueMvoForUpdate's own by-Id
            // check (not its tripwire consolidation) is what collapses the second call.
            Assert.That(_spySyncRepo.BatchMvoUpdateIds.Count(id => id == mvoId), Is.EqualTo(1),
                "exactly one entry for this MVO must reach the page-flush batch update");
        }
    }

    /// <summary>
    /// Same shape as <see cref="DeltaSync_GracePeriodDeletionWithSamePageRejoin_CancelsScheduledDeletionAsync"/>
    /// under <see cref="MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected"/> +
    /// <see cref="AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect"/>, with the disconnecting (and
    /// rejoining) source system itself listed as a trigger. Per <see cref="ISyncEngine.ShouldCancelScheduledDeletion"/>,
    /// SpecificSourcesDisconnect cancels only when the rejoining system equals the recorded
    /// <c>DeletionTriggeredBySystemId</c> - true here, since the same source system both disconnected (CSO
    /// A) and rejoined (CSO B) within the page.
    /// </summary>
    [Test]
    public async Task DeltaSync_AuthoritativeSourceDisconnectedWithSamePageRejoin_CancelsScheduledDeletionAsync()
    {
        const string RekeyedDisplayName = "John Smith II";

        var (sourceSystem, sourceType, mvType, mvDisplayNameAttr) = await ArrangeSamePageRejoinFixtureAsync(
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            triggerMode: AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            triggerSystemIdSelector: system => new List<int> { system.Id });

        var csoA = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", employeeId: "E100");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        csoA = await ReloadEntityAsync(csoA);
        Assert.That(csoA.MetaverseObjectId, Is.Not.Null, "precondition: CSO A must be projected to an MVO after Full Sync");
        var mvoId = csoA.MetaverseObjectId!.Value;

        await MarkCsoAsObsoleteAsync(csoA);
        var csoB = await CreateCsoAsync(sourceSystem.Id, sourceType, RekeyedDisplayName, employeeId: "E100", lastUpdated: DateTime.UtcNow);

        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);

        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, deltaSyncProfile, deltaSyncActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo, Is.Not.Null);
            Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Null,
                "the rejoining system is the recorded triggering system under SpecificSourcesDisconnect, so the deletion must cancel");
            Assert.That(mvo.DeletionTriggeredBySystemId, Is.Null);
            Assert.That(mvo.DeletionPolicySnapshotJson, Is.Null);

            var displayNameValue = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == mvDisplayNameAttr.Id);
            Assert.That(displayNameValue?.StringValue, Is.EqualTo(RekeyedDisplayName));
        }
    }

    /// <summary>
    /// Negative guard against over-cancelling (#1612): a scheduled deletion triggered by a LISTED system (A)
    /// under <see cref="AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect"/> must NOT be cancelled by
    /// a same-page obsolete-then-rekey of a DIFFERENT, non-listed system (B). This proves the mode-aware
    /// <see cref="ISyncEngine.ShouldCancelScheduledDeletion"/> check still governs a same-page rejoin
    /// correctly, rather than a same-page rejoin unconditionally cancelling any pending deletion.
    /// <para>
    /// Deliberately uses a plain <see cref="JIM.InMemoryData.SyncRepository"/> rather than
    /// <see cref="SpySyncRepository"/>: this test is not exercising the identity-split scenario (system B's
    /// rejoin correctly resolves to the same stored instance either way in the in-memory harness), only the
    /// mode-aware cancellation predicate itself. It is green both before and after #1612's identity map.
    /// </para>
    /// </summary>
    [Test]
    public async Task DeltaSync_SamePageRejoinByNonTriggeringSystem_DoesNotCancelScheduledDeletionAsync()
    {
        // Use a plain in-memory repository: no identity-split simulation needed for this guard.
        SyncRepo = new JIM.InMemoryData.SyncRepository();
        SyncRepo.SetSyncOutcomeTrackingLevel(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        Jim = new JimApplication(Repository, syncRepository: SyncRepo);

        const string SharedEmployeeId = "E200";

        var systemA = await CreateConnectedSystemAsync("System A (trigger)");
        var systemB = await CreateConnectedSystemAsync("System B (not a trigger)");
        var typeA = await CreateCsoTypeAsync(systemA.Id, "User");
        var typeB = await CreateCsoTypeAsync(systemB.Id, "User");

        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            gracePeriod: TimeSpan.FromDays(30),
            triggerConnectedSystemIds: new List<int> { systemA.Id },
            triggerMode: AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect);

        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var importRuleA = await CreateImportSyncRuleAsync(systemA.Id, typeA, mvType, "A Import");
        AddEmployeeIdMatchingAndFlow(importRuleA, typeA, mvType);

        var importRuleB = await CreateImportSyncRuleAsync(systemB.Id, typeB, mvType, "B Import", enableProjection: false);
        AddEmployeeIdMatchingAndFlow(importRuleB, typeB, mvType);
        await DbContext.SaveChangesAsync();

        // System A projects the MVO; System B joins it via matching (multi-source topology).
        var csoA = await CreateCsoAsync(systemA.Id, typeA, "John Smith", employeeId: SharedEmployeeId);
        var csoB1 = await CreateCsoAsync(systemB.Id, typeB, "John Smith (B)", employeeId: SharedEmployeeId);

        await RunFullSyncAsync(systemA);
        await RunFullSyncAsync(systemB);

        csoA = await ReloadEntityAsync(csoA);
        Assert.That(csoA.MetaverseObjectId, Is.Not.Null, "precondition: CSO A must be projected to an MVO");
        var mvoId = csoA.MetaverseObjectId!.Value;

        // Simulate a marker pre-existing from an earlier run: System A (the listed trigger) disconnected at
        // some point in the past and the deletion is still within its grace period.
        var mvo = SyncRepo.MetaverseObjects[mvoId];
        mvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-1);
        mvo.DeletionInitiatedByType = ActivityInitiatorType.System;
        mvo.DeletionInitiatedByName = "System Initialisation";
        mvo.DeletionTriggeredBySystemId = systemA.Id;
        mvo.DeletionTriggeredBySystemName = systemA.Name;
        mvo.DeletionPolicySnapshotJson = "{}";

        // Act: obsolete System B's CSO and, in the SAME page, seed a rekeyed replacement for System B. The
        // rejoin is from System B, which is NOT the recorded triggering system (A).
        var csoB1Reloaded = await ReloadEntityAsync(csoB1);
        await MarkCsoAsObsoleteAsync(csoB1Reloaded);
        var csoB2 = await CreateCsoAsync(systemB.Id, typeB, "John Smith (B, rekeyed)", employeeId: SharedEmployeeId, lastUpdated: DateTime.UtcNow);

        var deltaSyncProfile = await CreateRunProfileAsync(systemB.Id, "B Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var reloadedSystemB = await ReloadEntityAsync(systemB);
        var deltaSyncActivity = await CreateActivityAsync(systemB.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloadedSystemB, deltaSyncProfile, deltaSyncActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        var mvoAfter = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvoAfter, Is.Not.Null, "the MVO must still exist (grace period not elapsed)");
            Assert.That(mvoAfter!.LastConnectorDisconnectedDate, Is.Not.Null,
                "System B's rejoin must NOT cancel a deletion triggered by System A under SpecificSourcesDisconnect");
            Assert.That(mvoAfter.DeletionTriggeredBySystemId, Is.EqualTo(systemA.Id),
                "the triggering system marker must remain System A, unaffected by System B's same-page rejoin");
            Assert.That(mvoAfter.DeletionPolicySnapshotJson, Is.Not.Null,
                "the policy snapshot must remain, unaffected by System B's same-page rejoin");
        }
    }

    /// <summary>
    /// Coverage for <c>QueueMvoForUpdate</c>'s tripwire consolidation branch itself (#1612): with the page
    /// identity map in place, a second distinct instance should never reach <c>QueueMvoForUpdate</c> in
    /// practice, but the branch remains as a belt-and-braces fallback (a Warning-level consolidation, not a
    /// hard failure) for a load site that bypasses the map. Exercised directly via the
    /// <c>QueueMvoForUpdateForTests</c> hook rather than by contriving a genuine map-bypassing load (which
    /// the map's own coverage is designed to prevent), matching the file's established pattern of internal
    /// test hooks (<c>OnCsoProcessedInPass2</c>) for state the fixture cannot otherwise reach.
    /// </summary>
    [Test]
    public async Task QueueMvoForUpdate_DistinctInstanceForSameId_ConsolidatesRatherThanLosingDeletionMarkersAsync()
    {
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var runProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var activity = await CreateActivityAsync(sourceSystem.Id, runProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var processor = new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, runProfile, activity, new CancellationTokenSource());

        var mvoId = Guid.NewGuid();
        var firstInstance = new MetaverseObject
        {
            Id = mvoId,
            LastConnectorDisconnectedDate = DateTime.UtcNow,
            DeletionTriggeredBySystemId = sourceSystem.Id,
            DeletionTriggeredBySystemName = sourceSystem.Name,
            DeletionPolicySnapshotJson = "{}"
        };
        var secondInstance = new MetaverseObject
        {
            Id = mvoId,
            CachedDisplayName = "Second, attribute-bearing instance"
            // Deliberately no deletion markers: simulates a fresh Pass 2 load that never saw Pass 1's marks.
        };

        processor.QueueMvoForUpdateForTests(firstInstance);
        processor.QueueMvoForUpdateForTests(secondInstance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processor.PendingMvoUpdatesForTests.Count, Is.EqualTo(1),
                "a second distinct instance for the same Id must be consolidated, not queued as a second entry");
            var retained = processor.PendingMvoUpdatesForTests.Single();
            Assert.That(retained, Is.SameAs(secondInstance), "the newer (second) instance is kept");
            Assert.That(retained.LastConnectorDisconnectedDate, Is.Not.Null,
                "the deletion markers from the dropped first instance must be copied across, not lost");
            Assert.That(retained.DeletionTriggeredBySystemId, Is.EqualTo(sourceSystem.Id));
            Assert.That(retained.DeletionPolicySnapshotJson, Is.EqualTo("{}"));
            Assert.That(retained.CachedDisplayName, Is.EqualTo("Second, attribute-bearing instance"),
                "the retained instance's own (non-deletion-marker) state must be unaffected by the copy");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Regression coverage for the exception-path pending-list clear (#1612): when an inbound Attribute Flow
    /// expression throws mid-evaluation, any values a PRECEDING mapping in the same pass already staged onto
    /// the joined MVO's <c>PendingAttributeValueAdditions</c>/<c>Removals</c> must be cleared, not left
    /// sitting on the instance. Before this fix the comment on this catch block said "the MVO is left
    /// untouched", which was only true because every CSO in a page held its own distinct MVO instance; with
    /// the page identity map making that instance page-wide shared and canonical (#1612), leaving the
    /// partial state behind would risk a LATER operation that reaches the same canonical instance applying
    /// it alongside its own unrelated changes. This test proves the clearing itself fires; the sync engine's
    /// one-join-per-system-per-page constraint means a genuine second CSO cannot legitimately reach the same
    /// MVO within the same page to demonstrate downstream inheritance directly (see the PR description for
    /// the analysis), so this is the strongest assertion available at the workflow level.
    /// </summary>
    [Test]
    public async Task DeltaSync_AttributeFlowExpressionThrows_ClearsStalePendingAttributeChangesAsync()
    {
        const string OriginalDisplayName = "John Smith";
        const string ChangedDisplayName = "Jane Smith";

        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync("Person", MetaverseObjectDeletionRule.Manual);
        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");

        var csoDisplayNameAttr = sourceType.Attributes.First(a => a.Name == "DisplayName");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");

        // Only a normal, working DisplayName mapping for the FIRST (projecting) run: a bad expression on a
        // CSO that is still projecting (rather than already-joined) hits a different, pre-existing ordering
        // hazard unrelated to #1612 (the projected MVO's persistence is itself deferred past the point the
        // exception aborts, leaving Guid.Empty written to the CSO's join FK). Establish the join cleanly
        // first, then introduce the failing mapping once the CSO is an ordinary Attribute-Flow-only object.
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            SyncRuleId = importRule.Id,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = csoDisplayNameAttr,
                ConnectedSystemAttributeId = csoDisplayNameAttr.Id
            }}
        });
        await DbContext.SaveChangesAsync();

        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, OriginalDisplayName, employeeId: "E100");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "precondition: the CSO must be projected to an MVO on the (unbroken) first run");
        var mvoId = cso.MetaverseObjectId!.Value;

        // Now that the CSO is joined, add a second mapping with a malformed expression, and genuinely
        // change DisplayName so the working mapping stages a real addition on this run (an unchanged value
        // would not stage anything, and the test would prove nothing). No further DbContext.SaveChangesAsync
        // is needed here: SyncRepo (JIM.InMemoryData.SyncRepository) already holds this exact importRule
        // CLR instance via SeedSyncRule, so the mutation is visible to the next sync run without a reload;
        // re-saving via DbContext after the completed Full Sync above trips a spurious
        // DbUpdateConcurrencyException from the in-memory EF provider's change tracker.
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            SyncRuleId = importRule.Id,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                Expression = "@@@ not a valid expression @@@"
            }}
        });

        cso.AttributeValues.Single(av => av.AttributeId == csoDisplayNameAttr.Id).StringValue = ChangedDisplayName;
        cso.LastUpdated = DateTime.UtcNow;

        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, deltaSyncProfile, deltaSyncActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        var mvo = SyncRepo.MetaverseObjects[mvoId];
        using (Assert.EnterMultipleScope())
        {
            var errorItems = deltaSyncActivity.RunProfileExecutionItems
                .Where(item => item.ErrorType == ActivityRunProfileExecutionItemErrorType.ExpressionEvaluationError)
                .ToList();
            Assert.That(errorItems, Is.Not.Empty, "the malformed EmployeeId expression must be surfaced as an ExpressionEvaluationError, never silently swallowed");

            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty,
                "the DisplayName mapping's partial success (staged before the EmployeeId mapping threw) must not be left on the MVO");
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty,
                "no pending removals must be left staged on the MVO after the throw");

            var displayNameValue = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == mvDisplayNameAttr.Id);
            Assert.That(displayNameValue?.StringValue, Is.EqualTo(OriginalDisplayName),
                "the DisplayName change must never have been applied to the MVO, since the run errored before ApplyPendingMetaverseObjectAttributeChanges");
        }
    }

    /// <summary>
    /// Regression coverage for issue #1610: <c>HandleCsoOutOfScopeAsync</c> carried the identical
    /// mid-page hazard as the grace-period deletion path fixed above (#1613), for a joined CSO falling
    /// out of its import Synchronisation Rule's scope (<c>InboundOutOfScopeAction.Disconnect</c>) rather
    /// than being obsoleted. Before the fix, the Disconnect branch applied the recalled attribute changes
    /// and called the single-entity <c>UpdateMetaverseObjectAsync</c> directly, mid-page, while
    /// <c>AutoDetectChangesEnabled</c> is still true; in production that walks the tracked Activity's
    /// freshly-added RPEIs and inserts them early, colliding with the page flush's own raw-SQL RPEI
    /// insert. The fix routes the same MVO onto the page-flush batch via <c>QueueMvoForUpdate</c> instead.
    /// <para>
    /// This scenario uses a second, unscoped Connected System (Training) that joins the same Metaverse
    /// Object via matching but never contributes the recalled attribute (Description). That is what makes
    /// this a genuine clear rather than a freeze: <c>HandleCsoOutOfScopeAsync</c> preserves a
    /// no-surviving-contributor attribute instead of clearing it whenever no import-capable Connected
    /// System remains joined to the Metaverse Object (or a deletion is pending), and a single-source
    /// topology always falls into that case. Training remaining joined (with an import rule for the same
    /// Metaverse Object Type) is what keeps a surviving import source in play, so Description is asserted
    /// gone rather than frozen.
    /// </para>
    /// </summary>
    [Test]
    public async Task FullSync_OutOfScopeDisconnectWithSurvivingImportSource_QueuesRecallOnTheBatchFlushInsteadOfMidPageSaveAsync()
    {
        const string SharedEmployeeId = "EMP001";
        const string HrDescription = "HR Description";

        // Arrange: HR (scoped on EmployeeId, contributes DisplayName/EmployeeId/Description) and
        // Training (unscoped, contributes only EmployeeId - the join key), joined to the same MVO.
        var hrSystem = await CreateConnectedSystemAsync("HR Source");
        var hrExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var hrDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var hrEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var hrDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "Description", Type = AttributeDataType.Text, Selected = true };
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "HrUser",
            new List<ConnectedSystemObjectTypeAttribute> { hrExternalIdAttr, hrDisplayNameAttr, hrEmployeeIdAttr, hrDescriptionAttr });
        hrType.RemoveContributedAttributesOnObsoletion = true;

        var trainingSystem = await CreateConnectedSystemAsync("Training Source");
        var trainingExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var trainingEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var trainingType = await CreateCsoTypeAsync(trainingSystem.Id, "TrainingRecord",
            new List<ConnectedSystemObjectTypeAttribute> { trainingExternalIdAttr, trainingEmployeeIdAttr });

        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync("Person", MetaverseObjectDeletionRule.Manual);
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
        hrImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = hrImportRule,
            SyncRuleId = hrImportRule.Id,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrDisplayNameAttr, ConnectedSystemAttributeId = hrDisplayNameAttr.Id } }
        });
        hrImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = hrImportRule,
            SyncRuleId = hrImportRule.Id,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrEmployeeIdAttr, ConnectedSystemAttributeId = hrEmployeeIdAttr.Id } }
        });
        hrImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = hrImportRule,
            SyncRuleId = hrImportRule.Id,
            TargetMetaverseAttribute = mvDescriptionAttr,
            TargetMetaverseAttributeId = mvDescriptionAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrDescriptionAttr, ConnectedSystemAttributeId = hrDescriptionAttr.Id } }
        });
        hrImportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    ConnectedSystemAttribute = hrEmployeeIdAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = SharedEmployeeId,
                    CaseSensitive = true
                }
            }
        });
        await DbContext.SaveChangesAsync();

        // CaseSensitive: the in-memory store does not support EF.Functions.ILike (PostgreSQL-specific).
        var trainingImportRule = await CreateImportSyncRuleAsync(trainingSystem.Id, trainingType, mvType, "Training Import", enableProjection: false);
        trainingImportRule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = trainingImportRule,
            SyncRuleId = trainingImportRule.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = trainingEmployeeIdAttr, ConnectedSystemAttributeId = trainingEmployeeIdAttr.Id } }
        });
        await DbContext.SaveChangesAsync();

        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", SharedEmployeeId);
        hrCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = hrDescriptionAttr.Id, Attribute = hrDescriptionAttr, StringValue = HrDescription, ConnectedSystemObject = hrCso
        });
        var trainingCso = await CreateCsoAsync(trainingSystem.Id, trainingType, "unused", SharedEmployeeId);

        // Full Sync both: HR projects and flows Description; Training joins via the EmployeeId match
        // without ever contributing Description, so it counts as a surviving import source only.
        await RunFullSyncAsync(hrSystem);
        await RunFullSyncAsync(trainingSystem);

        var reloadedHrCso = await ReloadEntityAsync(hrCso);
        Assert.That(reloadedHrCso.MetaverseObjectId, Is.Not.Null, "precondition: HR CSO must be joined after Full Sync");
        var mvoId = reloadedHrCso.MetaverseObjectId!.Value;

        var mvoBefore = SyncRepo.MetaverseObjects[mvoId];
        Assert.That(
            mvoBefore.AttributeValues.SingleOrDefault(av => av.AttributeId == mvDescriptionAttr.Id && !av.NullValue)?.StringValue,
            Is.EqualTo(HrDescription), "precondition: Description flowed from HR while in scope");

        // Isolate the assertion to the scope-exit run.
        _spySyncRepo.SingleMvoUpdateCallCount = 0;
        _spySyncRepo.BatchMvoUpdateIds.Clear();

        // Act: push HR out of scope and re-sync just HR. This drives HandleCsoOutOfScopeAsync's
        // Disconnect branch mid-page.
        var empIdValue = reloadedHrCso.AttributeValues.Single(av => av.AttributeId == hrEmployeeIdAttr.Id);
        empIdValue.StringValue = "OUT_OF_SCOPE";
        reloadedHrCso.LastUpdated = DateTime.UtcNow;
        await RunFullSyncAsync(hrSystem);

        var mvo = SyncRepo.MetaverseObjects[mvoId];
        var hrCsoAfter = SyncRepo.ConnectedSystemObjects[reloadedHrCso.Id];
        using (Assert.EnterMultipleScope())
        {
            // The interaction assertion: before the fix, HandleCsoOutOfScopeAsync's Disconnect branch
            // called the single-entity UpdateMetaverseObjectAsync directly after ApplyPendingMetaverseObjectAttributeChanges,
            // so this assertion fails with a call count of 1.
            Assert.That(_spySyncRepo.SingleMvoUpdateCallCount, Is.Zero,
                "An out-of-scope disconnect must never persist the Metaverse Object via the single-entity update " +
                "path mid-page. In production that path performs Database.Update() + an immediate " +
                "SaveChangesAsync while AutoDetectChangesEnabled is still true, which walks the tracked " +
                "Activity's RunProfileExecutionItems collection and inserts this page's RPEIs early; the page " +
                "flush's own raw-SQL RPEI insert then collides on the same Id.");
            Assert.That(_spySyncRepo.BatchMvoUpdateIds, Does.Contain(mvoId),
                "The recalled attribute changes must instead be queued onto the ordinary page-flush batch " +
                "update (PersistPendingMetaverseObjectsAsync -> UpdateMetaverseObjectsAsync).");

            // The observable-state assertions: the fix must not merely avoid the dangerous call, the
            // recall and the join break must still land correctly via the batch path.
            Assert.That(hrCsoAfter.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.NotJoined), "the CSO's join must be broken");
            Assert.That(hrCsoAfter.MetaverseObjectId, Is.Null, "the CSO's MVO reference must be cleared");

            var description = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == mvDescriptionAttr.Id && !av.NullValue);
            Assert.That(description, Is.Null,
                "Description has no surviving contributor (Training never mapped it), so the recall must " +
                "genuinely clear it via the batch flush, not merely avoid the dangerous call");
        }
    }

    /// <summary>
    /// Companion regression for #1610: when the CSO falling out of scope is the Metaverse Object's LAST
    /// remaining connector and a deletion grace period is configured, <c>ProcessMvoDeletionRuleAsync</c>
    /// (called earlier in <c>HandleCsoOutOfScopeAsync</c>'s Disconnect branch) already queues the SAME MVO
    /// instance via <c>QueueMvoForUpdate</c> for the grace-period markers, before the fix's own
    /// <c>QueueMvoForUpdate</c> call runs at the end of the method. Both calls pass the identical CLR
    /// reference (there is only one load in this call), so <c>QueueMvoForUpdate</c>'s
    /// <c>ReferenceEquals</c> short-circuit must silently no-op the second call rather than adding a
    /// duplicate batch entry.
    /// </summary>
    [Test]
    public async Task FullSync_OutOfScopeDisconnectTriggersGraceDeletion_ConsolidatesOntoOneBatchEntryAsync()
    {
        // Arrange: a single source, scoped import rule, and a WhenLastConnectorDisconnected deletion
        // rule with a grace period.
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: TimeSpan.FromDays(30));
        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        var csoEmployeeIdAttr = sourceType.Attributes.First(a => a.Name == "EmployeeId");
        importRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    ConnectedSystemAttribute = csoEmployeeIdAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "EMP001",
                    CaseSensitive = true
                }
            }
        });
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        await RunFullSyncAsync(sourceSystem);

        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "precondition: the CSO must be joined to an MVO after Full Sync");
        var mvoId = cso.MetaverseObjectId!.Value;

        _spySyncRepo.SingleMvoUpdateCallCount = 0;
        _spySyncRepo.BatchMvoUpdateIds.Clear();

        // Act: fall out of scope. The CSO is the sole (and therefore last) connector, so the deletion
        // rule schedules a grace-period deletion in the same call that also breaks the join.
        var empIdValue = cso.AttributeValues.Single(av => av.Attribute?.Name == "EmployeeId");
        empIdValue.StringValue = "OUT_OF_SCOPE";
        cso.LastUpdated = DateTime.UtcNow;
        await RunFullSyncAsync(sourceSystem);

        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        var csoAfter = SyncRepo.ConnectedSystemObjects[cso.Id];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_spySyncRepo.SingleMvoUpdateCallCount, Is.Zero,
                "An out-of-scope disconnect that also schedules a grace-period deletion must never persist " +
                "the Metaverse Object via the single-entity update path mid-page.");
            Assert.That(_spySyncRepo.BatchMvoUpdateIds.Count(id => id == mvoId), Is.EqualTo(1),
                "QueueMvoForUpdate must collapse the deletion rule's enqueue and the Disconnect branch's own " +
                "enqueue onto the SAME MVO instance silently (ReferenceEquals short-circuit): exactly one " +
                "entry must ever reach the page-flush batch update, not two.");

            Assert.That(mvo, Is.Not.Null, "grace period > 0 defers deletion; the MVO must still exist");
            Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Not.Null,
                "the grace-period marker must be persisted via the batch flush");
            Assert.That(csoAfter.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.NotJoined), "the CSO's join must be broken");
            Assert.That(csoAfter.MetaverseObjectId, Is.Null, "the CSO's MVO reference must be cleared");
        }
    }

    #region helpers

    /// <summary>
    /// Runs a Full Sync for a Connected System. Reloads the entity first so a caller that mutated it
    /// via a different tracked instance (or a prior sync) still passes a valid reference to the processor.
    /// </summary>
    /// <remarks>
    /// Clears DbContext's change tracker after the run. The sync engine itself never touches DbContext
    /// for MVOs (it goes through <c>SyncRepo</c>, the in-memory fake), but EF's own navigation fixup can
    /// still reach a newly-created MVO through a tracked configuration entity (e.g. its Metaverse Object
    /// Type) during the next <c>DetectChanges()</c>. Left tracked across two systems' Full Syncs, a stale
    /// instance from an earlier one collides with the <c>SpySyncRepository</c>'s deliberately-cloned
    /// instance a later system's matching-rule join returns (see
    /// <see cref="SpySyncRepository.FindMetaverseObjectUsingMatchingRuleAsync"/>): DbContext reports
    /// "another instance with the same key value is already being tracked" on the next
    /// <c>ChangeTracker.Entries()</c> call (inside <see cref="WorkflowTestBase.CreateRunProfileAsync"/>) -
    /// a test-harness artefact, not the production behaviour under test. <c>Clear()</c> (rather than a
    /// targeted per-entity detach) is what actually prevents it: it drops every tracked reference,
    /// including whatever fixup already wired into a still-tracked entity's navigation collection, without
    /// itself running <c>DetectChanges()</c>.
    /// </remarks>
    private async Task RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        DbContext.ChangeTracker.Clear();
    }

    /// <summary>
    /// Creates a Metaverse Object Type with specific deletion rule settings. Duplicated from
    /// <c>DeletionRuleWorkflowTests</c> (a <c>protected</c> helper scoped to that fixture) rather than
    /// shared, to keep this regression test self-contained and independent of that file's evolution.
    /// </summary>
    private async Task<MetaverseObjectType> CreateMvObjectTypeWithDeletionRuleAsync(
        string name,
        MetaverseObjectDeletionRule deletionRule,
        TimeSpan? gracePeriod = null,
        List<int>? triggerConnectedSystemIds = null,
        AuthoritativeSourceTriggerMode? triggerMode = null)
    {
        var mvType = new MetaverseObjectType
        {
            Name = name,
            PluralName = name + "s",
            BuiltIn = false,
            DeletionRule = deletionRule,
            DeletionGracePeriod = gracePeriod,
            DeletionTriggerConnectedSystemIds = triggerConnectedSystemIds ?? new List<int>(),
            Attributes = new List<MetaverseAttribute>(),
            ExampleDataTemplateAttributes = new List<JIM.Models.ExampleData.ExampleDataTemplateAttribute>(),
            PredefinedSearches = new List<JIM.Models.Search.PredefinedSearch>()
        };
        if (triggerMode.HasValue)
            mvType.DeletionTriggerMode = triggerMode.Value;

        DbContext.MetaverseObjectTypes.Add(mvType);
        await DbContext.SaveChangesAsync();

        var displayNameAttr = new MetaverseAttribute
        {
            Name = "DisplayName",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        var employeeIdAttr = new MetaverseAttribute
        {
            Name = "EmployeeId",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };

        DbContext.MetaverseAttributes.Add(displayNameAttr);
        DbContext.MetaverseAttributes.Add(employeeIdAttr);
        await DbContext.SaveChangesAsync();

        mvType.Attributes.Add(displayNameAttr);
        mvType.Attributes.Add(employeeIdAttr);

        return mvType;
    }

    /// <summary>
    /// Marks a CSO as Obsolete (simulating a Delete detected by an earlier import). Duplicated from
    /// <c>DeletionRuleWorkflowTests</c> for the same self-containment reason as above.
    /// </summary>
    private static Task MarkCsoAsObsoleteAsync(ConnectedSystemObject cso)
    {
        cso.Status = ConnectedSystemObjectStatus.Obsolete;
        cso.LastUpdated = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds the shared fixture for the same-page disconnect-then-rejoin tests: a single source system and
    /// object type, a Metaverse Object Type carrying the given deletion rule/grace period/trigger
    /// configuration, and an import Synchronisation Rule that both projects (for the first CSO) and matches
    /// on EmployeeId (so a second CSO can rekey the same identity by joining, rather than projecting a brand
    /// new Metaverse Object).
    /// </summary>
    private async Task<(ConnectedSystem SourceSystem, ConnectedSystemObjectType SourceType, MetaverseObjectType MvType, MetaverseAttribute MvDisplayNameAttr)>
        ArrangeSamePageRejoinFixtureAsync(
            MetaverseObjectDeletionRule deletionRule,
            AuthoritativeSourceTriggerMode? triggerMode = null,
            Func<ConnectedSystem, List<int>>? triggerSystemIdSelector = null)
    {
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            deletionRule,
            gracePeriod: TimeSpan.FromDays(30),
            triggerConnectedSystemIds: triggerSystemIdSelector?.Invoke(sourceSystem),
            triggerMode: triggerMode);
        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");

        var mvDisplayNameAttr = AddEmployeeIdMatchingAndFlow(importRule, sourceType, mvType);
        await DbContext.SaveChangesAsync();

        return (sourceSystem, sourceType, mvType, mvDisplayNameAttr);
    }

    /// <summary>
    /// Adds a DisplayName Attribute Flow mapping, an EmployeeId Attribute Flow mapping, and an EmployeeId
    /// Object Matching Rule to an import Synchronisation Rule, so a CSO carrying an EmployeeId can join an
    /// existing Metaverse Object of matching EmployeeId rather than only ever projecting a new one. Shared by
    /// every test in this fixture that needs a rekey-by-matching topology. CaseSensitive is always true: the
    /// in-memory store does not support <c>EF.Functions.ILike</c> (PostgreSQL-specific).
    /// </summary>
    private static MetaverseAttribute AddEmployeeIdMatchingAndFlow(SyncRule importRule, ConnectedSystemObjectType sourceType, MetaverseObjectType mvType)
    {
        var csoDisplayNameAttr = sourceType.Attributes.First(a => a.Name == "DisplayName");
        var csoEmployeeIdAttr = sourceType.Attributes.First(a => a.Name == "EmployeeId");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");

        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            SyncRuleId = importRule.Id,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = csoDisplayNameAttr,
                ConnectedSystemAttributeId = csoDisplayNameAttr.Id
            }}
        });
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            SyncRuleId = importRule.Id,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = csoEmployeeIdAttr,
                ConnectedSystemAttributeId = csoEmployeeIdAttr.Id
            }}
        });
        importRule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = importRule,
            SyncRuleId = importRule.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new ObjectMatchingRuleSource
            {
                Order = 0,
                ConnectedSystemAttribute = csoEmployeeIdAttr,
                ConnectedSystemAttributeId = csoEmployeeIdAttr.Id
            }}
        });

        return mvDisplayNameAttr;
    }

    /// <summary>
    /// Spies on both Metaverse Object update paths so the test can assert which one a grace-period
    /// deletion actually uses, and reproduces the identity split a real EF load produces so the
    /// SamePageJoinConflict regression coverage can drive the real two-pass page pipeline. All three base
    /// methods are virtual for exactly this purpose (see <c>JIM.InMemoryData.SyncRepository</c>).
    /// </summary>
    private sealed class SpySyncRepository : JIM.InMemoryData.SyncRepository
    {
        public int SingleMvoUpdateCallCount { get; set; }

        public List<Guid> BatchMvoUpdateIds { get; } = [];

        public override Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject)
        {
            SingleMvoUpdateCallCount++;
            return base.UpdateMetaverseObjectAsync(metaverseObject);
        }

        public override Task UpdateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
        {
            var list = metaverseObjects.ToList();
            BatchMvoUpdateIds.AddRange(list.Select(mvo => mvo.Id));
            return base.UpdateMetaverseObjectsAsync(list);
        }

        /// <summary>
        /// Returns a clone of the matched Metaverse Object rather than the stored reference. Production's
        /// matching query and an already-loaded CSO's own MetaverseObject navigation are separate database
        /// round trips returning distinct CLR instances of the same row; this dictionary-backed store
        /// would otherwise return the identical stored reference for both, masking the identity-split bug
        /// entirely. Deliberately does NOT copy the deletion-marker fields: a genuine separate load
        /// performed before Pass 1's in-memory-only marker write would not see them either.
        /// </summary>
        public override async Task<MetaverseObject?> FindMetaverseObjectUsingMatchingRuleAsync(
            ConnectedSystemObject connectedSystemObject,
            MetaverseObjectType metaverseObjectType,
            ObjectMatchingRule rule)
        {
            var match = await base.FindMetaverseObjectUsingMatchingRuleAsync(connectedSystemObject, metaverseObjectType, rule);
            if (match == null)
                return null;

            return new MetaverseObject
            {
                Id = match.Id,
                Created = match.Created,
                LastUpdated = match.LastUpdated,
                Type = match.Type,
                AttributeValues = new List<MetaverseObjectAttributeValue>(match.AttributeValues),
                Roles = match.Roles,
                Status = match.Status,
                Origin = match.Origin,
                ConnectedSystemObjects = new List<ConnectedSystemObject>(match.ConnectedSystemObjects),
                CachedDisplayName = match.CachedDisplayName
            };
        }
    }

    #endregion
}
