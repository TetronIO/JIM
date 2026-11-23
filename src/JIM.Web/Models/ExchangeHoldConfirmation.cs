// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Resources;

namespace JIM.Web.Models;

/// <summary>
/// The question the Settings tab asks before a save turns a Connected System's Exchange recipient management off
/// (#2021, decided 2026-11-05): turning it off holds delivery rather than failing it, and the administrator should know
/// what that means before it happens.
/// </summary>
public static class ExchangeHoldConfirmation
{
    /// <summary>The dialog's title.</summary>
    public static string Title => MetaverseText.EhcTitle;

    /// <summary>The confirming button's words.</summary>
    public static string ConfirmButtonText => MetaverseText.EhcConfirm;

    /// <summary>What turning management off does, one consequence per line.</summary>
    public static IReadOnlyList<string> Consequences =>
    [
        MetaverseText.EhcConsequence1,
        MetaverseText.EhcConsequence2,
        MetaverseText.EhcConsequence3
    ];

    /// <summary>
    /// Whether a save asks first: only one that turns management off. Turning it on, or a save that leaves it as it was,
    /// changes nothing worth a question.
    /// </summary>
    public static bool IsNeeded(bool managedAsStored, bool managedAfterSave) => managedAsStored && !managedAfterSave;
}
