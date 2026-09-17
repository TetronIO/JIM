// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Transactional;
using JIM.Web.Causality;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Tests for <see cref="OutcomeDisplayMap.GetEventOperation"/> (#1495 follow-up): the tone-tinted
/// operation chip a this-run event card carries, in the same vocabulary as a Lineage chain-hop
/// card's own chip (<see cref="OutcomeDisplayMap.GetHopOperation"/>), derived entirely from facts
/// the event already carries: its outcome type, and (for Exported only) the export decision's
/// reason code where the causal chain resolved one.
/// </summary>
[TestFixture]
public class OutcomeDisplayMapEventOperationTests
{
    [Test]
    public void GetEventOperation_Projected_ReadsCreatedSuccess()
    {
        // Deliberate behaviour change (#1495 second follow-up): Projected used to chip Primary/AirlineStops,
        // the only operation with a look of its own. Every "Created" verb now shares one tone and icon
        // (Success/Add) so a column scans on colour alone.
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.Projected);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.Label, Is.EqualTo("Created"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Success));
            Assert.That(display.Icon, Is.EqualTo(Icons.Material.Filled.Add));
        }
    }

    [Test]
    public void GetEventOperation_Joined_ReadsJoinedSecondary()
    {
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.Joined);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.Label, Is.EqualTo("Joined"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Secondary));
        }
    }

    [Test]
    public void GetEventOperation_CsoAdded_ReadsCreatedSuccess()
    {
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.Label, Is.EqualTo("Created"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Success));
        }
    }

    [Test]
    public void GetEventOperation_CsoUpdated_ReadsUpdatedInfo()
    {
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.Label, Is.EqualTo("Updated"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Info));
        }
    }

    [Test]
    public void GetEventOperation_CsoDeleted_ReadsDeletedError()
    {
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.Label, Is.EqualTo("Deleted"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Error));
        }
    }

    [Test]
    public void GetEventOperation_AttributeFlow_ReadsUpdatedInfo()
    {
        // "Updated" matches the operation vocabulary shared by every chip; AttributeFlow's own title
        // ("Attributes flowed") is a different label entirely, carried by the card's head rather than
        // by this chip.
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.Label, Is.EqualTo("Updated"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Info));
        }
    }

    [Test]
    public void GetEventOperation_DriftCorrection_ReadsUpdatedInfo()
    {
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.Label, Is.EqualTo("Updated"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Info));
        }
    }

    [Test]
    public void GetEventOperation_Provisioned_ReadsCreatedSuccess()
    {
        // Deliberate behaviour change (#1495 second follow-up): Provisioned used to chip its own AddCircle
        // icon; every "Created" verb now shares Success/Add so a column scans on colour alone.
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.Label, Is.EqualTo("Created"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Success));
            Assert.That(display.Icon, Is.EqualTo(Icons.Material.Filled.Add));
        }
    }

    [Test]
    public void GetEventOperation_MvoDeleted_ReadsDeletedError()
    {
        // Deliberate behaviour change (#1495 second follow-up): MvoDeleted used to chip PersonRemove; every
        // "Deleted" verb now shares Error/Delete so a column scans on colour alone.
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.Label, Is.EqualTo("Deleted"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Error));
            Assert.That(display.Icon, Is.EqualTo(Icons.Material.Filled.Delete));
        }
    }

    [Test]
    public void GetEventOperation_Deprovisioned_ReadsDeletedError()
    {
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.Label, Is.EqualTo("Deleted"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Error));
        }
    }

    [Test]
    public void GetEventOperation_DeprovisionQueued_ReadsDeletedAsAStagedDeletePrecedent()
    {
        // A queued deprovision is a staged delete: the chain's own queueing-decision chip for a staged
        // delete is the precedent for how a staged (not-yet-executed) kind is marked.
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.Label, Is.EqualTo("Deleted"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Error));
        }
    }

    // ExportCreateStaged's icon is a deliberate behaviour change (#1495 second follow-up): it used to chip
    // AddCircle; every "Created" verb now shares Add so a column scans on colour alone.
    [TestCase(CausalReasonCode.ExportCreateStaged, "Created", CausalityTone.Success, Icons.Material.Filled.Add)]
    [TestCase(CausalReasonCode.ExportUpdateStaged, "Updated", CausalityTone.Info, Icons.Material.Filled.Edit)]
    [TestCase(CausalReasonCode.ExportDeleteStaged, "Deleted", CausalityTone.Error, Icons.Material.Filled.Delete)]
    public void GetEventOperation_ExportedWithAResolvedReason_ReadsTheDecision(
        CausalReasonCode reasonCode, string label, CausalityTone tone, string icon)
    {
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.Exported, reasonCode);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.Label, Is.EqualTo(label));
            Assert.That(display.Tone, Is.EqualTo(tone));
            Assert.That(display.Icon, Is.EqualTo(icon));
        }
    }

    [Test]
    public void GetEventOperation_ExportedWithNoResolvedReason_IsNullRatherThanGuessed()
    {
        Assert.That(OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.Exported), Is.Null);
    }

    [Test]
    public void GetEventOperation_ExportedWithNotSetReason_IsNullRatherThanGuessed()
    {
        // Pre-edge history: the chain resolved a queueing cohort, but it predates reason codes.
        Assert.That(OutcomeDisplayMap.GetEventOperation(
            ActivityRunProfileExecutionItemSyncOutcomeType.Exported, CausalReasonCode.NotSet), Is.Null);
    }

    /// <summary>
    /// PendingExportCreated collapses Create and Update into one outcome type (unlike DeprovisionQueued,
    /// which gets its own type for Delete): the staged kind recorded on the outcome (#1561 follow-up) is
    /// what tells them apart, routed through the same queueing-decision vocabulary a chain's queueing
    /// edge uses.
    /// </summary>
    [TestCase(PendingExportChangeType.Create, "Created", CausalityTone.Success, Icons.Material.Filled.Add)]
    [TestCase(PendingExportChangeType.Update, "Updated", CausalityTone.Info, Icons.Material.Filled.Edit)]
    public void GetEventOperation_PendingExportCreatedWithStagedChangeType_ReadsTheStagedKind(
        PendingExportChangeType stagedChangeType, string label, CausalityTone tone, string icon)
    {
        var display = OutcomeDisplayMap.GetEventOperation(
            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, stagedChangeType: stagedChangeType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.Label, Is.EqualTo(label));
            Assert.That(display.Tone, Is.EqualTo(tone));
            Assert.That(display.Icon, Is.EqualTo(icon));
        }
    }

    /// <summary>
    /// An outcome recorded before this was captured carries no staged kind, and null must stay null
    /// rather than default to a guessed Create.
    /// </summary>
    [Test]
    public void GetEventOperation_PendingExportCreatedWithNoStagedChangeType_IsNullForLegacyOutcomes()
    {
        Assert.That(OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated), Is.Null);
    }

    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionCancelled)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor)]
    public void GetEventOperation_OutcomesWithNoObjectOperationOfTheirOwn_AreNull(
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        Assert.That(OutcomeDisplayMap.GetEventOperation(outcomeType), Is.Null);
    }

    /// <summary>
    /// Every Configuration Change Preview transition (#827): nothing happened, so no operation chip.
    /// A representative sample across the preview vocabulary, including the one (WouldStageDeleteExport)
    /// that describes the same export-side event as DeprovisionQueued's own delete operation, to prove
    /// the null rule is not merely an oversight for that one.
    /// </summary>
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinDifferentMetaverseObject)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProjecting)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldLeaveExportScope)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldEnterExportScope)]
    public void GetEventOperation_PreviewOutcomes_AreNullBecauseNothingHappened(
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        Assert.That(OutcomeDisplayMap.GetEventOperation(outcomeType), Is.Null);
    }

    [Test]
    public void GetEventOperation_UnknownOutcomeType_IsNullRatherThanGuessed()
    {
        Assert.That(OutcomeDisplayMap.GetEventOperation((ActivityRunProfileExecutionItemSyncOutcomeType)999), Is.Null);
    }
}
