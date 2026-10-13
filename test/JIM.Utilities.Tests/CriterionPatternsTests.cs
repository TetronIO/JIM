// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;

namespace JIM.Utilities.Tests;

public class CriterionPatternsTests
{
    [Test]
    public void Get_CaseInsensitive_IgnoresCase()
    {
        Assert.That(CriterionPatterns.Get("^fin", caseSensitive: false).IsMatch("Finance"), Is.True);
    }

    [Test]
    public void Get_CaseSensitive_RespectsCase()
    {
        Assert.That(CriterionPatterns.Get("^fin", caseSensitive: true).IsMatch("Finance"), Is.False);
    }

    [Test]
    public void Get_SamePatternAndCaseSetting_ReturnsTheCachedExpression()
    {
        var first = CriterionPatterns.Get("^cached-[0-9]+$", caseSensitive: false);
        var second = CriterionPatterns.Get("^cached-[0-9]+$", caseSensitive: false);

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void Get_Expression_CarriesTheMatchTimeout()
    {
        Assert.That(CriterionPatterns.Get("^a+$", caseSensitive: true).MatchTimeout, Is.EqualTo(CriterionPatterns.MatchTimeout));
    }

    [Test]
    public void Get_CatastrophicBacktracking_TimesOutRatherThanRunningOn()
    {
        var regex = CriterionPatterns.Get("^(a+)+$", caseSensitive: true);

        Assert.That(() => regex.IsMatch(new string('a', 5000) + "!"), Throws.TypeOf<RegexMatchTimeoutException>());
    }

    [Test]
    public void Get_InvalidPattern_Throws()
    {
        Assert.That(() => CriterionPatterns.Get("[unclosed", caseSensitive: true), Throws.InstanceOf<ArgumentException>());
    }
}
