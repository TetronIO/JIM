// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Expressions;
using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

/// <summary>
/// The one conversion from an expression's result to the value an attribute of a given type holds (#2052). Attribute
/// Flow and Object Matching both use it, so an expression produces the same typed value whichever of them evaluates
/// it; a value it refuses is refused by both rather than silently corrupted (the #871 lossy-cast class).
/// </summary>
public class ExpressionResultConverterTests
{
    [Test]
    public void TryConvert_TextTarget_UsesTheResultsText()
    {
        Assert.That(ExpressionResultConverter.TryConvert(48213, AttributeDataType.Text, out var value), Is.True);
        Assert.That(value, Is.EqualTo("48213"));
    }

    [Test]
    public void TryConvert_NumberTarget_IntResult_KeptExactly()
    {
        Assert.That(ExpressionResultConverter.TryConvert(42, AttributeDataType.Number, out var value), Is.True);
        Assert.That(value, Is.EqualTo(42));
    }

    [Test]
    public void TryConvert_NumberTarget_LongResultWithinIntRange_Narrowed()
    {
        Assert.That(ExpressionResultConverter.TryConvert(42L, AttributeDataType.Number, out var value), Is.True);
        Assert.That(value, Is.EqualTo(42));
    }

    [Test]
    public void TryConvert_NumberTarget_LongResultOutOfIntRange_Refused()
    {
        Assert.That(ExpressionResultConverter.TryConvert((long)int.MaxValue + 1, AttributeDataType.Number, out _), Is.False);
    }

    [Test]
    public void TryConvert_NumberTarget_StringResult_Parsed()
    {
        Assert.That(ExpressionResultConverter.TryConvert("48213", AttributeDataType.Number, out var value), Is.True);
        Assert.That(value, Is.EqualTo(48213));
    }

    [Test]
    public void TryConvert_NumberTarget_UnparseableString_Refused()
    {
        Assert.That(ExpressionResultConverter.TryConvert("EMP-48213", AttributeDataType.Number, out _), Is.False);
    }

    [Test]
    public void TryConvert_LongNumberTarget_IntResult_Widened()
    {
        Assert.That(ExpressionResultConverter.TryConvert(42, AttributeDataType.LongNumber, out var value), Is.True);
        Assert.That(value, Is.EqualTo(42L));
    }

    [Test]
    public void TryConvert_LongNumberTarget_StringResult_ParsedWithInvariantCulture()
    {
        Assert.That(ExpressionResultConverter.TryConvert("9000000000", AttributeDataType.LongNumber, out var value), Is.True);
        Assert.That(value, Is.EqualTo(9000000000L));
    }

    [Test]
    public void TryConvert_DecimalTarget_DoubleResult_Refused()
    {
        // Binary floating point cannot represent most decimal fractions exactly, so converting would corrupt.
        Assert.That(ExpressionResultConverter.TryConvert(0.1d, AttributeDataType.Decimal, out _), Is.False);
    }

    [Test]
    public void TryConvert_DecimalTarget_StringResult_Parsed()
    {
        Assert.That(ExpressionResultConverter.TryConvert("12.50", AttributeDataType.Decimal, out var value), Is.True);
        Assert.That(value, Is.EqualTo(12.5m));
    }

    [Test]
    public void TryConvert_GuidTarget_StringResult_Parsed()
    {
        var guid = Guid.NewGuid();
        Assert.That(ExpressionResultConverter.TryConvert(guid.ToString(), AttributeDataType.Guid, out var value), Is.True);
        Assert.That(value, Is.EqualTo(guid));
    }

    [Test]
    public void TryConvert_BooleanTarget_StringResult_Parsed()
    {
        Assert.That(ExpressionResultConverter.TryConvert("true", AttributeDataType.Boolean, out var value), Is.True);
        Assert.That(value, Is.EqualTo(true));
    }

    [Test]
    public void TryConvert_DateTimeTarget_DateTimeResult_Kept()
    {
        var when = new DateTime(2026, 10, 11, 9, 30, 0, DateTimeKind.Utc);
        Assert.That(ExpressionResultConverter.TryConvert(when, AttributeDataType.DateTime, out var value), Is.True);
        Assert.That(value, Is.EqualTo(when));
    }

    [TestCase(AttributeDataType.Binary)]
    [TestCase(AttributeDataType.Reference)]
    [TestCase(AttributeDataType.NotSet)]
    public void TryConvert_TypeAnExpressionCannotProduce_Refused(AttributeDataType targetType)
    {
        Assert.That(ExpressionResultConverter.TryConvert("anything", targetType, out _), Is.False);
    }

    private static T Under<T>(string culture, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestCase("en-GB")]
    [TestCase("en-US")]
    public void TryConvert_TextTarget_DateResult_UsesJimsDateTextForm(string culture)
    {
        object? value = null;
        var converted = Under(culture, () => ExpressionResultConverter.TryConvert(
            new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc), AttributeDataType.Text, out value));

        Assert.That(converted, Is.True);
        Assert.That(value, Is.EqualTo("2026-03-15 10:30:00Z"));
    }

    [Test]
    public void TryConvert_TextTarget_DecimalResult_UsesAFullStopWhateverTheCulture()
    {
        object? value = null;
        var converted = Under("de-DE", () => ExpressionResultConverter.TryConvert(1.5m, AttributeDataType.Text, out value));

        Assert.That(converted, Is.True);
        Assert.That(value, Is.EqualTo("1.5"));
    }

    [Test]
    public void TryConvert_TextTarget_BinaryResult_Refused()
    {
        Assert.That(ExpressionResultConverter.TryConvert(new byte[] { 1, 2 }, AttributeDataType.Text, out _), Is.False,
            "a Binary value has no text form; writing System.Byte[] is never wanted");
    }

    [TestCase("en-GB")]
    [TestCase("en-US")]
    public void TryConvert_DateTimeTarget_NumericDateText_RefusedWhateverTheCulture(string culture)
    {
        Assert.That(Under(culture, () => ExpressionResultConverter.TryConvert("03/04/2024", AttributeDataType.DateTime, out _)), Is.False,
            "\"03/04/2024\" is 3 April under en-GB and 4 March under en-US, so it is read under neither");
    }

    [TestCase("2026-03-15T11:30:00+01:00")]
    [TestCase("2026-03-15T10:30:00Z")]
    [TestCase("2026-03-15 10:30:00Z")]
    [TestCase("2026-03-15 10:30:00")]
    [TestCase("2026-03-15T10:30")]
    public void TryConvert_DateTimeTarget_IsoText_ReadAsUtc(string text)
    {
        Assert.That(ExpressionResultConverter.TryConvert(text, AttributeDataType.DateTime, out var value), Is.True);
        var date = (DateTime)value!;
        Assert.That(date, Is.EqualTo(new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc)));
        Assert.That(date.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [TestCase("2026-03-15\n")]
    [TestCase("2026-03-15T10:30:00Z\n")]
    [TestCase("2026-03-15 ")]
    [TestCase(" 2026-03-15")]
    public void TryConvert_DateTimeTarget_IsoTextWithSurroundingWhitespace_Refused(string text)
    {
        // ISO 8601 exactly, and nothing around it: a trailing newline is refused just as a trailing space is.
        Assert.That(ExpressionResultConverter.TryConvert(text, AttributeDataType.DateTime, out _), Is.False);
    }

    [Test]
    public void TryConvert_DateTimeTarget_UnspecifiedDate_TakenAsUtcAndFlooredToTheMicrosecond()
    {
        var unspecified = new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Unspecified).AddTicks(1_234_567);

        Assert.That(ExpressionResultConverter.TryConvert(unspecified, AttributeDataType.DateTime, out var value), Is.True);
        var date = (DateTime)value!;
        Assert.That(date.Kind, Is.EqualTo(DateTimeKind.Utc));
        Assert.That(date.Ticks, Is.EqualTo(unspecified.Ticks - 7), "floored to the microsecond, the finest time JIM holds");
    }

    [Test]
    public void TryConvert_BinaryTarget_BinaryResult_Kept()
    {
        Assert.That(ExpressionResultConverter.TryConvert(new byte[] { 1, 2 }, AttributeDataType.Binary, out var value), Is.True);
        Assert.That(value, Is.EqualTo(new byte[] { 1, 2 }));
    }
}
