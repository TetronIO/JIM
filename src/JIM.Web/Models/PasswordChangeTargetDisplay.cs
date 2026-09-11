// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Transactional.DTOs;
using MudBlazor;

namespace JIM.Web.Models;

/// <summary>
/// How one target's delivery outcome reads on screen (#1635): the icon and colour beside the Connected System's
/// name, and the sentence beneath it.
/// <para>
/// One helper rather than a switch inside the Set Password dialog, for the reason
/// <see cref="PendingPasswordChangeDisplay"/> exists: the dialog's result stage and the REST response describe the
/// same outcomes, and two surfaces describing the same state in different words would read as two different
/// problems.
/// </para>
/// </summary>
public static class PasswordChangeTargetDisplay
{
    /// <summary>
    /// The states nothing will move out of without a person: the target refused the password or JIM gave up on it,
    /// or the change outlived its time to live. Retrying is deliberately not one of them; JIM is still on it.
    /// </summary>
    public static bool IsFailure(PasswordChangeTargetState state) =>
        state is PasswordChangeTargetState.Parked or PasswordChangeTargetState.Expired;

    /// <summary>
    /// The states a caller may still be waiting on.
    /// </summary>
    public static bool IsInFlight(PasswordChangeTargetState state) =>
        state is PasswordChangeTargetState.Queued or PasswordChangeTargetState.Delivering;

    public static string Icon(PasswordChangeTargetState state) => state switch
    {
        PasswordChangeTargetState.Set => Icons.Material.Filled.Check,
        PasswordChangeTargetState.Delivering => Icons.Material.Filled.Sync,
        PasswordChangeTargetState.Retrying => Icons.Material.Filled.Replay,
        PasswordChangeTargetState.Parked => Icons.Material.Filled.Close,
        PasswordChangeTargetState.Held => Icons.Material.Filled.PauseCircleOutline,
        PasswordChangeTargetState.Expired => Icons.Material.Filled.TimerOff,
        PasswordChangeTargetState.Cancelled => Icons.Material.Filled.CancelScheduleSend,
        _ => Icons.Material.Filled.Schedule
    };

    public static Color Colour(PasswordChangeTargetState state) => state switch
    {
        PasswordChangeTargetState.Set => Color.Success,
        PasswordChangeTargetState.Delivering => Color.Info,
        PasswordChangeTargetState.Retrying => Color.Warning,
        PasswordChangeTargetState.Parked or PasswordChangeTargetState.Expired => Color.Error,
        _ => Color.Default
    };

    /// <summary>
    /// The sentence under the Connected System's name. A parked target speaks in its own words, because that is
    /// where the remedy lives; a retrying one names the next attempt, because that is what the reader is waiting
    /// for; the rest say what state they are in and why.
    /// </summary>
    public static string Words(PasswordChangeTargetOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        switch (outcome.State)
        {
            case PasswordChangeTargetState.Set:
                return "Password set";

            case PasswordChangeTargetState.Delivering:
                return "Delivering now";

            case PasswordChangeTargetState.Queued:
                return "Queued; the Password Delivery Service will pick it up in a moment";

            case PasswordChangeTargetState.Retrying:
            {
                // JIM's classification first, then the target's own words, then the next attempt: what went wrong,
                // in whose words, and what happens next. The classification is absent for a target read from
                // history rather than from the queue, in which case the state word stands in for it.
                var lead = outcome.FailureReason is { } reason and not PasswordSetFailureReason.None
                    ? PendingPasswordChangeDisplay.Reason(reason)
                    : "Retrying";
                var words = string.IsNullOrWhiteSpace(outcome.Message) ? lead : $"{lead}: {outcome.Message}";
                return outcome.NextAttemptAt.HasValue
                    ? $"{words}. Next attempt {outcome.NextAttemptAt.Value.ToLocalTime().ToFriendlyDate()}"
                    : words;
            }

            case PasswordChangeTargetState.Parked:
                return string.IsNullOrWhiteSpace(outcome.Message)
                    ? "Parked: JIM has stopped trying until somebody retries it"
                    : $"Parked: {outcome.Message}";

            case PasswordChangeTargetState.Held:
                return "Held until Password Synchronisation is switched on for this Connected System";

            case PasswordChangeTargetState.Expired:
                return "Expired before it could be delivered";

            case PasswordChangeTargetState.Cancelled:
                return "Cancelled";

            default:
                return outcome.State.ToString();
        }
    }
}
