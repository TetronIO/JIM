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
using static JIM.Web.Tests.CausalityConsequenceTestData;

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
    public void Build_CompleteCascade_NamesTheSettingThatDecidedEachDeletion()
    {
        // The settings an administrator would change to get a different result: the Deletion Rule that deleted the
        // Metaverse Object, and the Deprovisioning Action that deleted the account rather than leaving it.
        var lineage = BuildImportLineage(CompleteCascade());

        var deleted = lineage.Columns[1].Objects.Single().Consequences[1];
        var deprovision = lineage.Columns[2].Objects[0].Consequences[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That((deleted.SettingLabel, deleted.SettingValue), Is.EqualTo(("Deletion Rule", "When Authoritative Source Disconnected")));
            Assert.That(deleted.SettingText, Is.EqualTo("No grace period, so it was deleted in the same run."));
            Assert.That((deprovision.SettingLabel, deprovision.SettingValue), Is.EqualTo(("Deprovisioning Action", "Delete")));
            Assert.That(deprovision.SettingText, Is.EqualTo("This rule removes the objects it deprovisions rather than leaving them in place."));
        }
    }

    [Test]
    public void Build_DisconnectAction_NamesItsDeprovisioningAction()
    {
        var lineage = BuildImportLineage(Consequences(SynchronisationCohort(disconnect: true)));

        var disconnected = lineage.Columns[2].Objects.Single().Consequences[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That((disconnected.SettingLabel, disconnected.SettingValue), Is.EqualTo(("Deprovisioning Action", "Disconnect")));
            Assert.That(disconnected.SettingText, Is.EqualTo("This rule leaves the objects it deprovisions in place, so nothing was exported."));
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

    [Test]
    public void Build_TypeWithNoCuratedPlural_StillReadsAsSeveralObjects()
    {
        // The walk finds a plural only where the snapshotted type is also a Metaverse Object Type's name; a Connected
        // System's own type ("jimGroup") has none, and "4 jimGroup" reads as one object called that.
        var cascade = CompleteCascade();
        var removals = cascade.Cohorts[0].Consequences[1];
        var uncurated = Recast(removals, objectType: "jimGroup", pluralName: null);
        cascade.Cohorts[0].Consequences[1] = uncurated;

        var lineage = BuildImportLineage(cascade);

        var groups = lineage.Columns[2].Objects[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups.Title, Is.EqualTo("jimGroup objects"));
            Assert.That(groups.Consequences[0].DisplayTitle, Is.EqualTo("Removed Tina Adams from Static Members on 4 jimGroup objects"));
        }
    }

    [Test]
    public void Build_LaterCohortWithNoTypeOfItsOwn_KeepsTheNameTheBranchAlreadyGaveTheObjects()
    {
        // A confirming import often snapshots no type. It is the same four Groups, so it says so, and the head the
        // removal gave them stands.
        var cascade = CompleteCascade();
        var groupExports = cascade.Cohorts[0].Consequences[1].Consequences[0];
        groupExports.Consequences[0] = Recast(groupExports.Consequences[0], objectType: null, pluralName: null);

        var lineage = BuildImportLineage(cascade);

        var groups = lineage.Columns[2].Objects[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups.Title, Is.EqualTo("Groups"));
            Assert.That(groups.Consequences[^1].DisplayTitle, Is.EqualTo("Confirmed by the next import, on 4 Groups"));
        }
    }

    [Test]
    public void Build_ConfirmingImportThatSnapshottedNoType_DoesNotRenameTheAccount()
    {
        var cascade = CompleteCascade();
        var accountExport = cascade.Cohorts[0].Consequences[0];
        accountExport.Consequences[0].Representative!.ObjectTypeSnapshot = null;

        var lineage = BuildImportLineage(cascade);

        var account = lineage.Columns[2].Objects[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.Title, Is.EqualTo("tina.adams99"));
            Assert.That(account.ObjectTypeName, Is.EqualTo("person"), "the export named it first, with its type");
        }
    }

    [Test]
    public void Build_PendingExportSinceCarriedOut_LinksTheQueueRatherThanTheGoneRow()
    {
        // A Pending Export is deleted once exported, so a later run's card must not link a row that no longer exists.
        var lineage = BuildImportLineage(CompleteCascade());

        var deprovision = lineage.Columns[2].Objects[0].Consequences[0];
        var pendingExportLink = deprovision.Event!.Links.Single(l => l.Kind == CausalityEntityKind.PendingExport);
        Assert.That(pendingExportLink.Href, Is.EqualTo($"/admin/connected-systems/{Glitterband}/pending-exports"));
    }

    [Test]
    public void Build_PendingExportStillQueued_LinksTheRowItself()
    {
        var pendingExportId = Guid.NewGuid();
        var synchronisation = SynchronisationCohort(pendingExportId: pendingExportId);
        synchronisation.Consequences.Add(OutstandingCohort(PendingExportStatus.Pending, pendingExportId));

        var lineage = BuildImportLineage(Consequences(synchronisation));

        var deprovision = lineage.Columns[2].Objects[0].Consequences[0];
        var pendingExportLink = deprovision.Event!.Links.Single(l => l.Kind == CausalityEntityKind.PendingExport);
        Assert.That(pendingExportLink.Href, Is.EqualTo($"/admin/connected-systems/{Glitterband}/pending-exports/{pendingExportId}"));
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
            Assert.That(account.Title, Is.EqualTo("Tina Adams"),
                "before any export names it, the account is the story's subject arriving in that system, as this run's own targets are");
            Assert.That(waiting.Kind, Is.EqualTo(CausalityLineageConsequenceKind.Waiting));
            Assert.That(waiting.DisplayTitle, Is.EqualTo("Waiting for the next Export of Glitterband EMEA"));
            Assert.That(waiting.LinkHref, Is.EqualTo($"/admin/connected-systems/{Glitterband}/pending-exports/{pendingExportId}"));
            Assert.That(lineage.Consequences!.StateSentences, Is.EqualTo(new[] { "1 branch is waiting on an Export of Glitterband EMEA." }));
        }
    }

    [Test]
    public void Build_ParkedExports_NeedAttentionRatherThanWaiting()
    {
        var synchronisation = SynchronisationCohort();
        synchronisation.Consequences.Add(OutstandingCohort(PendingExportStatus.Parked, Guid.NewGuid()));

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

    private static CausalConsequenceCohort Recast(CausalConsequenceCohort cohort, string? objectType, string? pluralName)
    {
        var recast = new CausalConsequenceCohort
        {
            CohortKey = cohort.CohortKey,
            HopKind = cohort.HopKind,
            EdgeType = cohort.EdgeType,
            ReasonCode = cohort.ReasonCode,
            ConnectedSystemId = cohort.ConnectedSystemId,
            ConnectedSystemName = cohort.ConnectedSystemName,
            ObjectTypeName = objectType,
            ObjectTypePluralName = pluralName,
            AttributeName = cohort.AttributeName,
            ChangeType = cohort.ChangeType,
            MemberCount = cohort.MemberCount,
            FirstOccurred = cohort.FirstOccurred,
            LastOccurred = cohort.LastOccurred,
            Members = cohort.Members,
            Ending = cohort.Ending,
            Representative = cohort.Representative
        };
        recast.Consequences.AddRange(cohort.Consequences);
        return recast;
    }
}
