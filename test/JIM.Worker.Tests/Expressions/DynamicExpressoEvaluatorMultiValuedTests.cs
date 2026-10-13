// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

/// <summary>
/// Multi-valued attributes in expressions (#2053, PRD Phase 2 requirement 8, Scenario 13): an accessor reads every
/// value, transforms apply to each, comparisons are true when any value satisfies them, whole-set functions work on
/// the set, and several values where one is needed are an error naming the attribute and the count.
/// </summary>
[TestFixture]
public class DynamicExpressoEvaluatorMultiValuedTests
{
    private const string Primary = "SMTP:priya.shah@example.org";
    private const string Secondary = "smtp:p.shah@example.org";

    private IExpressionEvaluator _evaluator = null!;
    private ExpressionContext _priya = null!;

    [SetUp]
    public void SetUp()
    {
        _evaluator = new DynamicExpressoEvaluator();
        _priya = Cs(("proxyAddresses", new List<object?> { Secondary, Primary }), ("cn", "Priya Shah"));
    }

    #region Scenario 13

    [Test]
    public void Scenario13_TheAccessor_ReadsBothAddressesInOrdinalOrder()
    {
        Assert.That(Values("cs[\"proxyAddresses\"]"), Is.EqualTo(new object[] { Primary, Secondary }));
    }

    [Test]
    public void Scenario13_Lower_LowerCasesEachAddress()
    {
        Assert.That(Values("Lower(cs[\"proxyAddresses\"])"), Is.EqualTo(new object[] { Primary.ToLowerInvariant(), Secondary }));
    }

    [Test]
    public void Scenario13_StartsWith_IsTrueWhenOneAddressDoes()
    {
        Assert.That(Eval("StartsWith(cs[\"proxyAddresses\"], \"SMTP:\")"), Is.True);
    }

    [Test]
    public void Scenario13_NotStartsWith_IsTrueWhenNoAddressDoes()
    {
        Assert.That(Eval("!StartsWith(cs[\"proxyAddresses\"], \"x500:\")"), Is.True);
    }

    [Test]
    public void Scenario13_Count_IsTwo()
    {
        Assert.That(Eval("Count(cs[\"proxyAddresses\"])"), Is.EqualTo(2));
    }

    [Test]
    public void Scenario13_Join_KeepsOrdinalOrder()
    {
        Assert.That(Eval("Join(cs[\"proxyAddresses\"], \";\")"), Is.EqualTo($"{Primary};{Secondary}"));
    }

    [Test]
    public void Scenario13_Where_KeepsTheAddressThatStartsWithUpperCaseSmtp()
    {
        Assert.That(Eval("Where(cs[\"proxyAddresses\"], \"starts with\", \"SMTP:\")"), Is.EqualTo(Primary));
    }

    [Test]
    public void Scenario13_ReplaceOverWhere_GivesThePrimaryAddress()
    {
        Assert.That(Eval("Replace(Where(cs[\"proxyAddresses\"], \"starts with\", \"SMTP:\"), \"SMTP:\", \"\")"),
            Is.EqualTo("priya.shah@example.org"));
    }

    #endregion

    #region One value is unaffected

    [Test]
    public void OneValue_InAList_ReadsAndTransformsExactlyAsASingleValue()
    {
        var context = Cs(("mail", new List<object?> { "Priya@Example.org" }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_evaluator.Evaluate("cs[\"mail\"]", context), Is.EqualTo("Priya@Example.org"));
            Assert.That(_evaluator.Evaluate("Lower(cs[\"mail\"]) + \"!\"", context), Is.EqualTo("priya@example.org!"));
            Assert.That(_evaluator.Evaluate("Lower(cs[\"mail\"]) == \"priya@example.org\"", context), Is.True);
        }
    }

    [Test]
    public void LateBinding_TwoTransformResultsJoinedWithPlus_Parse()
    {
        // Transforms return several values or one, so their results are untyped; late binding is what keeps these working.
        var context = Cs(("givenName", "priya"), ("sn", "shah"));

        Assert.That(_evaluator.Evaluate("Capitalise(cs[\"givenName\"]) + \" \" + Upper(cs[\"sn\"])", context), Is.EqualTo("Priya SHAH"));
    }

    [Test]
    public void LateBinding_TransformResultComparedWithEqualsOperator_ComparesTheText()
    {
        // A text value built at run time is a different instance from the literal: reference equality would be false.
        var context = Cs(("dept", new string("FINANCE".ToCharArray())));

        Assert.That(_evaluator.Evaluate("Lower(cs[\"dept\"]) == \"finance\"", context), Is.True);
    }

    #endregion

    #region Several values where one is needed

    [TestCase("cs[\"proxyAddresses\"] + \"x\"")]
    [TestCase("\"x\" + cs[\"proxyAddresses\"]")]
    [TestCase("cs[\"proxyAddresses\"] == \"x\"")]
    [TestCase("cs[\"proxyAddresses\"].ToString()")]
    [TestCase("Length(cs[\"proxyAddresses\"])")]
    [TestCase("Split(cs[\"proxyAddresses\"], \":\")")]
    [TestCase("Replace(cs[\"cn\"], cs[\"proxyAddresses\"], \"\")")]
    public void SeveralValues_WhereOneIsNeeded_IsAnErrorNamingTheAttributeAndCount(string expression)
    {
        var ex = Assert.Throws<ExpressionEvaluationException>(() => _evaluator.Evaluate(expression, _priya));

        Assert.That(ex!.Message, Does.Contain("'proxyAddresses' has 2 values"));
    }

    [TestCase("cs[\"levels\"] > 1")]
    [TestCase("cs[\"levels\"] + 1")]
    [TestCase("ToInt(cs[\"levels\"])")]
    [TestCase("HasBit(cs[\"levels\"], 2)")]
    public void SeveralNumbers_WhereOneIsNeeded_IsAnError(string expression)
    {
        var context = Cs(("levels", new List<object?> { 1, 2 }));

        var ex = Assert.Throws<ExpressionEvaluationException>(() => _evaluator.Evaluate(expression, context));

        Assert.That(ex!.Message, Does.Contain("'levels' has 2 values"));
    }

    [Test]
    public void SeveralValues_ComparedWithNull_IsAPresenceTest()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Eval("cs[\"proxyAddresses\"] == null"), Is.False);
            Assert.That(Eval("cs[\"proxyAddresses\"] != null"), Is.True);
        }
    }

    [Test]
    public void SeveralValues_TransformedThenUsedAsOne_StillNamesTheAttribute()
    {
        var ex = Assert.Throws<ExpressionEvaluationException>(() => _evaluator.Evaluate("Upper(cs[\"proxyAddresses\"]) + \"x\"", _priya));

        Assert.That(ex!.Message, Does.Contain("'proxyAddresses' has 2 values"));
    }

    #endregion

    #region Transforms

    [Test]
    public void Transform_WithArguments_AppliesToEachValue()
    {
        Assert.That(Values("Left(cs[\"proxyAddresses\"], 4)"), Is.EqualTo(new object[] { "SMTP", "smtp" }));
    }

    [Test]
    public void Transform_Chained_AppliesToEachValueInOrder()
    {
        Assert.That(Values("Upper(Replace(cs[\"proxyAddresses\"], \"example.org\", \"example.com\"))"),
            Is.EqualTo(new object[] { "SMTP:PRIYA.SHAH@EXAMPLE.COM", "SMTP:P.SHAH@EXAMPLE.COM" }));
    }

    [Test]
    public void FormatDate_OverSeveralDates_FormatsEach()
    {
        var context = Cs(("dates", new List<object?> { new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc) }));

        Assert.That(Values("FormatDate(cs[\"dates\"], \"yyyy-MM-dd\")", context), Is.EqualTo(new object[] { "2025-01-02", "2026-03-01" }));
    }

    #endregion

    #region Comparisons

    [Test]
    public void Eq_IsTrueWhenAnyValueEquals()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Eval($"Eq(cs[\"proxyAddresses\"], \"{Secondary}\")"), Is.True);
            Assert.That(Eval("Eq(cs[\"proxyAddresses\"], \"nobody\")"), Is.False);
        }
    }

    [Test]
    public void Eq_SeveralOnBothSides_IsTrueWhenAnyPairEquals()
    {
        var context = Cs(("a", new List<object?> { "x", "y" }), ("b", new List<object?> { "y", "z" }));

        Assert.That(_evaluator.Evaluate("Eq(cs[\"a\"], cs[\"b\"])", context), Is.True);
    }

    [Test]
    public void Contains_IsTrueWhenAnyValueContains()
    {
        Assert.That(Eval("Contains(cs[\"proxyAddresses\"], \"p.shah\")"), Is.True);
    }

    [Test]
    public void EndsWith_IsFalseWhenNoValueDoes()
    {
        Assert.That(Eval("EndsWith(cs[\"proxyAddresses\"], \".com\")"), Is.False);
    }

    [Test]
    public void StartsWith_OnNoValue_IsFalse()
    {
        Assert.That(Eval("StartsWith(cs[\"missing\"], \"x\")"), Is.False);
    }

    #endregion

    #region Whole-set functions

    [TestCase("Count(cs[\"missing\"])", 0)]
    [TestCase("Count(cs[\"cn\"])", 1)]
    [TestCase("Count(cs[\"empty\"])", 0)]
    public void Count_CountsValues(string expression, int expected)
    {
        var context = Cs(("cn", "Priya"), ("empty", ""));

        Assert.That(_evaluator.Evaluate(expression, context), Is.EqualTo(expected));
    }

    [Test]
    public void MinAndMax_OverNumbers_CompareByValue()
    {
        var context = Cs(("levels", new List<object?> { 10, 2, 33 }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_evaluator.Evaluate("Min(cs[\"levels\"])", context), Is.EqualTo(2));
            Assert.That(_evaluator.Evaluate("Max(cs[\"levels\"])", context), Is.EqualTo(33));
        }
    }

    [Test]
    public void MinAndMax_OverDates_CompareByInstant()
    {
        var earlier = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var later = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var context = Cs(("dates", new List<object?> { later, earlier }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_evaluator.Evaluate("Min(cs[\"dates\"])", context), Is.EqualTo(earlier));
            Assert.That(_evaluator.Evaluate("Max(cs[\"dates\"])", context), Is.EqualTo(later));
        }
    }

    [Test]
    public void MinAndMax_OverText_CompareByOrdinal()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Eval("Min(cs[\"proxyAddresses\"])"), Is.EqualTo(Primary));
            Assert.That(Eval("Max(cs[\"proxyAddresses\"])"), Is.EqualTo(Secondary));
        }
    }

    [Test]
    public void MinAndMax_OverNoValue_IsNull()
    {
        Assert.That(Eval("Max(cs[\"missing\"])"), Is.Null);
    }

    [Test]
    public void Min_OverBooleans_IsAnError()
    {
        var context = Cs(("flags", new List<object?> { true, false }));

        var ex = Assert.Throws<ExpressionEvaluationException>(() => _evaluator.Evaluate("Min(cs[\"flags\"])", context));

        Assert.That(ex!.Message, Does.Contain("Min"));
    }

    [Test]
    public void Where_WithTheCataloguesOperatorName_FiltersEachValue()
    {
        var context = Cs(("levels", new List<object?> { 10, 2, 33 }));

        Assert.That(Values("Where(cs[\"levels\"], \"greater than\", \"5\")", context), Is.EqualTo(new object[] { 10, 33 }));
    }

    [Test]
    public void Where_WithTheOperatorsCodeName_FiltersEachValue()
    {
        Assert.That(Eval("Where(cs[\"proxyAddresses\"], \"StartsWith\", \"smtp:\")"), Is.EqualTo(Secondary));
    }

    [Test]
    public void Where_IgnoringCase_KeepsBoth()
    {
        Assert.That(Values("Where(cs[\"proxyAddresses\"], \"starts with\", \"smtp:\", true)"), Is.EqualTo(new object[] { Primary, Secondary }));
    }

    [Test]
    public void Where_MatchingNothing_IsNoValue()
    {
        Assert.That(Eval("Where(cs[\"proxyAddresses\"], \"starts with\", \"x500:\")"), Is.Null);
    }

    [Test]
    public void Where_WithAPattern_UsesTheCataloguesPatterns()
    {
        Assert.That(Eval("Where(cs[\"proxyAddresses\"], \"matches pattern\", \"^SMTP:\")"), Is.EqualTo(Primary));
    }

    [Test]
    public void Where_WithAnOperatorItDoesNotKnow_IsAnErrorListingTheOperators()
    {
        var ex = Assert.Throws<ExpressionEvaluationException>(() => _evaluator.Evaluate("Where(cs[\"proxyAddresses\"], \"begins with\", \"x\")", _priya));

        Assert.That(ex!.Message, Does.Contain("begins with").And.Contain("starts with"));
    }

    [Test]
    public void Where_WithAPresenceOperator_IsAnError()
    {
        var ex = Assert.Throws<ExpressionEvaluationException>(() => _evaluator.Evaluate("Where(cs[\"proxyAddresses\"], \"has a value\", \"\")", _priya));

        Assert.That(ex!.Message, Does.Contain("has a value"));
    }

    [Test]
    public void SplitThenJoin_GivesBackWhatItWasGiven()
    {
        var context = Cs(("courses", "C3|C1|C2"));

        Assert.That(_evaluator.Evaluate("Join(Split(cs[\"courses\"], \"|\"), \"|\")", context), Is.EqualTo("C3|C1|C2"));
    }

    [Test]
    public void Split_ResultCanStillBeIndexed()
    {
        var context = Cs(("courses", "C3|C1|C2"));

        Assert.That(_evaluator.Evaluate("Split(cs[\"courses\"], \"|\")[1]", context), Is.EqualTo("C1"));
    }

    #endregion

    #region One definition of no value

    [TestCase("IsNullOrEmpty(cs[\"missing\"])", true)]
    [TestCase("IsNullOrEmpty(cs[\"empty\"])", true)]
    [TestCase("IsNullOrEmpty(cs[\"none\"])", true)]
    [TestCase("IsNullOrEmpty(cs[\"proxyAddresses\"])", false)]
    [TestCase("Coalesce(cs[\"empty\"], \"fallback\")", "fallback")]
    [TestCase("Coalesce(cs[\"none\"], \"fallback\")", "fallback")]
    [TestCase("Coalesce(cs[\"cn\"], \"fallback\")", "Priya")]
    public void NoValue_IsNullEmptyTextOrAnEmptyList(string expression, object expected)
    {
        var context = Cs(("empty", ""), ("none", new List<object?>()), ("cn", "Priya"), ("proxyAddresses", new List<object?> { "a", "b" }));

        Assert.That(_evaluator.Evaluate(expression, context), Is.EqualTo(expected));
    }

    #endregion

    #region Removed

    [TestCase("CollectionContains(cs[\"proxyAddresses\"], \"x\")")]
    [TestCase("cs.HasAttribute(\"proxyAddresses\")")]
    [TestCase("cs.AttributeNames")]
    public void Removed_FunctionsAndMembers_NoLongerParse(string expression)
    {
        Assert.That(_evaluator.Validate(expression).IsValid, Is.False);
    }

    #endregion

    private object? Eval(string expression) => _evaluator.Evaluate(expression, _priya);

    private IReadOnlyList<object?> Values(string expression, ExpressionContext? context = null)
    {
        var result = _evaluator.Evaluate(expression, context ?? _priya);
        Assert.That(result, Is.InstanceOf<ExpressionValues>(), "several values");
        return (ExpressionValues)result!;
    }

    private static ExpressionContext Cs(params (string Name, object? Value)[] attributes) =>
        new(connectedSystemAttributes: attributes.ToDictionary(a => a.Name, a => a.Value));
}
