// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using JIM.Models.Localisation;
using JIM.Models.Staging;
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

    /// <summary>
    /// What a validation or check finding says, in the viewer's language: its message where it carries one, else the
    /// English text it was given (another system's words, or a message not yet converted). Where the message names
    /// settings (the settings validator's do), each name is shown as its label, so "Please supply a value for Port" reads
    /// with the same words as the field beside it.
    /// </summary>
    public static string Describe(this ConnectorSettingValueValidationResult result, string? definitionName) =>
        result.ErrorDetail is { } detail ? RelabelSettingNames(detail, definitionName).ToCurrentLanguage() : result.ErrorMessage ?? string.Empty;

    private static JimMessage RelabelSettingNames(JimMessage message, string? definitionName)
    {
        if (string.IsNullOrEmpty(definitionName))
            return message;

        // the settings validator joins names into quoted lists; every other message passes a setting's name on its own
        var quotedLists = message.Catalogue == JIM.Models.Localisation.MessageCatalogues.SettingValidation;
        var arguments = message.Arguments
            .Select(argument =>
                argument.Text is { } text ? new MessageArgument { Text = SettingNamesRelabelled(text, definitionName, quotedLists) }
                : argument.Message is { } inner ? new MessageArgument { Message = RelabelSettingNames(inner, definitionName) }
                : argument)
            .ToList();
        return message with { Arguments = arguments };
    }

    private static string SettingNamesRelabelled(string text, string definitionName, bool quotedLists)
    {
        if (Lookup(definitionName, text, "Label") is { } label)
            return label;

        return quotedLists
            ? QuotedName().Replace(text, match => "'" + (Lookup(definitionName, match.Groups[1].Value, "Label") ?? match.Groups[1].Value) + "'")
            : text;
    }

    /// <summary>
    /// A message the Connector produced, in the viewer's language, with each setting it names shown as its label.
    /// </summary>
    public static string Show(JimMessage message, string? definitionName) =>
        RelabelSettingNames(message, definitionName).ToCurrentLanguage();

    /// <summary>
    /// What a problem with the SQL Connector's Object Types document says, in the viewer's language; the English the
    /// parser wrote where the problem carries no message.
    /// </summary>
    public static string Text(this JIM.Connectors.Sql.SqlObjectTypesProblem problem) =>
        problem.Detail is { } detail ? Show(detail, JIM.Connectors.ConnectorConstants.SqlConnectorName) : problem.Message;

    /// <summary>The resource key for a setting's text, for tests and tools.</summary>
    public static string KeyFor(string definitionName, string settingName, string part) =>
        $"{Slug(definitionName)}_{Slug(settingName)}_{part}";

    private static string? Lookup(string? definitionName, string? settingName, string part) =>
        string.IsNullOrEmpty(definitionName) || string.IsNullOrEmpty(settingName)
            ? null
            : Find(KeyFor(definitionName, settingName, part));

    private static string? Find(string key) =>
        ConnectorSettingText.ResourceManager.GetString(key, CultureInfo.CurrentUICulture);

    [GeneratedRegex("'([^']+)'")]
    private static partial Regex QuotedName();

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
