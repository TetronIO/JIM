// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// A relative time reads the same way whichever side of now it falls. A Schedule's Next Run is in the future, and
/// the helper used to treat every future time as "just now", so a run fourteen hours away read as though it had
/// started already (Home page and Operations > Schedules).
/// </summary>
[TestFixture]
public class HelpersRelativeTimeTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [TestCase(30, "in under a minute")]
    [TestCase(60, "in 1 minute")]
    [TestCase(5 * 60, "in 5 minutes")]
    [TestCase(60 * 60, "in 1 hour")]
    [TestCase(14 * 60 * 60, "in 14 hours")]
    [TestCase(24 * 60 * 60, "in 1 day")]
    [TestCase(2 * 24 * 60 * 60, "in 2 days")]
    [TestCase(45 * 24 * 60 * 60, "in 1 month")]
    [TestCase(400 * 24 * 60 * 60, "in 1 year")]
    public void ToRelativeTime_FutureTime_SaysHowLongUntilIt(int secondsAhead, string expected)
    {
        Assert.That(Now.AddSeconds(secondsAhead).ToRelativeTime(Now), Is.EqualTo(expected));
    }

    [TestCase(0, "just now")]
    [TestCase(30, "just now")]
    [TestCase(60, "1 minute ago")]
    [TestCase(5 * 60, "5 minutes ago")]
    [TestCase(60 * 60, "1 hour ago")]
    [TestCase(3 * 60 * 60, "3 hours ago")]
    [TestCase(2 * 24 * 60 * 60, "2 days ago")]
    [TestCase(45 * 24 * 60 * 60, "1 month ago")]
    [TestCase(400 * 24 * 60 * 60, "1 year ago")]
    public void ToRelativeTime_PastTime_SaysHowLongAgoItWas(int secondsAgo, string expected)
    {
        Assert.That(Now.AddSeconds(-secondsAgo).ToRelativeTime(Now), Is.EqualTo(expected));
    }

    [Test]
    public void ToRelativeTime_LocalTimeInTheFuture_IsComparedInUtc()
    {
        // Both call sites showing a Next Run convert to local time before calling the helper.
        var nextRunLocal = Now.AddHours(3).ToLocalTime();

        Assert.That(nextRunLocal.ToRelativeTime(Now), Is.EqualTo("in 3 hours"));
    }
}
