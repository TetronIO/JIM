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
    /// Regression coverage for the SamePageJoinConflict identity-split bug: when a page's Pass 1 schedules a
    /// grace-period deletion on a Metaverse Object (via the obsoleting CSO's own, already-loaded MVO
    /// instance) and Pass 2 joins a DIFFERENT CSO to that same Metaverse Object via matching (which, in
    /// production, performs a separate database load and so returns a distinct CLR instance of the same
    /// row), the previous reference-equality <c>Contains()</c> guard on <c>_pendingMvoUpdates</c> could not
    /// see the collision: both instances were queued, and the real-PostgreSQL batch flush either applied a
    /// nondeterministic UPDATE (duplicate-keyed VALUES row) or failed outright on a duplicate-key attribute
    /// value INSERT, aborting the whole Activity.
    /// <para>
    /// The in-memory <see cref="JIM.InMemoryData.SyncRepository"/>'s matching lookup returns the exact
    /// object reference it has stored, so it cannot reproduce the identity split on its own: Pass 1 and
    /// Pass 2 would operate on the SAME instance, masking the bug entirely. <see cref="SpySyncRepository"/>
    /// overrides the matching lookup to return a clone instead, deterministically reproducing what a
    /// genuine separate EF load produces in production, so this test can drive the real two-pass page
    /// pipeline (<see cref="SyncDeltaSyncTaskProcessor.PerformDeltaSyncAsync"/>) and still observe the
    /// collision. This is the "workflow level" reproduction the fix's test plan asked for; the
    /// alternative would have been calling the (deliberately private) QueueMvoForUpdate consolidation
    /// helper directly via a lower-level harness, which this achieves without needing to loosen that
    /// method's accessibility.
    /// </para>
    /// </summary>
    [Test]
    public async Task DeltaSync_GracePeriodDeletionWithSamePageJoin_ConsolidatesOntoOneMvoInstanceAsync()
    {
        const string RekeyedDisplayName = "John Smith II";

        // Arrange: an import Synchronisation Rule that both projects (for the first CSO) and matches on
        // EmployeeId (so a second CSO can rekey the same identity by joining, rather than projecting a
        // brand new Metaverse Object).
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: TimeSpan.FromDays(30));
        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");

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
        // CaseSensitive: the in-memory store does not support EF.Functions.ILike (PostgreSQL-specific).
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
        await DbContext.SaveChangesAsync();

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
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, deltaSyncProfile, deltaSyncActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo, Is.Not.Null, "the MVO must still exist (grace period > 0 defers deletion to housekeeping)");
            Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Not.Null,
                "the grace-period deletion marker queued from CSO A's obsoletion must survive consolidation " +
                "with the distinct MVO instance CSO B's join queued for the same Id");
            Assert.That(mvo.DeletionTriggeredBySystemId, Is.EqualTo(sourceSystem.Id),
                "the triggering system marker must survive consolidation");
            Assert.That(mvo.DeletionPolicySnapshotJson, Is.Not.Null.And.Not.Empty,
                "the decision-time policy snapshot must survive consolidation");

            // The join's flowed attribute state must also survive: CSO B's DisplayName, not CSO A's stale
            // one, proves the fix kept the attribute-bearing instance rather than discarding it in favour
            // of the deletion-only one.
            var displayNameValue = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == mvDisplayNameAttr.Id);
            Assert.That(displayNameValue?.StringValue, Is.EqualTo(RekeyedDisplayName),
                "CSO B's flowed DisplayName must survive consolidation, proving the join's Attribute Flow was not discarded");

            // The observable proof that QueueMvoForUpdate deduped by Id rather than merely producing a
            // correct-by-luck final dictionary state: exactly one entry for this MVO Id must ever have
            // reached the batch flush call.
            Assert.That(_spySyncRepo.BatchMvoUpdateIds.Count(id => id == mvoId), Is.EqualTo(1),
                "QueueMvoForUpdate must dedupe by Id: only one entry for this MVO should ever reach the " +
                "page-flush batch update, not two distinct instances of it");
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
        TimeSpan? gracePeriod = null)
    {
        var mvType = new MetaverseObjectType
        {
            Name = name,
            PluralName = name + "s",
            BuiltIn = false,
            DeletionRule = deletionRule,
            DeletionGracePeriod = gracePeriod,
            DeletionTriggerConnectedSystemIds = new List<int>(),
            Attributes = new List<MetaverseAttribute>(),
            ExampleDataTemplateAttributes = new List<JIM.Models.ExampleData.ExampleDataTemplateAttribute>(),
            PredefinedSearches = new List<JIM.Models.Search.PredefinedSearch>()
        };

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
