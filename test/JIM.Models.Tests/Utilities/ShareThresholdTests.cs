// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Utility;
using NUnit.Framework;

namespace JIM.Models.Tests.Utilities;

/// <summary>
/// <see cref="ShareThreshold.Exceeds"/> is the one place JIM decides "is this count too large a share
/// of that base?", shared by the #1605 post-clear reconciliation shortfall check and the #1618 Run
/// Profile Safeguards Full Import deletion detection limit. Both need the boundary to fall in exactly
/// the same place, so it is pinned here independently of either caller.
/// </summary>
[TestFixture]
public class ShareThresholdTests
{
    [Test]
    public void Exceeds_OneBelowThreshold_ReturnsFalse()
    {
        // 9 of 100 is below the 10% threshold.
        Assert.That(ShareThreshold.Exceeds(count: 9, baseCount: 100, maxPercent: 10), Is.False);
    }

    [Test]
    public void Exceeds_ExactlyAtThreshold_ReturnsFalse()
    {
        // 10 of 100 is exactly 10%; the check is strictly greater than, so this must not exceed.
        Assert.That(ShareThreshold.Exceeds(count: 10, baseCount: 100, maxPercent: 10), Is.False);
    }

    [Test]
    public void Exceeds_OneAboveThreshold_ReturnsTrue()
    {
        // 11 of 100 is above the 10% threshold.
        Assert.That(ShareThreshold.Exceeds(count: 11, baseCount: 100, maxPercent: 10), Is.True);
    }

    [Test]
    public void Exceeds_ZeroBaseCount_ReturnsFalse()
    {
        // Nothing to compare against: the share is undefined, not infinite, so this never exceeds even
        // when the count is positive.
        Assert.That(ShareThreshold.Exceeds(count: 5, baseCount: 0, maxPercent: 0), Is.False);
    }

    [Test]
    public void Exceeds_NegativeBaseCount_ReturnsFalse()
    {
        Assert.That(ShareThreshold.Exceeds(count: 5, baseCount: -1, maxPercent: 10), Is.False);
    }

    [Test]
    public void Exceeds_ZeroPercentAndAnyCount_ReturnsTrue()
    {
        // A limit of 0% refuses whenever anything at all is present against a genuine base.
        Assert.That(ShareThreshold.Exceeds(count: 1, baseCount: 100, maxPercent: 0), Is.True);
    }

    [Test]
    public void Exceeds_ZeroCount_ReturnsFalse()
    {
        Assert.That(ShareThreshold.Exceeds(count: 0, baseCount: 100, maxPercent: 0), Is.False);
    }

    [Test]
    public void Exceeds_HundredPercent_NeverExceedsWhateverTheCount()
    {
        Assert.That(ShareThreshold.Exceeds(count: 1_000_000, baseCount: 1_000_000, maxPercent: 100), Is.False);
    }

    [Test]
    public void Exceeds_LargeNumbersThatWouldOverflowInt_ComputesCorrectly()
    {
        // count * 100 alone overflows a 32-bit int at this scale (3,000,000,000 * 100 > int.MaxValue);
        // the long cross-multiplication must not truncate.
        const long count = 3_000_000_000L;
        const long baseCount = 4_000_000_000L;

        // 75% of 4,000,000,000 is exactly 3,000,000,000, so a 76% limit must not exceed...
        Assert.That(ShareThreshold.Exceeds(count, baseCount, maxPercent: 76), Is.False);

        // ...but a 74% limit must.
        Assert.That(ShareThreshold.Exceeds(count, baseCount, maxPercent: 74), Is.True);
    }
}
