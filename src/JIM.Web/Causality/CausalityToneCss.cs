// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// Maps <see cref="CausalityTone"/> values onto the causality stylesheet's tone hooks so components
/// share one source of truth for tone class names and CSS custom property references. The CSS
/// variables are defined on <c>.causality-panel</c> in <c>wwwroot/css/causality.css</c> and derive
/// from the active theme's MudBlazor palette tokens.
/// </summary>
public static class CausalityToneCss
{
    /// <summary>
    /// The CSS class fragment for a tone (e.g. "primary"), as used by the pill and badge styles.
    /// </summary>
    public static string CssClass(CausalityTone tone)
    {
        return tone switch
        {
            CausalityTone.Primary => "primary",
            CausalityTone.Success => "success",
            CausalityTone.Info => "info",
            CausalityTone.Warning => "warning",
            CausalityTone.Error => "error",
            _ => "secondary"
        };
    }

    /// <summary>
    /// A CSS var() reference for a tone's colour (e.g. "var(--cz-primary)"), for inline
    /// <c>--tone</c> custom property assignments on dots, icons and badges.
    /// </summary>
    public static string CssVar(CausalityTone tone)
    {
        return $"var(--cz-{CssClass(tone)})";
    }

    /// <summary>
    /// A CSS var() reference for a tone's TEXT colour (e.g. "var(--cz-primary-text)"), for a tone painted as
    /// text on a tint of itself: an outcome pill, the Table view's change chip, a lineage operation chip. The
    /// raw palette colour is a fill and fails WCAG AA as text in several themes; each <c>--cz-*-text</c> token
    /// is the portal-wide chip label blend defined in site.css, so these chips match every Text-variant
    /// MudChip in the portal.
    /// </summary>
    public static string TextCssVar(CausalityTone tone)
    {
        return $"var(--cz-{CssClass(tone)}-text)";
    }
}
