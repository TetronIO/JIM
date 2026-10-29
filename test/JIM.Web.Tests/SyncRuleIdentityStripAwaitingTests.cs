// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Logic;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// What the identity strip on a Synchronisation Rule's page says the rule is waiting on (#2615): an import rule whose Object
/// Type has saved changes awaiting synchronisation, an export rule whose Metaverse Object Type has objects awaiting export
/// review. Each shows only while something is waiting, and only on the direction it is about.
/// </summary>
[TestFixture]
public class SyncRuleIdentityStripAwaitingTests : JimComponentTestContext
{
    private const string AwaitingChip = "[data-testid='jim-awaiting-synchronisation-chip']";
    private const string ExportReviewChip = "[data-testid='jim-export-review-pending-chip']";

    [Test]
    public void Render_ImportRuleWithChangesAwaiting_ShowsTheAwaitingChipLinkedToTheConnectedSystem()
    {
        var cut = RenderStrip(SyncRuleDirection.Import, awaiting: 2, exportReview: 0);

        var chip = cut.Find(AwaitingChip);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chip.GetAttribute("href"), Is.EqualTo("/admin/connected-systems/7"));
            Assert.That(cut.FindAll(ExportReviewChip), Is.Empty);
        }
    }

    [Test]
    public void Render_ImportRuleWithNothingAwaiting_ShowsNoChip()
    {
        var cut = RenderStrip(SyncRuleDirection.Import, awaiting: 0, exportReview: 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(AwaitingChip), Is.Empty);
            Assert.That(cut.FindAll(ExportReviewChip), Is.Empty);
        }
    }

    [Test]
    public void Render_ExportRuleWithObjectsAwaitingReview_ShowsTheCount()
    {
        var cut = RenderStrip(SyncRuleDirection.Export, awaiting: 0, exportReview: 1250);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find(ExportReviewChip).TextContent, Does.Contain("1,250"));
            Assert.That(cut.FindAll(AwaitingChip), Is.Empty);
        }
    }

    [Test]
    public void Render_ExportRuleWithNothingAwaitingReview_ShowsNoChip()
    {
        var cut = RenderStrip(SyncRuleDirection.Export, awaiting: 0, exportReview: 0);

        Assert.That(cut.FindAll(ExportReviewChip), Is.Empty);
    }

    [Test]
    public void Render_ExportRule_IgnoresAChangeCountThatIsAnImportRulesToCarry()
    {
        var cut = RenderStrip(SyncRuleDirection.Export, awaiting: 3, exportReview: 0);

        Assert.That(cut.FindAll(AwaitingChip), Is.Empty);
    }

    [Test]
    public void Render_ImportRule_IgnoresAnExportReviewCountThatIsAnExportRulesToCarry()
    {
        var cut = RenderStrip(SyncRuleDirection.Import, awaiting: 0, exportReview: 9);

        Assert.That(cut.FindAll(ExportReviewChip), Is.Empty);
    }

    private IRenderedComponent<SyncRuleIdentityStrip> RenderStrip(SyncRuleDirection direction, int awaiting, int exportReview) =>
        Render<SyncRuleIdentityStrip>(p => p
            .Add(c => c.Direction, direction)
            .Add(c => c.MetaverseObjectTypeName, "User")
            .Add(c => c.ConnectedSystemObjectTypeName, "inetOrgPerson")
            .Add(c => c.ConnectedSystemId, 7)
            .Add(c => c.ConnectedSystemName, "Corporate Directory")
            .Add(c => c.AwaitingSynchronisationChangeCount, awaiting)
            .Add(c => c.ExportReviewPendingCount, exportReview));
}
