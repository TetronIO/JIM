// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Web.Resources;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace JIM.Web.Localisation;

/// <summary>
/// Supplies MudBlazor's own component text (the data grid's filter operators, pager, close buttons and so on) from
/// <c>Resources/MudBlazorText*.resx</c>, so a translated page is not left with English inside its tables and dialogs
/// (#3030).
/// </summary>
/// <remarks>
/// A key missing from every resource is reported as not found, which tells MudBlazor to use its own built-in English
/// rather than show the key. <c>LocalisationResourceTests</c> fails the build when a MudBlazor upgrade adds a key the
/// neutral file does not carry, so that fallback is a safety net rather than a way strings go untranslated.
/// </remarks>
public sealed class JimMudLocalizer : MudLocalizer
{
    public override LocalizedString this[string key]
    {
        get
        {
            var value = MudBlazorText.ResourceManager.GetString(key, CultureInfo.CurrentUICulture);
            return value == null
                ? new LocalizedString(key, key, resourceNotFound: true)
                : new LocalizedString(key, value);
        }
    }

    public override LocalizedString this[string key, params object[] arguments]
    {
        get
        {
            var template = this[key];
            return template.ResourceNotFound
                ? template
                : new LocalizedString(key, string.Format(CultureInfo.CurrentCulture, template.Value, arguments));
        }
    }
}
