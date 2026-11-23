// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;
using JIM.Web.Resources;
using MudBlazor;

namespace JIM.Web.Models;

/// <summary>
/// How the portal names why a generated value was retired (Unique Value Generation, #242, Phase 6). The stored
/// reasons stay as they are on the REST API and in PowerShell; the portal speaks in what the administrator saw
/// happen rather than in the engine's terms ("No longer generated" rather than "Superseded").
/// </summary>
public static class RetiredGeneratedValueReasonDisplay
{
    /// <summary>The short label on the reason chip.</summary>
    public static string Label(RetiredGeneratedValueReason reason) => reason switch
    {
        RetiredGeneratedValueReason.ObjectDeleted => MetaverseText.RgvObjectDeleted,
        RetiredGeneratedValueReason.Superseded => MetaverseText.RgvSuperseded,
        RetiredGeneratedValueReason.Recalled => MetaverseText.RgvRecalled,
        RetiredGeneratedValueReason.Regenerated => MetaverseText.RgvRegenerated,
        _ => reason.ToString()
    };

    /// <summary>The one-line explanation behind the chip.</summary>
    public static string Tooltip(RetiredGeneratedValueReason reason) => reason switch
    {
        RetiredGeneratedValueReason.ObjectDeleted => MetaverseText.RgvTipObjectDeleted,
        RetiredGeneratedValueReason.Superseded => MetaverseText.RgvTipSuperseded,
        RetiredGeneratedValueReason.Recalled => MetaverseText.RgvTipRecalled,
        RetiredGeneratedValueReason.Regenerated => MetaverseText.RgvTipRegenerated,
        _ => string.Empty
    };

    /// <summary>The chip colour: a deletion is the ordinary case; the others are a change of who supplies the value.</summary>
    public static Color Colour(RetiredGeneratedValueReason reason) => reason switch
    {
        RetiredGeneratedValueReason.ObjectDeleted => Color.Default,
        _ => Color.Warning
    };
}
