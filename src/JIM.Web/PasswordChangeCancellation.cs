// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Models;
using MudBlazor;

namespace JIM.Web;

/// <summary>
/// The question asked before a person's queued password change is cancelled for one Connected System from a page other
/// than Operations > Passwords (the identity's Password panel, and Stop trying in the set-password dialog), so every
/// place that can cancel a Connected System Object's first password says what that leaves behind (#2160).
/// </summary>
public static class PasswordChangeCancellation
{
    /// <summary>
    /// The person's queued change for the Connected System, if there is one, read fresh: what the page showed may be
    /// older than the change a dialog has just queued.
    /// </summary>
    public static async Task<PendingPasswordChangeHeader?> FindQueuedAsync(JimApplication jim, Guid metaverseObjectId, int connectedSystemId)
    {
        ArgumentNullException.ThrowIfNull(jim);

        var queued = await jim.PasswordSynchronisation.GetPendingPasswordChangesAsync(
            new PendingPasswordChangeFilter { MetaverseObjectId = metaverseObjectId, ConnectedSystemId = connectedSystemId },
            startIndex: 0,
            count: 1,
            sortBy: "queued",
            sortDescending: false,
            includeTotalCount: false);
        return queued.Results.FirstOrDefault();
    }

    /// <summary>
    /// Whether to go ahead: asks first where cancelling leaves a Connected System Object without its first password,
    /// and answers true at once for an ordinary change, or where nothing is queued, which stops as it always has.
    /// </summary>
    public static async Task<bool> ConfirmAsync(IDialogService dialogService, PendingPasswordChangeHeader? queued)
    {
        ArgumentNullException.ThrowIfNull(dialogService);

        if (queued == null || !PendingPasswordChangeDisplay.OwesFirstPassword(queued))
            return true;

        var confirmed = await dialogService.ShowMessageBoxAsync("Cancel password change",
            PendingPasswordChangeDisplay.CancelConfirmation(queued), yesText: "Cancel delivery", cancelText: "Close");
        return confirmed == true;
    }
}
