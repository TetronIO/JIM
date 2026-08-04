// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Processors;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Proves the reference-recall causal edge seam (#1223): the one place in the engine where cause and effect
/// sit on two entirely different objects.
///
/// When Metaverse Objects are deleted, reference recall stages a Pending Export against every group that
/// referenced them. That group's Run Profile Execution Item is the only record of the removal, and nothing on
/// it says why: <c>_deferredRecallRpeisByCsoId</c> keys on the referencing Connected System Object alone, so
/// the deleted members that caused the removal leave no trace whatsoever. This is the "this change has no
/// cause" case the whole feature exists to fix, and the outcome tree structurally cannot reach it, because the
/// cause is an event on a different object recorded on a different item.
///
/// It is also the cohort case. A group whose deleted members span many pages stages once per page and emits
/// exactly one Run Profile Execution Item at end of run; every page's members caused that one effect, so the
/// causes must accumulate across pages rather than the last page's overwriting the rest.
/// </summary>
[TestFixture]
public class ReferenceRecallCausalEdgeTests
{
    private const int TargetSystemId = 5;
    private const int SourceSystemId = 9;
    private const int CsGroupTypeId = 70;
    private const int CsExternalIdAttributeId = 81;

    private JimApplication Jim { get; set; } = null!;
    private SyncRepository SyncRepo { get; set; } = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        SyncRepo = new SyncRepository();
        var mockJimDbContext = new Mock<JimDbContext>();
        Jim = new JimApplication(new PostgresDataRepository(mockJimDbContext.Object), syncRepository: SyncRepo);
    }

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
    }

    /// <summary>
    /// The base case: one deleted member causes one group's removal export, and the edge records which object,
    /// why, and on which Connected System, so the group's item can state its cause without the deleted object
    /// still existing.
    /// </summary>
    [Test]
    public async Task FlushDeferredRecallRpeis_OneDeletedMember_WritesAnEdgeNamingItAsync()
    {
        var processor = CreateProcessor(out _);
        processor.SetRecallExportEvaluationCache(BuildRecallExportEvaluationCache(TargetSystemId, "Target LDAP"));
        var groupCso = SeedGroupCso("cn=Engineering,ou=Groups,dc=corp", "group");

        processor.CallStageDeferredRecallRpei(
            BuildRecallPendingExport(groupCso.Id, changeCount: 1),
            "Engineering",
            [NewCause("Tina Adams (S8-99)")]);

        await processor.CallFlushDeferredRecallRpeisAsync();

        var edge = SyncRepo.CausalEdges.Single();
        Assert.Multiple(() =>
        {
            Assert.That(edge.EdgeType, Is.EqualTo(CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval));
            Assert.That(edge.CauseDisplayName, Is.EqualTo("Tina Adams (S8-99)"),
                "the deleted object is gone by now, so the chain can only name it from the snapshot");
            Assert.That(edge.ReasonCode, Is.EqualTo(CausalReasonCode.AuthoritativeSourceDisconnected));
            Assert.That(edge.ConnectedSystemId, Is.EqualTo(SourceSystemId));
            Assert.That(edge.ConnectedSystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(edge.EffectSyncOutcomeId, Is.Not.Null,
                "the edge must name the Pending Export outcome it explains, not just the item");
        });
    }

    /// <summary>
    /// The cohort case, and the reason accumulation cannot be "last write wins" like the Pending Export itself.
    /// Ten members deleted across two pages produce one Run Profile Execution Item for the group, and all ten
    /// must be recorded as its causes; keeping only the final page's would attribute a ten-member removal to
    /// the handful that happened to be processed last.
    /// </summary>
    [Test]
    public async Task FlushDeferredRecallRpeis_MembersDeletedAcrossPages_AccumulatesEveryCauseAsync()
    {
        var processor = CreateProcessor(out _);
        processor.SetRecallExportEvaluationCache(BuildRecallExportEvaluationCache(TargetSystemId, "Target LDAP"));
        var groupCso = SeedGroupCso("cn=Engineering,ou=Groups,dc=corp", "group");

        // Page one removes six members; page two removes the remaining four. The second page's Pending Export
        // already carries all ten removals (delete-then-create coalescing), which is why the export itself is
        // overwritten while the causes are not.
        processor.CallStageDeferredRecallRpei(
            BuildRecallPendingExport(groupCso.Id, changeCount: 6),
            "Engineering",
            Enumerable.Range(0, 6).Select(i => NewCause($"Member {i}")).ToList());
        processor.CallStageDeferredRecallRpei(
            BuildRecallPendingExport(groupCso.Id, changeCount: 10),
            "Engineering",
            Enumerable.Range(6, 4).Select(i => NewCause($"Member {i}")).ToList());

        await processor.CallFlushDeferredRecallRpeisAsync();

        var edges = SyncRepo.CausalEdges.ToList();
        Assert.That(edges, Has.Count.EqualTo(10),
            "every deleted member contributed to this one removal, so every one of them is a cause of it");
        Assert.That(edges.Select(e => e.CauseDisplayName).Distinct().Count(), Is.EqualTo(10));
        Assert.That(edges.Select(e => e.EffectRunProfileExecutionItemId).Distinct().Count(), Is.EqualTo(1),
            "the cohort's causes all point at the single Run Profile Execution Item emitted for the group");
    }

    /// <summary>
    /// The same member deleted on two pages (a group referenced through more than one attribute, or a retried
    /// page) must be recorded once. A cohort that double-counts its members reports "12 users removed" for ten,
    /// which is worse than reporting nothing.
    /// </summary>
    [Test]
    public async Task FlushDeferredRecallRpeis_SameMemberStagedTwice_WritesOneEdgeAsync()
    {
        var processor = CreateProcessor(out _);
        processor.SetRecallExportEvaluationCache(BuildRecallExportEvaluationCache(TargetSystemId, "Target LDAP"));
        var groupCso = SeedGroupCso("cn=Engineering,ou=Groups,dc=corp", "group");
        var duplicated = NewCause("Tina Adams (S8-99)");

        processor.CallStageDeferredRecallRpei(BuildRecallPendingExport(groupCso.Id, 1), "Engineering", [duplicated]);
        processor.CallStageDeferredRecallRpei(BuildRecallPendingExport(groupCso.Id, 1), "Engineering",
            [NewCause("Tina Adams (S8-99)", duplicated.MetaverseObjectId)]);

        await processor.CallFlushDeferredRecallRpeisAsync();

        Assert.That(SyncRepo.CausalEdges, Has.Count.EqualTo(1),
            "the same deleted object staged twice is one cause, not two");
    }

    /// <summary>
    /// Two groups losing the same member each get their own edge: the cause is shared, the effects are not.
    /// </summary>
    [Test]
    public async Task FlushDeferredRecallRpeis_TwoGroupsLosingTheSameMember_EachGetTheirOwnEdgeAsync()
    {
        var processor = CreateProcessor(out _);
        processor.SetRecallExportEvaluationCache(BuildRecallExportEvaluationCache(TargetSystemId, "Target LDAP"));
        var groupA = SeedGroupCso("cn=Alpha,ou=Groups,dc=corp", "group");
        var groupB = SeedGroupCso("cn=Bravo,ou=Groups,dc=corp", "group");
        var member = NewCause("Tina Adams (S8-99)");

        processor.CallStageDeferredRecallRpei(BuildRecallPendingExport(groupA.Id, 1), "Alpha", [member]);
        processor.CallStageDeferredRecallRpei(BuildRecallPendingExport(groupB.Id, 1), "Bravo", [member]);

        await processor.CallFlushDeferredRecallRpeisAsync();

        var edges = SyncRepo.CausalEdges.ToList();
        Assert.That(edges, Has.Count.EqualTo(2));
        Assert.That(edges.Select(e => e.EffectRunProfileExecutionItemId).Distinct().Count(), Is.EqualTo(2));
        Assert.That(edges.Select(e => e.CauseMetaverseObjectId).Distinct().Single(), Is.EqualTo(member.MetaverseObjectId));
    }

    /// <summary>
    /// Recall staged without any known cause (a path that has not been given causes, or a deletion whose
    /// triggering record was lost) must still emit its Run Profile Execution Item. Provenance is additive: its
    /// absence degrades the explanation and must never suppress the record of what actually changed.
    /// </summary>
    [Test]
    public async Task FlushDeferredRecallRpeis_NoCauses_StillEmitsTheRpeiAsync()
    {
        var processor = CreateProcessor(out var activity);
        processor.SetRecallExportEvaluationCache(BuildRecallExportEvaluationCache(TargetSystemId, "Target LDAP"));
        var groupCso = SeedGroupCso("cn=Engineering,ou=Groups,dc=corp", "group");

        processor.CallStageDeferredRecallRpei(BuildRecallPendingExport(groupCso.Id, 1), "Engineering", []);

        await processor.CallFlushDeferredRecallRpeisAsync();

        Assert.That(activity.RunProfileExecutionItems, Has.Count.EqualTo(1));
        Assert.That(SyncRepo.CausalEdges, Is.Empty);
    }

    private static CausalCause NewCause(string displayName, Guid? metaverseObjectId = null)
    {
        return new CausalCause
        {
            MetaverseObjectId = metaverseObjectId ?? Guid.NewGuid(),
            DisplayName = displayName,
            ReasonCode = CausalReasonCode.AuthoritativeSourceDisconnected,
            ConnectedSystemId = SourceSystemId,
            ConnectedSystemName = "Yellowstone APAC"
        };
    }

    private ConnectedSystemObject SeedGroupCso(string externalId, string typeName, int connectedSystemId = TargetSystemId)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            TypeId = CsGroupTypeId,
            Type = new ConnectedSystemObjectType { Id = CsGroupTypeId, Name = typeName }
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = CsExternalIdAttributeId,
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = CsExternalIdAttributeId,
                Name = "distinguishedName",
                IsExternalId = true,
                Type = AttributeDataType.Text
            },
            StringValue = externalId,
            ConnectedSystemObject = cso
        });
        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private static PendingExport BuildRecallPendingExport(Guid connectedSystemObjectId, int changeCount, int connectedSystemId = TargetSystemId)
    {
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemObjectId = connectedSystemObjectId,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending
        };
        for (var i = 0; i < changeCount; i++)
        {
            pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                AttributeId = 900 + i,
                ChangeType = PendingExportAttributeChangeType.Remove,
                StringValue = $"uid=member{i},ou=People,dc=corp"
            });
        }
        return pendingExport;
    }

    private static ExportEvaluationCache BuildRecallExportEvaluationCache(int connectedSystemId, string connectedSystemName)
    {
        const int mvoTypeId = 1;
        var exportRule = new SyncRule
        {
            Id = 1,
            Name = "Test Export Rule",
            MetaverseObjectTypeId = mvoTypeId,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = connectedSystemName },
            Direction = SyncRuleDirection.Export,
            Enabled = true
        };
        var exportRulesByMvoTypeId = new Dictionary<int, List<SyncRule>> { { mvoTypeId, new List<SyncRule> { exportRule } } };
        var csoLookup = new Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject>();
        var csoAttributeValues = Enumerable.Empty<ConnectedSystemObjectAttributeValue>()
            .ToLookup(av => (av.ConnectedSystemObject.Id, av.AttributeId));
        return new ExportEvaluationCache(exportRulesByMvoTypeId, csoLookup, csoAttributeValues, new List<int> { connectedSystemId });
    }

    private DeferredRecallCausalTestProcessor CreateProcessor(out Activity activity)
    {
        var connectedSystem = new ConnectedSystem { Id = TargetSystemId, Name = "Target LDAP" };
        var runProfile = new ConnectedSystemRunProfile { Id = 1, Name = "Full Sync", RunType = ConnectedSystemRunType.FullSynchronisation };
        activity = new Activity { Id = Guid.NewGuid() };
        var processor = new DeferredRecallCausalTestProcessor(
            new SyncEngine(),
            new JIM.Application.Servers.SyncServer(Jim),
            SyncRepo,
            connectedSystem,
            runProfile,
            activity,
            new CancellationTokenSource());
        processor.SetOutcomeTracking(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        processor.SetCsoChangeTrackingEnabled(false);
        return processor;
    }

    /// <summary>
    /// Exposes the protected deferred-recall seam so the causal capture can be driven directly without standing
    /// up a full paged sync run.
    /// </summary>
    private sealed class DeferredRecallCausalTestProcessor : SyncFullSyncTaskProcessor
    {
        public DeferredRecallCausalTestProcessor(
            ISyncEngine syncEngine,
            JIM.Application.Interfaces.ISyncServer syncServer,
            ISyncRepository syncRepository,
            ConnectedSystem connectedSystem,
            ConnectedSystemRunProfile connectedSystemRunProfile,
            Activity activity,
            CancellationTokenSource cancellationTokenSource)
            : base(syncEngine, syncServer, syncRepository, connectedSystem, connectedSystemRunProfile, activity, cancellationTokenSource)
        {
        }

        public void SetOutcomeTracking(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel level) => _syncOutcomeTrackingLevel = level;

        public void SetCsoChangeTrackingEnabled(bool enabled) => _csoChangeTrackingEnabled = enabled;

        public void SetRecallExportEvaluationCache(ExportEvaluationCache cache) => _recallExportEvaluationCache = cache;

        public void CallStageDeferredRecallRpei(PendingExport pendingExport, string? displayName, IReadOnlyCollection<CausalCause> causes)
            => StageDeferredRecallRpei(pendingExport, displayName, causes);

        public Task CallFlushDeferredRecallRpeisAsync() => FlushDeferredRecallRpeisAsync();
    }
}
