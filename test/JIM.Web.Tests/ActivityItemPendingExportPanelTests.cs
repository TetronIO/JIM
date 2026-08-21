// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Pending Export tab's content (#1223), extracted from the execution item detail page when that page
/// gained tabs. The four opening sentences are the point of this component: the same Pending Export means
/// "staged and waiting" on a clean item and "failed after retries" on a failed one, and the table beneath
/// looks identical in both cases.
/// </summary>
[TestFixture]
public class ActivityItemPendingExportPanelTests : JimComponentTestContext
{
    private static PendingExport Export(PendingExportChangeType changeType = PendingExportChangeType.Update)
    {
        return new PendingExport
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            ChangeType = changeType,
            Status = PendingExportStatus.Pending,
            ConnectedSystemId = 4,
            MaxRetries = 5,
            CreatedAt = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc)
        };
    }

    private IRenderedComponent<ActivityItemPendingExportPanel> RenderPanel(
        PendingExport export,
        ActivityRunProfileExecutionItemErrorType errorType = ActivityRunProfileExecutionItemErrorType.NotSet,
        ObjectChangeType changeType = ObjectChangeType.PendingExport,
        int? connectedSystemId = 4)
    {
        return Render<ActivityItemPendingExportPanel>(p => p
            .Add(c => c.PendingExport, export)
            .Add(c => c.ItemErrorType, errorType)
            .Add(c => c.ItemObjectChangeType, changeType)
            .Add(c => c.ConnectedSystemId, connectedSystemId));
    }

    [Test]
    public void Render_StagedDeleteOnACleanItem_SaysTheObjectIsStagedForDeletion()
    {
        var cut = RenderPanel(Export(PendingExportChangeType.Delete));

        Assert.That(cut.Markup, Does.Contain("staged for deletion in the Connected System"));
    }

    [Test]
    public void Render_QueuedChangesOnACleanItem_SaysTheyAreAwaitingExport()
    {
        var cut = RenderPanel(Export());

        Assert.That(cut.Markup, Does.Contain("staged and awaiting export"));
    }

    [Test]
    public void Render_ExportNotConfirmed_SaysTheyWillBeRetried()
    {
        var cut = RenderPanel(Export(), ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed);

        Assert.That(cut.Markup, Does.Contain("have not yet been confirmed"));
    }

    [Test]
    public void Render_ExportFailed_SaysItMayNeedManualIntervention()
    {
        var cut = RenderPanel(Export(), ActivityRunProfileExecutionItemErrorType.ExportConfirmationFailed);

        Assert.That(cut.Markup, Does.Contain("may require manual"));
    }

    [Test]
    public void Render_StagedDeleteOnAFailedItem_DoesNotClaimItIsMerelyStaged()
    {
        // The delete branch is guarded on the item being clean; a failed delete is a failure first.
        var cut = RenderPanel(Export(PendingExportChangeType.Delete),
            ActivityRunProfileExecutionItemErrorType.ExportConfirmationFailed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Not.Contain("staged for deletion in the Connected System"));
            Assert.That(cut.Markup, Does.Contain("may require manual"));
        }
    }

    [Test]
    public void Render_WithAConnectedSystem_LinksToTheQueueEntryItself()
    {
        var export = Export();

        var cut = RenderPanel(export);

        Assert.That(cut.Find($"a[href='/admin/connected-systems/4/pending-exports/{export.Id}']"), Is.Not.Null);
    }

    [Test]
    public void Render_WithoutAConnectedSystem_OmitsTheLinkRatherThanBuildingABrokenOne()
    {
        var cut = RenderPanel(Export(), connectedSystemId: null);

        // Pending Export lookups are scoped by both the Connected System id and the export id, so a link
        // built without the system would 404.
        Assert.That(cut.FindAll("a[href*='pending-exports']"), Is.Empty);
    }

    [Test]
    public void Render_WithNoAttributeChanges_OmitsTheChangesTable()
    {
        var cut = RenderPanel(Export());

        Assert.That(cut.Markup, Does.Not.Contain("Pending Attribute Changes"));
    }

    [Test]
    public void Render_WithAttributeChanges_CountsThemInTheHeading()
    {
        var export = Export();
        export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            AttributeId = 7,
            ChangeType = PendingExportAttributeChangeType.Add,
            Status = PendingExportAttributeChangeStatus.Pending,
            StringValue = "CN=Tina Adams,OU=Users,DC=panoply,DC=local"
        });

        var cut = RenderPanel(export);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Pending Attribute Changes (1)"));
            Assert.That(cut.Markup, Does.Contain("CN=Tina Adams"));
        }
    }

    [Test]
    public void Render_RetriesExhausted_SaysSoRatherThanLeavingTheReaderToCompareTwoNumbers()
    {
        var export = Export();
        export.ErrorCount = 5;

        var cut = RenderPanel(export, ActivityRunProfileExecutionItemErrorType.ExportConfirmationFailed);

        Assert.That(cut.Markup, Does.Contain("Max Reached"));
    }
}
