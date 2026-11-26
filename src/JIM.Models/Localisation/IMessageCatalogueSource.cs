// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Models.Localisation;

/// <summary>
/// Where the text of a message catalogue comes from (#3103): JIM's own resource files, a built-in Connector's, or, once
/// Connectors can be installed, a catalogue stored when the Connector was installed. The renderer asks each registered
/// source in turn (<see cref="MessageCatalogueSources"/>), so a new kind of catalogue needs a new source and nothing else.
/// </summary>
public interface IMessageCatalogueSource
{
    /// <summary>Whether this source answers for <paramref name="catalogue"/>.</summary>
    bool Serves(string catalogue);

    /// <summary>
    /// The template for <paramref name="key"/> in <paramref name="language"/>, or the catalogue's English where that
    /// language has no text for it; null when the catalogue does not hold the key at all.
    /// </summary>
    string? Template(string catalogue, string key, CultureInfo language);
}
