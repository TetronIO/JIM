// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;

namespace JIM.Utilities.Tests;

public class CriterionValidationTests
{
    private static PredefinedSearchCriteria Criterion(SearchComparisonType op) => new() { ComparisonType = op };

    [Test]
    public void Validate_TextEqualsWithAValue_IsValid()
    {
        var criterion = Criterion(SearchComparisonType.Equals);
        criterion.StringValue = "Finance";

        Assert.That(CriterionValidation.Validate(criterion, AttributeDataType.Text, CriterionContext.Scoping, "department"), Is.Null);
    }

    [TestCase(AttributeDataType.Text, "StringValue is required")]
    [TestCase(AttributeDataType.Number, "IntValue is required")]
    [TestCase(AttributeDataType.LongNumber, "LongValue is required")]
    [TestCase(AttributeDataType.Decimal, "DecimalValue is required")]
    [TestCase(AttributeDataType.DateTime, "DateTimeValue is required")]
    [TestCase(AttributeDataType.Boolean, "BoolValue is required")]
    [TestCase(AttributeDataType.Guid, "GuidValue is required")]
    public void Validate_ValueTakingOperatorWithNoValue_PointsAtThePresenceOperators(AttributeDataType type, string expected)
    {
        // "Equals with no value" was the workaround for "has no value"; it is refused now the operator exists.
        var error = CriterionValidation.Validate(Criterion(SearchComparisonType.Equals), type, CriterionContext.Scoping, "attr");

        Assert.That(error, Does.StartWith(expected).And.Contain("HasNoValue"));
    }

    [TestCase(SearchComparisonType.HasValue)]
    [TestCase(SearchComparisonType.HasNoValue)]
    public void Validate_PresenceOperatorWithNoValue_IsValidOnEveryType(SearchComparisonType op)
    {
        foreach (var type in new[] { AttributeDataType.Text, AttributeDataType.Number, AttributeDataType.LongNumber, AttributeDataType.Decimal,
                     AttributeDataType.DateTime, AttributeDataType.Boolean, AttributeDataType.Guid })
        {
            Assert.That(CriterionValidation.Validate(Criterion(op), type, CriterionContext.PredefinedSearch, "attr"), Is.Null, type.ToString());
        }
    }

    [Test]
    public void Validate_PatternInPredefinedSearch_IsRefusedNamingWhereItIsOffered()
    {
        var criterion = Criterion(SearchComparisonType.MatchesPattern);
        criterion.StringValue = "^svc_";

        Assert.That(CriterionValidation.Validate(criterion, AttributeDataType.Text, CriterionContext.PredefinedSearch, "name"),
            Does.Contain("not available in Predefined Searches"));
    }

    [Test]
    public void Validate_InvalidPatternInScoping_IsRefused()
    {
        var criterion = Criterion(SearchComparisonType.MatchesPattern);
        criterion.StringValue = "[a-z";

        Assert.That(CriterionValidation.Validate(criterion, AttributeDataType.Text, CriterionContext.Scoping, "mail"),
            Does.StartWith("The pattern is not a valid regular expression"));
    }

    [Test]
    public void Validate_OperatorInvalidForType_IsRefused()
    {
        var criterion = Criterion(SearchComparisonType.StartsWith);
        criterion.IntValue = 5;

        Assert.That(CriterionValidation.Validate(criterion, AttributeDataType.Number, CriterionContext.Scoping, "level"),
            Is.EqualTo("Operator 'StartsWith' is not valid for the Number attribute 'level'."));
    }

    [Test]
    public void Validate_NotSet_IsRefused()
    {
        Assert.That(CriterionValidation.Validate(Criterion(SearchComparisonType.NotSet), AttributeDataType.Text, CriterionContext.Scoping, "a"),
            Is.EqualTo("Invalid comparison type 'NotSet'."));
    }

    [Test]
    public void Validate_RelativeDateWithEveryField_IsValid()
    {
        var criterion = Criterion(SearchComparisonType.LessThan);
        criterion.ValueMode = DateCriteriaValueMode.Relative;
        criterion.RelativeCount = 30;
        criterion.RelativeUnit = RelativeDateUnit.Days;
        criterion.RelativeDirection = RelativeDateDirection.Ago;

        Assert.That(CriterionValidation.Validate(criterion, AttributeDataType.DateTime, CriterionContext.Scoping, "hired"), Is.Null);
    }

    [Test]
    public void ClearValues_PresenceOperator_ClearsEveryCarrier()
    {
        var criterion = Criterion(SearchComparisonType.HasValue);
        criterion.StringValue = "stale";
        criterion.IntValue = 1;
        criterion.ValueMode = DateCriteriaValueMode.Relative;
        criterion.RelativeCount = 3;

        CriterionValidation.ClearValues(criterion);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(criterion.StringValue, Is.Null);
            Assert.That(criterion.IntValue, Is.Null);
            Assert.That(criterion.ValueMode, Is.EqualTo(DateCriteriaValueMode.Absolute));
            Assert.That(criterion.RelativeCount, Is.Null);
        }
    }

    [Test]
    public void ClearValues_ValueTakingOperator_LeavesTheValue()
    {
        var criterion = Criterion(SearchComparisonType.Equals);
        criterion.StringValue = "kept";

        CriterionValidation.ClearValues(criterion);

        Assert.That(criterion.StringValue, Is.EqualTo("kept"));
    }

    [Test]
    public void ValidateText_NumberDependency_ReadsTheTextAsANumber()
    {
        var error = CriterionValidation.ValidateText(SearchComparisonType.GreaterThan, "30", AttributeDataType.Number,
            CriterionContext.ExampleData, "Age", caseSensitive: true, out var criterion);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error, Is.Null);
            Assert.That(criterion.IntValue, Is.EqualTo(30));
            Assert.That(criterion.ComparisonType, Is.EqualTo(SearchComparisonType.GreaterThan));
        }
    }

    [Test]
    public void ValidateText_TextThatIsNotTheAttributesType_IsRefused()
    {
        var error = CriterionValidation.ValidateText(SearchComparisonType.Equals, "thirty", AttributeDataType.Number,
            CriterionContext.ExampleData, "Age", caseSensitive: true, out _);

        Assert.That(error, Does.StartWith("'thirty' is not a whole number"));
    }

    [Test]
    public void ValidateText_PresenceOperatorWithNoText_IsValid()
    {
        Assert.That(CriterionValidation.ValidateText(SearchComparisonType.HasNoValue, "", AttributeDataType.DateTime,
            CriterionContext.ExampleData, "Leaving Date", caseSensitive: true, out _), Is.Null);
    }
}
