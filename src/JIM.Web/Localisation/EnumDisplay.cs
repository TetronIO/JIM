// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Utilities;
using JIM.Web.Resources;

namespace JIM.Web.Localisation;

/// <summary>
/// An enum value as a person reads it, in their language (#3030): <c>ActivityStatus.CompleteWithWarning</c> reads
/// "Complete With Warning" in English and "Terminée avec avertissement" in French. The text lives in
/// <c>Resources/EnumText.resx</c> under <c>{EnumType}_{Value}</c>; never show an enum through <c>ToString()</c>,
/// <c>SplitOnCapitalLetters()</c> or <c>Humanize()</c>, which are English whatever the reader's language.
/// </summary>
/// <remarks>
/// A value with no entry falls back to its name split at capitals, which is what the portal showed before, so a new
/// enum value never renders blank; <c>EnumTextTests</c> fails the build for any enum with some values translated and
/// others not. Where one enum needs different words on different pages, give that page its own keys instead.
/// </remarks>
public static class EnumDisplay
{
    /// <summary>The value's name in the viewer's language.</summary>
    public static string ForDisplay(this Enum value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return EnumText.ResourceManager.GetString(KeyFor(value), CultureInfo.CurrentUICulture)
               ?? value.ToString().SplitOnCapitalLetters();
    }

    /// <summary>
    /// The display text for an enum value held as its name (a drop-down whose items carry the name as their value);
    /// an empty string for no name, and the name itself for one that is not a value of the enum.
    /// </summary>
    public static string NameForDisplay<TEnum>(string? name) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(name, out var value) ? value.ForDisplay() : name ?? string.Empty;

    /// <summary>The resource key for a value: <c>{EnumType}_{Value}</c>.</summary>
    public static string KeyFor(Enum value) => $"{value.GetType().Name}_{value}";
}
