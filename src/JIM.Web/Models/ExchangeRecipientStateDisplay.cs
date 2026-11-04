// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exchange;

namespace JIM.Web.Models;

/// <summary>
/// How an account's Exchange recipient panel reads (#2021, Phase 6).
/// </summary>
public static class ExchangeRecipientStateDisplay
{
    /// <summary>
    /// The panel's headline pill: in step; a change being worked on; a change waiting for a person, by where it
    /// stands; or declared but not yet read back by an import, which is when JIM believes Exchange.
    /// </summary>
    public static (string Label, string Modifier) Headline(ExchangeRecipientState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.QueuedChange is { IsSettled: false })
            return ("1 change outstanding", "warn");

        // A settled change still on the queue is one JIM has stopped on: parked, refused or not made.
        if (state.QueuedChange is { } stopped && stopped.State is not (JIM.Models.Transactional.DTOs.ExchangeChangeState.Delivered
                or JIM.Models.Transactional.DTOs.ExchangeChangeState.Withdrawn or JIM.Models.Transactional.DTOs.ExchangeChangeState.Replaced))
        {
            var (label, modifier, _) = ExchangeChangeOutcomeDisplay.Pill(stopped.State);
            return ($"Change {label.ToLowerInvariant()}", modifier);
        }

        return state.IsInStep ? ("In step", "ok") : ("Awaiting import", "neutral");
    }
}
