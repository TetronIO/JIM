// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
}
