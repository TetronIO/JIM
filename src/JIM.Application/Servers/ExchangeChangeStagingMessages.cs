// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exchange;
using JIM.Models.Transactional;

namespace JIM.Application.Servers;

/// <summary>
/// What export staging says, on the follow-up Activities of an export item, about the Exchange change the export
/// declared (#2021): queued, replacing an earlier change, or already queued with the same declared state. Every staged
/// change gets one of these, so an object's recipient state is never decided without a word.
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

    /// <summary>
    /// The object's queued change already declares this state, so it was left as it is; says what that means for it, so
    /// an administrator who cancelled or is waiting on a parked change is not left wondering why nothing happens.
    /// </summary>
    internal static string Unchanged(PendingExchangeChangeStatus? existingStatus, string connectedSystemName)
    {
        const string message = "This object's queued Exchange change already declares this state, so it was left as it is.";
        return existingStatus switch
        {
            PendingExchangeChangeStatus.Cancelled => $"{message} It stays cancelled until the declared state changes.",
            PendingExchangeChangeStatus.Parked => $"{message} It stays parked until someone retries it or the declared state changes.",
            PendingExchangeChangeStatus.Delivering => $"{message} It is being delivered to Exchange on {connectedSystemName} now.",
            _ => $"{message} It is still queued for Exchange on {connectedSystemName}."
        };
    }
}
