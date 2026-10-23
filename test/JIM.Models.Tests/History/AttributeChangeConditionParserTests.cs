// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Core;
using JIM.Models.History;
using NUnit.Framework;

namespace JIM.Models.Tests.History;

[TestFixture]
public class AttributeChangeConditionParserTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void TryParse_NoValue_MatchesAnyValue(string? value)
    {
        var parsed = AttributeChangeConditionParser.TryParse(AttributeDataType.Text, value, out var match, out var error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(match, Is.Null);
            Assert.That(error, Is.Null);
        }
    }

    [Test]
    public void TryParse_Text_KeepsTrimmedTextWithoutWildcard()
    {
        var parsed = AttributeChangeConditionParser.TryParse(AttributeDataType.Text, "  Disabled ", out var match, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(match!.Text, Is.EqualTo("Disabled"));
            Assert.That(match!.TextHasWildcard, Is.False);
        }
    }

    [Test]
    public void TryParse_TextWithAsterisk_FlagsWildcard()
    {
        AttributeChangeConditionParser.TryParse(AttributeDataType.Text, "Fin*", out var match, out _);

        Assert.That(match!.TextHasWildcard, Is.True);
    }

    [Test]
    public void TryParse_Reference_MatchesOnText()
    {
        var parsed = AttributeChangeConditionParser.TryParse(AttributeDataType.Reference, "Ana Ruiz", out var match, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(match!.Text, Is.EqualTo("Ana Ruiz"));
        }
    }

    [Test]
    public void TryParse_Number_ParsesInvariantInteger()
    {
        var parsed = AttributeChangeConditionParser.TryParse(AttributeDataType.Number, "42", out var match, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(match!.IntValue, Is.EqualTo(42));
        }
    }

    [Test]
    public void TryParse_LongNumber_ParsesLong()
    {
        AttributeChangeConditionParser.TryParse(AttributeDataType.LongNumber, "9000000000", out var match, out _);

        Assert.That(match!.LongValue, Is.EqualTo(9_000_000_000L));
    }

    [Test]
    public void TryParse_Decimal_ParsesInvariantDecimal()
    {
        AttributeChangeConditionParser.TryParse(AttributeDataType.Decimal, "12.50", out var match, out _);

        Assert.That(match!.DecimalValue, Is.EqualTo(12.50m));
    }

    [TestCase("true", true)]
    [TestCase("False", false)]
    public void TryParse_Boolean_ParsesTrueOrFalse(string value, bool expected)
    {
        AttributeChangeConditionParser.TryParse(AttributeDataType.Boolean, value, out var match, out _);

        Assert.That(match!.BoolValue, Is.EqualTo(expected));
    }

    [Test]
    public void TryParse_Guid_ParsesGuid()
    {
        var guid = Guid.NewGuid();

        AttributeChangeConditionParser.TryParse(AttributeDataType.Guid, guid.ToString(), out var match, out _);

        Assert.That(match!.GuidValue, Is.EqualTo(guid));
    }

    [Test]
    public void TryParse_DateOnly_MatchesTheWholeUtcDay()
    {
        AttributeChangeConditionParser.TryParse(AttributeDataType.DateTime, "2026-10-01", out var match, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(match!.DateTimeFrom, Is.EqualTo(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(match!.DateTimeTo, Is.EqualTo(new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(match!.DateTimeFrom!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
        }
    }

    [Test]
    public void TryParse_DateAndTime_MatchesThatSecond()
    {
        AttributeChangeConditionParser.TryParse(AttributeDataType.DateTime, "2026-10-01T09:14:00Z", out var match, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(match!.DateTimeFrom, Is.EqualTo(new DateTime(2026, 10, 1, 9, 14, 0, DateTimeKind.Utc)));
            Assert.That(match!.DateTimeTo, Is.EqualTo(new DateTime(2026, 10, 1, 9, 14, 1, DateTimeKind.Utc)));
        }
    }

    [TestCase(AttributeDataType.Number, "forty-two")]
    [TestCase(AttributeDataType.Number, "9000000000")]
    [TestCase(AttributeDataType.LongNumber, "1.5")]
    [TestCase(AttributeDataType.Decimal, "abc")]
    [TestCase(AttributeDataType.Boolean, "yes")]
    [TestCase(AttributeDataType.Guid, "not-a-guid")]
    [TestCase(AttributeDataType.DateTime, "next tuesday")]
    public void TryParse_ValueNotOfTheAttributesType_FailsWithReason(AttributeDataType type, string value)
    {
        var parsed = AttributeChangeConditionParser.TryParse(type, value, out var match, out var error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.False);
            Assert.That(match, Is.Null);
            Assert.That(error, Does.Contain(value));
        }
    }

    [Test]
    public void TryParse_BinaryWithValue_FailsBecauseBinaryValuesAreNotRecorded()
    {
        var parsed = AttributeChangeConditionParser.TryParse(AttributeDataType.Binary, "abc", out _, out var error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }
    }

    [Test]
    public void TryParse_BinaryWithoutValue_MatchesAnyChange()
    {
        var parsed = AttributeChangeConditionParser.TryParse(AttributeDataType.Binary, null, out var match, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(match, Is.Null);
        }
    }
}
