// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Localisation;

namespace JIM.TestSupport;

/// <summary>
/// A Connector message catalogue held in memory, for tests that need a message a Connector wrote to render in another
/// language (#3103). Register it for the test and dispose it afterwards:
/// <code>using var catalogue = TestMessageCatalogue.Register("test.example", ("PermissionMissing", "Grant {0}.", "Accordez {0}."));</code>
/// </summary>
public sealed class TestMessageCatalogue : IMessageCatalogueSource, IDisposable
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");
    private readonly Dictionary<string, (string English, string? French)> _templates;
    private IDisposable? _registration;

    private TestMessageCatalogue(string connectorIdentifier, IEnumerable<(string Key, string English, string? French)> templates)
    {
        Catalogue = ConnectorCatalogueNames.For(connectorIdentifier);
        _templates = templates.ToDictionary(t => t.Key, t => (t.English, t.French), StringComparer.Ordinal);
    }

    /// <summary>The catalogue's name, as a message written in it stores it.</summary>
    public string Catalogue { get; }

    /// <summary>Registers a catalogue for the Connector <paramref name="connectorIdentifier"/> until disposed.</summary>
    public static TestMessageCatalogue Register(string connectorIdentifier, params (string Key, string English, string? French)[] templates)
    {
        var catalogue = new TestMessageCatalogue(connectorIdentifier, templates);
        catalogue._registration = MessageCatalogueSources.Register(catalogue);
        return catalogue;
    }

    /// <summary>A message in this catalogue.</summary>
    public JimMessage Message(string key, params MessageArgument[] arguments) => JimMessage.Create(Catalogue, key, arguments);

    /// <inheritdoc />
    public bool Serves(string catalogue) => string.Equals(catalogue, Catalogue, StringComparison.Ordinal);

    /// <inheritdoc />
    public string? Template(string catalogue, string key, CultureInfo language)
    {
        if (!Serves(catalogue) || !_templates.TryGetValue(key, out var template))
            return null;

        return language.TwoLetterISOLanguageName == French.TwoLetterISOLanguageName && template.French != null
            ? template.French
            : template.English;
    }

    /// <summary>Removes the catalogue again.</summary>
    public void Dispose() => _registration?.Dispose();
}
