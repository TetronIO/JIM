// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Tests for <see cref="OutcomeDisplayMap.GetExportDecision"/> (#1495): the decision-aware Exported
/// captions, keyed on the queueing edge's reason code because that is the only durable copy of the
/// create/update/delete decision once the Pending Export row is gone.
/// </summary>
[TestFixture]
public class OutcomeDisplayMapExportDecisionTests
{
    [Test]
    public void GetExportDecision_CreateStaged_ReadsRecordCreated()
    {
        var display = OutcomeDisplayMap.GetExportDecision(CausalReasonCode.ExportCreateStaged);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display.PlainLabel, Is.EqualTo("Record created"));
            Assert.That(display.TechnicalLabel, Is.EqualTo("CSO Exported (Create)"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Success));
        }
    }

    [Test]
    public void GetExportDecision_UpdateStaged_ReadsChangesApplied()
    {
        var display = OutcomeDisplayMap.GetExportDecision(CausalReasonCode.ExportUpdateStaged);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display.PlainLabel, Is.EqualTo("Changes applied"));
            Assert.That(display.TechnicalLabel, Is.EqualTo("CSO Exported (Update)"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Info));
        }
    }

    [Test]
    public void GetExportDecision_DeleteStaged_ReadsRecordDeleted()
    {
        var display = OutcomeDisplayMap.GetExportDecision(CausalReasonCode.ExportDeleteStaged);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display.PlainLabel, Is.EqualTo("Record deleted"));
            Assert.That(display.TechnicalLabel, Is.EqualTo("CSO Exported (Delete)"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Error));
        }
    }

    [Test]
    public void GetExportDecision_NonExportReason_FallsBackToTheBareExportedMapping()
    {
        var display = OutcomeDisplayMap.GetExportDecision(CausalReasonCode.NotSet);

        Assert.That(display, Is.EqualTo(OutcomeDisplayMap.Get(
            ActivityRunProfileExecutionItemSyncOutcomeType.Exported)));
    }
}
