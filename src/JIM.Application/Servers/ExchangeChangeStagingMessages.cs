// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Exchange;
using JIM.Models.Localisation;
using JIM.Models.Resources;

namespace JIM.Application.Servers;

/// <summary>
/// What export staging says, on the follow-up Activities of an export item, about the Exchange change the export
/// declared (#2021): queued, or replacing an earlier change. Every change newly queued gets one of these, so an object's
/// recipient state is never decided without a word; a re-stage of the change already queued gets none, since its own
/// Activity already says what became of it.
/// </summary>
internal static class ExchangeChangeStagingMessages
{
    /// <summary>
    /// A change newly queued, or replacing an earlier one: "Mailbox queued for Exchange on Corporate AD." One sentence per
    /// recipient type, so each language can inflect the type for its place in the sentence (#3084).
    /// </summary>
    internal static JimMessage Queued(string recipientType, ExchangeRecipientChangeKind change, string connectedSystemName)
    {
        var prefix = change == ExchangeRecipientChangeKind.Update ? "QueuedUpdate" : "Queued";
        if (recipientType != ExchangeRecipientTypes.None && ExchangeRecipientTypes.All.Contains(recipientType))
            return JimMessage.Create(MessageCatalogues.Exchange, prefix + recipientType, connectedSystemName);

        // Only a type the rules accepted is staged, so this is not reached; were it, the English is kept as it was.
        var noun = ExchangeRecipientRules.Noun(recipientType);
        return ActivityMessage.Create(nameof(ActivityMessages.Verbatim), change == ExchangeRecipientChangeKind.Update
            ? $"Update to the {noun} queued for Exchange on {connectedSystemName}."
            : $"{char.ToUpperInvariant(noun[0])}{noun[1..]} queued for Exchange on {connectedSystemName}.");
    }

    /// <summary>Told to the change a newer declared state replaced, beneath its own Activity.</summary>
    internal static JimMessage Replaced(string connectedSystemName) =>
        JimMessage.Create(MessageCatalogues.Exchange, nameof(ExchangeMessages.Replaced), connectedSystemName);
}
