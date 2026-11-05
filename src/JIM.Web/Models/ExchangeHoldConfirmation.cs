// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// The question the Settings tab asks before a save turns a Connected System's Exchange recipient management off
/// (#2021, decided 2026-11-05): turning it off holds delivery rather than failing it, and the administrator should know
/// what that means before it happens.
/// </summary>
public static class ExchangeHoldConfirmation
{
    /// <summary>The dialog's title.</summary>
    public const string Title = "Stop managing Exchange recipients?";

    /// <summary>The confirming button's words.</summary>
    public const string ConfirmButtonText = "Hold Exchange Delivery";

    /// <summary>What turning management off does, one consequence per line.</summary>
    public static IReadOnlyList<string> Consequences { get; } =
    [
        "Changes already queued for Exchange wait, held, and are delivered once this Connected System manages Exchange recipients again.",
        "Exports go on queueing changes, which are held the same way.",
        "A schema refresh while it is off reports the Exchange attributes as removed: apply it plainly to keep them, or the Synchronisation Rules that declare recipients stop."
    ];

    /// <summary>
    /// Whether a save asks first: only one that turns management off. Turning it on, or a save that leaves it as it was,
    /// changes nothing worth a question.
    /// </summary>
    public static bool IsNeeded(bool managedAsStored, bool managedAfterSave) => managedAsStored && !managedAfterSave;
}
