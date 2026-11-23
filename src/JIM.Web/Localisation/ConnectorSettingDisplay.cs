// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using JIM.Web.Resources;

namespace JIM.Web.Localisation;

/// <summary>
/// How a Connector's setting reads in the viewer's language (#3030). A Connector declares its settings in English and JIM
/// copies them into the database, where the English name is an identifier (the Connector looks a value up by it, the REST
/// API and PowerShell address it by it) and a drop-down's value is what is stored and compared. None of that may change,
/// so the translation happens here, at display time: the label, description and choices are looked up in
/// <c>ConnectorSettingText.resx</c> by the Connector definition's name and the setting's name, and fall back to the
/// stored English where the file has no entry (a third-party or future Connector, or a setting added since).
/// </summary>
/// <remarks>
/// Keys are <c>{Definition}_{Setting}_Label</c>, <c>_Description</c> and <c>_Option_{Value}</c>, each part written with
/// every run of characters other than letters and digits replaced by one underscore (<see cref="Slug"/>);
/// <c>ConnectorSettingDisplayTests</c> fails the build for a built-in Connector setting with no label.
/// </remarks>
public static partial class ConnectorSettingDisplay
{
    /// <summary>A name as it appears in a resource key: runs of anything but letters and digits become one underscore.</summary>
    public static string Slug(string value) => NonAlphanumeric().Replace(value, "_").Trim('_');

    /// <summary>The setting's label (its name, for a heading or a field) in the viewer's language.</summary>
    public static string Label(string? definitionName, string? settingName) =>
        Lookup(definitionName, settingName, "Label") ?? settingName ?? string.Empty;

    /// <summary>The setting's description in the viewer's language; the stored text where there is no translation.</summary>
    public static string? Description(string? definitionName, string? settingName, string? stored) =>
        Lookup(definitionName, settingName, "Description") ?? stored;

    /// <summary>A drop-down choice as it reads in the viewer's language. The choice's English text is what is stored.</summary>
    public static string Option(string? definitionName, string? settingName, string value) =>
        Lookup(definitionName, settingName, "Option_" + Slug(value)) ?? value;

    /// <summary>The heading of a group of settings of which some must be filled in, in the viewer's language.</summary>
    public static string Group(string? definitionName, string groupName) =>
        Find(Slug(definitionName ?? string.Empty) + "_Group_" + Slug(groupName)) ?? groupName;

    /// <summary>A Connector's description in the viewer's language; the stored text where there is no translation.</summary>
    public static string? ConnectorDescription(string? definitionName, string? stored) =>
        string.IsNullOrEmpty(definitionName) ? stored : Find(Slug(definitionName) + "_ConnectorDescription") ?? stored;

    /// <summary>
    /// A schema attribute's description in the viewer's language, for the Microsoft Entra ID Connector's curated attributes
    /// (<c>ConnectorSchemaText.resx</c>, keyed <c>Graph_{object type}_{attribute}</c>). A directory extension, or another
    /// Connector's attribute (whose description is the directory's own text), shows the stored description.
    /// </summary>
    public static string? AttributeDescription(string? definitionName, string? objectTypeName, string? attributeName, string? stored)
    {
        if (definitionName != JIM.Connectors.ConnectorConstants.GraphConnectorName || string.IsNullOrEmpty(objectTypeName) || string.IsNullOrEmpty(attributeName))
            return stored;

        return ConnectorSchemaText.ResourceManager.GetString($"Graph_{Slug(objectTypeName)}_{Slug(attributeName)}", CultureInfo.CurrentUICulture) ?? stored;
    }

    /// <summary>The resource key for a setting's text, for tests and tools.</summary>
    public static string KeyFor(string definitionName, string settingName, string part) =>
        $"{Slug(definitionName)}_{Slug(settingName)}_{part}";

    private static string? Lookup(string? definitionName, string? settingName, string part) =>
        string.IsNullOrEmpty(definitionName) || string.IsNullOrEmpty(settingName)
            ? null
            : Find(KeyFor(definitionName, settingName, part));

    private static string? Find(string key) =>
        ConnectorSettingText.ResourceManager.GetString(key, CultureInfo.CurrentUICulture);

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
