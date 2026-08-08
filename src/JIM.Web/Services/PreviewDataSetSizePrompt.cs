// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;
using JIM.Web.Shared;
using MudBlazor;

namespace JIM.Web.Services;

/// <summary>
/// Shows <see cref="PreviewDataSetSizeDialog"/>. Deliberately nothing but the adapter between the seam and the
/// dialog: every decision about whether to ask, and what to do with the answer, belongs to
/// <see cref="ConfigurationChangePreviewStarter"/> where it can be tested.
/// </summary>
public class PreviewDataSetSizePrompt(IDialogService dialogService) : IPreviewDataSetSizePrompt
{
    private readonly IDialogService _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

    public async Task<ConfigurationChangePreviewDeltaPersistence?> AskAsync(long estimatedDeltaRows)
    {
        var parameters = new DialogParameters<PreviewDataSetSizeDialog>
        {
            { x => x.EstimatedDeltaRows, estimatedDeltaRows }
        };

        var dialog = await _dialogService.ShowAsync<PreviewDataSetSizeDialog>("How much detail should this preview keep?", parameters);
        var result = await dialog.Result;

        // Cancelled, dismissed, or closed without a value: all of them mean the administrator did not agree to run
        // this preview, and none of them may be read as accepting the recommendation.
        return result is { Canceled: false, Data: ConfigurationChangePreviewDeltaPersistence choice } ? choice : null;
    }
}
