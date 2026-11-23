// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Resources;

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

    private static readonly ConcurrentDictionary<string, ResourceManager> Catalogues = new(StringComparer.Ordinal);

    /// <summary>The message in British English, with British formats.</summary>
    public static string ToEnglish(this JimMessage message) => Render(message, English, English);

    /// <summary>The message in the current UI culture's language and the current culture's formats.</summary>
    public static string ToCurrentLanguage(this JimMessage message) =>
        Render(message, CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);

    /// <summary>
    /// The message in <paramref name="language"/>, with values formatted for <paramref name="formats"/>. A key the
    /// catalogue lacks renders its English (a translation missing for one language), and a catalogue or key missing
    /// altogether renders the key itself, which <c>JimMessageConventionTests</c> stops reaching a build.
    /// </summary>
    public static string Render(JimMessage message, CultureInfo language, CultureInfo formats)
    {
        ArgumentNullException.ThrowIfNull(message);

        var key = message.PluralCount is { } count
            ? message.Key + (PluralRules.IsOne(language, count) ? "One" : "Other")
            : message.Key;

        var template = CatalogueFor(message.Catalogue)?.GetString(key, language) ?? key;
        if (message.Arguments.Count == 0)
            return template;

        var values = message.Arguments.Select(argument => Format(argument, language, formats)).ToArray<object?>();
        return string.Format(formats, template, values);
    }

    /// <summary>The resource file a catalogue names, or null when the assembly or file does not exist.</summary>
    public static ResourceManager? CatalogueFor(string catalogue)
    {
        if (Catalogues.TryGetValue(catalogue, out var known))
            return known;

        var separator = catalogue.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
            return null;

        var assemblyName = catalogue[..separator];
        var fileName = catalogue[(separator + 1)..];
        Assembly assembly;
        try
        {
            assembly = Assembly.Load(assemblyName);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        var manager = new ResourceManager($"{assemblyName}.Resources.{fileName}", assembly);
        return Catalogues.GetOrAdd(catalogue, manager);
    }

    private static string Format(MessageArgument argument, CultureInfo language, CultureInfo formats)
    {
        if (argument.Message is { } inner)
            return Render(inner, language, formats);
        if (argument.Number is { } number)
            return number.ToString("N0", formats);
        if (argument.Utc is { } utc)
            return utc.ToLocalTime().ToString("dd MMM yyyy HH:mm:ss", formats);
        return argument.Text ?? string.Empty;
    }
}
