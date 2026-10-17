// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Utilities.Tests;

/// <summary>
/// A date at a Connected System's resolution (#2153, decision D2): canonical, then floored to the finest step of time
/// the Connected System holds, so JIM sends, compares and diffs exactly what the target can hold.
/// </summary>
public class DateTimeAttributeValueResolutionTests
{
    private static readonly DateTime Instant = new(2026, 3, 15, 10, 30, 45, DateTimeKind.Utc);
    private static readonly DateTime WithFraction = Instant.AddTicks(1_234_567); // .1234567

    [TestCase(null, 1_234_560L)]
    [TestCase(DateTimeResolution.Microsecond, 1_234_560L)]
    [TestCase(DateTimeResolution.TenMicroseconds, 1_234_500L)]
    [TestCase(DateTimeResolution.HundredMicroseconds, 1_234_000L)]
    [TestCase(DateTimeResolution.Millisecond, 1_230_000L)]
    [TestCase(DateTimeResolution.TenMilliseconds, 1_200_000L)]
    [TestCase(DateTimeResolution.HundredMilliseconds, 1_000_000L)]
    [TestCase(DateTimeResolution.Second, 0L)]
    public void ToResolution_SubSecondValue_IsFlooredToTheResolution(DateTimeResolution? resolution, long expectedTicksPastTheSecond)
    {
        var floored = DateTimeAttributeValue.ToResolution(WithFraction, resolution);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(floored.Ticks - Instant.Ticks, Is.EqualTo(expectedTicksPastTheSecond));
            Assert.That(floored.Kind, Is.EqualTo(DateTimeKind.Utc));
        }
    }

    [Test]
    public void ToResolution_Minute_FloorsToTheStartOfTheMinute()
    {
        Assert.That(DateTimeAttributeValue.ToResolution(WithFraction, DateTimeResolution.Minute),
            Is.EqualTo(new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void ToResolution_Day_FloorsToTheStartOfTheUtcDay()
    {
        Assert.That(DateTimeAttributeValue.ToResolution(WithFraction, DateTimeResolution.Day),
            Is.EqualTo(new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void ToResolution_LocalValue_IsFlooredAsTheSameUtcInstant()
    {
        var floored = DateTimeAttributeValue.ToResolution(WithFraction.ToLocalTime(), DateTimeResolution.Second);

        Assert.That(floored, Is.EqualTo(Instant));
    }

    [Test]
    public void ToResolution_PreEpochValue_FloorsNeverRoundsUp()
    {
        var lastFractionOf1999 = new DateTime(1999, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9_999_999);

        Assert.That(DateTimeAttributeValue.ToResolution(lastFractionOf1999, DateTimeResolution.Second),
            Is.EqualTo(new DateTime(1999, 12, 31, 23, 59, 59, DateTimeKind.Utc)), "never moves into the next second, minute or day");
    }

    [Test]
    public void ToResolution_MinAndMaxValue_PassThroughUnchanged()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DateTimeAttributeValue.ToResolution(DateTime.MinValue, DateTimeResolution.Day), Is.EqualTo(DateTime.MinValue));
            Assert.That(DateTimeAttributeValue.ToResolution(DateTime.MaxValue, DateTimeResolution.Day), Is.EqualTo(DateTime.MaxValue));
        }
    }

    [Test]
    public void Key_TwoValuesInTheSameSecond_AreOneValueAtSecondResolution()
    {
        var a = Instant.AddMilliseconds(100);
        var b = Instant.AddMilliseconds(900);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DateTimeAttributeValue.Key(a, DateTimeResolution.Second), Is.EqualTo(DateTimeAttributeValue.Key(b, DateTimeResolution.Second)));
            Assert.That(DateTimeAttributeValue.Key(a, null), Is.Not.EqualTo(DateTimeAttributeValue.Key(b, null)),
                "no resolution means the microsecond JIM holds");
        }
    }

    [Test]
    public void AreEqual_AtResolution_ComparesFlooredValues()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DateTimeAttributeValue.AreEqual(Instant, WithFraction, DateTimeResolution.Second), Is.True);
            Assert.That(DateTimeAttributeValue.AreEqual(Instant, WithFraction, DateTimeResolution.Millisecond), Is.False);
            Assert.That(DateTimeAttributeValue.AreEqual(null, null, DateTimeResolution.Second), Is.True);
            Assert.That(DateTimeAttributeValue.AreEqual(Instant, null, DateTimeResolution.Second), Is.False);
        }
    }

    [Test]
    public void IsCoarserThanMicrosecond_ReflectsWhetherFlooringCanChangeACanonicalValue()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DateTimeAttributeValue.IsCoarserThanMicrosecond(null), Is.False);
            Assert.That(DateTimeAttributeValue.IsCoarserThanMicrosecond(DateTimeResolution.Microsecond), Is.False);
            Assert.That(DateTimeAttributeValue.IsCoarserThanMicrosecond(DateTimeResolution.Second), Is.True);
        }
    }

    [Test]
    public void ResolutionTicks_EveryResolution_HasAPositiveStepThatDividesADay()
    {
        foreach (var resolution in Enum.GetValues<DateTimeResolution>())
        {
            var ticks = DateTimeAttributeValue.ResolutionTicks(resolution);
            Assert.That(ticks > 0 && TimeSpan.TicksPerDay % ticks == 0, Is.True, resolution.ToString());
        }
    }
}
