// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Web.Resources;

namespace JIM.Web.Localisation;

/// <summary>
/// The name of a Run Profile phase as a person reads it, in their language (#3030). Phases are stored with their
/// English name, but the key (<c>import.fetch</c>) is the stable identity, so the portal looks the name up by key in
/// <c>Resources/RunPhaseText.resx</c> (<c>Import_Fetch</c>) and falls back to the stored English when there is no entry,
/// as for a phase a Connector declares.
/// </summary>
public static class RunPhaseDisplay
{
    /// <summary>The phase's name in the viewer's language, or <paramref name="fallbackEnglish"/> when none is translated.</summary>
    public static string Name(string key, string fallbackEnglish)
    {
        ArgumentNullException.ThrowIfNull(key);
        return RunPhaseText.ResourceManager.GetString(ResourceKeyFor(key), CultureInfo.CurrentUICulture) ?? fallbackEnglish;
    }

    /// <summary>The resource key for a phase key: each dot-separated part capitalised and joined with underscores.</summary>
    public static string ResourceKeyFor(string phaseKey) => string.Join('_', phaseKey
        .Split('.')
        .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}
