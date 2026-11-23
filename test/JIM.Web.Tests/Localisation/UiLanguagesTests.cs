// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Web.Localisation;
using NUnit.Framework;

namespace JIM.Web.Tests.Localisation;

/// <summary>
/// Which languages each environment offers, and how a choice arriving on a query string is trusted (#3030).
/// </summary>
[TestFixture]
public class UiLanguagesTests
{
    [Test]
    public void Offered_Production_ListsOnlyReleasedLanguages()
    {
        var offered = UiLanguages.Offered(includeUnreleased: false);

        Assert.That(offered.All(language => language.IsReleased), Is.True);
        Assert.That(offered.Select(language => language.CultureName), Does.Contain("en-GB"));
    }

    [Test]
    public void Offered_Development_ListsEveryLanguage()
    {
        Assert.That(UiLanguages.Offered(includeUnreleased: true), Is.EquivalentTo(UiLanguages.All));
    }

    [Test]
    public void Default_IsBritishEnglish_AndListedFirst()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(UiLanguages.Default.CultureName, Is.EqualTo("en-GB"));
            Assert.That(UiLanguages.All[0], Is.EqualTo(UiLanguages.Default));
        }
    }

    [TestCase("de-AT", "de")]
    [TestCase("fr-CA", "fr")]
    [TestCase("en-US", "en-US")]
    [TestCase("en-AU", "en-GB")]
    [TestCase("ja-JP", "en-GB")]
    public void Current_Culture_ResolvesToTheLanguageServingIt(string culture, string expected)
    {
        var current = UiLanguages.Current(CultureInfo.GetCultureInfo(culture), includeUnreleased: true);

        Assert.That(current.CultureName, Is.EqualTo(expected));
    }

    [Test]
    public void Find_UnreleasedLanguageInProduction_IsNotFound()
    {
        var unreleased = UiLanguages.All.FirstOrDefault(language => !language.IsReleased);
        Assume.That(unreleased, Is.Not.Null, "Every language is released; nothing to check.");

        Assert.That(UiLanguages.Find(unreleased!.CultureName, includeUnreleased: false), Is.Null);
    }

    [TestCase("EN-us", true)]
    [TestCase("en-gb", false)]
    [TestCase("De", true)]
    public void CookieValueFor_OfferedLanguageInAnyCase_IsAccepted(string culture, bool includeUnreleased)
    {
        Assert.That(CultureSelection.CookieValueFor(culture, includeUnreleased), Does.StartWith("c="));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("xx-YY")]
    [TestCase("en-GB|uic=de")]
    [TestCase("c=en-GB|uic=en-GB")]
    public void CookieValueFor_AnythingElse_IsRefused(string? culture)
    {
        Assert.That(CultureSelection.CookieValueFor(culture, includeUnreleased: true), Is.Null);
    }

    [TestCase("/admin/connected-systems?t=schema", "/admin/connected-systems?t=schema")]
    [TestCase("/", "/")]
    [TestCase(null, "/")]
    [TestCase("", "/")]
    [TestCase("https://evil.example/", "/")]
    [TestCase("//evil.example/", "/")]
    [TestCase("/\\evil.example/", "/")]
    [TestCase("javascript:alert(1)", "/")]
    [TestCase("/a\r\nSet-Cookie: x=y", "/")]
    public void SafeReturnPath_OnlyALocalPathIsReturnedTo(string? returnUrl, string expected)
    {
        Assert.That(CultureSelection.SafeReturnPath(returnUrl), Is.EqualTo(expected));
    }
}
