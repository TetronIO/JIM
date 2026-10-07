// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Application.Interfaces;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;

namespace JIM.Web;

/// <summary>
/// The two actions on a held generated value (Unique Value Generation, #242, release 4, Phase 9), shared by the Operations
/// tab and the Metaverse Object's banner so both confirm, act and report alike. Each returns whether anything changed, so
/// the caller knows to re-read.
/// </summary>
public static class GeneratedValueDecisionActions
{
    /// <summary>
    /// "Allow the rename…": opens the confirmation, which names every system that will change and performs the action.
    /// </summary>
    public static async Task<bool> AllowRenameAsync(IDialogService dialogService, Guid assignmentId)
    {
        ArgumentNullException.ThrowIfNull(dialogService);

        var parameters = new DialogParameters<AllowGeneratedValueRenameDialog> { { x => x.AssignmentId, assignmentId } };
        var dialog = await dialogService.ShowAsync<AllowGeneratedValueRenameDialog>(null, parameters,
            new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small, FullWidth = true });
        var result = await dialog.Result;
        return result is { Canceled: false, Data: true };
    }

    /// <summary>
    /// "Try again" for one value: releases its held export so the next export run tries the same value. No confirmation:
    /// nothing is renamed, and a value rejected again simply comes back.
    /// </summary>
    public static async Task<bool> TryAgainAsync(IJimApplicationFactory jimFactory, ISnackbar snackbar,
        Task<AuthenticationState>? authenticationStateTask, Guid assignmentId)
    {
        ArgumentNullException.ThrowIfNull(jimFactory);
        ArgumentNullException.ThrowIfNull(snackbar);

        using var jim = jimFactory.Create();
        var user = await Helpers.GetUserAsync(jim, authenticationStateTask);
        var outcome = await jim.GeneratedValueDecisions.TryAgainAsync(assignmentId, user, null);
        var done = outcome == GeneratedValueDecisionActionOutcome.Done;
        snackbar.Add(done
                ? "Released. The next export run tries the same value again."
                : "Nothing was changed; the value is no longer waiting on a decision.",
            done ? Severity.Success : Severity.Info);
        return done;
    }

    /// <summary>
    /// "Try again" for every value <paramref name="filter"/> matches that needs a decision, after a confirmation naming how
    /// many. Zero is reported rather than passed over in silence.
    /// </summary>
    public static async Task<bool> TryAgainAllAsync(IJimApplicationFactory jimFactory, IDialogService dialogService, ISnackbar snackbar,
        Task<AuthenticationState>? authenticationStateTask, GeneratedValueDecisionFilter filter, int count)
    {
        ArgumentNullException.ThrowIfNull(jimFactory);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(snackbar);
        ArgumentNullException.ThrowIfNull(filter);

        var confirmed = await dialogService.ShowMessageBoxAsync(
            "Try again",
            $"Release {Describe(count)} so the next export run tries the same value again? Any the target rejects again come back here.",
            yesText: "Try again",
            cancelText: "Cancel");
        if (confirmed != true)
            return false;

        using var jim = jimFactory.Create();
        var user = await Helpers.GetUserAsync(jim, authenticationStateTask);
        var released = await jim.GeneratedValueDecisions.TryAgainAsync(filter, user, null);
        snackbar.Add(released == 0
                ? "Nothing was released; no value matching these filters needs a decision."
                : $"{Describe(released)} released. The next export run tries them again.",
            released == 0 ? Severity.Info : Severity.Success);
        return released > 0;
    }

    private static string Describe(int count) => count == 1 ? "1 held value" : $"{count:N0} held values";
}
