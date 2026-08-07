// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.Models.Core;
using JIM.Utilities;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers the conversion of a keyset-pagination anchor to and from the string form carried in a
/// Connected System Pagination Token. A lossy conversion here silently skips or repeats rows between
/// pages, which is the worst kind of import defect: it looks like a successful run.
/// </summary>
[TestFixture]
public class SqlAnchorValueTests
{
    #region Decimal

    [Test]
    public void ToTokenString_DecimalWithTrailingZeros_ProducesTheCanonicalForm()
    {
        var token = SqlAnchorValue.ToTokenString(5.00m, AttributeDataType.Decimal);

        Assert.That(token, Is.EqualTo(DecimalAttributeValue.ToCanonicalString(5.00m)),
            "Numerically equal decimals must produce identical tokens, or a resumed page compares against a different string for the same value.");
    }

    [TestCase("0.5")]
    [TestCase("-12345.6789")]
    [TestCase("79228162514264337593543950335")]
    [TestCase("0.0000001")]
    public void RoundTrip_Decimal_PreservesTheValueExactly(string literal)
    {
        var original = decimal.Parse(literal, System.Globalization.CultureInfo.InvariantCulture);

        var token = SqlAnchorValue.ToTokenString(original, AttributeDataType.Decimal);
        var parsed = SqlAnchorValue.TryFromTokenString(token, AttributeDataType.Decimal, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True, $"'{literal}' is a valid decimal anchor and must parse back.");
            Assert.That(value, Is.EqualTo(original), "Routing a decimal through double would lose digits that a 38-digit Oracle NUMBER key relies on.");
            Assert.That(token, Does.Not.Contain("E").IgnoreCase, "Exponent notation would not compare as the database does.");
        }
    }

    [Test]
    [SetCulture("de-DE")]
    public void ToTokenString_DecimalUnderACommaDecimalCulture_StillUsesTheInvariantForm()
    {
        var token = SqlAnchorValue.ToTokenString(1.5m, AttributeDataType.Decimal);

        Assert.That(token, Is.EqualTo("1.5"),
            "A culture-sensitive format would write '1,5' and make the persisted watermark unreadable on a differently configured host.");
    }

    [Test]
    [SetCulture("de-DE")]
    public void TryFromTokenString_DecimalUnderACommaDecimalCulture_StillParsesTheInvariantForm()
    {
        var parsed = SqlAnchorValue.TryFromTokenString("1.5", AttributeDataType.Decimal, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(value, Is.EqualTo(1.5m));
        }
    }

    [Test]
    public void TryFromTokenString_DecimalOverflow_ReturnsFalseRatherThanRounding()
    {
        var parsed = SqlAnchorValue.TryFromTokenString("792281625142643375935439503350", AttributeDataType.Decimal, out _);

        Assert.That(parsed, Is.False, "A value outside decimal's range must surface as an error, never as a silently rounded anchor.");
    }

    #endregion

    #region Other anchor types

    [Test]
    public void RoundTrip_Number_PreservesTheValue()
    {
        var token = SqlAnchorValue.ToTokenString(4711, AttributeDataType.Number);
        var parsed = SqlAnchorValue.TryFromTokenString(token, AttributeDataType.Number, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(value, Is.EqualTo(4711));
        }
    }

    [Test]
    public void RoundTrip_LongNumber_PreservesTheValue()
    {
        var token = SqlAnchorValue.ToTokenString(long.MaxValue, AttributeDataType.LongNumber);
        var parsed = SqlAnchorValue.TryFromTokenString(token, AttributeDataType.LongNumber, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(value, Is.EqualTo(long.MaxValue));
        }
    }

    [Test]
    public void RoundTrip_Text_PreservesTheValue()
    {
        var token = SqlAnchorValue.ToTokenString("EMP-0042", AttributeDataType.Text);
        var parsed = SqlAnchorValue.TryFromTokenString(token, AttributeDataType.Text, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(value, Is.EqualTo("EMP-0042"));
        }
    }

    [Test]
    public void RoundTrip_Guid_PreservesTheValue()
    {
        var original = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");

        var token = SqlAnchorValue.ToTokenString(original, AttributeDataType.Guid);
        var parsed = SqlAnchorValue.TryFromTokenString(token, AttributeDataType.Guid, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(value, Is.EqualTo(original));
            Assert.That(token, Is.EqualTo("550e8400-e29b-41d4-a716-446655440000"), "The canonical hyphenated form is what every provider round-trips predictably.");
        }
    }

    [Test]
    public void RoundTrip_DateTime_PreservesTheValueAsUtc()
    {
        var original = new DateTime(2026, 7, 14, 22, 0, 0, 123, DateTimeKind.Utc);

        var token = SqlAnchorValue.ToTokenString(original, AttributeDataType.DateTime);
        var parsed = SqlAnchorValue.TryFromTokenString(token, AttributeDataType.DateTime, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(value, Is.EqualTo(original));
            Assert.That(((DateTime)value!).Kind, Is.EqualTo(DateTimeKind.Utc), "JIM stores every DateTime in UTC; an unspecified kind would shift the watermark by the host's offset.");
        }
    }

    [Test]
    public void RoundTrip_Binary_PreservesTheBytes()
    {
        var original = new byte[] { 0x00, 0x0F, 0xA1, 0xFF };

        var token = SqlAnchorValue.ToTokenString(original, AttributeDataType.Binary);
        var parsed = SqlAnchorValue.TryFromTokenString(token, AttributeDataType.Binary, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(value, Is.EqualTo(original));
        }
    }

    #endregion

    #region Rejections

    [Test]
    public void ToTokenString_BooleanAnchor_Throws()
    {
        Assert.Throws<NotSupportedException>(() => SqlAnchorValue.ToTokenString(true, AttributeDataType.Boolean),
            "A two-state column cannot order a keyset page, so accepting it would produce an import that never terminates.");
    }

    [Test]
    public void ToTokenString_NullValue_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SqlAnchorValue.ToTokenString(null!, AttributeDataType.Text),
            "A null anchor means the row cannot be positioned, which is a configuration error rather than a page boundary.");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-a-number")]
    public void TryFromTokenString_UnusableToken_ReturnsFalse(string? token)
    {
        var parsed = SqlAnchorValue.TryFromTokenString(token, AttributeDataType.Number, out var value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.False);
            Assert.That(value, Is.Null, "A failed parse must not leave a partially converted anchor behind.");
        }
    }

    #endregion
}
