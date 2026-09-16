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
    public void GetExportDecision_CreateStaged_ReadsConnectedSystemObjectCreated()
    {
        var display = OutcomeDisplayMap.GetExportDecision(CausalReasonCode.ExportCreateStaged);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display.Label, Is.EqualTo("Connected System Object created"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Success));
        }
    }

    [Test]
    public void GetExportDecision_UpdateStaged_ReadsChangesApplied()
    {
        var display = OutcomeDisplayMap.GetExportDecision(CausalReasonCode.ExportUpdateStaged);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display.Label, Is.EqualTo("Changes applied"));
            Assert.That(display.Tone, Is.EqualTo(CausalityTone.Info));
        }
    }

    [Test]
    public void GetExportDecision_DeleteStaged_ReadsConnectedSystemObjectDeleted()
    {
        var display = OutcomeDisplayMap.GetExportDecision(CausalReasonCode.ExportDeleteStaged);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(display.Label, Is.EqualTo("Connected System Object deleted"));
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
