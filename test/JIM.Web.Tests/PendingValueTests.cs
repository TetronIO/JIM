// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Transactional;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers PendingValue: a value a Pending Export will write, shown in place of one the Connected System
/// Object does not hold yet. The tooltip is the part with logic. It used to say "awaiting confirmation"
/// for every pending value, which is untrue of a Create that has not been exported at all; an
/// administrator reading it would go looking for a confirming import that has nothing to confirm.
/// </summary>
[TestFixture]
public class PendingValueTests : JimComponentTestContext
{
    [Test]
    public void PendingValue_RendersTheValue()
    {
        var cut = Render<PendingValue>(p => p
            .Add(c => c.Value, "oscar.harper18")
            .Add(c => c.Status, PendingExportStatus.Pending));

        Assert.That(cut.Find(".jim-pending-value").TextContent, Does.Contain("oscar.harper18"));
    }

    [TestCase(PendingExportStatus.Pending)]
    [TestCase(PendingExportStatus.Executing)]
    public void PendingValue_ExportNotYetSent_SaysItIsStagedNotAwaitingConfirmation(PendingExportStatus status)
    {
        var cut = Render<PendingValue>(p => p
            .Add(c => c.Value, "oscar.harper18")
            .Add(c => c.Status, status));

        Assert.That(cut.FindComponent<MudTooltip>().Instance.Text, Is.EqualTo(PendingValue.NotYetSentTooltip));
    }

    [TestCase(PendingExportStatus.Exported)]
    [TestCase(PendingExportStatus.ExportNotConfirmed)]
    public void PendingValue_ExportSent_SaysItAwaitsConfirmationByImport(PendingExportStatus status)
    {
        var cut = Render<PendingValue>(p => p
            .Add(c => c.Value, "oscar.harper18")
            .Add(c => c.Status, status));

        Assert.That(cut.FindComponent<MudTooltip>().Instance.Text, Is.EqualTo(PendingValue.AwaitingConfirmationTooltip));
    }

    [Test]
    public void PendingValue_ExportFailed_SaysSoRatherThanImplyingItIsStillOnItsWay()
    {
        var cut = Render<PendingValue>(p => p
            .Add(c => c.Value, "oscar.harper18")
            .Add(c => c.Status, PendingExportStatus.Failed));

        Assert.That(cut.FindComponent<MudTooltip>().Instance.Text, Is.EqualTo(PendingValue.FailedTooltip));
    }

    [Test]
    public void PendingValue_StatusUnknown_MakesNoClaimAboutWhetherItWasSent()
    {
        var cut = Render<PendingValue>(p => p.Add(c => c.Value, "oscar.harper18"));

        Assert.That(cut.FindComponent<MudTooltip>().Instance.Text, Is.EqualTo(PendingValue.UnknownStateTooltip));
    }

    /// <summary>
    /// Unique Value Generation (#242, release 4): a Parked export is waiting on an administrator's
    /// decision about its generated value, not on a confirming import, so it needs its own tooltip
    /// rather than falling into the "awaiting confirmation" wording other held-back statuses share.
    /// </summary>
    [Test]
    public void PendingValue_ExportParked_SaysItIsWaitingForADecision()
    {
        var cut = Render<PendingValue>(p => p
            .Add(c => c.Value, "oscar.harper18")
            .Add(c => c.Status, PendingExportStatus.Parked));

        Assert.That(cut.FindComponent<MudTooltip>().Instance.Text, Is.EqualTo(PendingValue.ParkedTooltip));
    }

    [Test]
    public void Tooltips_NameTheDomainConcept()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PendingValue.NotYetSentTooltip, Is.EqualTo("Staged for export, not yet sent"));
            Assert.That(PendingValue.AwaitingConfirmationTooltip, Is.EqualTo("Exported, awaiting confirmation by import"));
            Assert.That(PendingValue.FailedTooltip, Is.EqualTo("Export failed, value not written"));
            Assert.That(PendingValue.ParkedTooltip, Is.EqualTo("Parked: waiting for an administrator's decision on its generated value"));
            Assert.That(PendingValue.UnknownStateTooltip, Is.EqualTo("Value from a Pending Export"));
        }
    }
}
