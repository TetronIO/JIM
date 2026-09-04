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
        // (Success/Add) so a column scans on colour alone; the technical label is unchanged.
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.Projected);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.PlainLabel, Is.EqualTo("Created"));
            Assert.That(display.TechnicalLabel, Is.EqualTo("MVO Projected"));
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
            Assert.That(display!.PlainLabel, Is.EqualTo("Joined"));
            Assert.That(display.TechnicalLabel, Is.EqualTo("CSO Joined"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Secondary));
        }
    }

    [Test]
    public void GetEventOperation_CsoAdded_ReadsCreatedSuccess()
    {
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.PlainLabel, Is.EqualTo("Created"));
            Assert.That(display.TechnicalLabel, Is.EqualTo("CSO Added"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Success));
        }
    }

    [Test]
    public void GetEventOperation_CsoUpdated_ReadsUpdatedInfo()
    {
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.PlainLabel, Is.EqualTo("Updated"));
            Assert.That(display.TechnicalLabel, Is.EqualTo("CSO Updated"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Info));
        }
    }

    [Test]
    public void GetEventOperation_CsoDeleted_ReadsDeletedError()
    {
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.PlainLabel, Is.EqualTo("Deleted"));
            Assert.That(display.TechnicalLabel, Is.EqualTo("CSO Deleted"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Error));
        }
    }

    [Test]
    public void GetEventOperation_AttributeFlow_ReadsUpdatedInfoWithTheMapsTechnicalLabel()
    {
        // "MVO Attribute Flow" is the technical label OutcomeDisplayMap.Get already uses for this
        // outcome's own title; the chip stays consistent with that vocabulary rather than inventing
        // a second one.
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.PlainLabel, Is.EqualTo("Updated"));
            Assert.That(display.TechnicalLabel, Is.EqualTo("MVO Attribute Flow"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Info));
        }
    }

    [Test]
    public void GetEventOperation_DriftCorrection_ReadsUpdatedInfoWithTheMapsTechnicalLabel()
    {
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.PlainLabel, Is.EqualTo("Updated"));
            Assert.That(display.TechnicalLabel, Is.EqualTo("CSO Drift Corrected"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Info));
        }
    }

    [Test]
    public void GetEventOperation_Provisioned_ReadsCreatedSuccessWithTheMapsTechnicalLabel()
    {
        // Deliberate behaviour change (#1495 second follow-up): Provisioned used to chip its own AddCircle
        // icon; every "Created" verb now shares Success/Add so a column scans on colour alone.
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.PlainLabel, Is.EqualTo("Created"));
            Assert.That(display.TechnicalLabel, Is.EqualTo("CSO Provisioned"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Success));
            Assert.That(display.Icon, Is.EqualTo(Icons.Material.Filled.Add));
        }
    }

    [Test]
    public void GetEventOperation_MvoDeleted_ReadsDeletedErrorWithTheMapsTechnicalLabel()
    {
        // Deliberate behaviour change (#1495 second follow-up): MvoDeleted used to chip PersonRemove; every
        // "Deleted" verb now shares Error/Delete so a column scans on colour alone.
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.PlainLabel, Is.EqualTo("Deleted"));
            Assert.That(display.TechnicalLabel, Is.EqualTo("MVO Deleted"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Error));
            Assert.That(display.Icon, Is.EqualTo(Icons.Material.Filled.Delete));
        }
    }

    [Test]
    public void GetEventOperation_Deprovisioned_ReadsDeletedErrorWithTheMapsTechnicalLabel()
    {
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.PlainLabel, Is.EqualTo("Deleted"));
            Assert.That(display.TechnicalLabel, Is.EqualTo("CSO Deprovisioned"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Error));
        }
    }

    [Test]
    public void GetEventOperation_DeprovisionQueued_ReadsDeletedAsAStagedDeletePrecedent()
    {
        // A queued deprovision is a staged delete: the chain's own "Export Staged (Delete)" chip is
        // the precedent for how a staged (not-yet-executed) kind is marked.
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.PlainLabel, Is.EqualTo("Deleted"));
            Assert.That(display.TechnicalLabel, Is.EqualTo("Export Staged (Delete)"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Error));
        }
    }

    // ExportCreateStaged's icon is a deliberate behaviour change (#1495 second follow-up): it used to chip
    // AddCircle; every "Created" verb now shares Add so a column scans on colour alone.
    [TestCase(CausalReasonCode.ExportCreateStaged, "Created", "Export Staged (Create)", CausalityTone.Success, Icons.Material.Filled.Add)]
    [TestCase(CausalReasonCode.ExportUpdateStaged, "Updated", "Export Staged (Update)", CausalityTone.Info, Icons.Material.Filled.Edit)]
    [TestCase(CausalReasonCode.ExportDeleteStaged, "Deleted", "Export Staged (Delete)", CausalityTone.Error, Icons.Material.Filled.Delete)]
    public void GetEventOperation_ExportedWithAResolvedReason_ReadsTheDecision(
        CausalReasonCode reasonCode, string plainLabel, string technicalLabel, CausalityTone tone, string icon)
    {
        var display = OutcomeDisplayMap.GetEventOperation(ActivityRunProfileExecutionItemSyncOutcomeType.Exported, reasonCode);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.PlainLabel, Is.EqualTo(plainLabel));
            Assert.That(display.TechnicalLabel, Is.EqualTo(technicalLabel));
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
    /// edge uses (Export Staged (Create)/(Update)).
    /// </summary>
    [TestCase(PendingExportChangeType.Create, "Created", "Export Staged (Create)", CausalityTone.Success, Icons.Material.Filled.Add)]
    [TestCase(PendingExportChangeType.Update, "Updated", "Export Staged (Update)", CausalityTone.Info, Icons.Material.Filled.Edit)]
    public void GetEventOperation_PendingExportCreatedWithStagedChangeType_ReadsTheStagedKind(
        PendingExportChangeType stagedChangeType, string plainLabel, string technicalLabel, CausalityTone tone, string icon)
    {
        var display = OutcomeDisplayMap.GetEventOperation(
            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, stagedChangeType: stagedChangeType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.PlainLabel, Is.EqualTo(plainLabel));
            Assert.That(display.TechnicalLabel, Is.EqualTo(technicalLabel));
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
