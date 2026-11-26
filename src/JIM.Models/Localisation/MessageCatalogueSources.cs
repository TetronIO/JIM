// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Models.Localisation;

/// <summary>
/// The catalogue sources messages are rendered from (#3103). JIM's own resource files are always served; a host adds
/// further sources at start-up, such as the built-in Connectors' catalogues, or, later, catalogues stored when a
/// Connector was installed.
/// </summary>
public static class MessageCatalogueSources
{
    private static readonly object Gate = new();
    private static IMessageCatalogueSource[] _sources = [new AssemblyResxCatalogueSource()];

    /// <summary>
    /// Adds a source, asked after those already registered. Dispose the result to remove it again, as a test does.
    /// </summary>
    public static IDisposable Register(IMessageCatalogueSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (Gate)
            _sources = [.. _sources, source];

        return new Registration(source);
    }

    /// <summary>Whether any registered source serves <paramref name="catalogue"/>.</summary>
    public static bool Serves(string catalogue) => Volatile.Read(ref _sources).Any(source => source.Serves(catalogue));

    /// <summary>
    /// The template for <paramref name="key"/> in <paramref name="language"/> from the first source that serves the
    /// catalogue, or null when no source serves it or the one that does holds no such key.
    /// </summary>
    public static string? Template(string catalogue, string key, CultureInfo language)
    {
        foreach (var source in Volatile.Read(ref _sources).Where(source => source.Serves(catalogue)))
            return source.Template(catalogue, key, language);

        return null;
    }

    private sealed class Registration(IMessageCatalogueSource source) : IDisposable
    {
        public void Dispose()
        {
            lock (Gate)
                _sources = _sources.Where(registered => !ReferenceEquals(registered, source)).ToArray();
        }
    }
}
