// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// How the Connected System delete dialog was left, and so what its host does next.
/// </summary>
public enum DeleteConnectedSystemDialogOutcome
{
    /// <summary>Closed without deleting or asking for anything.</summary>
    Cancelled = 0,

    /// <summary>The deletion was accepted, completed or queued; the system's pages stop being meaningful.</summary>
    Deleted = 1,

    /// <summary>
    /// The administrator asked to preview the deletion first (#134). The preview runs where its panel lives, on the
    /// Danger Zone, so the host starts it there.
    /// </summary>
    PreviewRequested = 2,

    /// <summary>The administrator asked to read the deletion preview, or wait for it, on the Danger Zone (#134).</summary>
    ShowPreview = 3
}
