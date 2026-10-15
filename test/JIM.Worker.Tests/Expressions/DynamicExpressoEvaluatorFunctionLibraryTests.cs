// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

/// <summary>
/// The functions that mirror the operator catalogue (#2053, PRD Phase 2 requirement 7): <c>StartsWith</c>,
/// <c>EndsWith</c> and <c>Contains</c> compare ordinally and take an optional <c>ignoreCase</c>; <c>HasValue</c> is the
/// opposite of <c>IsNullOrEmpty</c>; and <c>IsMatch</c>, <c>RegexReplace</c> and <c>RegexExtract</c> use the same
/// cached, time-limited patterns as the scoping operators.
/// </summary>
[TestFixture]
public class DynamicExpressoEvaluatorFunctionLibraryTests
{
    private IExpressionEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp() => _evaluator = new DynamicExpressoEvaluator();

    private object? Eval(string expression, params (string Name, object? Value)[] cs) =>
        _evaluator.Evaluate(expression, new ExpressionContext(connectedSystemAttributes: cs.ToDictionary(a => a.Name, a => a.Value)));

    #region StartsWith, EndsWith, Contains

    [TestCase("StartsWith(cs[\"mail\"], \"ADMIN\")", false)]
    [TestCase("StartsWith(cs[\"mail\"], \"ADMIN\", true)", true)]
    [TestCase("StartsWith(cs[\"mail\"], \"ADMIN\", false)", false)]
    [TestCase("EndsWith(cs[\"mail\"], \"@EXAMPLE.ORG\")", false)]
    [TestCase("EndsWith(cs[\"mail\"], \"@EXAMPLE.ORG\", true)", true)]
    [TestCase("Contains(cs[\"mail\"], \"@EXAMPLE\")", false)]
    [TestCase("Contains(cs[\"mail\"], \"@EXAMPLE\", true)", true)]
    public void TextComparison_ComparesExactlyUnlessIgnoreCaseIsTrue(string expression, bool expected)
    {
        Assert.That(Eval(expression, ("mail", "admin@example.org")), Is.EqualTo(expected));
    }

    [Test]
    public void StartsWith_IsOrdinal_SoAnIgnorableCharacterIsNotIgnored()
    {
        // A soft hyphen is ignorable to a culture-aware comparison, which would call "a­b" a match for "ab".
        Assert.That(Eval("StartsWith(cs[\"x\"], \"ab\")", ("x", "a­b")), Is.False);
    }

    [Test]
    public void StartsWith_IgnoringCase_IsTrueWhenAnyValueIs()
    {
        Assert.That(Eval("StartsWith(cs[\"proxyAddresses\"], \"x500:\", true)", ("proxyAddresses", new List<object?> { "smtp:a", "X500:/o=Example" })), Is.True);
    }

    #endregion

    #region HasValue

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("Priya", true)]
    public void HasValue_IsTheOppositeOfIsNullOrEmpty(string? value, bool expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Eval("HasValue(cs[\"x\"])", ("x", value)), Is.EqualTo(expected));
            Assert.That(Eval("IsNullOrEmpty(cs[\"x\"])", ("x", value)), Is.EqualTo(!expected));
        }
    }

    [Test]
    public void HasValue_OverAnEmptyList_IsFalse()
    {
        Assert.That(Eval("HasValue(cs[\"x\"])", ("x", new List<object?>())), Is.False);
    }

    #endregion

    #region IsMatch

    [TestCase("E123456", true)]
    [TestCase("e123456", false)]
    [TestCase("E12345", false)]
    public void IsMatch_MatchesExactlyByDefault(string value, bool expected)
    {
        Assert.That(Eval("IsMatch(cs[\"id\"], \"^E[0-9]{6}$\")", ("id", value)), Is.EqualTo(expected));
    }

    [Test]
    public void IsMatch_IgnoringCase_MatchesEitherCase()
    {
        Assert.That(Eval("IsMatch(cs[\"id\"], \"^E[0-9]{6}$\", true)", ("id", "e123456")), Is.True);
    }

    [Test]
    public void IsMatch_IsTrueWhenAnyValueMatches()
    {
        Assert.That(Eval("IsMatch(cs[\"ids\"], \"^E\")", ("ids", new List<object?> { "X1", "E2" })), Is.True);
    }

    [Test]
    public void IsMatch_OnNoValue_IsFalse()
    {
        Assert.That(Eval("IsMatch(cs[\"missing\"], \".*\")"), Is.False);
    }

    #endregion

    #region RegexReplace

    [Test]
    public void RegexReplace_ReplacesEveryMatch()
    {
        Assert.That(Eval("RegexReplace(cs[\"phone\"], \"[^0-9+]\", \"\")", ("phone", "+44 (0)20 7946-0000")), Is.EqualTo("+440207946000" + "0"));
    }

    [Test]
    public void RegexReplace_UsesGroupsInTheReplacement()
    {
        Assert.That(Eval("RegexReplace(cs[\"name\"], \"^(\\\\w+), (\\\\w+)$\", \"$2 $1\")", ("name", "Shah, Priya")), Is.EqualTo("Priya Shah"));
    }

    [Test]
    public void RegexReplace_IgnoringCase_ReplacesEitherCase()
    {
        Assert.That(Eval("RegexReplace(cs[\"mail\"], \"^smtp:\", \"\", true)", ("mail", "SMTP:priya@example.org")), Is.EqualTo("priya@example.org"));
    }

    [Test]
    public void RegexReplace_AppliesToEachValue()
    {
        var result = Eval("RegexReplace(cs[\"proxyAddresses\"], \"^[A-Za-z0-9]+:\", \"\")",
            ("proxyAddresses", new List<object?> { "SMTP:a@example.org", "smtp:b@example.org" }));

        Assert.That((ExpressionValues)result!, Is.EqualTo(new object[] { "a@example.org", "b@example.org" }));
    }

    [Test]
    public void RegexReplace_OnNoValue_IsNoValue()
    {
        Assert.That(Eval("RegexReplace(cs[\"missing\"], \"a\", \"b\")"), Is.Null);
    }

    #endregion

    #region RegexExtract

    [Test]
    public void RegexExtract_WithoutAGroup_IsTheWholeMatch()
    {
        Assert.That(Eval("RegexExtract(cs[\"mail\"], \"@.+$\")", ("mail", "priya@example.org")), Is.EqualTo("@example.org"));
    }

    [Test]
    public void RegexExtract_WithAGroup_IsThatGroup()
    {
        Assert.That(Eval("RegexExtract(cs[\"mail\"], \"@(.+)$\", 1)", ("mail", "priya@example.org")), Is.EqualTo("example.org"));
    }

    [Test]
    public void RegexExtract_NoMatch_IsNoValue()
    {
        Assert.That(Eval("RegexExtract(cs[\"mail\"], \"^x\")", ("mail", "priya@example.org")), Is.Null);
    }

    [Test]
    public void RegexExtract_AGroupThePatternDoesNotHave_IsAnError()
    {
        var ex = Assert.Throws<ExpressionEvaluationException>(() => Eval("RegexExtract(cs[\"mail\"], \"@(.+)$\", 2)", ("mail", "a@b")));

        Assert.That(ex!.Message, Does.Contain("group 2"));
    }

    [Test]
    public void RegexExtract_AppliesToEachValue()
    {
        var result = Eval("RegexExtract(cs[\"proxyAddresses\"], \"@(.+)$\", 1)",
            ("proxyAddresses", new List<object?> { "SMTP:a@example.org", "smtp:b@example.com" }));

        Assert.That((ExpressionValues)result!, Is.EqualTo(new object[] { "example.org", "example.com" }));
    }

    #endregion

    #region Patterns are the catalogue's

    [TestCase("IsMatch(cs[\"x\"], \"(\")")]
    [TestCase("RegexReplace(cs[\"x\"], \"[\", \"\")")]
    [TestCase("RegexExtract(cs[\"x\"], \"(?<\")")]
    public void APatternThatIsNotARegularExpression_IsAnErrorNamingThePattern(string expression)
    {
        var ex = Assert.Throws<ExpressionEvaluationException>(() => Eval(expression, ("x", "abc")));

        Assert.That(ex!.Message, Does.Contain("not a valid regular expression"));
    }

    [TestCase("IsMatch(cs[\"x\"], \"^(a+)+$\")")]
    [TestCase("RegexReplace(cs[\"x\"], \"^(a+)+$\", \"\")")]
    [TestCase("RegexExtract(cs[\"x\"], \"^(a+)+$\")")]
    public void APatternThatRunsPastItsTimeLimit_IsAnExpressionError(string expression)
    {
        var ex = Assert.Throws<ExpressionEvaluationException>(() => Eval(expression, ("x", new string('a', 5000) + "!")));

        Assert.That(ex!.Message, Does.Contain("time limit"));
    }

    #endregion
}
