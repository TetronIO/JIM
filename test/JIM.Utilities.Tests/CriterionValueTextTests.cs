// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;

namespace JIM.Utilities.Tests;

public class CriterionValueTextTests
{
    [Test]
    public void Read_Text_SetsTheStringValue()
    {
        var values = new CriterionValues();

        Assert.That(CriterionValueText.Read("Contractor", AttributeDataType.Text, values), Is.Null);
        Assert.That(values.StringValue, Is.EqualTo("Contractor"));
    }

    [Test]
    public void Read_NumberInInvariantCulture_SetsTheIntValue()
    {
        var values = new CriterionValues();

        Assert.That(CriterionValueText.Read("42", AttributeDataType.Number, values), Is.Null);
        Assert.That(values.IntValue, Is.EqualTo(42));
    }

    [Test]
    public void Read_Decimal_UsesAFullStopWhateverTheCulture()
    {
        var values = new CriterionValues();

        Assert.That(CriterionValueText.Read("1234.5", AttributeDataType.Decimal, values), Is.Null);
        Assert.That(values.DecimalValue, Is.EqualTo(1234.5m));
    }

    [Test]
    public void Read_Iso8601Date_SetsAUtcDateTimeValue()
    {
        var values = new CriterionValues();

        Assert.That(CriterionValueText.Read("2026-07-01T09:30:00Z", AttributeDataType.DateTime, values), Is.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(values.DateTimeValue, Is.EqualTo(new DateTime(2026, 7, 1, 9, 30, 0, DateTimeKind.Utc)));
            Assert.That(values.DateTimeValue!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
        }
    }

    [TestCase("true", true)]
    [TestCase("False", false)]
    public void Read_Boolean_SetsTheBoolValue(string text, bool expected)
    {
        var values = new CriterionValues();

        Assert.That(CriterionValueText.Read(text, AttributeDataType.Boolean, values), Is.Null);
        Assert.That(values.BoolValue, Is.EqualTo(expected));
    }

    [TestCase("forty-two", AttributeDataType.Number)]
    [TestCase("1.5", AttributeDataType.LongNumber)]
    [TestCase("01/07/2026", AttributeDataType.DateTime)]
    [TestCase("yes", AttributeDataType.Boolean)]
    [TestCase("not-a-guid", AttributeDataType.Guid)]
    public void Read_TextThatIsNotTheType_ReturnsAnError(string text, AttributeDataType type)
    {
        Assert.That(CriterionValueText.Read(text, type, new CriterionValues()), Does.StartWith($"'{text}' is not a"));
    }

    [Test]
    public void Read_NoText_SetsNothing()
    {
        var values = new CriterionValues();

        Assert.That(CriterionValueText.Read(null, AttributeDataType.Number, values), Is.Null);
        Assert.That(values.IntValue, Is.Null);
    }
}
