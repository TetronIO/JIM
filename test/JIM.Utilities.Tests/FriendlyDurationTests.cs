// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Utilities;
using NUnit.Framework;

namespace JIM.Utilities.Tests;

/// <summary>
/// A Deletion Grace Period reaches an administrator on a Configuration Change Preview, in a save confirmation, and
/// in a Metaverse Object Type's settings, and until #1275 it reached all three as a raw <see cref="TimeSpan"/>
/// ("Eligible for deletion after 0:45:00"). A grace period is configured in whole units and is read as a
/// reassurance about how long there is to undo a mistake, so it is written the way it was configured.
/// </summary>
[TestFixture]
public class FriendlyDurationTests
{
    [TestCase(45, "45 minutes")]
    [TestCase(1, "1 minute")]
    [TestCase(90, "1 hour 30 minutes")]
    [TestCase(60, "1 hour")]
    [TestCase(120, "2 hours")]
    [TestCase(1440, "1 day")]
    [TestCase(2880, "2 days")]
    [TestCase(1500, "1 day 1 hour")]
    public void ToFriendlyDuration_WholeUnits_WritesTheUnitsOut(int totalMinutes, string expected)
    {
        Assert.That(TimeSpan.FromMinutes(totalMinutes).ToFriendlyDuration(), Is.EqualTo(expected));
    }

    [Test]
    public void ToFriendlyDuration_Zero_ReadsAsImmediate()
    {
        // A zero grace period is not "0 minutes"; it means the deletion happens on the next synchronisation, and
        // the two read very differently to someone deciding whether to save.
        Assert.That(TimeSpan.Zero.ToFriendlyDuration(), Is.EqualTo("immediately"));
    }

    [Test]
    public void ToFriendlyDuration_SecondsOnly_KeepsThemRatherThanRoundingToZero()
    {
        // Grace periods are configured in minutes and above, so this is defensive: a sub-minute value must never
        // render as "immediately", which would understate the delay to the point of being wrong.
        Assert.That(TimeSpan.FromSeconds(30).ToFriendlyDuration(), Is.EqualTo("30 seconds"));
    }

    [Test]
    public void ToFriendlyDuration_MoreThanTwoUnits_StopsAtTheTwoLargest()
    {
        // "1 day 2 hours" is the answer someone wants; the trailing minutes and seconds are noise at that scale.
        Assert.That(new TimeSpan(1, 2, 3, 4).ToFriendlyDuration(), Is.EqualTo("1 day 2 hours"));
    }

    [Test]
    public void ToFriendlyDuration_Negative_IsWrittenAsItsMagnitude()
    {
        // Nothing should produce one, but a negative grace period rendering as "-1 hour -30 minutes" would be a
        // worse failure than stating the magnitude.
        Assert.That(TimeSpan.FromMinutes(-90).ToFriendlyDuration(), Is.EqualTo("1 hour 30 minutes"));
    }
}
