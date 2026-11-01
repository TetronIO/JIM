// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Transactional.DTOs;
using JIM.Utilities;

namespace JIM.Web.Models;

/// <summary>
/// How one follow-up of an execution item reads on screen (#2668): what it is, where it stands as a status pill, the
/// sentence beneath, and whether it is still running. Shared by the item page's Follow-up section, the causality
/// Lineage's "After this run" cards and the object timeline, so they describe the same work the same way.
/// </summary>
/// <param name="ActivityId">The follow-up's Activity.</param>
/// <param name="Title">What the work is, such as "Initial password".</param>
/// <param name="TargetName">The Connected System the work is for.</param>
/// <param name="StateLabel">Where it stands, such as "Delivered" or "Retrying".</param>
/// <param name="PillModifier">The <c>jim-status-pill--</c> modifier for that state: ok, warn, err or neutral.</param>
/// <param name="Detail">The sentence beneath: why, or what happens next.</param>
/// <param name="IsRunning">Whether JIM is still working on it, so the story it belongs to has not ended.</param>
/// <param name="Created">When it was recorded (UTC).</param>
public sealed record ExecutionItemFollowUpDisplay(
    Guid ActivityId,
    string Title,
    string? TargetName,
    string StateLabel,
    string PillModifier,
    string? Detail,
    bool IsRunning,
    DateTime Created)
{
    /// <summary>The follow-up's own Activity page.</summary>
    public string ActivityHref => $"/activity/{ActivityId}";

    public static ExecutionItemFollowUpDisplay From(ExecutionItemFollowUp followUp)
    {
        ArgumentNullException.ThrowIfNull(followUp);

        if (followUp.TargetType == ActivityTargetType.PasswordSynchronisation)
        {
            // An Initial Password reaches one Connected System. With no target of its own it was carried by a password
            // change already waiting for the account, whose own Activity reports the delivery.
            var target = followUp.PasswordOutcomes?.Targets.FirstOrDefault();
            if (target == null)
                return new(followUp.ActivityId, "Initial password", followUp.TargetName, "Carried", "neutral", followUp.Message, false, followUp.Created);

            var (label, modifier, running) = target.State switch
            {
                PasswordChangeTargetState.Set => ("Delivered", target.CompletedWithWarning ? "warn" : "ok", false),
                PasswordChangeTargetState.Queued => ("Queued", "neutral", true),
                PasswordChangeTargetState.Delivering => ("Delivering", "neutral", true),
                PasswordChangeTargetState.Retrying => ("Retrying", "warn", true),
                PasswordChangeTargetState.Held => ("Held", "warn", true),
                PasswordChangeTargetState.Parked => ("Parked", "err", false),
                PasswordChangeTargetState.Expired => ("Expired", "err", false),
                PasswordChangeTargetState.Cancelled => ("Cancelled", "err", false),
                _ => (target.State.ToString(), "neutral", false)
            };
            return new(followUp.ActivityId, "Initial password", followUp.TargetName, label, modifier,
                PasswordChangeTargetDisplay.Words(target), running, followUp.Created);
        }

        var statusModifier = followUp.Status switch
        {
            ActivityStatus.Complete => "ok",
            ActivityStatus.CompleteWithWarning => "warn",
            ActivityStatus.CompleteWithError or ActivityStatus.FailedWithError => "err",
            _ => "neutral"
        };
        return new(followUp.ActivityId, Helpers.GetActivityTargetTypeName(followUp.TargetType), followUp.TargetName,
            followUp.Status.ToString().SplitOnCapitalLetters(), statusModifier, followUp.Message,
            followUp.Status is ActivityStatus.InProgress or ActivityStatus.NotSet, followUp.Created);
    }
}
