// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using NUnit.Framework;

namespace JIM.Web.Tests.Localisation;

/// <summary>
/// "3 hours ago" appears on almost every portal page, so it follows the viewer's language (#3030). Each language
/// needs its own grammar, not just its own words: German puts the unit in the dative ("vor 3 Tagen", not "Tage"),
/// and French counts zero and one alike as singular.
/// </summary>
[TestFixture]
public class LocalisedRelativeTimeTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [TestCase(-30, "gerade eben")]
    [TestCase(30, "in weniger als einer Minute")]
    [TestCase(-60, "vor 1 Minute")]
    [TestCase(-3 * 60 * 60, "vor 3 Stunden")]
    [TestCase(14 * 60 * 60, "in 14 Stunden")]
    [TestCase(-24 * 60 * 60, "vor 1 Tag")]
    [TestCase(-2 * 24 * 60 * 60, "vor 2 Tagen")]
    [TestCase(45 * 24 * 60 * 60, "in 1 Monat")]
    [TestCase(-90 * 24 * 60 * 60, "vor 3 Monaten")]
    [TestCase(-800 * 24 * 60 * 60, "vor 2 Jahren")]
    [SetUICulture("de")]
    [SetCulture("de")]
    public void ToRelativeTime_German_UsesGermanGrammar(int secondsFromNow, string expected)
    {
        Assert.That(Now.AddSeconds(secondsFromNow).ToRelativeTime(Now), Is.EqualTo(expected));
    }

    [TestCase(-30, "à l’instant")]
    [TestCase(30, "dans moins d’une minute")]
    [TestCase(-60, "il y a 1 minute")]
    [TestCase(-3 * 60 * 60, "il y a 3 heures")]
    [TestCase(14 * 60 * 60, "dans 14 heures")]
    [TestCase(-24 * 60 * 60, "il y a 1 jour")]
    [TestCase(45 * 24 * 60 * 60, "dans 1 mois")]
    [TestCase(-90 * 24 * 60 * 60, "il y a 3 mois")]
    [TestCase(-800 * 24 * 60 * 60, "il y a 2 ans")]
    [SetUICulture("fr")]
    [SetCulture("fr")]
    public void ToRelativeTime_French_UsesFrenchGrammar(int secondsFromNow, string expected)
    {
        Assert.That(Now.AddSeconds(secondsFromNow).ToRelativeTime(Now), Is.EqualTo(expected));
    }

    [TestCase(-3 * 60 * 60, "3 hours ago")]
    [TestCase(14 * 60 * 60, "in 14 hours")]
    [SetUICulture("en-US")]
    [SetCulture("en-US")]
    public void ToRelativeTime_AmericanEnglish_ReadsAsEnglish(int secondsFromNow, string expected)
    {
        Assert.That(Now.AddSeconds(secondsFromNow).ToRelativeTime(Now), Is.EqualTo(expected));
    }

    [TestCase(-3 * 60 * 60, "vor 3 Stunden")]
    [SetUICulture("de-AT")]
    [SetCulture("de-AT")]
    public void ToRelativeTime_RegionalVariant_FallsBackToItsLanguage(int secondsFromNow, string expected)
    {
        Assert.That(Now.AddSeconds(secondsFromNow).ToRelativeTime(Now), Is.EqualTo(expected));
    }
}
