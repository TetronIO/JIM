// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Globalization;
using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

/// <summary>
/// Covers the canonical string form of an external ID value (#1283).
///
/// The Connected System Object lookup cache is keyed by a string built from the external ID value.
/// A Decimal anchor makes that dangerous in a way the other anchor types are not: decimal carries
/// its scale, so two decimals that compare equal can render as different strings. Keying the cache
/// on the raw rendering would mean an imported object failing to match the object it already has,
/// and a duplicate being created on every import, with no error anywhere.
/// </summary>
[TestFixture]
public class ExternalIdValueTests
{
    [Test]
    public void ToCanonicalString_DecimalsDifferingOnlyByScale_ProduceTheSameKey()
    {
        // 123.40m and 123.4m are equal decimals, but ToString() renders them differently
        // because decimal preserves the scale it was constructed with.
        var withTrailingZero = decimal.Parse("123.40", CultureInfo.InvariantCulture);
        var withoutTrailingZero = decimal.Parse("123.4", CultureInfo.InvariantCulture);

        Assert.That(withTrailingZero, Is.EqualTo(withoutTrailingZero), "precondition: the two values are equal");
        Assert.That(
            ExternalIdValue.ToCanonicalString(withTrailingZero),
            Is.EqualTo(ExternalIdValue.ToCanonicalString(withoutTrailingZero)));
    }

    [Test]
    public void ToCanonicalString_IntegralDecimal_HasNoDecimalPoint()
    {
        // A sequence-backed Oracle NUMBER key arrives as a decimal with a zero scale. Its key must
        // read as the integer it is, so a system whose anchors are whole numbers stays legible.
        Assert.That(ExternalIdValue.ToCanonicalString(decimal.Parse("4200", CultureInfo.InvariantCulture)), Is.EqualTo("4200"));
        Assert.That(ExternalIdValue.ToCanonicalString(decimal.Parse("4200.00", CultureInfo.InvariantCulture)), Is.EqualTo("4200"));
    }

    [Test]
    public void ToCanonicalString_UnderACommaDecimalSeparatorCulture_StillUsesAPoint()
    {
        // The key is persisted and compared across processes, so it must not depend on the culture
        // of whichever thread happened to build it. de-DE renders 123.40m as "123,40".
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.That(
                ExternalIdValue.ToCanonicalString(decimal.Parse("123.4", CultureInfo.InvariantCulture)),
                Is.EqualTo("123.4"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Test]
    public void ToCanonicalString_NegativeAndHighPrecisionValues_RoundTripReadably()
    {
        // Oracle NUMBER exceeds long at high precision, which is why it is discovered as Decimal
        // rather than mapped down. The canonical form must not lose those digits.
        Assert.That(ExternalIdValue.ToCanonicalString(decimal.Parse("-17", CultureInfo.InvariantCulture)), Is.EqualTo("-17"));
        Assert.That(
            ExternalIdValue.ToCanonicalString(decimal.Parse("79228162514264337593543950335", CultureInfo.InvariantCulture)),
            Is.EqualTo("79228162514264337593543950335"));
    }

    [Test]
    public void ToCanonicalString_EqualDecimals_AgreeWithDictionaryKeying()
    {
        // Where a lookup is keyed on the decimal itself rather than on its string form, equality and
        // hash code already agree across scales. This test pins that, because the fix relies on it:
        // the deletion-detection Except() and the reference-resolution dictionary both key on decimal.
        var a = decimal.Parse("500.00", CultureInfo.InvariantCulture);
        var b = decimal.Parse("500", CultureInfo.InvariantCulture);

        Assert.Multiple(() =>
        {
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
            Assert.That(new HashSet<decimal> { a }.Contains(b), Is.True);
            Assert.That(ExternalIdValue.ToCanonicalString(a), Is.EqualTo(ExternalIdValue.ToCanonicalString(b)));
        });
    }
}
