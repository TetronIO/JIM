// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A joined Connected System Object that falls out of scope of its import Synchronisation Rule, whose Inbound
/// Out-of-Scope Action is Disconnect, has its join released in memory by the synchronisation. These tests prove the
/// release is also handed to the page flush's join-state write, which is the only thing that stores it: without that
/// write the Activity reports the object as disconnected while its row keeps the Metaverse Object and the join
/// record, whenever the Metaverse Object itself is kept.
/// <para>
/// The in-memory repository stores the very instances the run holds, so reading the object back afterwards shows the
/// in-memory release whether or not it was ever written (<c>JoinRecordWorkflowTests</c> cannot fail for this reason).
/// These tests therefore record what the run hands <see cref="ISyncRepository.UpdateConnectedSystemObjectJoinStatesAsync"/>,
/// at the moment it does. The real-PostgreSQL proof that the row is written is
/// <c>OutOfScopeDisconnectPersistenceDatabaseTests</c>.
/// </para>
/// </summary>
[TestFixture]
public class OutOfScopeDisconnectPersistenceWorkflowTests : WorkflowTestBase
{
    /// <summary>The join state of one object as the run handed it to the join-state write.</summary>
    private sealed record JoinStateWrite(
        Guid CsoId,
        Guid? MetaverseObjectId,
        ConnectedSystemObjectJoinType JoinType,
        DateTime? DateJoined,
        ConnectedSystemObjectJoinMethod? JoinMethod,
        int? JoinSyncRuleId,
        string? JoinSyncRuleName);

    [Test]
    public async Task FullSync_CsoLeavesScopeAndMvoIsKeptByManualDeletionRule_WritesTheReleasedJoinAsync()
    {
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(MetaverseObjectDeletionRule.Manual);

        var (cso, mvoId, writes) = await ArrangeAndTakeCsoOutOfScopeAsync(mvType);

        Assert.That(SyncRepo.MetaverseObjects.ContainsKey(mvoId), Is.True, "precondition: the Metaverse Object is kept");
        AssertJoinReleaseWritten(cso, writes);
    }

    [Test]
    public async Task FullSync_CsoLeavesScopeAndMvoIsScheduledForDeletion_WritesTheReleasedJoinAsync()
    {
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TimeSpan.FromDays(7));

        var (cso, mvoId, writes) = await ArrangeAndTakeCsoOutOfScopeAsync(mvType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.MetaverseObjects.ContainsKey(mvoId), Is.True, "precondition: the grace period keeps the Metaverse Object for now");
            Assert.That(SyncRepo.MetaverseObjects[mvoId].LastConnectorDisconnectedDate, Is.Not.Null, "precondition: its deletion is scheduled");
        }
        AssertJoinReleaseWritten(cso, writes);
    }

    [Test]
    public async Task FullSync_CsoLeavesScopeAndMvoKeepsAnotherConnector_WritesTheReleasedJoinAsync()
    {
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TimeSpan.Zero);

        var (cso, mvoId, writes) = await ArrangeAndTakeCsoOutOfScopeAsync(mvType, joinAnotherConnector: true);

        Assert.That(SyncRepo.MetaverseObjects.ContainsKey(mvoId), Is.True, "precondition: the other connector keeps the Metaverse Object");
        AssertJoinReleaseWritten(cso, writes);
    }

    [Test]
    public async Task FullSync_CsoLeavesScopeAndMvoIsDeletedImmediately_WritesTheReleasedJoinAsync()
    {
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TimeSpan.Zero);

        var (cso, mvoId, writes) = await ArrangeAndTakeCsoOutOfScopeAsync(mvType);

        Assert.That(SyncRepo.MetaverseObjects.ContainsKey(mvoId), Is.False, "precondition: the Metaverse Object is deleted in the same flush");
        AssertJoinReleaseWritten(cso, writes);
    }

    [Test]
    public async Task FullSync_CsoLeavesScopeWithRemainJoined_DoesNotWriteAReleasedJoinAsync()
    {
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(MetaverseObjectDeletionRule.Manual);

        var (cso, _, writes) = await ArrangeAndTakeCsoOutOfScopeAsync(mvType, InboundOutOfScopeAction.RemainJoined);

        var released = writes.Where(w => w.CsoId == cso.Id && w.MetaverseObjectId == null).ToList();
        Assert.That(released, Is.Empty, "once managed, always managed: the join is kept, so no release may be written");
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "the join is kept in memory too");
    }

    private async Task<MetaverseObjectType> CreateMvObjectTypeWithDeletionRuleAsync(MetaverseObjectDeletionRule deletionRule, TimeSpan? gracePeriod = null)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        mvType.DeletionRule = deletionRule;
        mvType.DeletionGracePeriod = gracePeriod;
        await DbContext.SaveChangesAsync();
        return mvType;
    }

    /// <summary>
    /// What the release must look like when written: no Metaverse Object, and none of the join record the join left on
    /// the row. Every column the join-state write covers is checked, because a half-cleared join is as wrong as an
    /// unwritten one.
    /// </summary>
    private static void AssertJoinReleaseWritten(ConnectedSystemObject cso, List<JoinStateWrite> writes)
    {
        var written = writes.LastOrDefault(w => w.CsoId == cso.Id);
        Assert.That(written, Is.Not.Null,
            "the released join was never handed to the join-state write, so the row keeps its Metaverse Object and join record");

        var released = written!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(released.MetaverseObjectId, Is.Null, "the Metaverse Object is released");
            Assert.That(released.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.NotJoined));
            Assert.That(released.DateJoined, Is.Null);
            Assert.That(released.JoinMethod, Is.Null);
            Assert.That(released.JoinSyncRuleId, Is.Null);
            Assert.That(released.JoinSyncRuleName, Is.Null);
        }
    }

    /// <summary>
    /// Projects one HR object through an import rule scoped to EmployeeId = "EMP001", optionally joins an object from
    /// another Connected System to the same Metaverse Object, then changes the HR object's EmployeeId so it leaves scope
    /// and runs a second Full Synchronisation, recording the join states that run hands the repository to write.
    /// </summary>
    private async Task<(ConnectedSystemObject Cso, Guid MvoId, List<JoinStateWrite> Writes)> ArrangeAndTakeCsoOutOfScopeAsync(
        MetaverseObjectType mvType,
        InboundOutOfScopeAction outOfScopeAction = InboundOutOfScopeAction.Disconnect,
        bool joinAnotherConnector = false)
    {
        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "User");
        var importRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        importRule.InboundOutOfScopeAction = outOfScopeAction;
        importRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria =
            [
                new SyncRuleScopingCriteria
                {
                    ConnectedSystemAttribute = hrType.Attributes.First(a => a.Name == "EmployeeId"),
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "EMP001",
                    CaseSensitive = true
                }
            ]
        });
        var cso = await CreateCsoAsync(hr.Id, hrType, "Jane Smith", "EMP001");

        // Created before any run: the Entity Framework context cannot save a new Connected System once a processor has
        // modified the entities it tracks.
        var anotherConnectorCso = joinAnotherConnector ? await CreateAnotherConnectedSystemsObjectAsync() : null;

        await RunFullSyncAsync(hr, SyncRepo);
        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "precondition: the first Full Synchronisation projected the object");
        var mvoId = cso.MetaverseObjectId!.Value;

        if (anotherConnectorCso != null)
            await JoinToMetaverseObjectAsync(anotherConnectorCso, mvoId);

        cso.AttributeValues.Single(av => av.Attribute?.Name == "EmployeeId").StringValue = "OUT_OF_SCOPE";
        cso.LastUpdated = DateTime.UtcNow;

        var writes = new List<JoinStateWrite>();
        var recordingRepository = InterceptingSyncRepositoryProxy.Create(SyncRepo,
            nameof(ISyncRepository.UpdateConnectedSystemObjectJoinStatesAsync),
            arguments =>
            {
                // Snapshotted now: the run goes on to change the instances it handed over.
                writes.AddRange(((List<ConnectedSystemObject>)arguments[0]!).Select(handed => new JoinStateWrite(
                    handed.Id, handed.MetaverseObjectId, handed.JoinType, handed.DateJoined,
                    handed.JoinMethod, handed.JoinSyncRuleId, handed.JoinSyncRuleName)));
            });

        var activity = await RunFullSyncAsync(await ReloadEntityAsync(hr), recordingRepository, "Full Sync 2");
        var errors = activity.RunProfileExecutionItems.Where(i => i.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet).ToList();
        Assert.That(errors, Is.Empty, "precondition: the second Full Synchronisation reported no errors");
        Assert.That(activity.RunProfileExecutionItems.Any(i => i.ObjectChangeType == ObjectChangeType.DisconnectedOutOfScope),
            Is.EqualTo(outOfScopeAction == InboundOutOfScopeAction.Disconnect),
            "precondition: the Activity reports the disconnection exactly when the rule disconnects");

        return (cso, mvoId, writes);
    }

    /// <summary>An object in a second Connected System, not yet joined to anything.</summary>
    private async Task<ConnectedSystemObject> CreateAnotherConnectedSystemsObjectAsync()
    {
        var directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(directory.Id, "Account");
        return await CreateCsoAsync(directory.Id, directoryType, "Jane Smith (Directory)");
    }

    /// <summary>
    /// Joins the object to the Metaverse Object the way a provisioned object is, so the Metaverse Object has a connector
    /// left when the HR object departs.
    /// </summary>
    private async Task JoinToMetaverseObjectAsync(ConnectedSystemObject cso, Guid mvoId)
    {
        cso.MetaverseObjectId = mvoId;
        cso.MetaverseObject = SyncRepo.MetaverseObjects[mvoId];
        cso.RecordJoin(ConnectedSystemObjectJoinMethod.Provisioning, null, DateTime.UtcNow);
        await SyncRepo.UpdateConnectedSystemObjectJoinStatesAsync([cso]);
    }

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem, ISyncRepository syncRepository, string profileName = "Full Sync")
    {
        var profile = await CreateRunProfileAsync(connectedSystem.Id, profileName, ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(connectedSystem.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), syncRepository, connectedSystem, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }
}
