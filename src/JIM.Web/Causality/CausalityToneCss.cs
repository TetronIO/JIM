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
    /// A CSS var() reference for a tone's TEXT colour, as used by a lineage chain-hop card's operation
    /// chip. Primary resolves to <c>--cz-primary-text</c>: the raw palette primary fails WCAG AA as text
    /// (see the note on that custom property in causality.css), and blending it toward the theme's text
    /// colour is what fixes it. Every other tone's fill already meets contrast as text, so it resolves to
    /// the ordinary <see cref="CssVar"/>.
    /// </summary>
    public static string TextCssVar(CausalityTone tone)
    {
        return tone == CausalityTone.Primary ? "var(--cz-primary-text)" : CssVar(tone);
    }
}
