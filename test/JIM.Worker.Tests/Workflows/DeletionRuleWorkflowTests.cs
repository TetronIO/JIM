// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Workflow tests for MVO deletion rules.
/// These tests verify the full lifecycle of object deletion:
/// 1. CSO becomes Obsolete (via import or sync)
/// 2. CSO is disconnected from MVO during sync
/// 3. MVO deletion rule is evaluated
/// 4. MVO is marked for deletion (LastConnectorDisconnectedDate set)
/// 5. Housekeeping deletes MVO and creates delete Pending Exports for downstream CSOs
///
/// Test scenarios cover:
/// - DeletionRule.Manual - no automatic deletion
/// - DeletionRule.WhenLastConnectorDisconnected - delete when all CSOs are gone
/// - DeletionTriggerConnectedSystemIds - delete when specific system disconnects (even if other CSOs remain)
/// - DeletionGracePeriod - immediate vs delayed deletion
/// </summary>
[TestFixture]
public class DeletionRuleWorkflowTests : WorkflowTestBase
{
    #region DeletionRule.Manual Tests

    /// <summary>
    /// Verifies that MVOs with DeletionRule=Manual are not marked for deletion
    /// even when all CSOs are disconnected.
    /// </summary>
    [Test]
    public async Task Manual_WhenLastCsoDisconnected_MvoNotMarkedForDeletionAsync()
    {
        // Arrange: Create Source system with a CSO that projects to an MVO
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync("Person", MetaverseObjectDeletionRule.Manual);
        await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");

        // Create a CSO
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith");

        // Run Full Sync to project the CSO to MVO
        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,sourceSystem, fullSyncProfile, fullSyncActivity, cts1)
            .PerformFullSyncAsync();

        // Verify MVO was created and CSO is joined
        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "CSO should be joined to MVO after Full Sync");
        var mvoId = cso.MetaverseObjectId!.Value;

        // Mark CSO as Obsolete (simulating a Delete from delta import)
        await MarkCsoAsObsoleteAsync(cso);

        // Run Delta Sync to process the Obsolete CSO
        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,sourceSystem, deltaSyncProfile, deltaSyncActivity, cts2)
            .PerformDeltaSyncAsync();

        // Assert: MVO should NOT be marked for deletion (Manual rule)
        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        Assert.That(mvo, Is.Not.Null, "MVO should still exist");
        Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Null,
            "MVO with DeletionRule=Manual should NOT have LastConnectorDisconnectedDate set, " +
            "even when all CSOs are disconnected");

        // Assert: Delta Sync should produce a single Disconnected RPEI with Disconnected root and CsoDeleted child
        var rpeis = deltaSyncActivity.RunProfileExecutionItems;
        Assert.That(rpeis.Count(r => r.ObjectChangeType == ObjectChangeType.Disconnected), Is.EqualTo(1),
            "Delta Sync should produce a single Disconnected RPEI when a joined CSO is obsoleted");
        Assert.That(rpeis.Count(r => r.ObjectChangeType == ObjectChangeType.Deleted), Is.EqualTo(0),
            "Delta Sync should NOT produce a separate Deleted RPEI — CsoDeleted is an outcome on the Disconnected RPEI");

        // Assert: The single RPEI should have Disconnected as root with CsoDeleted as child
        var disconnectedRpei = rpeis.Single(r => r.ObjectChangeType == ObjectChangeType.Disconnected);
        var rootOutcome = disconnectedRpei.SyncOutcomes
            .SingleOrDefault(o => o.ParentSyncOutcome == null);
        Assert.That(rootOutcome, Is.Not.Null, "RPEI should have a single root outcome");
        Assert.That(rootOutcome!.OutcomeType,
            Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected),
            "Root outcome should be Disconnected");
        Assert.That(rootOutcome.Children, Has.Count.EqualTo(1),
            "Disconnected root should have exactly one child (CsoDeleted)");
        Assert.That(rootOutcome.Children[0].OutcomeType,
            Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted),
            "Child of Disconnected should be CsoDeleted");
    }

    #endregion

    #region DeletionRule.WhenLastConnectorDisconnected Tests

    /// <summary>
    /// Verifies that MVOs with DeletionRule=WhenLastConnectorDisconnected and a grace period
    /// are marked for deletion (but not deleted) when the last CSO is disconnected.
    /// Actual deletion is handled asynchronously by housekeeping after the grace period.
    /// </summary>
    [Test]
    public async Task WhenLastConnectorDisconnected_WithGracePeriod_MvoMarkedForDeletionAsync()
    {
        // Arrange: Create Source system with a CSO that projects to an MVO
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: TimeSpan.FromDays(30));  // 30 day grace period - handled by housekeeping
        await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");

        // Create a CSO
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith");

        // Run Full Sync to project the CSO to MVO
        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,sourceSystem, fullSyncProfile, fullSyncActivity, cts1)
            .PerformFullSyncAsync();

        // Verify MVO was created and CSO is joined
        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "CSO should be joined to MVO after Full Sync");
        var mvoId = cso.MetaverseObjectId!.Value;

        // Mark CSO as Obsolete (simulating a Delete from delta import)
        await MarkCsoAsObsoleteAsync(cso);

        // Run Delta Sync to process the Obsolete CSO
        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,sourceSystem, deltaSyncProfile, deltaSyncActivity, cts2)
            .PerformDeltaSyncAsync();

        // Assert: MVO should be marked for deletion (not deleted yet due to grace period)
        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        Assert.That(mvo, Is.Not.Null, "MVO should still exist (grace period > 0 means housekeeping handles deletion)");
        Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Not.Null,
            "MVO with grace period > 0 should have LastConnectorDisconnectedDate set");
        Assert.That(mvo.LastConnectorDisconnectedDate!.Value, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromMinutes(1)),
            "LastConnectorDisconnectedDate should be approximately now");
    }

    /// <summary>
    /// Verifies that MVOs with DeletionRule=WhenLastConnectorDisconnected and zero grace period
    /// are deleted synchronously during sync (not deferred to housekeeping).
    /// </summary>
    [Test]
    public async Task WhenLastConnectorDisconnected_ZeroGracePeriod_MvoDeletedImmediatelyAsync()
    {
        // Arrange: Create Source system with a CSO that projects to an MVO
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: TimeSpan.Zero);  // Zero grace period - delete synchronously
        await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");

        // Create a CSO
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith");

        // Run Full Sync to project the CSO to MVO
        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,sourceSystem, fullSyncProfile, fullSyncActivity, cts1)
            .PerformFullSyncAsync();

        // Verify MVO was created and CSO is joined
        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "CSO should be joined to MVO after Full Sync");
        var mvoId = cso.MetaverseObjectId!.Value;

        // Mark CSO as Obsolete (simulating a Delete from delta import)
        await MarkCsoAsObsoleteAsync(cso);

        // Run Delta Sync to process the Obsolete CSO
        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,sourceSystem, deltaSyncProfile, deltaSyncActivity, cts2)
            .PerformDeltaSyncAsync();

        // Assert: MVO should be DELETED (not just marked) due to zero grace period
        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        Assert.That(mvo, Is.Null,
            "MVO with grace period = 0 should be deleted immediately during sync, not deferred to housekeeping");

        // Assert: Delta Sync should produce a single Disconnected RPEI with Disconnected root and CsoDeleted child
        var rpeis = deltaSyncActivity.RunProfileExecutionItems;
        Assert.That(rpeis.Count(r => r.ObjectChangeType == ObjectChangeType.Disconnected), Is.EqualTo(1),
            "Delta Sync should produce a single Disconnected RPEI when a joined CSO is obsoleted");
        Assert.That(rpeis.Count(r => r.ObjectChangeType == ObjectChangeType.Deleted), Is.EqualTo(0),
            "Delta Sync should NOT produce a separate Deleted RPEI — CsoDeleted is an outcome on the Disconnected RPEI");
    }

    /// <summary>
    /// Verifies that MVOs with DeletionRule=WhenLastConnectorDisconnected and null grace period
    /// (no grace period configured) are deleted synchronously during sync.
    /// </summary>
    [Test]
    public async Task WhenLastConnectorDisconnected_NullGracePeriod_MvoDeletedImmediatelyAsync()
    {
        // Arrange: Create Source system with a CSO that projects to an MVO
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: null);  // Null grace period - same as 0, delete synchronously
        await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");

        // Create a CSO
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith");

        // Run Full Sync to project the CSO to MVO
        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,sourceSystem, fullSyncProfile, fullSyncActivity, cts1)
            .PerformFullSyncAsync();

        // Verify MVO was created and CSO is joined
        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "CSO should be joined to MVO after Full Sync");
        var mvoId = cso.MetaverseObjectId!.Value;

        // Mark CSO as Obsolete (simulating a Delete from delta import)
        await MarkCsoAsObsoleteAsync(cso);

        // Run Delta Sync to process the Obsolete CSO
        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,sourceSystem, deltaSyncProfile, deltaSyncActivity, cts2)
            .PerformDeltaSyncAsync();

        // Assert: MVO should be DELETED (not just marked) due to null grace period
        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        Assert.That(mvo, Is.Null,
            "MVO with grace period = null should be deleted immediately during sync, not deferred to housekeeping");
    }

    /// <summary>
    /// Verifies that MVOs with multiple CSOs are NOT marked for deletion when only
    /// one CSO is disconnected (other CSOs still connected).
    /// </summary>
    [Test]
    public async Task WhenLastConnectorDisconnected_WhenOneCsoDisconnectedButOthersRemain_MvoNotMarkedAsync()
    {
        // Arrange: Create Source system with a CSO that projects to an MVO
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: TimeSpan.Zero);
        await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");

        // Create CSO and run Full Sync to project to MVO
        var cso1 = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,sourceSystem, fullSyncProfile, fullSyncActivity, cts1)
            .PerformFullSyncAsync();

        cso1 = await ReloadEntityAsync(cso1);
        Assert.That(cso1.MetaverseObjectId, Is.Not.Null, "CSO1 should be joined to MVO after Full Sync");
        var mvoId = cso1.MetaverseObjectId!.Value;

        // Manually create a second CSO and join it to the same MVO (simulating a second system)
        var cso2 = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith Second", "EMP002");
        cso2.MetaverseObjectId = mvoId;
        cso2.JoinType = ConnectedSystemObjectJoinType.Joined;
        cso2.DateJoined = DateTime.UtcNow;
        SyncRepo.RefreshCsoMvoIndex(cso2);

        // Verify both CSOs are joined to the same MVO
        cso2 = await ReloadEntityAsync(cso2);
        Assert.That(cso2.MetaverseObjectId, Is.EqualTo(mvoId), "CSO2 should be joined to same MVO");

        // Mark only CSO1 as Obsolete (simulating a Delete from delta import)
        await MarkCsoAsObsoleteAsync(cso1);

        // Run Delta Sync to process the Obsolete CSO
        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,sourceSystem, deltaSyncProfile, deltaSyncActivity, cts2)
            .PerformDeltaSyncAsync();

        // Assert: MVO should NOT be marked for deletion (CSO2 still connected)
        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        Assert.That(mvo, Is.Not.Null, "MVO should still exist");
        Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Null,
            "MVO should NOT have LastConnectorDisconnectedDate set when other CSOs are still connected");
    }

    #endregion

    #region DeletionTriggerConnectedSystemIds Tests

    /// <summary>
    /// Verifies that MVOs with grace period are marked for deletion (but not deleted)
    /// when a specific trigger system disconnects, even if other CSOs remain connected.
    /// This is the key feature for "delete from Target when deleted from Source".
    /// </summary>
    [Test]
    public async Task DeletionTrigger_WithGracePeriod_WhenTriggerSystemDisconnects_MvoMarkedAsync()
    {
        // Arrange: Create Source (HR) and Target (AD) systems
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var targetSystem = await CreateConnectedSystemAsync("Target AD System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "User");

        // Create MV type with WhenAuthoritativeSourceDisconnected and Source as authoritative
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            gracePeriod: TimeSpan.FromDays(30),  // 30 day grace period - handled by housekeeping
            triggerConnectedSystemIds: new List<int> { sourceSystem.Id });

        // Create Synchronisation Rules
        await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        await CreateExportSyncRuleAsync(targetSystem.Id, targetType, mvType, "AD Export");

        // Create matching rules
        await CreateMatchingRuleAsync(sourceType, mvType, "EmployeeId");
        await CreateMatchingRuleAsync(targetType, mvType, "EmployeeId");

        // Create Source CSO
        var sourceCso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        // Run Full Sync on Source to create MVO
        var sourceFullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var sourceFullSyncActivity = await CreateActivityAsync(sourceSystem.Id, sourceFullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,sourceSystem, sourceFullSyncProfile, sourceFullSyncActivity, cts1)
            .PerformFullSyncAsync();

        // Verify MVO created
        sourceCso = await ReloadEntityAsync(sourceCso);
        Assert.That(sourceCso.MetaverseObjectId, Is.Not.Null, "Source CSO should be joined to MVO");
        var mvoId = sourceCso.MetaverseObjectId!.Value;

        // Create Target CSO and join it to the MVO (simulating a provisioned export)
        var targetCso = await CreateCsoAsync(targetSystem.Id, targetType, "John Smith", "EMP001");
        targetCso.MetaverseObjectId = mvoId;
        targetCso.JoinType = ConnectedSystemObjectJoinType.Provisioned;
        targetCso.DateJoined = DateTime.UtcNow;
        SyncRepo.RefreshCsoMvoIndex(targetCso);

        // Verify both CSOs are joined
        targetCso = await ReloadEntityAsync(targetCso);
        Assert.That(targetCso.MetaverseObjectId, Is.EqualTo(mvoId), "Target CSO should be joined to same MVO");

        // Mark Source CSO as Obsolete (simulating a Delete from delta import)
        await MarkCsoAsObsoleteAsync(sourceCso);

        // Run Delta Sync on Source to process the Obsolete CSO
        var sourceDeltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var sourceDeltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, sourceDeltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,sourceSystem, sourceDeltaSyncProfile, sourceDeltaSyncActivity, cts2)
            .PerformDeltaSyncAsync();

        // Assert: MVO SHOULD be marked for deletion (not deleted yet due to grace period)
        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        Assert.That(mvo, Is.Not.Null, "MVO should still exist (grace period > 0 means housekeeping handles deletion)");
        Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Not.Null,
            "MVO SHOULD have LastConnectorDisconnectedDate set when a DeletionTriggerConnectedSystemId disconnects");
    }

    /// <summary>
    /// Verifies that MVOs with zero grace period are deleted synchronously
    /// when a specific trigger system disconnects, and delete Pending Exports
    /// are created for any remaining Provisioned CSOs.
    /// </summary>
    [Test]
    public async Task DeletionTrigger_ZeroGracePeriod_MvoDeletedAndExportsCreatedAsync()
    {
        // Arrange: Create Source (HR) and Target (AD) systems
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var targetSystem = await CreateConnectedSystemAsync("Target AD System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "User");

        // Create MV type with WhenAuthoritativeSourceDisconnected and zero grace period
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            gracePeriod: TimeSpan.Zero,  // Zero grace period - delete synchronously
            triggerConnectedSystemIds: new List<int> { sourceSystem.Id });

        // Create Synchronisation Rules; the export rule's Delete action drives the delete export
        // when the MVO is deleted (issue #655)
        await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        await CreateExportSyncRuleAsync(targetSystem.Id, targetType, mvType, "AD Export",
            deprovisionAction: OutboundDeprovisionAction.Delete);

        // Create matching rules
        await CreateMatchingRuleAsync(sourceType, mvType, "EmployeeId");
        await CreateMatchingRuleAsync(targetType, mvType, "EmployeeId");

        // Create Source CSO
        var sourceCso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        // Run Full Sync on Source to create MVO
        var sourceFullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var sourceFullSyncActivity = await CreateActivityAsync(sourceSystem.Id, sourceFullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,sourceSystem, sourceFullSyncProfile, sourceFullSyncActivity, cts1)
            .PerformFullSyncAsync();

        // Verify MVO created
        sourceCso = await ReloadEntityAsync(sourceCso);
        Assert.That(sourceCso.MetaverseObjectId, Is.Not.Null, "Source CSO should be joined to MVO");
        var mvoId = sourceCso.MetaverseObjectId!.Value;

        // Create Target CSO and join it to the MVO (simulating a provisioned export)
        var targetCso = await CreateCsoAsync(targetSystem.Id, targetType, "John Smith", "EMP001");
        targetCso.MetaverseObjectId = mvoId;
        targetCso.JoinType = ConnectedSystemObjectJoinType.Provisioned;
        targetCso.DateJoined = DateTime.UtcNow;
        SyncRepo.RefreshCsoMvoIndex(targetCso);

        // Verify both CSOs are joined
        targetCso = await ReloadEntityAsync(targetCso);
        Assert.That(targetCso.MetaverseObjectId, Is.EqualTo(mvoId), "Target CSO should be joined to same MVO");
        var targetCsoId = targetCso.Id;

        // Mark Source CSO as Obsolete (simulating a Delete from delta import)
        await MarkCsoAsObsoleteAsync(sourceCso);

        // Run Delta Sync on Source to process the Obsolete CSO
        var sourceDeltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var sourceDeltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, sourceDeltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,sourceSystem, sourceDeltaSyncProfile, sourceDeltaSyncActivity, cts2)
            .PerformDeltaSyncAsync();

        // Assert: MVO SHOULD be DELETED (not just marked) due to zero grace period
        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        Assert.That(mvo, Is.Null,
            "MVO with grace period = 0 should be deleted immediately during sync");

        // Assert: Delete Pending Export should be created for Target CSO (Provisioned)
        var deletePendingExports = SyncRepo.PendingExports.Values
            .Where(pe => pe.ConnectedSystemId == targetSystem.Id &&
                        pe.ChangeType == JIM.Models.Transactional.PendingExportChangeType.Delete)
            .ToList();
        Assert.That(deletePendingExports, Has.Count.EqualTo(1),
            "Delete Pending Export should be created for the Provisioned target CSO");
        Assert.That(deletePendingExports[0].ConnectedSystemObjectId, Is.EqualTo(targetCsoId),
            "Delete Pending Export should reference the target CSO");
    }

    /// <summary>
    /// Verifies that MVOs are NOT marked for deletion when a non-trigger system
    /// disconnects, even if its CSO is gone (because trigger system CSO remains).
    /// </summary>
    [Test]
    public async Task DeletionTrigger_WhenNonTriggerSystemDisconnects_MvoNotMarkedAsync()
    {
        // Arrange: Create Source (HR) system that is the authoritative source
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");

        // Create MV type with WhenAuthoritativeSourceDisconnected and Source as the only authoritative system
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            gracePeriod: TimeSpan.Zero,
            triggerConnectedSystemIds: new List<int> { sourceSystem.Id });

        await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");

        // Create a second (non-authoritative) system and Synchronisation Rule BEFORE any processor runs
        // to avoid EF Core in-memory change tracker conflicts
        var targetSystem = await CreateConnectedSystemAsync("Target AD System");
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "User");
        await CreateImportSyncRuleAsync(targetSystem.Id, targetType, mvType, "AD Import");

        // Create Source CSO and run Full Sync to project to MVO
        var sourceCso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,sourceSystem, fullSyncProfile, fullSyncActivity, cts1)
            .PerformFullSyncAsync();

        sourceCso = await ReloadEntityAsync(sourceCso);
        Assert.That(sourceCso.MetaverseObjectId, Is.Not.Null, "Source CSO should be joined to MVO");
        var mvoId = sourceCso.MetaverseObjectId!.Value;

        // Create target CSO and manually join to same MVO
        var targetCso = await CreateCsoAsync(targetSystem.Id, targetType, "John Smith AD", "EMP001");
        targetCso.MetaverseObjectId = mvoId;
        targetCso.JoinType = ConnectedSystemObjectJoinType.Provisioned;
        targetCso.DateJoined = DateTime.UtcNow;
        SyncRepo.RefreshCsoMvoIndex(targetCso);

        targetCso = await ReloadEntityAsync(targetCso);
        Assert.That(targetCso.MetaverseObjectId, Is.EqualTo(mvoId), "Target CSO should be joined to same MVO");

        // Mark Target CSO as Obsolete (non-authoritative system)
        await MarkCsoAsObsoleteAsync(targetCso);

        // Run Delta Sync on Target to process the Obsolete CSO
        var targetDeltaSyncProfile = await CreateRunProfileAsync(targetSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        targetSystem = await ReloadEntityAsync(targetSystem);
        var deltaSyncActivity = await CreateActivityAsync(targetSystem.Id, targetDeltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,targetSystem, targetDeltaSyncProfile, deltaSyncActivity, cts2)
            .PerformDeltaSyncAsync();

        // Assert: MVO should NOT be marked for deletion (non-authoritative system disconnected)
        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        Assert.That(mvo, Is.Not.Null, "MVO should still exist");
        Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Null,
            "MVO should NOT have LastConnectorDisconnectedDate set when a non-authoritative system disconnects " +
            "(Source CSO is still connected and Source is the only authoritative system)");
    }

    /// <summary>
    /// Tests a realistic multi-source scenario:
    /// - HR system is the authoritative source (contributes most user attributes)
    /// - Training system is a secondary source (contributes trainingCompleted and trainingExpires attributes)
    /// - AD is the target system (receives provisioned users)
    ///
    /// Validates:
    /// 1. Deleting Training CSO does NOT cause MVO deletion (non-authoritative)
    /// 2. Deleting HR CSO DOES cause MVO deletion (authoritative source)
    /// 3. Multi-source attribute fusing works correctly
    /// </summary>
    [Test]
    public async Task DeletionTrigger_MultiSourceScenario_OnlyAuthoritativeSourceTriggersDeleteAsync()
    {
        // Arrange: Create HR (authoritative), Training (secondary source), and AD (target) systems
        var hrSystem = await CreateConnectedSystemAsync("HR System");
        var trainingSystem = await CreateConnectedSystemAsync("Training System");
        var adSystem = await CreateConnectedSystemAsync("Target AD System");

        var hrUserType = await CreateCsoTypeAsync(hrSystem.Id, "Employee");
        var trainingUserType = await CreateCsoTypeAsync(trainingSystem.Id, "Trainee");
        var adUserType = await CreateCsoTypeAsync(adSystem.Id, "User");

        // Create MV type with WhenAuthoritativeSourceDisconnected and HR as the only authoritative source
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            gracePeriod: TimeSpan.Zero,
            triggerConnectedSystemIds: new List<int> { hrSystem.Id });

        // Add additional attributes for multi-source fusing test
        var trainingCompletedAttr = new MetaverseAttribute
        {
            Name = "TrainingCompleted",
            Type = AttributeDataType.Boolean,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        var trainingExpiresAttr = new MetaverseAttribute
        {
            Name = "TrainingExpires",
            Type = AttributeDataType.DateTime,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(trainingCompletedAttr);
        DbContext.MetaverseAttributes.Add(trainingExpiresAttr);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(trainingCompletedAttr);
        mvType.Attributes.Add(trainingExpiresAttr);

        // Create import Synchronisation Rules for HR and Training (both contribute attributes)
        await CreateImportSyncRuleAsync(hrSystem.Id, hrUserType, mvType, "HR Import");
        await CreateImportSyncRuleAsync(trainingSystem.Id, trainingUserType, mvType, "Training Import");

        // Create export Synchronisation Rule for AD; its Delete action drives the delete export
        // when the MVO is deleted (issue #655)
        await CreateExportSyncRuleAsync(adSystem.Id, adUserType, mvType, "AD Export",
            deprovisionAction: OutboundDeprovisionAction.Delete);

        // Create HR CSO and run Full Sync to project to MVO
        var hrCso = await CreateCsoAsync(hrSystem.Id, hrUserType, "John Smith", "EMP001");

        var hrFullSyncProfile = await CreateRunProfileAsync(hrSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var hrFullSyncActivity = await CreateActivityAsync(hrSystem.Id, hrFullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,hrSystem, hrFullSyncProfile, hrFullSyncActivity, cts1)
            .PerformFullSyncAsync();

        hrCso = await ReloadEntityAsync(hrCso);
        Assert.That(hrCso.MetaverseObjectId, Is.Not.Null, "HR CSO should be joined to MVO after Full Sync");
        var mvoId = hrCso.MetaverseObjectId!.Value;

        // Create Training CSO and manually join to the same MVO (simulating a matched join)
        var trainingCso = await CreateCsoAsync(trainingSystem.Id, trainingUserType, "John Smith Training", "EMP001");
        trainingCso.MetaverseObjectId = mvoId;
        trainingCso.JoinType = ConnectedSystemObjectJoinType.Joined;
        trainingCso.DateJoined = DateTime.UtcNow;
        SyncRepo.RefreshCsoMvoIndex(trainingCso);

        // Create AD CSO and manually join to the same MVO (simulating provisioning)
        var adCso = await CreateCsoAsync(adSystem.Id, adUserType, "John Smith AD", "EMP001");
        adCso.MetaverseObjectId = mvoId;
        adCso.JoinType = ConnectedSystemObjectJoinType.Provisioned;
        adCso.DateJoined = DateTime.UtcNow;
        SyncRepo.RefreshCsoMvoIndex(adCso);

        // Verify all three CSOs are joined to the same MVO
        trainingCso = await ReloadEntityAsync(trainingCso);
        adCso = await ReloadEntityAsync(adCso);
        Assert.That(trainingCso.MetaverseObjectId, Is.EqualTo(mvoId), "Training CSO should be joined to same MVO");
        Assert.That(adCso.MetaverseObjectId, Is.EqualTo(mvoId), "AD CSO should be joined to same MVO");

        // ===== Part 1: Delete Training CSO (non-authoritative) - MVO should NOT be marked for deletion =====
        await MarkCsoAsObsoleteAsync(trainingCso);

        var trainingDeltaSyncProfile = await CreateRunProfileAsync(trainingSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        trainingSystem = await ReloadEntityAsync(trainingSystem);
        var trainingDeltaSyncActivity = await CreateActivityAsync(trainingSystem.Id, trainingDeltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,trainingSystem, trainingDeltaSyncProfile, trainingDeltaSyncActivity, cts2)
            .PerformDeltaSyncAsync();

        // Assert: MVO should NOT be marked for deletion (Training is not authoritative)
        var mvoAfterTrainingDelete = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        Assert.That(mvoAfterTrainingDelete, Is.Not.Null, "MVO should still exist after Training CSO deletion");
        Assert.That(mvoAfterTrainingDelete!.LastConnectorDisconnectedDate, Is.Null,
            "MVO should NOT have LastConnectorDisconnectedDate set when Training (non-authoritative) system disconnects. " +
            "HR (authoritative) and AD CSOs are still connected.");

        // Verify HR and AD CSOs are still joined
        hrCso = await ReloadEntityAsync(hrCso);
        adCso = await ReloadEntityAsync(adCso);
        Assert.That(hrCso.MetaverseObjectId, Is.EqualTo(mvoId), "HR CSO should still be joined to MVO");
        Assert.That(adCso.MetaverseObjectId, Is.EqualTo(mvoId), "AD CSO should still be joined to MVO");

        // ===== Part 2: Delete HR CSO (authoritative) - MVO SHOULD be deleted =====
        await MarkCsoAsObsoleteAsync(hrCso);
        var adCsoId = adCso.Id;  // Store before MVO deletion disconnects it

        var hrDeltaSyncProfile = await CreateRunProfileAsync(hrSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        hrSystem = await ReloadEntityAsync(hrSystem);
        var hrDeltaSyncActivity = await CreateActivityAsync(hrSystem.Id, hrDeltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var cts3 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,hrSystem, hrDeltaSyncProfile, hrDeltaSyncActivity, cts3)
            .PerformDeltaSyncAsync();

        // Assert: MVO SHOULD be DELETED (not just marked) because HR (authoritative source) disconnected
        // and grace period is 0
        var mvoAfterHrDelete = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        Assert.That(mvoAfterHrDelete, Is.Null,
            "MVO with grace period = 0 should be deleted immediately when HR (authoritative source) disconnects. " +
            "This is the core Leaver scenario with synchronous deletion.");

        // Verify delete Pending Export was created for AD CSO (Provisioned)
        var deletePendingExports = SyncRepo.PendingExports.Values
            .Where(pe => pe.ConnectedSystemId == adSystem.Id &&
                        pe.ChangeType == JIM.Models.Transactional.PendingExportChangeType.Delete)
            .ToList();
        Assert.That(deletePendingExports, Has.Count.EqualTo(1),
            "Delete Pending Export should be created for the Provisioned AD CSO");
        Assert.That(deletePendingExports[0].ConnectedSystemObjectId, Is.EqualTo(adCsoId),
            "Delete Pending Export should reference the AD CSO");
    }

    /// <summary>
    /// A 0-grace-period deletion of a Metaverse Object referenced by a group must surface the
    /// recall-staged membership-removal Pending Export on the sync Activity (#1003): one RPEI with
    /// a PendingExportCreated outcome per staged export, counted into TotalPendingExports. Before
    /// this, deletion-staged exports were only logged, so the Activity reported 0 Pending Exports
    /// while thousands were staged.
    /// </summary>
    [Test]
    public async Task ZeroGracePeriod_DeletedMvoReferencedByGroup_RecallPendingExportCountedOnActivityAsync()
    {
        // Arrange: source system projects a person MVO with immediate deletion on disconnect.
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");

        // Target side is created before any sync runs (the harness DbContext rejects new
        // connected-system saves afterwards); an export rule flows group members to it.
        var targetSystem = await CreateConnectedSystemAsync("Target LDAP");

        // The person deletes immediately when the authoritative source disconnects, even though
        // its target CSO remains joined - the same shape as a leaver-cohort deprovisioning.
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            gracePeriod: TimeSpan.Zero,
            triggerConnectedSystemIds: [sourceSystem.Id]);
        await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        cso = await ReloadEntityAsync(cso);
        var mvoId = cso.MetaverseObjectId!.Value;

        // A group referencing the person, both with provisioned target CSOs (the person's
        // carrying its DN). Seeded directly into the sync repository (no DbContext writes).
        var mvMemberAttribute = new MetaverseAttribute
        {
            Id = 9060,
            Name = "Static Members",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued
        };
        var mvGroupType = new MetaverseObjectType { Id = 9050, Name = "Group", Attributes = [mvMemberAttribute] };
        var csMemberAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 9080,
            Name = "member",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued,
            Selected = true
        };
        var csDnAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 9081,
            Name = "distinguishedName",
            Type = AttributeDataType.Text,
            IsSecondaryExternalId = true,
            Selected = true
        };
        SyncRepo.SeedSyncRule(new SyncRule
        {
            Id = 9900,
            Name = "Target Export Groups",
            Enabled = true,
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = targetSystem.Id,
            MetaverseObjectTypeId = mvGroupType.Id,
            AttributeFlowRules =
            {
                new SyncRuleMapping
                {
                    Id = 9901,
                    TargetConnectedSystemAttribute = csMemberAttribute,
                    TargetConnectedSystemAttributeId = csMemberAttribute.Id,
                    Sources =
                    {
                        new SyncRuleMappingSource
                        {
                            Id = 9902,
                            Order = 0,
                            MetaverseAttribute = mvMemberAttribute,
                            MetaverseAttributeId = mvMemberAttribute.Id
                        }
                    }
                }
            }
        });

        const string memberDn = "uid=john.smith,ou=People,dc=target,dc=local";
        var personTargetCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            TypeId = 9070,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            MetaverseObjectId = mvoId
        };
        personTargetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = personTargetCso,
            Attribute = csDnAttribute,
            AttributeId = csDnAttribute.Id,
            StringValue = memberDn
        });
        SyncRepo.SeedConnectedSystemObject(personTargetCso);

        var groupMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvGroupType, CachedDisplayName = "Team Alpha" };
        groupMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = groupMvo,
            Attribute = mvMemberAttribute,
            AttributeId = mvMemberAttribute.Id,
            ReferenceValueId = mvoId
        });
        SyncRepo.SeedMetaverseObject(groupMvo);

        var groupTargetCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            TypeId = 9070,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            MetaverseObjectId = groupMvo.Id
        };
        groupTargetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = groupTargetCso,
            Attribute = csMemberAttribute,
            AttributeId = csMemberAttribute.Id,
            ReferenceValueId = personTargetCso.Id,
            UnresolvedReferenceValue = memberDn
        });
        SyncRepo.SeedConnectedSystemObject(groupTargetCso);

        // Act: obsolete the source CSO and run a Delta Sync, which deletes the MVO synchronously
        // and stages the group's membership removal via reference recall.
        await MarkCsoAsObsoleteAsync(cso);
        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, deltaSyncProfile, deltaSyncActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        // Assert: the recall-staged Pending Export exists and is surfaced on the Activity.
        var stagedPendingExport = SyncRepo.PendingExports.Values
            .SingleOrDefault(pe => pe.ConnectedSystemObjectId == groupTargetCso.Id);
        Assert.That(stagedPendingExport, Is.Not.Null, "Reference recall must stage a membership-removal Pending Export");

        var recallRpei = deltaSyncActivity.RunProfileExecutionItems
            .SingleOrDefault(rpei => rpei.ObjectChangeType == ObjectChangeType.PendingExport);
        Assert.That(recallRpei, Is.Not.Null,
            "A recall-staged Pending Export must be surfaced as an RPEI on the sync Activity");
        Assert.That(recallRpei!.PendingExportId, Is.EqualTo(stagedPendingExport!.Id));
        Assert.That(recallRpei.ConnectedSystemObjectId, Is.EqualTo(groupTargetCso.Id));
        Assert.That(recallRpei.DisplayNameSnapshot, Is.EqualTo("Team Alpha"),
            "The RPEI must carry the referencing group's display name for Activity drill-down");

        Worker.CalculateActivitySummaryStats(deltaSyncActivity);
        Assert.That(deltaSyncActivity.TotalPendingExports, Is.GreaterThanOrEqualTo(1),
            "Recall-staged Pending Exports must be counted into the Activity's TotalPendingExports");
    }

    #endregion

    #region Trigger Mode and Decision-Time Policy Snapshot Tests (#119)

    /// <summary>
    /// All sources mode: the first of two listed sources disconnecting must NOT mark the Metaverse Object
    /// for deletion (the other source remains connected), and the disconnection's execution item must carry
    /// a decision-time policy snapshot recording the evaluated-but-not-triggered decision: All mode, the
    /// triggering system, and the still-connected source, so the decision stays explainable after
    /// configuration changes.
    /// </summary>
    [Test]
    public async Task AllMode_FirstSourceDisconnects_MvoNotMarkedAndRpeiCarriesSnapshotAsync()
    {
        // Arrange: two authoritative sources, All mode, 30 day grace period.
        var hrSystem = await CreateConnectedSystemAsync("HR System");
        var crmSystem = await CreateConnectedSystemAsync("CRM System");
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "Employee");
        var crmType = await CreateCsoTypeAsync(crmSystem.Id, "Contact");

        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            gracePeriod: TimeSpan.FromDays(30),
            triggerConnectedSystemIds: [hrSystem.Id, crmSystem.Id],
            triggerMode: AuthoritativeSourceTriggerMode.AllSourcesDisconnect);

        await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        await CreateImportSyncRuleAsync(crmSystem.Id, crmType, mvType, "CRM Import");

        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(hrSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(hrSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, hrSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        hrCso = await ReloadEntityAsync(hrCso);
        var mvoId = hrCso.MetaverseObjectId!.Value;

        // Join a CRM CSO to the same MVO (the second authoritative source).
        var crmCso = await CreateCsoAsync(crmSystem.Id, crmType, "John Smith CRM", "EMP001");
        crmCso.MetaverseObjectId = mvoId;
        crmCso.JoinType = ConnectedSystemObjectJoinType.Joined;
        crmCso.DateJoined = DateTime.UtcNow;
        SyncRepo.RefreshCsoMvoIndex(crmCso);

        // Act: obsolete the HR CSO and run a Delta Sync on HR (the first source disconnects).
        await MarkCsoAsObsoleteAsync(hrCso);
        var deltaSyncProfile = await CreateRunProfileAsync(hrSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        hrSystem = await ReloadEntityAsync(hrSystem);
        var deltaSyncActivity = await CreateActivityAsync(hrSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, hrSystem, deltaSyncProfile, deltaSyncActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        // Assert: the MVO must NOT be marked (the CRM source remains connected in All mode).
        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        Assert.That(mvo, Is.Not.Null, "MVO should still exist");
        Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Null,
            "All sources mode must not mark the MVO for deletion while another listed source remains connected");
        Assert.That(mvo!.DeletionTriggeredBySystemId, Is.Null,
            "No deletion was scheduled, so no triggering system should be recorded on the MVO");
        Assert.That(mvo!.DeletionPolicySnapshotJson, Is.Null,
            "No deletion was scheduled, so no policy snapshot should be recorded on the MVO");

        // Assert: the disconnection execution item carries the evaluated-but-not-triggered snapshot.
        var disconnectedRpei = deltaSyncActivity.RunProfileExecutionItems
            .Single(r => r.ObjectChangeType == ObjectChangeType.Disconnected);
        var snapshot = MvoDeletionPolicySnapshot.FromJson(disconnectedRpei.DeletionPolicySnapshotJson);
        Assert.That(snapshot, Is.Not.Null,
            "A deletion rule evaluation against a listed source must record a decision-time policy snapshot on the execution item, " +
            "including when mode semantics decided not to trigger");
        Assert.That(snapshot!.DeletionRule, Is.EqualTo(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected));
        Assert.That(snapshot!.TriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.AllSourcesDisconnect));
        Assert.That(snapshot!.TriggeringSystemId, Is.EqualTo(hrSystem.Id));
        Assert.That(snapshot!.TriggeringSystemName, Is.EqualTo("HR System"));
        Assert.That(snapshot!.SelectedSourceSystemIds, Is.EqualTo(new[] { hrSystem.Id, crmSystem.Id }));
        Assert.That(snapshot!.SelectedSourceSystemNames, Is.EqualTo(new[] { "HR System", "CRM System" }));
        Assert.That(snapshot!.RemainingConnectedSourceSystemIds, Is.EqualTo(new[] { crmSystem.Id }),
            "The snapshot must record which listed sources were still connected at decision time");
        Assert.That(snapshot!.RemainingConnectedSourceSystemNames, Is.EqualTo(new[] { "CRM System" }));
        Assert.That(snapshot!.GracePeriod, Is.EqualTo(TimeSpan.FromDays(30)));
    }

    /// <summary>
    /// All sources mode end-to-end: once the last listed source disconnects the Metaverse Object must be
    /// marked for deletion with the grace period, carrying the triggering system and a deserialisable
    /// decision-time policy snapshot whose fields match the configuration at decision time.
    /// </summary>
    [Test]
    public async Task AllMode_LastSourceDisconnects_MvoMarkedWithTriggerAndSnapshotAsync()
    {
        // Arrange: two authoritative sources, All mode, 30 day grace period.
        var hrSystem = await CreateConnectedSystemAsync("HR System");
        var crmSystem = await CreateConnectedSystemAsync("CRM System");
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "Employee");
        var crmType = await CreateCsoTypeAsync(crmSystem.Id, "Contact");

        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            gracePeriod: TimeSpan.FromDays(30),
            triggerConnectedSystemIds: [hrSystem.Id, crmSystem.Id],
            triggerMode: AuthoritativeSourceTriggerMode.AllSourcesDisconnect);

        await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        await CreateImportSyncRuleAsync(crmSystem.Id, crmType, mvType, "CRM Import");

        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(hrSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(hrSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, hrSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        hrCso = await ReloadEntityAsync(hrCso);
        var mvoId = hrCso.MetaverseObjectId!.Value;

        var crmCso = await CreateCsoAsync(crmSystem.Id, crmType, "John Smith CRM", "EMP001");
        crmCso.MetaverseObjectId = mvoId;
        crmCso.JoinType = ConnectedSystemObjectJoinType.Joined;
        crmCso.DateJoined = DateTime.UtcNow;
        SyncRepo.RefreshCsoMvoIndex(crmCso);

        // First disconnect: HR goes; CRM remains, so nothing is marked.
        await MarkCsoAsObsoleteAsync(hrCso);
        var hrDeltaProfile = await CreateRunProfileAsync(hrSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        hrSystem = await ReloadEntityAsync(hrSystem);
        var hrDeltaActivity = await CreateActivityAsync(hrSystem.Id, hrDeltaProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, hrSystem, hrDeltaProfile, hrDeltaActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        // Act: second disconnect: CRM goes too; no listed source remains.
        crmCso = await ReloadEntityAsync(crmCso);
        await MarkCsoAsObsoleteAsync(crmCso);
        var crmDeltaProfile = await CreateRunProfileAsync(crmSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        crmSystem = await ReloadEntityAsync(crmSystem);
        var crmDeltaActivity = await CreateActivityAsync(crmSystem.Id, crmDeltaProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, crmSystem, crmDeltaProfile, crmDeltaActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        // Assert: the MVO is marked, records the triggering system, and carries the mark-time snapshot.
        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        Assert.That(mvo, Is.Not.Null, "MVO should still exist (grace period > 0 means housekeeping deletes it)");
        Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Not.Null,
            "All sources mode must mark the MVO once no listed source remains connected");
        Assert.That(mvo!.DeletionTriggeredBySystemId, Is.EqualTo(crmSystem.Id),
            "The system whose disconnection completed the trigger condition must be recorded");
        Assert.That(mvo!.DeletionTriggeredBySystemName, Is.EqualTo("CRM System"),
            "The triggering system's display name must be snapshotted at decision time");

        var mvoSnapshot = MvoDeletionPolicySnapshot.FromJson(mvo!.DeletionPolicySnapshotJson);
        Assert.That(mvoSnapshot, Is.Not.Null,
            "A scheduled deletion must carry the decision-time policy snapshot on the MVO so housekeeping can carry it through");
        Assert.That(mvoSnapshot!.DeletionRule, Is.EqualTo(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected));
        Assert.That(mvoSnapshot!.TriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.AllSourcesDisconnect));
        Assert.That(mvoSnapshot!.GracePeriod, Is.EqualTo(TimeSpan.FromDays(30)));
        Assert.That(mvoSnapshot!.TriggeringSystemId, Is.EqualTo(crmSystem.Id));
        Assert.That(mvoSnapshot!.TriggeringSystemName, Is.EqualTo("CRM System"));
        Assert.That(mvoSnapshot!.SelectedSourceSystemIds, Is.EqualTo(new[] { hrSystem.Id, crmSystem.Id }));
        Assert.That(mvoSnapshot!.SelectedSourceSystemNames, Is.EqualTo(new[] { "HR System", "CRM System" }));
        Assert.That(mvoSnapshot!.RemainingConnectedSourceSystemIds, Is.Empty,
            "No listed source remained connected at decision time");

        // Assert: the disconnection execution item carries the same snapshot, and the scheduled
        // outcome's detail message names the mode.
        var disconnectedRpei = crmDeltaActivity.RunProfileExecutionItems
            .Single(r => r.ObjectChangeType == ObjectChangeType.Disconnected);
        Assert.That(disconnectedRpei.DeletionPolicySnapshotJson, Is.EqualTo(mvo!.DeletionPolicySnapshotJson),
            "The outcome-bearing execution item must carry the same decision-time snapshot as the MVO");
        var scheduledOutcome = disconnectedRpei.SyncOutcomes
            .SingleOrDefault(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled);
        Assert.That(scheduledOutcome, Is.Not.Null, "An MvoDeletionScheduled outcome must be recorded");
        Assert.That(scheduledOutcome!.DetailMessage, Does.Contain("All sources mode"),
            "The scheduled deletion's reason must name the trigger mode");
    }

    /// <summary>
    /// Specific sources mode end-to-end (pre-existing behaviour): a listed source disconnecting marks the
    /// Metaverse Object even though other connectors remain, and the persisted decision-time snapshot
    /// records SpecificSourcesDisconnect.
    /// </summary>
    [Test]
    public async Task SpecificMode_ListedSourceDisconnects_MvoMarkedAndSnapshotRecordsSpecificModeAsync()
    {
        // Arrange: HR is the only listed source; a target system remains connected.
        var hrSystem = await CreateConnectedSystemAsync("HR System");
        var adSystem = await CreateConnectedSystemAsync("Target AD System");
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "Employee");
        var adType = await CreateCsoTypeAsync(adSystem.Id, "User");

        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            gracePeriod: TimeSpan.FromDays(30),
            triggerConnectedSystemIds: [hrSystem.Id],
            triggerMode: AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect);

        await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        await CreateExportSyncRuleAsync(adSystem.Id, adType, mvType, "AD Export");

        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(hrSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(hrSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, hrSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        hrCso = await ReloadEntityAsync(hrCso);
        var mvoId = hrCso.MetaverseObjectId!.Value;

        var adCso = await CreateCsoAsync(adSystem.Id, adType, "John Smith AD", "EMP001");
        adCso.MetaverseObjectId = mvoId;
        adCso.JoinType = ConnectedSystemObjectJoinType.Provisioned;
        adCso.DateJoined = DateTime.UtcNow;
        SyncRepo.RefreshCsoMvoIndex(adCso);

        // Act: obsolete the HR CSO and run a Delta Sync on HR.
        await MarkCsoAsObsoleteAsync(hrCso);
        var deltaSyncProfile = await CreateRunProfileAsync(hrSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        hrSystem = await ReloadEntityAsync(hrSystem);
        var deltaSyncActivity = await CreateActivityAsync(hrSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, hrSystem, deltaSyncProfile, deltaSyncActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        // Assert: marked with the triggering system recorded and a Specific mode snapshot persisted.
        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        Assert.That(mvo, Is.Not.Null, "MVO should still exist (grace period > 0)");
        Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Not.Null,
            "Specific sources mode must mark the MVO when a listed source disconnects, even though the target remains");
        Assert.That(mvo!.DeletionTriggeredBySystemId, Is.EqualTo(hrSystem.Id));
        Assert.That(mvo!.DeletionTriggeredBySystemName, Is.EqualTo("HR System"));

        var snapshot = MvoDeletionPolicySnapshot.FromJson(mvo!.DeletionPolicySnapshotJson);
        Assert.That(snapshot, Is.Not.Null, "The scheduled deletion must persist a decision-time policy snapshot");
        Assert.That(snapshot!.TriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect));
        Assert.That(snapshot!.DeletionRule, Is.EqualTo(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected));
        Assert.That(snapshot!.TriggeringSystemId, Is.EqualTo(hrSystem.Id));
        Assert.That(snapshot!.SelectedSourceSystemIds, Is.EqualTo(new[] { hrSystem.Id }));
        Assert.That(snapshot!.SelectedSourceSystemNames, Is.EqualTo(new[] { "HR System" }));
        Assert.That(snapshot!.RemainingConnectedSourceSystemIds, Is.Empty,
            "The target system is not a listed source, so no listed source remained connected");

        // Assert: the disconnection execution item carries the snapshot too.
        var disconnectedRpei = deltaSyncActivity.RunProfileExecutionItems
            .Single(r => r.ObjectChangeType == ObjectChangeType.Disconnected);
        Assert.That(disconnectedRpei.DeletionPolicySnapshotJson, Is.EqualTo(mvo!.DeletionPolicySnapshotJson));
    }

    /// <summary>
    /// Causality integrity: the persisted decision-time snapshot must keep reflecting the facts at
    /// decision time after an administrator changes the object type's deletion configuration.
    /// </summary>
    [Test]
    public async Task Snapshot_ConfigurationChangedAfterMarking_SnapshotRetainsDecisionTimeFactsAsync()
    {
        // Arrange: Specific mode, one listed source, 30 day grace period; mark the MVO for deletion.
        var hrSystem = await CreateConnectedSystemAsync("HR System");
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "Employee");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            gracePeriod: TimeSpan.FromDays(30),
            triggerConnectedSystemIds: [hrSystem.Id],
            triggerMode: AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect);
        await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");

        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(hrSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(hrSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, hrSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        hrCso = await ReloadEntityAsync(hrCso);
        var mvoId = hrCso.MetaverseObjectId!.Value;

        await MarkCsoAsObsoleteAsync(hrCso);
        var deltaSyncProfile = await CreateRunProfileAsync(hrSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        hrSystem = await ReloadEntityAsync(hrSystem);
        var deltaSyncActivity = await CreateActivityAsync(hrSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, hrSystem, deltaSyncProfile, deltaSyncActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        Assert.That(mvo?.DeletionPolicySnapshotJson, Is.Not.Null, "The scheduled deletion must persist a policy snapshot");

        // Act: an administrator changes the deletion configuration AFTER the deletion was scheduled.
        mvType.DeletionTriggerMode = AuthoritativeSourceTriggerMode.AllSourcesDisconnect;
        mvType.DeletionGracePeriod = TimeSpan.FromDays(90);
        mvType.DeletionTriggerConnectedSystemIds.Add(999);

        // Assert: the persisted snapshot still reflects the decision-time facts, not the new configuration.
        var snapshot = MvoDeletionPolicySnapshot.FromJson(mvo!.DeletionPolicySnapshotJson);
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot!.TriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect),
            "The snapshot must retain the trigger mode in force at decision time");
        Assert.That(snapshot!.GracePeriod, Is.EqualTo(TimeSpan.FromDays(30)),
            "The snapshot must retain the grace period in force at decision time");
        Assert.That(snapshot!.SelectedSourceSystemIds, Is.EqualTo(new[] { hrSystem.Id }),
            "The snapshot must retain the source selection in force at decision time");
    }

    #endregion

    #region Grace Period Tests

    /// <summary>
    /// Verifies that MVOs with a grace period are marked for deletion but not immediately eligible.
    /// </summary>
    [Test]
    public async Task GracePeriod_WhenSet_MvoMarkedButNotImmediatelyEligibleAsync()
    {
        // Arrange: Create system with a 30-day grace period
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: TimeSpan.FromDays(30));
        await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");

        // Create a CSO
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith");

        // Run Full Sync
        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,sourceSystem, fullSyncProfile, fullSyncActivity, cts1)
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        var mvoId = cso.MetaverseObjectId!.Value;

        // Mark CSO as Obsolete
        await MarkCsoAsObsoleteAsync(cso);

        // Run Delta Sync
        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,sourceSystem, deltaSyncProfile, deltaSyncActivity, cts2)
            .PerformDeltaSyncAsync();

        // Assert: MVO should be marked for deletion but not immediately eligible
        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        Assert.That(mvo, Is.Not.Null, "MVO should still exist");
        Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Not.Null, "MVO should be marked for deletion");

        // Check the computed properties
        Assert.That(mvo.IsPendingDeletion, Is.True, "MVO should report IsPendingDeletion=true");
        Assert.That(mvo.DeletionEligibleDate, Is.Not.Null, "MVO should have a DeletionEligibleDate");
        Assert.That(mvo.DeletionEligibleDate!.Value,
            Is.EqualTo(mvo.LastConnectorDisconnectedDate!.Value.AddDays(30)).Within(TimeSpan.FromSeconds(1)),
            "DeletionEligibleDate should be 30 days after LastConnectorDisconnectedDate");
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a Metaverse Object Type with specific deletion rule settings.
    /// </summary>
    protected async Task<MetaverseObjectType> CreateMvObjectTypeWithDeletionRuleAsync(
        string name,
        MetaverseObjectDeletionRule deletionRule,
        TimeSpan? gracePeriod = null,
        List<int>? triggerConnectedSystemIds = null,
        AuthoritativeSourceTriggerMode triggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect)
    {
        var mvType = new MetaverseObjectType
        {
            Name = name,
            PluralName = name + "s",
            BuiltIn = false,
            DeletionRule = deletionRule,
            DeletionGracePeriod = gracePeriod,
            DeletionTriggerConnectedSystemIds = triggerConnectedSystemIds ?? new List<int>(),
            DeletionTriggerMode = triggerMode,
            Attributes = new List<MetaverseAttribute>(),
            ExampleDataTemplateAttributes = new List<JIM.Models.ExampleData.ExampleDataTemplateAttribute>(),
            PredefinedSearches = new List<JIM.Models.Search.PredefinedSearch>()
        };

        DbContext.MetaverseObjectTypes.Add(mvType);
        await DbContext.SaveChangesAsync();

        // Add attributes
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

        var typeAttr = new MetaverseAttribute
        {
            Name = "Type",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };

        DbContext.MetaverseAttributes.Add(displayNameAttr);
        DbContext.MetaverseAttributes.Add(employeeIdAttr);
        DbContext.MetaverseAttributes.Add(typeAttr);
        await DbContext.SaveChangesAsync();

        mvType.Attributes.Add(displayNameAttr);
        mvType.Attributes.Add(employeeIdAttr);
        mvType.Attributes.Add(typeAttr);

        return mvType;
    }

    /// <summary>
    /// Creates an export Synchronisation Rule.
    /// </summary>
    protected async Task<SyncRule> CreateExportSyncRuleAsync(
        int connectedSystemId,
        ConnectedSystemObjectType csoType,
        MetaverseObjectType mvType,
        string name,
        bool enableProvisioning = true,
        OutboundDeprovisionAction deprovisionAction = OutboundDeprovisionAction.Disconnect)
    {
        var syncRule = new SyncRule
        {
            ConnectedSystemId = connectedSystemId,
            Name = name,
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemObjectTypeId = csoType.Id,
            ConnectedSystemObjectType = csoType,
            MetaverseObjectTypeId = mvType.Id,
            MetaverseObjectType = mvType,
            ProvisionToConnectedSystem = enableProvisioning,
            OutboundDeprovisionAction = deprovisionAction
        };

        DbContext.SyncRules.Add(syncRule);
        await DbContext.SaveChangesAsync();

        SyncRepo.SeedSyncRule(syncRule);

        return syncRule;
    }

    /// <summary>
    /// Creates a matching rule for joining CSOs to MVOs.
    /// Matching rules belong to the ConnectedSystemObjectType and define how to find/match existing MVOs.
    /// </summary>
    protected async Task<ObjectMatchingRule> CreateMatchingRuleAsync(
        ConnectedSystemObjectType csoType,
        MetaverseObjectType mvType,
        string attributeName)
    {
        var csoAttr = csoType.Attributes.First(a => a.Name == attributeName);
        var mvAttr = mvType.Attributes.First(a => a.Name == attributeName);

        var matchingRule = new ObjectMatchingRule
        {
            Order = 1,
            CaseSensitive = true, // Required for in-memory test database (EF.Functions.ILike not supported)
            ConnectedSystemObjectType = csoType,
            ConnectedSystemObjectTypeId = csoType.Id,
            TargetMetaverseAttribute = mvAttr,
            TargetMetaverseAttributeId = mvAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Order = 1,
                    ConnectedSystemAttribute = csoAttr,
                    ConnectedSystemAttributeId = csoAttr.Id
                }
            }
        };

        DbContext.ObjectMatchingRules.Add(matchingRule);
        await DbContext.SaveChangesAsync();

        return matchingRule;
    }

    /// <summary>
    /// Marks a CSO as Obsolete (simulating a Delete from delta import).
    /// </summary>
    protected Task MarkCsoAsObsoleteAsync(ConnectedSystemObject cso)
    {
        cso.Status = ConnectedSystemObjectStatus.Obsolete;
        cso.LastUpdated = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    #endregion
}
