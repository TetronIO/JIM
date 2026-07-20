// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Processors;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Proves the reference-recall RPEI deduplication (#1003 follow-up). Reference recall stages a
/// membership-removal Pending Export for a referencing group on EVERY page that deletes one of its
/// members, and the delete-then-create persistence coalesces every page's removals into a single
/// Pending Export row. Emitting one RPEI per page-flush therefore reported that single Pending Export
/// as many RPEIs: on the Scale500k25kGroups run, 21,824 Pending Export RPEIs for 5,421 real Pending
/// Exports (a ~4x average, and for hub groups referenced by leavers on hundreds of pages, >100x). The
/// fix defers recall RPEI emission to end of run, keyed by referencing CSO (last write wins, because
/// the final flush's Pending Export already carries every prior page's removals), so exactly one RPEI
/// is emitted per group, carrying the coalesced Pending Export's id and cumulative removal count.
/// </summary>
[TestFixture]
public class ReferenceRecallDeduplicationTests
{
    private const int TargetSystemId = 5;
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
    /// Two page-flushes staging a recall Pending Export for the SAME group CSO must produce exactly one
    /// RPEI at end of run, carrying the final (coalesced) Pending Export's id, its cumulative removal
    /// count, and the group CSO's external-ID and type snapshots.
    /// </summary>
    [Test]
    public async Task FlushDeferredRecallRpeis_SameCsoStagedAcrossPages_EmitsSingleRpeiForFinalPendingExportAsync()
    {
        var groupCso = SeedGroupCso("cn=Team Alpha,ou=Groups,dc=corp", "group");
        var processor = CreateProcessor(out var activity);

        // Page 1: recall stages the group's Pending Export carrying one member removal.
        var pageOnePendingExport = BuildRecallPendingExport(groupCso.Id, changeCount: 1);
        processor.CallStageDeferredRecallRpei(pageOnePendingExport, "Team Alpha");

        // Page 2: another member of the same group is deleted; the delete-then-create merge produces a
        // fresh Pending Export (new id) carrying BOTH removals, superseding page 1's.
        var pageTwoPendingExport = BuildRecallPendingExport(groupCso.Id, changeCount: 2);
        processor.CallStageDeferredRecallRpei(pageTwoPendingExport, "Team Alpha");

        await processor.CallFlushDeferredRecallRpeisAsync();

        var recallRpeis = activity.RunProfileExecutionItems
            .Where(r => r.ObjectChangeType == ObjectChangeType.PendingExport)
            .ToList();
        Assert.That(recallRpeis, Has.Count.EqualTo(1),
            "A group whose members were deleted across two pages must yield exactly one recall RPEI");

        var rpei = recallRpeis[0];
        Assert.That(rpei.ConnectedSystemObjectId, Is.EqualTo(groupCso.Id));
        Assert.That(rpei.PendingExportId, Is.EqualTo(pageTwoPendingExport.Id),
            "The surviving RPEI must reference the final coalesced Pending Export, not the superseded one");
        Assert.That(rpei.DisplayNameSnapshot, Is.EqualTo("Team Alpha"));
        Assert.That(rpei.ExternalIdSnapshot, Is.EqualTo("cn=Team Alpha,ou=Groups,dc=corp"),
            "The RPEI must snapshot the group CSO's external ID so it stays identifiable if the CSO is later deleted");
        Assert.That(rpei.ObjectTypeSnapshot, Is.EqualTo("group"));

        var rootOutcome = rpei.SyncOutcomes.SingleOrDefault();
        Assert.That(rootOutcome, Is.Not.Null, "Detailed outcome tracking must record a PendingExportCreated outcome");
        Assert.That(rootOutcome!.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated));
        Assert.That(rootOutcome.DetailCount, Is.EqualTo(2),
            "The outcome's removal count must reflect the final coalesced Pending Export, not just the first page's removal");
    }

    /// <summary>
    /// Distinct group CSOs each get their own recall RPEI; deduplication is per referencing CSO, not global.
    /// </summary>
    [Test]
    public async Task FlushDeferredRecallRpeis_DistinctCsos_EmitsOneRpeiEachAsync()
    {
        var groupCsoA = SeedGroupCso("cn=Alpha,ou=Groups,dc=corp", "group");
        var groupCsoB = SeedGroupCso("cn=Bravo,ou=Groups,dc=corp", "group");
        var processor = CreateProcessor(out var activity);

        processor.CallStageDeferredRecallRpei(BuildRecallPendingExport(groupCsoA.Id, changeCount: 1), "Alpha");
        processor.CallStageDeferredRecallRpei(BuildRecallPendingExport(groupCsoB.Id, changeCount: 1), "Bravo");

        await processor.CallFlushDeferredRecallRpeisAsync();

        var recallRpeis = activity.RunProfileExecutionItems
            .Where(r => r.ObjectChangeType == ObjectChangeType.PendingExport)
            .ToList();
        Assert.That(recallRpeis, Has.Count.EqualTo(2));
        Assert.That(recallRpeis.Select(r => r.ConnectedSystemObjectId),
            Is.EquivalentTo(new[] { (Guid?)groupCsoA.Id, groupCsoB.Id }));
    }

    /// <summary>
    /// Nothing staged means nothing emitted (the end-of-run flush is a no-op).
    /// </summary>
    [Test]
    public async Task FlushDeferredRecallRpeis_NothingStaged_EmitsNoRpeisAsync()
    {
        var processor = CreateProcessor(out var activity);
        await processor.CallFlushDeferredRecallRpeisAsync();
        Assert.That(activity.RunProfileExecutionItems, Is.Empty);
    }

    private ConnectedSystemObject SeedGroupCso(string externalId, string typeName)
    {
        var groupType = new ConnectedSystemObjectType { Id = CsGroupTypeId, Name = typeName };
        var externalIdAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = CsExternalIdAttributeId,
            Name = "distinguishedName",
            Type = AttributeDataType.Text,
            IsExternalId = true,
            Selected = true
        };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystemId,
            TypeId = CsGroupTypeId,
            Type = groupType,
            Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = CsExternalIdAttributeId
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            Attribute = externalIdAttribute,
            AttributeId = CsExternalIdAttributeId,
            StringValue = externalId
        });
        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private static PendingExport BuildRecallPendingExport(Guid connectedSystemObjectId, int changeCount)
    {
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystemId,
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

    private DeferredRecallTestProcessor CreateProcessor(out Activity activity)
    {
        var connectedSystem = new ConnectedSystem { Id = TargetSystemId, Name = "Target LDAP" };
        var runProfile = new ConnectedSystemRunProfile { Id = 1, Name = "Full Sync", RunType = ConnectedSystemRunType.FullSynchronisation };
        activity = new Activity { Id = Guid.NewGuid() };
        var processor = new DeferredRecallTestProcessor(
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
    /// Exposes the protected deferred-recall seam on the concrete processor so the deduplication and
    /// end-of-run emission can be driven directly without standing up a full paged sync run.
    /// </summary>
    private sealed class DeferredRecallTestProcessor : SyncFullSyncTaskProcessor
    {
        public DeferredRecallTestProcessor(
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

        public void CallStageDeferredRecallRpei(PendingExport pendingExport, string? displayName) => StageDeferredRecallRpei(pendingExport, displayName);

        public Task CallFlushDeferredRecallRpeisAsync() => FlushDeferredRecallRpeisAsync();
    }
}
