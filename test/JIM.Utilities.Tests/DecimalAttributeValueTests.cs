// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Utilities.Tests;

public class DecimalAttributeValueTests
{
    [Test]
    public void ToCanonicalString_TrailingZeros_AreDropped()
    {
        Assert.That(DecimalAttributeValue.ToCanonicalString(5.00m), Is.EqualTo("5"));
        Assert.That(DecimalAttributeValue.ToCanonicalString(5.10m), Is.EqualTo("5.1"));
    }

    [Test]
    public void ToCanonicalString_NumericallyEqualValues_ProduceIdenticalStrings()
    {
        Assert.That(
            DecimalAttributeValue.ToCanonicalString(5.0m),
            Is.EqualTo(DecimalAttributeValue.ToCanonicalString(5.00m)));
    }

    [Test]
    public void ToCanonicalString_SmallFraction_UsesPlainNotationNeverExponent()
    {
        // Raw "G29" would render this as "1E-07"; the canonical form must stay plain notation.
        Assert.That(DecimalAttributeValue.ToCanonicalString(0.0000001m), Is.EqualTo("0.0000001"));
    }

    [Test]
    public void ToCanonicalString_MaximumScaleFraction_UsesPlainNotationNeverExponent()
    {
        Assert.That(
            DecimalAttributeValue.ToCanonicalString(0.0000000000000000000000000001m),
            Is.EqualTo("0.0000000000000000000000000001"));
    }

    [Test]
    public void ToCanonicalString_SmallFractionWithTrailingZeros_DropsZerosWithoutExponent()
    {
        Assert.That(DecimalAttributeValue.ToCanonicalString(0.000000100m), Is.EqualTo("0.0000001"));
    }

    [Test]
    public void ToCanonicalString_DecimalMaxValue_UsesPlainNotationNeverExponent()
    {
        Assert.That(DecimalAttributeValue.ToCanonicalString(decimal.MaxValue), Is.EqualTo("79228162514264337593543950335"));
    }

    [Test]
    public void ToCanonicalString_NegativeValue_RendersSignAndValue()
    {
        Assert.That(DecimalAttributeValue.ToCanonicalString(-1234.5600m), Is.EqualTo("-1234.56"));
    }

    [Test]
    public void ToCanonicalString_UnderCommaDecimalCulture_RendersInvariant()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.That(DecimalAttributeValue.ToCanonicalString(1234.56m), Is.EqualTo("1234.56"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    public void TryParse_PlainNotation_ParsesValue()
    {
        var parsed = DecimalAttributeValue.TryParse("1234.56", out var value);

        Assert.That(parsed, Is.True);
        Assert.That(value, Is.EqualTo(1234.56m));
    }

    [Test]
    public void TryParse_ExponentNotation_ParsesAndCanonicalises()
    {
        var parsed = DecimalAttributeValue.TryParse("1.5E3", out var value);

        Assert.That(parsed, Is.True);
        Assert.That(value, Is.EqualTo(1500m));
    }

    [Test]
    public void TryParse_NegativeExponentNotation_ParsesValue()
    {
        var parsed = DecimalAttributeValue.TryParse("-2.5E-2", out var value);

        Assert.That(parsed, Is.True);
        Assert.That(value, Is.EqualTo(-0.025m));
    }

    [Test]
    public void TryParse_OverflowBeyondDecimalRange_ReturnsFalse()
    {
        // 1E29 exceeds decimal.MaxValue (~7.92E28); overflow must fail, never truncate or round.
        Assert.That(DecimalAttributeValue.TryParse("1E29", out _), Is.False);
    }

    [Test]
    public void TryParse_NullInput_ReturnsFalse()
    {
        Assert.That(DecimalAttributeValue.TryParse(null, out _), Is.False);
    }

    [Test]
    public void TryParse_NonNumericInput_ReturnsFalse()
    {
        Assert.That(DecimalAttributeValue.TryParse("not a number", out _), Is.False);
    }

    [Test]
    public void TryParse_CommaDecimalSeparator_ReturnsFalse()
    {
        // Parsing is invariant-culture; a comma is not a decimal separator and thousands separators are not allowed.
        Assert.That(DecimalAttributeValue.TryParse("5,5", out _), Is.False);
    }

    [Test]
    public void TryParse_UnderCommaDecimalCulture_StillParsesInvariant()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var parsed = DecimalAttributeValue.TryParse("1234.56", out var value);

            Assert.That(parsed, Is.True);
            Assert.That(value, Is.EqualTo(1234.56m));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
