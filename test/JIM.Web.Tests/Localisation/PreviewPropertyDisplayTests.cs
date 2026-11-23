// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections;
using System.Globalization;
using System.Resources;
using JIM.Utilities;
using JIM.Web.Localisation;
using JIM.Web.Resources;
using NUnit.Framework;

namespace JIM.Web.Tests.Localisation;

/// <summary>
/// The changed property a Configuration Change Preview finding names, shown beside it in the reader's language (#3030).
/// </summary>
[TestFixture]
public class PreviewPropertyDisplayTests
{
    [Test]
    public void Name_KnownPropertyInFrench_ReadsTheTranslation()
    {
        using var _ = new UiCulture("fr");

        Assert.That(PreviewPropertyDisplay.Name("ProjectToMetaverse"), Is.EqualTo("Projeter vers le Metaverse"));
    }

    [Test]
    public void Name_UnknownProperty_ReadsAsTheSplitName()
    {
        using var _ = new UiCulture("fr");

        Assert.That(PreviewPropertyDisplay.Name("SomeNewProperty"), Is.EqualTo("Some New Property"));
    }

    [Test]
    public void EveryKey_InBritishEnglish_ReadsAsTheSplitNameDid()
    {
        var english = PreviewPropertyText.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;

        foreach (DictionaryEntry entry in english)
            Assert.That(entry.Value, Is.EqualTo(((string)entry.Key).SplitOnCapitalLetters()), $"'{entry.Key}' must read as it did before.");
    }

    private sealed class UiCulture : System.IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentUICulture;

        public UiCulture(string name) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);

        public void Dispose() => CultureInfo.CurrentUICulture = _previous;
    }
}
