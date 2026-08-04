// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Preview;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Components.Tests;

/// <summary>
/// The dialog offering the informed choice before a large Configuration Change Preview runs (#827, PRD scenario 5).
///
/// What it owns is the framing. A dialog that simply asked "full or capped?" would be answered by whoever guessed
/// hardest, so the size has to be on screen; and because both answers are legitimate, it must state a cost rather
/// than issue a warning. The one thing it must never do is leave the recommendation ambiguous: an administrator who
/// clicks through without reading should land on the capped option, which is right for all but the largest previews.
/// </summary>
[TestFixture]
public class PreviewDataSetSizeDialogTests : JimComponentTestContext
{
    private const string ConfirmButtonMarker = "jim-preview-size-confirm";

    [Test]
    public void Dialog_LargePreview_StatesTheRowCountAndTheStorageItWouldUse()
    {
        var provider = ShowDialog(250_000);

        Assert.Multiple(() =>
        {
            Assert.That(provider.Markup, Does.Contain("250,000"), "a choice offered without a size is not informed");
            Assert.That(provider.Markup, Does.Contain("MB").Or.Contain("GB"),
                "storage is the cost the administrator is actually being asked to accept");
        });
    }

    [Test]
    public void Dialog_VeryLargePreview_ScalesTheStorageUnitRatherThanPrintingSixDigitsOfMegabytes()
    {
        var provider = ShowDialog(5_000_000);

        Assert.That(provider.Markup, Does.Contain("GB"));
    }

    [Test]
    public void Dialog_AsShown_RecommendsTheCappedOptionAndSaysSummaryCountsAreUnaffected()
    {
        var provider = ShowDialog(250_000);

        Assert.Multiple(() =>
        {
            Assert.That(provider.Markup, Does.Contain("recommended"),
                "both answers are legitimate, so the dialog has to say which one it would pick");
            Assert.That(provider.Markup, Does.Contain("exact"),
                "an administrator must not think capping would under-report what the change does");
        });
    }

    [Test]
    public async Task Dialog_ConfirmedWithoutChangingAnything_ReturnsTheCappedRecommendationAsync()
    {
        var provider = ShowDialog(250_000);
        var result = LastResult!;

        provider.Find($"[data-testid='{ConfirmButtonMarker}']").Click();

        var completed = await result;
        Assert.Multiple(() =>
        {
            Assert.That(completed!.Canceled, Is.False);
            Assert.That(completed.Data, Is.EqualTo(ConfigurationChangePreviewDeltaPersistence.Capped));
        });
    }

    [Test]
    public async Task Dialog_Cancelled_ReturnsNoChoiceRatherThanTheRecommendationAsync()
    {
        // Backing out means the preview does not run. Returning the recommendation here would spend the cost the
        // administrator had just declined.
        var provider = ShowDialog(250_000);
        var result = LastResult!;

        provider.FindAll("button").First(b => b.TextContent.Contains("Cancel")).Click();

        var completed = await result;
        Assert.Multiple(() =>
        {
            Assert.That(completed!.Canceled, Is.True);
            Assert.That(completed.Data, Is.Null);
        });
    }

    private Task<DialogResult?>? LastResult { get; set; }

    private IRenderedComponent<MudDialogProvider> ShowDialog(long estimatedDeltaRows)
    {
        var parameters = new DialogParameters<PreviewDataSetSizeDialog>
        {
            { x => x.EstimatedDeltaRows, estimatedDeltaRows }
        };

        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        provider.InvokeAsync(async () =>
        {
            var reference = await dialogService.ShowAsync<PreviewDataSetSizeDialog>("Preview detail", parameters);
            LastResult = reference.Result;
        });
        provider.WaitForElement($"[data-testid='{ConfirmButtonMarker}']");

        return provider;
    }
}
