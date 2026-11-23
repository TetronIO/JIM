// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Web.Localisation;
using NUnit.Framework;

namespace JIM.Web.Tests.Localisation;

/// <summary>
/// Which of a pair of singular and plural forms a count takes depends on the language, not on the count alone
/// (#3030): English and German use the singular for exactly one, French for zero and one.
/// </summary>
[TestFixture]
public class PluralRulesTests
{
    [TestCase("en-GB", 0, "other")]
    [TestCase("en-GB", 1, "one")]
    [TestCase("en-GB", 2, "other")]
    [TestCase("en-US", 1, "one")]
    [TestCase("de", 0, "other")]
    [TestCase("de", 1, "one")]
    [TestCase("de-AT", 2, "other")]
    [TestCase("fr", 0, "one")]
    [TestCase("fr", 1, "one")]
    [TestCase("fr", 2, "other")]
    [TestCase("fr-CA", 0, "one")]
    public void Choose_CountInLanguage_PicksThatLanguagesForm(string culture, long count, string expected)
    {
        var chosen = PluralRules.Choose(CultureInfo.GetCultureInfo(culture), count, one: "one", other: "other");

        Assert.That(chosen, Is.EqualTo(expected));
    }
}
