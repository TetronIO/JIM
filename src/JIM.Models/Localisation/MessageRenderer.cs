// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Models.Localisation;

/// <summary>
/// Turns a <see cref="JimMessage"/> into text in a given language (#3030). The portal renders in the viewer's language
/// (<see cref="ToCurrentLanguage"/>); everything else (the REST API, logs, stored English) uses
/// <see cref="ToEnglish"/>, so a person's language never reaches a machine contract.
/// </summary>
public static class MessageRenderer
{
    /// <summary>The language JIM's own records and machine-facing text are written in.</summary>
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>The message in British English, with British formats.</summary>
    public static string ToEnglish(this JimMessage message) => Render(message, English, English);

    /// <summary>The message in the current UI culture's language and the current culture's formats.</summary>
    public static string ToCurrentLanguage(this JimMessage message) =>
        Render(message, CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);

    /// <summary>
    /// The message in <paramref name="language"/>, with values formatted for <paramref name="formats"/>. Never throws: a
    /// message <see cref="TryRender"/> cannot render renders in the catalogue's English where it can, and otherwise as
    /// its key. A caller holding the English stored beside the message should show that instead (the portal's
    /// <c>MessageDisplay.Show(detail, english)</c> does), since a key is no use to a reader.
    /// </summary>
    public static string Render(JimMessage message, CultureInfo language, CultureInfo formats)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (TryRender(message, language, formats, out var text))
            return text;
        if (!Equals(language, English) && TryRender(message, English, formats, out var english))
            return english;
        return message.Key;
    }

    /// <summary>
    /// Renders the message, and every message nested in its arguments, in <paramref name="language"/> (#3103). False when
    /// any of them cannot be: no registered source serves its catalogue, the catalogue holds no such key (one renamed or
    /// removed since the record was written), or its template's placeholders do not fit its arguments. A language's
    /// malformed template falls back to the catalogue's English before giving up.
    /// </summary>
    public static bool TryRender(JimMessage message, CultureInfo language, CultureInfo formats, out string text)
    {
        ArgumentNullException.ThrowIfNull(message);
        text = string.Empty;

        var key = message.PluralCount is { } count
            ? message.Key + (PluralRules.IsOne(language, count) ? "One" : "Other")
            : message.Key;

        var template = MessageCatalogueSources.Template(message.Catalogue, key, language);
        if (template == null)
            return false;

        if (message.Arguments.Count == 0)
        {
            text = template;
            return true;
        }

        var values = new object?[message.Arguments.Count];
        for (var i = 0; i < values.Length; i++)
        {
            if (!TryFormat(message.Arguments[i], language, formats, out var value))
                return false;
            values[i] = value;
        }

        if (TryFill(template, formats, values, out text))
            return true;

        var englishTemplate = Equals(language, English) ? null : MessageCatalogueSources.Template(message.Catalogue, key, English);
        return englishTemplate != null && TryFill(englishTemplate, formats, values, out text);
    }

    private static bool TryFill(string template, CultureInfo formats, object?[] values, out string text)
    {
        try
        {
            text = string.Format(formats, template, values);
            return true;
        }
        catch (FormatException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool TryFormat(MessageArgument argument, CultureInfo language, CultureInfo formats, out string value)
    {
        if (argument.Message is { } inner)
            return TryRender(inner, language, formats, out value);

        value = Format(argument, formats);
        return true;
    }

    private static string Format(MessageArgument argument, CultureInfo formats)
    {
        if (argument.Number is { } number)
            return number.ToString("N0", formats);
        if (argument.Utc is { } utc)
            return utc.ToLocalTime().ToString("dd MMM yyyy HH:mm:ss", formats);
        return argument.Text ?? string.Empty;
    }
}
