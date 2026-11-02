// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exchange;

namespace JIM.Application.Servers;

/// <summary>
/// What export staging says, on the follow-up Activities of an export item, about the Exchange change the export
/// declared (#2021): queued, or replacing an earlier change. Every change newly queued gets one of these, so an object's
/// recipient state is never decided without a word; a re-stage of the change already queued gets none, since its own
/// Activity already says what became of it.
/// </summary>
internal static class ExchangeChangeStagingMessages
{
    /// <summary>A change newly queued, or replacing an earlier one: "Mailbox queued for Exchange on Corporate AD."</summary>
    internal static string Queued(string recipientType, ExchangeRecipientChangeKind change, string connectedSystemName)
    {
        var noun = ExchangeRecipientRules.Noun(recipientType);
        return change == ExchangeRecipientChangeKind.Update
            ? $"Update to the {noun} queued for Exchange on {connectedSystemName}."
            : $"{char.ToUpperInvariant(noun[0])}{noun[1..]} queued for Exchange on {connectedSystemName}.";
    }

    /// <summary>Told to the change a newer declared state replaced, beneath its own Activity.</summary>
    internal static string Replaced(string connectedSystemName) =>
        $"Not delivered to Exchange: replaced by a newer declared state for this object, queued by a later export to {connectedSystemName}.";
}
