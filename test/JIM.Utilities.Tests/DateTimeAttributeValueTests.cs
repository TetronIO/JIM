// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Utilities.Tests;

/// <summary>
/// The canonical form of a date (#2153): UTC, to the microsecond.
/// </summary>
public class DateTimeAttributeValueTests
{
    private static readonly DateTime March15 = new(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc);

    [Test]
    public void ToCanonical_UnspecifiedValue_IsTakenAsUtc()
    {
        var canonical = DateTimeAttributeValue.ToCanonical(DateTime.SpecifyKind(March15, DateTimeKind.Unspecified));

        Assert.That(canonical.Kind, Is.EqualTo(DateTimeKind.Utc));
        Assert.That(canonical.Ticks, Is.EqualTo(March15.Ticks));
    }

    [Test]
    public void ToCanonical_LocalValue_IsConvertedToTheSameInstant()
    {
        var canonical = DateTimeAttributeValue.ToCanonical(March15.ToLocalTime());

        Assert.That(canonical, Is.EqualTo(March15));
        Assert.That(canonical.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public void ToCanonical_SubMicrosecondTicks_AreFlooredNeverRounded()
    {
        var canonical = DateTimeAttributeValue.ToCanonical(March15.AddTicks(9));

        Assert.That(canonical, Is.EqualTo(March15), "nine ticks below a microsecond floor to the microsecond, never round up into it");
    }

    [Test]
    public void ToCanonical_MinAndMaxValue_PassThroughUnchanged()
    {
        Assert.That(DateTimeAttributeValue.ToCanonical(DateTime.MinValue), Is.EqualTo(DateTime.MinValue));
        Assert.That(DateTimeAttributeValue.ToCanonical(DateTime.MaxValue).Ticks, Is.EqualTo(DateTime.MaxValue.Ticks));
    }

    [Test]
    public void Key_SameInstantInDifferentKinds_IsEqual()
    {
        Assert.That(DateTimeAttributeValue.Key(March15), Is.EqualTo(DateTimeAttributeValue.Key(DateTime.SpecifyKind(March15, DateTimeKind.Unspecified))));
        Assert.That(DateTimeAttributeValue.Key(March15), Is.EqualTo(DateTimeAttributeValue.Key(March15.ToLocalTime())));
    }

    [Test]
    public void ToCanonical_PreEpochSubMicrosecondTicks_FloorsDownNeverIntoTheNextYear()
    {
        // 1999-12-31T23:59:59.9999995: Npgsql's own conversion truncates toward 2000-01-01, which would round this up
        // into the next year; flooring in JIM first keeps it in 1999.
        var lastInstantOf1999 = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(-5);

        var canonical = DateTimeAttributeValue.ToCanonical(lastInstantOf1999);

        Assert.That(canonical, Is.EqualTo(new DateTime(1999, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9999990)));
    }

    [TestCase(0, "2026-03-15T10:30:00Z")]
    [TestCase(1230000, "2026-03-15T10:30:00.123Z")]
    [TestCase(1234567, "2026-03-15T10:30:00.123456Z")]
    [TestCase(10, "2026-03-15T10:30:00.000001Z")]
    public void ToRoundTripString_TrimsTrailingFractionZerosAndWritesZ(long extraTicks, string expected)
    {
        Assert.That(DateTimeAttributeValue.ToRoundTripString(March15.AddTicks(extraTicks)), Is.EqualTo(expected));
    }

    [Test]
    public void ToRoundTripString_SameInstantInDifferentKinds_IsTheSameText()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DateTimeAttributeValue.ToRoundTripString(March15.ToLocalTime()), Is.EqualTo(DateTimeAttributeValue.ToRoundTripString(March15)));
            Assert.That(DateTimeAttributeValue.ToRoundTripString(DateTime.SpecifyKind(March15, DateTimeKind.Unspecified)), Is.EqualTo(DateTimeAttributeValue.ToRoundTripString(March15)));
        }
    }

    [Test]
    public void ToRoundTripString_UnderANonGregorianCulture_IsInvariant()
    {
        var host = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.That(DateTimeAttributeValue.ToRoundTripString(March15), Is.EqualTo("2026-03-15T10:30:00Z"));
        }
        finally
        {
            CultureInfo.CurrentCulture = host;
        }
    }

    [Test]
    public void AreEqual_ValuesDifferingBelowAMicrosecond_AreEqual()
    {
        Assert.That(DateTimeAttributeValue.AreEqual(March15.AddTicks(3), March15), Is.True);
    }

    [Test]
    public void AreEqual_ValuesAMicrosecondApart_AreNotEqual()
    {
        Assert.That(DateTimeAttributeValue.AreEqual(March15.AddTicks(10), March15), Is.False);
    }

    [Test]
    public void AreEqual_NullAgainstNullAndAgainstAValue()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DateTimeAttributeValue.AreEqual(null, null), Is.True);
            Assert.That(DateTimeAttributeValue.AreEqual(March15, null), Is.False);
            Assert.That(DateTimeAttributeValue.AreEqual(null, March15), Is.False);
        }
    }
}
