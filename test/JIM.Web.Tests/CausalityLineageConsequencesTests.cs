// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Lineage's After this run sections (#1811): where <see cref="CausalityLineageModelBuilder"/> places what an item went
/// on to cause. Consequences land on the object they happened to, keyed by object rather than by system, so an account
/// and the Groups that referenced its owner are separate objects in the same Connected System; every branch ends in a
/// card saying where it stopped; and the footer's reading of the walk is derived here so the view only renders it.
/// </summary>
[TestFixture]
public class CausalityLineageConsequencesTests
{
    private const int Yellowstone = 1;
    private const int Glitterband = 2;
    private static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid DeletedMvoId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid AccountCsoId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    // ─── Tina Adams's import, seen once the cascade has run (PRD Scenario 2) ───

    [Test]
    public void Build_CompleteCascade_PlacesEachConsequenceOnTheObjectItHappenedTo()
    {
        var lineage = BuildImportLineage(CompleteCascade());

        var columns = lineage.Columns;
        Assert.That(columns.Select(c => c.Kind), Is.EqualTo(new[]
        {
            CausalityLineageColumnKind.Record, CausalityLineageColumnKind.Identity, CausalityLineageColumnKind.Record
        }));
        var record = columns[0].Objects.Single();
        var identity = columns[1].Objects.Single();
        var targets = columns[2].Objects;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.Consequences, Is.Empty, "the Synchronisation's events happened to the Metaverse Object and beyond");
            Assert.That(identity.Consequences.Select(c => c.Event?.OutcomeType), Is.EqualTo(new ActivityRunProfileExecutionItemSyncOutcomeType?[]
            {
                ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope,
                ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted
            }));
            Assert.That(targets, Has.Count.EqualTo(2), "the account and the Groups are separate objects in one Connected System");
            Assert.That(targets.Select(t => t.SystemName), Is.All.EqualTo("Glitterband EMEA"));
        }
    }

    [Test]
    public void Build_CompleteCascade_TheAccountCarriesItsDeprovisionThroughToTheConfirmation()
    {
        var lineage = BuildImportLineage(CompleteCascade());

        var account = lineage.Columns[2].Objects[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.Title, Is.EqualTo("tina.adams99"), "named by the export item that acted on it");
            Assert.That(account.ObjectTypeName, Is.EqualTo("person"));
            Assert.That(account.Consequences.Select(c => c.Kind), Is.EqualTo(new[]
            {
                CausalityLineageConsequenceKind.Outcome, CausalityLineageConsequenceKind.Outcome, CausalityLineageConsequenceKind.Confirmation
            }));
            Assert.That(account.Consequences[0].Event?.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued));
            Assert.That(account.Consequences[0].Text, Is.EqualTo("Its Metaverse Object was deleted."));
            Assert.That(account.Consequences[0].RunLabel, Is.EqualTo("Full Synchronisation"));
            Assert.That(account.Consequences[1].RunLabel, Is.EqualTo("Export"));
            Assert.That(account.Consequences[1].ActivityItemHref, Does.StartWith("/activity/item/"));
            Assert.That(account.Consequences[2].DisplayTitle, Is.EqualTo("Confirmed by the next import"));
        }
    }

    [Test]
    public void Build_CompleteCascade_TheGroupsShareOneObjectHeadedByTheirExactCount()
    {
        var lineage = BuildImportLineage(CompleteCascade());

        var groups = lineage.Columns[2].Objects[1];
        var removal = groups.Consequences[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups.Title, Is.EqualTo("Groups"));
            Assert.That(groups.IsRoleHead, Is.True);
            Assert.That(groups.MemberCount, Is.EqualTo(4));
            Assert.That(removal.DisplayTitle, Is.EqualTo("Removed Tina Adams from Static Members on 4 Groups"));
            Assert.That(removal.Text, Is.EqualTo("The Metaverse Object they referenced was deleted."));
            Assert.That(removal.Cohort?.MemberCount, Is.EqualTo(4), "the card pages its members");
            Assert.That(removal.ActivityItemHref, Is.Null, "each member links its own item");
            Assert.That(groups.Consequences[1].Cohort?.MemberCount, Is.EqualTo(4));
            Assert.That(groups.Consequences[2].Kind, Is.EqualTo(CausalityLineageConsequenceKind.Confirmation));
            Assert.That(groups.Consequences[2].DisplayTitle, Is.EqualTo("Confirmed by the next import, on 4 Groups"));
        }
    }

    [Test]
    public void Build_CompleteCascade_TheDeletedCardReplacesTheDeletedAfterThisRunNote()
    {
        var lineage = BuildImportLineage(CompleteCascade(), deletedMvoId: DeletedMvoId);

        var identity = lineage.Columns[1].Objects.Single();
        Assert.That(identity.IsDeletedAfterThisRun, Is.False,
            "the Deleted card says it, with its rule and its deletion record; the note would say it twice");
    }

    [Test]
    public void Build_CompleteCascade_ReadsAsFinishedWithPillsForWhatChanged()
    {
        var lineage = BuildImportLineage(CompleteCascade());

        var summary = lineage.Consequences!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.HasWaitingBranches, Is.False);
            Assert.That(summary.StateSentences, Is.EqualTo(new[] { "Every branch has run to its end." }));
            Assert.That(summary.Pills.Select(p => p.Label), Does.Contain("Metaverse Object deleted"));
            Assert.That(summary.Pills.Select(p => p.Label), Does.Contain("Removed from Static Members · 4"));
            Assert.That(summary.Pills.Select(p => p.Label), Has.None.StartsWith("Confirmed"), "a confirmation changes nothing");
        }
    }

    // ─── Moments before the cascade finishes (PRD Scenario 5) ───

    [Test]
    public void Build_ImportNotYetSynchronised_EndsOnTheRecordWaitingForThatSystem()
    {
        var consequences = Consequences(endings: [Ending(CausalConsequenceEnding.WaitingForSynchronisation, Yellowstone, "Yellowstone APAC")]);

        var lineage = BuildImportLineage(consequences);

        var record = lineage.Columns.Single().Objects.Single();
        var waiting = record.Consequences.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(waiting.Kind, Is.EqualTo(CausalityLineageConsequenceKind.Waiting));
            Assert.That(waiting.DisplayTitle, Is.EqualTo("Waiting for the next Synchronisation of Yellowstone APAC"));
            Assert.That(lineage.Consequences!.HasWaitingBranches, Is.True);
            Assert.That(lineage.Consequences.StateSentences, Is.EqualTo(new[] { "1 branch is waiting on a Synchronisation of Yellowstone APAC." }));
        }
    }

    [Test]
    public void Build_ImportSynchronisedWithNothingRecorded_EndsSettledWithNothingToCheck()
    {
        var consequences = Consequences(endings: [Ending(CausalConsequenceEnding.NothingFollowed, Yellowstone, "Yellowstone APAC")]);

        var lineage = BuildImportLineage(consequences);

        var settled = lineage.Columns.Single().Objects.Single().Consequences.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(settled.Kind, Is.EqualTo(CausalityLineageConsequenceKind.Settled));
            Assert.That(settled.DisplayTitle, Is.EqualTo("Nothing followed"));
            Assert.That(lineage.Consequences!.HasWaitingBranches, Is.False);
            Assert.That(lineage.Consequences.StateSentences, Is.EqualTo(new[] { "Nothing followed from this run." }));
        }
    }

    [Test]
    public void Build_ExportStillQueued_EndsOnTheAccountTheDeprovisionWasQueuedFor()
    {
        var synchronisation = SynchronisationCohort();
        var pendingExportId = Guid.NewGuid();
        synchronisation.Consequences.Add(OutstandingCohort(PendingExportStatus.Pending, pendingExportId));

        var lineage = BuildImportLineage(Consequences(synchronisation));

        var account = lineage.Columns[2].Objects.Single();
        var waiting = account.Consequences[^1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(waiting.Kind, Is.EqualTo(CausalityLineageConsequenceKind.Waiting));
            Assert.That(waiting.DisplayTitle, Is.EqualTo("Waiting for the next Export of Glitterband EMEA"));
            Assert.That(waiting.LinkHref, Is.EqualTo($"/admin/connected-systems/{Glitterband}/pending-exports/{pendingExportId}"));
            Assert.That(lineage.Consequences!.StateSentences, Is.EqualTo(new[] { "1 branch is waiting on an Export of Glitterband EMEA." }));
        }
    }

    [Test]
    public void Build_FailedExports_NeedAttentionRatherThanWaiting()
    {
        var synchronisation = SynchronisationCohort();
        synchronisation.Consequences.Add(OutstandingCohort(PendingExportStatus.Failed, Guid.NewGuid()));

        var lineage = BuildImportLineage(Consequences(synchronisation));

        var card = lineage.Columns[2].Objects.Single().Consequences[^1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(card.Kind, Is.EqualTo(CausalityLineageConsequenceKind.NeedsAttention));
            Assert.That(lineage.Consequences!.HasWaitingBranches, Is.False, "nothing will happen without someone acting");
            Assert.That(lineage.Consequences.StateSentences, Is.EqualTo(new[] { "1 branch needs attention." }));
        }
    }

    [Test]
    public void Build_DeletionStillInItsGracePeriod_WaitsOnTheMetaverseObjectWithItsDueDate()
    {
        var due = T0.AddDays(30);
        var scheduled = Cohort(CausalConsequenceHopKind.ScheduledDeletion, representative: null, memberCount: 1, dueAt: due,
            ending: CausalConsequenceEnding.DeletionScheduled);

        var lineage = BuildImportLineage(Consequences(scheduled));

        var identity = lineage.Columns.Single(c => c.Kind == CausalityLineageColumnKind.Identity).Objects.Single();
        var waiting = identity.Consequences.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(waiting.Kind, Is.EqualTo(CausalityLineageConsequenceKind.Waiting));
            Assert.That(waiting.DisplayTitle, Is.EqualTo("Deletion scheduled"));
            Assert.That(waiting.DueAt, Is.EqualTo(due));
        }
    }

    [Test]
    public void Build_DisconnectAction_SaysNothingMoreFollowsForTheAccountLeftInPlace()
    {
        var lineage = BuildImportLineage(Consequences(SynchronisationCohort(disconnect: true)));

        var account = lineage.Columns[2].Objects.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.Consequences[0].Event?.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.TargetDisconnected));
            Assert.That(account.Consequences[^1].Kind, Is.EqualTo(CausalityLineageConsequenceKind.Settled));
            Assert.That(account.Consequences[^1].Text, Is.EqualTo("It stays in Glitterband EMEA, and JIM no longer manages it."));
        }
    }

    [Test]
    public void Build_BranchPastTheBound_SaysItGoesFurtherThanShown()
    {
        var synchronisation = SynchronisationCohort();
        synchronisation.IsFurtherThanShown = true;

        var lineage = BuildImportLineage(Consequences(synchronisation, truncatedByDepth: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lineage.Consequences!.IsFurtherThanShown, Is.True);
            Assert.That(lineage.Columns.SelectMany(c => c.Objects).SelectMany(o => o.Consequences)
                .Count(c => c.Kind == CausalityLineageConsequenceKind.FurtherThanShown), Is.EqualTo(1));
        }
    }

    // ─── What the walk does not change ───

    [Test]
    public void Build_ItemThatCausesNothing_HasNoAfterThisRunAndNoFooter()
    {
        var lineage = BuildImportLineage(Consequences(endings: [Ending(CausalConsequenceEnding.NoFurtherConsequences)]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lineage.Columns.SelectMany(c => c.Objects).SelectMany(o => o.Consequences), Is.Empty);
            Assert.That(lineage.Consequences!.HasAny, Is.False, "a confirmation never causes anything, so nothing hints that it might");
        }
    }

    [Test]
    public void Build_ConsequencesNotYetLoaded_LeavesTheLineageAsItWas()
    {
        var lineage = BuildImportLineage(consequences: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lineage.Consequences, Is.Null);
            Assert.That(lineage.Columns.Single().Objects.Single().Consequences, Is.Empty);
        }
    }

    [Test]
    public void Build_SynchronisationItem_ItsExportLandsOnTheTargetThisRunStaged()
    {
        // The target already has a column from this run's own Provisioned outcome; the export that carried it out is
        // the same object's story, not a new object.
        var item = CausalityTestData.NewJoinerItem();
        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());
        var export = ExportCohort(CausalReasonCode.ExportCreateStaged, memberCount: 1, displayName: "Liam Allen");

        var lineage = CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Projected, Consequences(export));

        var target = lineage.Columns[^1].Objects.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.Cards, Has.Count.EqualTo(2), "this run's own events");
            Assert.That(target.Consequences.Single().Event?.Label, Is.EqualTo("Connected System Object created"));
        }
    }

    // ─── Fixtures ───

    private static CausalityPageContext ImportContext(Guid? deletedMvoId = null) => new(
        ConnectedSystemId: Yellowstone,
        ConnectedSystemName: "Yellowstone APAC",
        RunProfileName: "Delta Import",
        CsoId: Guid.NewGuid(),
        CsoConnectedSystemId: Yellowstone,
        CsoConnectedSystemName: "Yellowstone APAC",
        CsoDisplayName: "S8-99",
        CsoExternalId: "S8-99",
        CsoObjectTypeName: "person",
        MvoTypeName: "User",
        MvoTypePluralName: "Users",
        DeletedMetaverseObjectId: deletedMvoId);

    private static CausalityLineageModel BuildImportLineage(CausalConsequences? consequences, Guid? deletedMvoId = null)
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ObjectChangeType = ObjectChangeType.Updated };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated, parent: null, ordinal: 0, detailCount: 1);
        var model = CausalityModelBuilder.Build(item, ImportContext(deletedMvoId));
        return CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Updated, consequences);
    }

    private static CausalConsequences Consequences(CausalConsequenceCohort cohort, bool truncatedByDepth = false) => new()
    {
        Cohorts = [cohort],
        IsTruncatedByDepth = truncatedByDepth,
        CheckedAt = T0.AddDays(3)
    };

    private static CausalConsequences Consequences(List<CausalConsequenceEndingCount> endings) => new()
    {
        Endings = endings,
        CheckedAt = T0.AddDays(3)
    };

    private static CausalConsequenceEndingCount Ending(CausalConsequenceEnding ending, int? systemId = null, string? systemName = null) => new()
    {
        Ending = ending,
        MemberCount = 1,
        ConnectedSystemId = systemId,
        ConnectedSystemName = systemName
    };

    /// <summary>
    /// The Synchronisation that acted on the import: the record left scope, its Metaverse Object was deleted, and the
    /// deletion queued a deprovision (or, under a Disconnect action, left the account in place) in Glitterband EMEA.
    /// </summary>
    private static CausalConsequenceCohort SynchronisationCohort(bool disconnect = false)
    {
        var item = Item(ConnectedSystemRunType.FullSynchronisation, Yellowstone, T0.AddHours(1), "Tina Adams", "S8-99", "person");
        var scopeExit = CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope,
            parent: null, ordinal: 0, targetEntityId: DeletedMvoId, targetEntityDescription: "Tina Adams",
            syncRuleId: 7, syncRuleName: "Yellowstone APAC Inbound Users");
        var deleted = CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted,
            parent: scopeExit, ordinal: 0, targetEntityId: DeletedMvoId, targetEntityDescription: "Tina Adams",
            detailMessage: "Deleted immediately: last authoritative source disconnected");
        if (disconnect)
        {
            CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.TargetDisconnected,
                parent: deleted, ordinal: 0, targetEntityId: AccountCsoId, targetEntityDescription: "Glitterband EMEA",
                detailMessage: $"{Glitterband}|person", syncRuleId: 9, syncRuleName: "Glitterband EMEA Outbound Users");
        }
        else
        {
            CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued,
                parent: deleted, ordinal: 0, targetEntityId: Guid.NewGuid(), targetEntityDescription: "Glitterband EMEA",
                detailCount: 1, detailMessage: $"{Glitterband}|person", syncRuleId: 9, syncRuleName: "Glitterband EMEA Outbound Users");
        }

        return Cohort(CausalConsequenceHopKind.NextSynchronisation, item, memberCount: 1, systemId: Yellowstone,
            systemName: "Yellowstone APAC", objectType: "person");
    }

    private static CausalConsequences CompleteCascade()
    {
        var synchronisation = SynchronisationCohort();

        var accountExport = ExportCohort(CausalReasonCode.ExportDeleteStaged, memberCount: 1, displayName: "tina.adams99", csoId: AccountCsoId);
        accountExport.Consequences.Add(ConfirmationCohort(memberCount: 1, displayName: "tina.adams99"));
        synchronisation.Consequences.Add(accountExport);

        var recall = Item(ConnectedSystemRunType.FullSynchronisation, Yellowstone, T0.AddHours(1), "Project-Catalyst", "cn=Project-Catalyst", "group");
        recall.ConnectedSystemObjectId = Guid.NewGuid();
        CausalityTestData.AddOutcome(recall, ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, parent: null, ordinal: 0,
            targetEntityId: Guid.NewGuid(), targetEntityDescription: "Glitterband EMEA", detailCount: 1,
            detailMessage: $"{Glitterband}|group", stagedChangeType: PendingExportChangeType.Update);
        var removals = Cohort(CausalConsequenceHopKind.Edge, recall, memberCount: 4, systemId: Yellowstone, systemName: "Yellowstone APAC",
            edgeType: CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval, reasonCode: CausalReasonCode.AuthoritativeSourceDisconnected,
            objectType: "group", pluralName: "Groups", attributeName: "Static Members");
        var groupExports = ExportCohort(CausalReasonCode.ExportUpdateStaged, memberCount: 4, displayName: "Project-Catalyst", objectType: "group");
        groupExports.Consequences.Add(ConfirmationCohort(memberCount: 4, displayName: "Project-Catalyst", objectType: "group"));
        removals.Consequences.Add(groupExports);
        synchronisation.Consequences.Add(removals);

        return Consequences(synchronisation);
    }

    private static CausalConsequenceCohort ExportCohort(CausalReasonCode reasonCode, int memberCount, string displayName,
        Guid? csoId = null, string objectType = "person")
    {
        var item = Item(ConnectedSystemRunType.Export, Glitterband, T0.AddHours(2), displayName, $"cn={displayName}", objectType);
        item.ConnectedSystemObjectId = csoId ?? Guid.NewGuid();
        CausalityTestData.AddOutcome(item, reasonCode == CausalReasonCode.ExportDeleteStaged
                ? ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned
                : ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0, detailCount: 1);
        return Cohort(CausalConsequenceHopKind.Edge, item, memberCount, systemId: Glitterband, systemName: "Glitterband EMEA",
            edgeType: CausalEdgeType.PendingExportQueueingCausedExportExecution, reasonCode: reasonCode, objectType: objectType,
            pluralName: objectType == "group" ? "Groups" : null);
    }

    private static CausalConsequenceCohort ConfirmationCohort(int memberCount, string displayName, string objectType = "person")
    {
        var item = Item(ConnectedSystemRunType.DeltaImport, Glitterband, T0.AddHours(3), displayName, $"cn={displayName}", objectType);
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed, parent: null, ordinal: 0);
        return Cohort(CausalConsequenceHopKind.Confirmation, item, memberCount, systemId: Glitterband, systemName: "Glitterband EMEA",
            edgeType: CausalEdgeType.ExportCausedImportConfirmation, objectType: objectType,
            pluralName: objectType == "group" ? "Groups" : null, ending: CausalConsequenceEnding.Confirmed);
    }

    private static CausalConsequenceCohort OutstandingCohort(PendingExportStatus status, Guid pendingExportId)
    {
        var cohort = Cohort(CausalConsequenceHopKind.OutstandingPendingExport, representative: null, memberCount: 1,
            systemId: Glitterband, systemName: "Glitterband EMEA", pendingExportStatus: status,
            ending: status is PendingExportStatus.Failed or PendingExportStatus.Parked
                ? CausalConsequenceEnding.NeedsAttention
                : CausalConsequenceEnding.WaitingForExport);
        cohort.Members.Add(new CausalConsequenceMember { PendingExportId = pendingExportId, DisplayName = "Tina Adams", Occurred = T0.AddHours(1) });
        return cohort;
    }

    private static ActivityRunProfileExecutionItem Item(ConnectedSystemRunType runType, int systemId, DateTime executed,
        string displayName, string externalId, string objectType)
    {
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = systemId,
            ConnectedSystemRunType = runType,
            Executed = executed,
            TargetName = runType.ToString()
        };
        return new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            Activity = activity,
            ActivityId = activity.Id,
            DisplayNameSnapshot = displayName,
            ExternalIdSnapshot = externalId,
            ObjectTypeSnapshot = objectType
        };
    }

    private static CausalConsequenceCohort Cohort(CausalConsequenceHopKind hopKind, ActivityRunProfileExecutionItem? representative,
        int memberCount, int? systemId = null, string? systemName = null, CausalEdgeType? edgeType = null,
        CausalReasonCode reasonCode = CausalReasonCode.NotSet, string? objectType = null, string? pluralName = null,
        string? attributeName = null, PendingExportStatus? pendingExportStatus = null, DateTime? dueAt = null,
        CausalConsequenceEnding? ending = null)
    {
        var occurred = representative?.Activity.Executed ?? T0.AddHours(1);
        var members = new List<CausalConsequenceMember>();
        if (representative != null)
        {
            members.Add(new CausalConsequenceMember
            {
                RunProfileExecutionItemId = representative.Id, DisplayName = representative.DisplayNameSnapshot, Occurred = occurred
            });
            members.AddRange(Enumerable.Range(1, memberCount - 1).Select(i => new CausalConsequenceMember
            {
                RunProfileExecutionItemId = Guid.NewGuid(), DisplayName = $"{representative.DisplayNameSnapshot} {i}", Occurred = occurred
            }));
        }

        return new CausalConsequenceCohort
        {
            CohortKey = Guid.NewGuid().ToString("N"),
            HopKind = hopKind,
            EdgeType = edgeType,
            ReasonCode = reasonCode,
            ConnectedSystemId = systemId,
            ConnectedSystemName = systemName,
            ObjectTypeName = objectType,
            ObjectTypePluralName = pluralName,
            AttributeName = attributeName,
            PendingExportStatus = pendingExportStatus,
            MemberCount = memberCount,
            FirstOccurred = occurred,
            LastOccurred = occurred,
            DueAt = dueAt,
            Members = members,
            Ending = ending,
            Representative = representative
        };
    }
}
