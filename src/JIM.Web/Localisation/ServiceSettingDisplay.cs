// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Web.Resources;

namespace JIM.Web.Localisation;

/// <summary>
/// The name and description of a Service Setting as a person reads them, in their language (#3030). A setting is stored
/// with its English name and description, but its key (<c>Sync.PageSize</c>) is the stable identity, so the portal looks
/// both up by key in <c>Resources/ServiceSettingText.resx</c> (<c>Sync_PageSize</c>, and <c>Sync_PageSize_Description</c>)
/// and falls back to the stored English where there is no entry.
/// </summary>
public static class ServiceSettingDisplay
{
    /// <summary>The setting's name in the viewer's language, or <paramref name="fallbackEnglish"/> when none is translated.</summary>
    public static string Name(string key, string fallbackEnglish) =>
        ServiceSettingText.ResourceManager.GetString(ResourceKeyFor(key), CultureInfo.CurrentUICulture) ?? fallbackEnglish;

    /// <summary>The setting's description in the viewer's language, or <paramref name="fallbackEnglish"/> when none is translated.</summary>
    public static string? Description(string key, string? fallbackEnglish) =>
        ServiceSettingText.ResourceManager.GetString(ResourceKeyFor(key) + "_Description", CultureInfo.CurrentUICulture) ?? fallbackEnglish;

    /// <summary>The resource key for a setting key: each dot-separated part capitalised and joined with underscores.</summary>
    public static string ResourceKeyFor(string settingKey) => string.Join('_', settingKey
        .Split('.')
        .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}
