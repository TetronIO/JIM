// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Web.Causality;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Tests for <see cref="OutcomeDisplayMap.GetHopOperation"/> (#1495 follow-up): the tone-tinted operation
/// chip every chain-hop card carries, derived from data the cohort already holds. Checked in the order the
/// hazard on <see cref="CausalChainCohort"/> requires: <see cref="CausalChainCohort.MetaverseChangeType"/>
/// before <see cref="CausalChainCohort.SourceImportChangeType"/> before the edge type, because a derived
/// cohort's <see cref="CausalChainCohort.EdgeType"/> defaults to 0
/// (<see cref="CausalEdgeType.MetaverseObjectDeletionCausedDeprovision"/>).
/// </summary>
[TestFixture]
public class OutcomeDisplayMapHopOperationTests
{
    [Test]
    public void GetHopOperation_MetaverseChangeTypeProjected_ReadsCreatedSuccess()
    {
        // Deliberate behaviour change (#1495 second follow-up): Projected used to chip Primary/AirlineStops,
        // the only operation with a look of its own. Every "Created" verb now shares one tone and icon
        // (Success/Add) so a column scans on colour alone; the technical label is unchanged.
        var cohort = new CausalChainCohort { MetaverseChangeType = ObjectChangeType.Projected };

        var display = OutcomeDisplayMap.GetHopOperation(cohort);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.PlainLabel, Is.EqualTo("Created"));
            Assert.That(display.TechnicalLabel, Is.EqualTo("MVO Projected"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Success));
            Assert.That(display.Icon, Is.EqualTo(Icons.Material.Filled.Add));
        }
    }

    [Test]
    public void GetHopOperation_MetaverseChangeTypeJoined_ReadsJoinedSecondary()
    {
        var cohort = new CausalChainCohort { MetaverseChangeType = ObjectChangeType.Joined };

        var display = OutcomeDisplayMap.GetHopOperation(cohort);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.PlainLabel, Is.EqualTo("Joined"));
            Assert.That(display.TechnicalLabel, Is.EqualTo("CSO Joined"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Secondary));
        }
    }

    [Test]
    public void GetHopOperation_MetaverseChangeTypeCreated_ReadsCreatedSuccess()
    {
        var cohort = new CausalChainCohort { MetaverseChangeType = ObjectChangeType.Created };

        var display = OutcomeDisplayMap.GetHopOperation(cohort);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.PlainLabel, Is.EqualTo("Created"));
            Assert.That(display.TechnicalLabel, Is.EqualTo("MVO Created"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Success));
        }
    }

    [TestCase(ObjectChangeType.Added, "Created", "CSO Added", CausalityTone.Success)]
    [TestCase(ObjectChangeType.Updated, "Updated", "CSO Updated", CausalityTone.Info)]
    [TestCase(ObjectChangeType.Deleted, "Deleted", "CSO Deleted", CausalityTone.Error)]
    public void GetHopOperation_SourceImportChangeType_ReadsTheImportOutcome(
        ObjectChangeType changeType, string plainLabel, string technicalLabel, CausalityTone tone)
    {
        var cohort = new CausalChainCohort { SourceImportChangeType = changeType };

        var display = OutcomeDisplayMap.GetHopOperation(cohort);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.PlainLabel, Is.EqualTo(plainLabel));
            Assert.That(display.TechnicalLabel, Is.EqualTo(technicalLabel));
            Assert.That(display.Tone, Is.EqualTo(tone));
        }
    }

    // ExportCreateStaged's icon is a deliberate behaviour change (#1495 second follow-up): it used to chip
    // AddCircle; every "Created" verb now shares Add so a column scans on colour alone.
    [TestCase(CausalReasonCode.ExportCreateStaged, "Created", "Export Staged (Create)", CausalityTone.Success, Icons.Material.Filled.Add)]
    [TestCase(CausalReasonCode.ExportUpdateStaged, "Updated", "Export Staged (Update)", CausalityTone.Info, Icons.Material.Filled.Edit)]
    [TestCase(CausalReasonCode.ExportDeleteStaged, "Deleted", "Export Staged (Delete)", CausalityTone.Error, Icons.Material.Filled.Delete)]
    public void GetHopOperation_QueueingEdgeWithADecision_ReadsTheDecision(
        CausalReasonCode reasonCode, string plainLabel, string technicalLabel, CausalityTone tone, string icon)
    {
        var cohort = new CausalChainCohort
        {
            EdgeType = CausalEdgeType.PendingExportQueueingCausedExportExecution,
            ReasonCode = reasonCode
        };

        var display = OutcomeDisplayMap.GetHopOperation(cohort);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.PlainLabel, Is.EqualTo(plainLabel));
            Assert.That(display.TechnicalLabel, Is.EqualTo(technicalLabel));
            Assert.That(display.Tone, Is.EqualTo(tone));
            Assert.That(display.Icon, Is.EqualTo(icon));
        }
    }

    [TestCase(CausalEdgeType.MetaverseObjectDeletionCausedDeprovision)]
    [TestCase(CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval)]
    public void GetHopOperation_MvoDeletionEdges_ReadDeletedError(CausalEdgeType edgeType)
    {
        // Deliberate behaviour change (#1495 second follow-up): this edge used to chip PersonRemove; every
        // "Deleted" verb now shares Error/Delete so a column scans on colour alone.
        var cohort = new CausalChainCohort { EdgeType = edgeType };

        var display = OutcomeDisplayMap.GetHopOperation(cohort);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display!.PlainLabel, Is.EqualTo("Deleted"));
            Assert.That(display.TechnicalLabel, Is.EqualTo("MVO Deleted"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Error));
            Assert.That(display.Icon, Is.EqualTo(Icons.Material.Filled.Delete));
        }
    }

    [Test]
    public void GetHopOperation_ExportConfirmation_IsNullBecauseAConfirmationIsNotAnObjectOperation()
    {
        var cohort = new CausalChainCohort { EdgeType = CausalEdgeType.ExportCausedImportConfirmation };

        Assert.That(OutcomeDisplayMap.GetHopOperation(cohort), Is.Null);
    }

    [Test]
    public void GetHopOperation_QueueingEdgeWithNoReasonCode_IsNullRatherThanGuessed()
    {
        var cohort = new CausalChainCohort
        {
            EdgeType = CausalEdgeType.PendingExportQueueingCausedExportExecution,
            ReasonCode = CausalReasonCode.NotSet
        };

        Assert.That(OutcomeDisplayMap.GetHopOperation(cohort), Is.Null);
    }

    [Test]
    public void GetHopOperation_UnknownEdgeType_IsNullRatherThanGuessed()
    {
        var cohort = new CausalChainCohort { EdgeType = (CausalEdgeType)99 };

        Assert.That(OutcomeDisplayMap.GetHopOperation(cohort), Is.Null);
    }
}
