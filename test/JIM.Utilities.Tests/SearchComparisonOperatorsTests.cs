// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;

namespace JIM.Utilities.Tests;

public class SearchComparisonOperatorsTests
{
    [Test]
    public void ValidOperatorsFor_Text_ReturnsTextOperatorsInDisplayOrder()
    {
        var operators = SearchComparisonOperators.ValidOperatorsFor(AttributeDataType.Text);

        Assert.That(operators, Is.EqualTo(new[]
        {
            SearchComparisonType.Equals,
            SearchComparisonType.NotEquals,
            SearchComparisonType.StartsWith,
            SearchComparisonType.NotStartsWith,
            SearchComparisonType.EndsWith,
            SearchComparisonType.NotEndsWith,
            SearchComparisonType.Contains,
            SearchComparisonType.NotContains
        }));
    }

    [Test]
    public void ValidOperatorsFor_DateTime_ReturnsOrderedOperatorsWithDateDisplayOrder()
    {
        var operators = SearchComparisonOperators.ValidOperatorsFor(AttributeDataType.DateTime);

        Assert.That(operators, Is.EqualTo(new[]
        {
            SearchComparisonType.LessThan,
            SearchComparisonType.LessThanOrEquals,
            SearchComparisonType.GreaterThan,
            SearchComparisonType.GreaterThanOrEquals,
            SearchComparisonType.Equals,
            SearchComparisonType.NotEquals
        }));
    }

    [Test]
    public void ValidOperatorsFor_Number_ReturnsOrderedOperators()
    {
        var expected = new[]
        {
            SearchComparisonType.Equals,
            SearchComparisonType.NotEquals,
            SearchComparisonType.LessThan,
            SearchComparisonType.LessThanOrEquals,
            SearchComparisonType.GreaterThan,
            SearchComparisonType.GreaterThanOrEquals
        };

        Assert.That(SearchComparisonOperators.ValidOperatorsFor(AttributeDataType.Number), Is.EqualTo(expected));
        Assert.That(SearchComparisonOperators.ValidOperatorsFor(AttributeDataType.LongNumber), Is.EqualTo(expected));
        Assert.That(SearchComparisonOperators.ValidOperatorsFor(AttributeDataType.Decimal), Is.EqualTo(expected));
    }

    [Test]
    public void IsValid_OrderedOperatorOnDecimal_IsTrue()
    {
        Assert.That(SearchComparisonOperators.IsValid(SearchComparisonType.GreaterThanOrEquals, AttributeDataType.Decimal), Is.True);
    }

    [Test]
    public void IsValid_TextOperatorOnDecimal_IsFalse()
    {
        Assert.That(SearchComparisonOperators.IsValid(SearchComparisonType.Contains, AttributeDataType.Decimal), Is.False);
    }

    [Test]
    public void ValidOperatorsFor_BooleanAndGuid_ReturnEqualityOperatorsOnly()
    {
        var expected = new[] { SearchComparisonType.Equals, SearchComparisonType.NotEquals };

        Assert.That(SearchComparisonOperators.ValidOperatorsFor(AttributeDataType.Boolean), Is.EqualTo(expected));
        Assert.That(SearchComparisonOperators.ValidOperatorsFor(AttributeDataType.Guid), Is.EqualTo(expected));
    }

    [Test]
    public void ValidOperatorsFor_UnsupportedTypes_ReturnEmpty()
    {
        Assert.That(SearchComparisonOperators.ValidOperatorsFor(AttributeDataType.Binary), Is.Empty);
        Assert.That(SearchComparisonOperators.ValidOperatorsFor(AttributeDataType.Reference), Is.Empty);
        Assert.That(SearchComparisonOperators.ValidOperatorsFor(AttributeDataType.NotSet), Is.Empty);
    }

    [Test]
    public void IsValid_TextOperatorOnDateTime_IsFalse()
    {
        Assert.That(SearchComparisonOperators.IsValid(SearchComparisonType.StartsWith, AttributeDataType.DateTime), Is.False);
    }

    [Test]
    public void IsValid_OrderedOperatorOnDateTime_IsTrue()
    {
        Assert.That(SearchComparisonOperators.IsValid(SearchComparisonType.GreaterThanOrEquals, AttributeDataType.DateTime), Is.True);
    }

    [Test]
    public void IsValid_NotSetOperator_IsAlwaysFalse()
    {
        Assert.That(SearchComparisonOperators.IsValid(SearchComparisonType.NotSet, AttributeDataType.Text), Is.False);
    }

    [Test]
    public void IsValid_ContainsOnText_IsTrue()
    {
        Assert.That(SearchComparisonOperators.IsValid(SearchComparisonType.Contains, AttributeDataType.Text), Is.True);
    }
}
