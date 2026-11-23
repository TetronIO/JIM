// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Utilities;
using JIM.Web.Resources;

namespace JIM.Web.Localisation;

/// <summary>
/// The changed setting a Configuration Change Preview finding names (#3030). Findings carry the setting as the name of
/// the property it lives in; this reads it in the viewer's language, and reads a property with no translation as its
/// name split into words, as the portal always showed it.
/// </summary>
public static class PreviewPropertyDisplay
{
    /// <summary>The property's name for display.</summary>
    /// <param name="propertyName">The name of the property, such as <c>ProjectToMetaverse</c>.</param>
    public static string Name(string propertyName) =>
        PreviewPropertyText.ResourceManager.GetString(propertyName, CultureInfo.CurrentUICulture) ?? propertyName.SplitOnCapitalLetters();
}
