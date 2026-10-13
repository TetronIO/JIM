// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;

namespace JIM.Utilities.Tests;

public class SearchComparisonOperatorsTests
{
    private static readonly CriterionContext[] EveryContext = Enum.GetValues<CriterionContext>();

    [Test]
    public void ValidOperatorsFor_TextInScoping_ReturnsTextOperatorsInDisplayOrder()
    {
        var operators = SearchComparisonOperators.ValidOperatorsFor(AttributeDataType.Text, CriterionContext.Scoping);

        Assert.That(operators, Is.EqualTo(new[]
        {
            SearchComparisonType.Equals,
            SearchComparisonType.NotEquals,
            SearchComparisonType.StartsWith,
            SearchComparisonType.NotStartsWith,
            SearchComparisonType.EndsWith,
            SearchComparisonType.NotEndsWith,
            SearchComparisonType.Contains,
            SearchComparisonType.NotContains,
            SearchComparisonType.MatchesPattern,
            SearchComparisonType.NotMatchesPattern,
            SearchComparisonType.HasValue,
            SearchComparisonType.HasNoValue
        }));
    }

    [Test]
    public void ValidOperatorsFor_TextInPredefinedSearch_LeavesOutThePatternOperators()
    {
        var operators = SearchComparisonOperators.ValidOperatorsFor(AttributeDataType.Text, CriterionContext.PredefinedSearch);

        Assert.That(operators, Is.EqualTo(new[]
        {
            SearchComparisonType.Equals,
            SearchComparisonType.NotEquals,
            SearchComparisonType.StartsWith,
            SearchComparisonType.NotStartsWith,
            SearchComparisonType.EndsWith,
            SearchComparisonType.NotEndsWith,
            SearchComparisonType.Contains,
            SearchComparisonType.NotContains,
            SearchComparisonType.HasValue,
            SearchComparisonType.HasNoValue
        }));
    }

    [Test]
    public void ValidOperatorsFor_TextInExampleData_OffersThePatternOperators()
    {
        var operators = SearchComparisonOperators.ValidOperatorsFor(AttributeDataType.Text, CriterionContext.ExampleData);

        Assert.That(operators, Does.Contain(SearchComparisonType.MatchesPattern).And.Contain(SearchComparisonType.NotMatchesPattern));
    }

    [Test]
    public void ValidOperatorsFor_DateTime_ReturnsOrderedOperatorsWithDateDisplayOrder()
    {
        var operators = SearchComparisonOperators.ValidOperatorsFor(AttributeDataType.DateTime, CriterionContext.Scoping);

        Assert.That(operators, Is.EqualTo(new[]
        {
            SearchComparisonType.LessThan,
            SearchComparisonType.LessThanOrEquals,
            SearchComparisonType.GreaterThan,
            SearchComparisonType.GreaterThanOrEquals,
            SearchComparisonType.Equals,
            SearchComparisonType.NotEquals,
            SearchComparisonType.HasValue,
            SearchComparisonType.HasNoValue
        }));
    }

    [TestCase(AttributeDataType.Number)]
    [TestCase(AttributeDataType.LongNumber)]
    [TestCase(AttributeDataType.Decimal)]
    public void ValidOperatorsFor_Numbers_ReturnOrderedOperators(AttributeDataType type)
    {
        Assert.That(SearchComparisonOperators.ValidOperatorsFor(type, CriterionContext.Scoping), Is.EqualTo(new[]
        {
            SearchComparisonType.Equals,
            SearchComparisonType.NotEquals,
            SearchComparisonType.LessThan,
            SearchComparisonType.LessThanOrEquals,
            SearchComparisonType.GreaterThan,
            SearchComparisonType.GreaterThanOrEquals,
            SearchComparisonType.HasValue,
            SearchComparisonType.HasNoValue
        }));
    }

    [TestCase(AttributeDataType.Boolean)]
    [TestCase(AttributeDataType.Guid)]
    public void ValidOperatorsFor_BooleanAndGuid_ReturnEqualityAndPresenceOperators(AttributeDataType type)
    {
        Assert.That(SearchComparisonOperators.ValidOperatorsFor(type, CriterionContext.Scoping), Is.EqualTo(new[]
        {
            SearchComparisonType.Equals,
            SearchComparisonType.NotEquals,
            SearchComparisonType.HasValue,
            SearchComparisonType.HasNoValue
        }));
    }

    [TestCase(AttributeDataType.Binary)]
    [TestCase(AttributeDataType.Reference)]
    [TestCase(AttributeDataType.NotSet)]
    public void ValidOperatorsFor_UnsupportedTypes_ReturnEmptyInEveryContext(AttributeDataType type)
    {
        foreach (var context in EveryContext)
            Assert.That(SearchComparisonOperators.ValidOperatorsFor(type, context), Is.Empty, $"{type} in {context}");
    }

    [Test]
    public void IsValid_PatternOperatorInPredefinedSearch_IsFalse()
    {
        // Predefined Searches run as SQL, and regular expressions are never translated to SQL.
        Assert.That(SearchComparisonOperators.IsValid(SearchComparisonType.MatchesPattern, AttributeDataType.Text, CriterionContext.PredefinedSearch), Is.False);
    }

    [Test]
    public void IsValid_PatternOperatorOnNumber_IsFalse()
    {
        Assert.That(SearchComparisonOperators.IsValid(SearchComparisonType.MatchesPattern, AttributeDataType.Number, CriterionContext.Scoping), Is.False);
    }

    [Test]
    public void IsValid_TextOperatorOnDateTime_IsFalse()
    {
        Assert.That(SearchComparisonOperators.IsValid(SearchComparisonType.StartsWith, AttributeDataType.DateTime, CriterionContext.Scoping), Is.False);
    }

    [Test]
    public void IsValid_NotSet_IsFalseInEveryContext()
    {
        foreach (var context in EveryContext)
            Assert.That(SearchComparisonOperators.IsValid(SearchComparisonType.NotSet, AttributeDataType.Text, context), Is.False);
    }

    [Test]
    public void IsValid_UndefinedOrdinal_IsFalse()
    {
        Assert.That(SearchComparisonOperators.IsValid((SearchComparisonType)999, AttributeDataType.Text, CriterionContext.Scoping), Is.False);
    }

    [Test]
    public void All_ListsEveryOperatorExceptNotSet()
    {
        var everyOperator = Enum.GetValues<SearchComparisonType>().Where(op => op != SearchComparisonType.NotSet);

        Assert.That(SearchComparisonOperators.All, Is.EquivalentTo(everyOperator));
    }

    [Test]
    public void OperatorsFor_PredefinedSearch_IsEveryOperatorOfferedThere()
    {
        // What PowerShell's Predefined Search cmdlets put in their ValidateSet.
        Assert.That(SearchComparisonOperators.OperatorsFor(CriterionContext.PredefinedSearch),
            Is.EquivalentTo(SearchComparisonOperators.All.Except(new[] { SearchComparisonType.MatchesPattern, SearchComparisonType.NotMatchesPattern })));
    }

    [Test]
    public void OperatorsFor_Scoping_IsEveryOperator()
    {
        Assert.That(SearchComparisonOperators.OperatorsFor(CriterionContext.Scoping), Is.EquivalentTo(SearchComparisonOperators.All));
    }

    [TestCase(SearchComparisonType.NotEquals, SearchComparisonType.Equals)]
    [TestCase(SearchComparisonType.NotStartsWith, SearchComparisonType.StartsWith)]
    [TestCase(SearchComparisonType.NotEndsWith, SearchComparisonType.EndsWith)]
    [TestCase(SearchComparisonType.NotContains, SearchComparisonType.Contains)]
    [TestCase(SearchComparisonType.NotMatchesPattern, SearchComparisonType.MatchesPattern)]
    public void Negates_NegatedOperator_ReturnsTheOperatorItNegates(SearchComparisonType negated, SearchComparisonType positive)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SearchComparisonOperators.IsNegated(negated), Is.True);
            Assert.That(SearchComparisonOperators.Negates(negated), Is.EqualTo(positive));
        }
    }

    [Test]
    public void IsNegated_EveryOtherOperator_IsFalse()
    {
        // The scoping evaluator decides "any value" versus "no value" semantics for multi-valued attributes from this
        // (#1923). The presence operators are not negations of a per-value test: "has no value" is about the count.
        var negated = new[]
        {
            SearchComparisonType.NotEquals, SearchComparisonType.NotStartsWith, SearchComparisonType.NotEndsWith,
            SearchComparisonType.NotContains, SearchComparisonType.NotMatchesPattern
        };

        foreach (var op in Enum.GetValues<SearchComparisonType>().Except(negated))
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(SearchComparisonOperators.IsNegated(op), Is.False, op.ToString());
                Assert.That(SearchComparisonOperators.Negates(op), Is.Null, op.ToString());
            }
        }
    }

    [TestCase(SearchComparisonType.HasValue, false)]
    [TestCase(SearchComparisonType.HasNoValue, false)]
    [TestCase(SearchComparisonType.Equals, true)]
    [TestCase(SearchComparisonType.MatchesPattern, true)]
    [TestCase(SearchComparisonType.GreaterThan, true)]
    public void TakesValue_ReturnsWhetherTheOperatorComparesAgainstAValue(SearchComparisonType op, bool expected)
    {
        Assert.That(SearchComparisonOperators.TakesValue(op), Is.EqualTo(expected));
    }

    [TestCase(SearchComparisonType.MatchesPattern, true)]
    [TestCase(SearchComparisonType.NotMatchesPattern, true)]
    [TestCase(SearchComparisonType.Contains, false)]
    public void IsPattern_ReturnsWhetherTheOperatorTakesARegularExpression(SearchComparisonType op, bool expected)
    {
        Assert.That(SearchComparisonOperators.IsPattern(op), Is.EqualTo(expected));
    }

    [TestCase(SearchComparisonType.HasValue, AttributeDataType.Number, "has a value")]
    [TestCase(SearchComparisonType.HasNoValue, AttributeDataType.DateTime, "has no value")]
    [TestCase(SearchComparisonType.MatchesPattern, AttributeDataType.Text, "matches pattern")]
    [TestCase(SearchComparisonType.NotMatchesPattern, AttributeDataType.Text, "does not match pattern")]
    [TestCase(SearchComparisonType.LessThanOrEquals, AttributeDataType.DateTime, "on or before")]
    public void LabelFor_ReturnsTheEditorsWording(SearchComparisonType op, AttributeDataType type, string expected)
    {
        Assert.That(SearchComparisonOperators.LabelFor(op, type), Is.EqualTo(expected));
    }

    [Test]
    public void LabelFor_EveryOperatorOnEveryTypeItAppliesTo_IsHumanWording()
    {
        // A label falling back to the split enum name ("Not Matches Pattern") means an operator was added without
        // wording; every label the editors and explanations show must come from the catalogue.
        foreach (var type in Enum.GetValues<AttributeDataType>())
        foreach (var op in SearchComparisonOperators.ValidOperatorsFor(type, CriterionContext.Scoping))
            Assert.That(SearchComparisonOperators.LabelFor(op, type), Does.Match("^[a-z ]+$"), $"{op} on {type}");
    }

    [Test]
    public void ValidatePattern_ValidExpression_ReturnsNoError()
    {
        Assert.That(SearchComparisonOperators.ValidatePattern(@"^[a-z.]+@example\.org$"), Is.Null);
    }

    [TestCase("[a-z")]
    [TestCase("(?<open")]
    public void ValidatePattern_InvalidExpression_ReturnsAnError(string pattern)
    {
        Assert.That(SearchComparisonOperators.ValidatePattern(pattern), Does.StartWith("The pattern is not a valid regular expression"));
    }

    [TestCase(null)]
    [TestCase("")]
    public void ValidatePattern_NoPattern_ReturnsAnError(string? pattern)
    {
        Assert.That(SearchComparisonOperators.ValidatePattern(pattern), Is.EqualTo("A pattern is required."));
    }

    [Test]
    public void ValidatePattern_TooLong_ReturnsAnError()
    {
        Assert.That(SearchComparisonOperators.ValidatePattern(new string('a', SearchComparisonOperators.MaximumPatternLength + 1)),
            Does.Contain("longer than"));
    }
}
