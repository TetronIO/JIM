// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using JIM.Models.Localisation;
using JIM.Models.Tests.Resources;
using NUnit.Framework;

namespace JIM.Models.Tests.Localisation;

/// <summary>
/// Message text is found through catalogue sources (#3103): JIM's own resource files today, and catalogues a Connector
/// brings later, without the reader's process loading that Connector. A message that cannot be rendered says so, so the
/// portal can show the English stored beside it instead of a key, and a bad template never throws on a page.
/// </summary>
[TestFixture]
public class MessageCatalogueSourceTests
{
    private const string JimCatalogue = "JIM.Models.Tests:TestMessages";
    private const string ConnectorCatalogue = "connector:test.example/Messages";
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    [Test]
    public void TryRender_MessageItsCatalogueHolds_RendersIt()
    {
        var message = JimMessage.Create(JimCatalogue, nameof(TestMessages.NotFound), "HR");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(MessageRenderer.TryRender(message, French, French, out var text), Is.True);
            Assert.That(text, Is.EqualTo("Le système connecté « HR » n’existe plus."));
        }
    }

    [Test]
    public void TryRender_CatalogueNoSourceKnows_CannotRender()
    {
        var message = JimMessage.Create("connector:uninstalled.example/Messages", "PermissionMissing", "User.ReadWrite");

        Assert.That(MessageRenderer.TryRender(message, French, French, out _), Is.False);
    }

    [Test]
    public void TryRender_KeyTheCatalogueDoesNotHold_CannotRender()
    {
        var message = JimMessage.Create(JimCatalogue, "RenamedInALaterVersion");

        Assert.That(MessageRenderer.TryRender(message, French, French, out _), Is.False);
    }

    [Test]
    public void TryRender_NestedMessageThatCannotRender_CannotRenderTheWhole()
    {
        var message = JimMessage.Create(JimCatalogue, nameof(TestMessages.NotFound),
            JimMessage.Create("connector:uninstalled.example/Messages", "ObjectName"));

        Assert.That(MessageRenderer.TryRender(message, French, French, out _), Is.False);
    }

    [Test]
    public void RegisteredSource_ServesItsCatalogue_UntilDisposed()
    {
        var message = JimMessage.Create(ConnectorCatalogue, "PermissionMissing", "User.ReadWrite");

        string? whileRegistered;
        using (MessageCatalogueSources.Register(new FixedSource(ConnectorCatalogue, new()
        {
            ["en-GB/PermissionMissing"] = "Grant {0} to the app registration.",
            ["fr/PermissionMissing"] = "Accordez {0} à l’inscription d’application."
        })))
        {
            MessageRenderer.TryRender(message, French, French, out whileRegistered);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileRegistered, Is.EqualTo("Accordez User.ReadWrite à l’inscription d’application."));
            Assert.That(MessageRenderer.TryRender(message, French, French, out _), Is.False, "the source is gone once disposed");
        }
    }

    [Test]
    public void RegisteredSource_LanguageItLacks_UsesItsEnglish()
    {
        var message = JimMessage.Create(ConnectorCatalogue, "PermissionMissing", "User.ReadWrite");
        using var registration = MessageCatalogueSources.Register(new FixedSource(ConnectorCatalogue, new()
        {
            ["en-GB/PermissionMissing"] = "Grant {0} to the app registration."
        }));

        var rendered = MessageRenderer.TryRender(message, CultureInfo.GetCultureInfo("de"), French, out var text);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rendered, Is.True);
            Assert.That(text, Is.EqualTo("Grant User.ReadWrite to the app registration."));
        }
    }

    [Test]
    public void TranslationWhosePlaceholdersDoNotFitItsArguments_RendersTheCatalogueEnglish_AndNeverThrows()
    {
        var message = JimMessage.Create(ConnectorCatalogue, "PermissionMissing", "User.ReadWrite");
        using var registration = MessageCatalogueSources.Register(new FixedSource(ConnectorCatalogue, new()
        {
            ["en-GB/PermissionMissing"] = "Grant {0} to the app registration.",
            ["fr/PermissionMissing"] = "Accordez {0} à {1}."
        }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(MessageRenderer.TryRender(message, French, French, out var text), Is.True);
            Assert.That(text, Is.EqualTo("Grant User.ReadWrite to the app registration."), "the broken French falls back to the catalogue's English");
            Assert.That(() => MessageRenderer.Render(message, French, French), Throws.Nothing);
        }
    }

    [Test]
    public void TemplateBrokenInEveryLanguage_CannotRender_AndRenderShowsTheKey()
    {
        var message = JimMessage.Create(ConnectorCatalogue, "PermissionMissing", "User.ReadWrite");
        using var registration = MessageCatalogueSources.Register(new FixedSource(ConnectorCatalogue, new()
        {
            ["en-GB/PermissionMissing"] = "Grant {0} to {1}."
        }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(MessageRenderer.TryRender(message, French, French, out _), Is.False);
            Assert.That(MessageRenderer.Render(message, French, French), Is.EqualTo("PermissionMissing"), "Render never throws");
        }
    }

    /// <summary>A catalogue held in memory, keyed by "language/key", as a stored catalogue would be.</summary>
    private sealed class FixedSource(string catalogue, Dictionary<string, string> templates) : IMessageCatalogueSource
    {
        public string? Template(string catalogueName, string key, CultureInfo language)
        {
            if (!string.Equals(catalogueName, catalogue, StringComparison.Ordinal))
                return null;

            return templates.TryGetValue($"{language.Name}/{key}", out var exact) ? exact
                : templates.TryGetValue($"{language.TwoLetterISOLanguageName}/{key}", out var neutral) ? neutral
                : templates.GetValueOrDefault($"{MessageRenderer.English.Name}/{key}");
        }

        public bool Serves(string catalogueName) => string.Equals(catalogueName, catalogue, StringComparison.Ordinal);
    }
}
