// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
}
