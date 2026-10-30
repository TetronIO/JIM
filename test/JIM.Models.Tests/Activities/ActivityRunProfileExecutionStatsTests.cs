// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using NUnit.Framework;

namespace JIM.Models.Tests.Activities;

/// <summary>
/// Tests for the figures <see cref="ActivityRunProfileExecutionStats"/> derives from its counters. The portal,
/// and anything else that shows "how many Connected System Objects were disconnected", must read the derived
/// figure rather than add the counters up itself, or one surface drops a kind of disconnection another counts.
/// </summary>
[TestFixture]
public class ActivityRunProfileExecutionStatsTests
{
    /// <summary>
    /// A Connected System Object leaves its Metaverse Object either because it was removed from its source
    /// system (<c>TotalDisconnections</c>) or because it left an import Synchronisation Rule's scope
    /// (<c>TotalDisconnectedOutOfScope</c>). The Activity's "CSOs Disconnected" tile read only the first, so a
    /// run whose only disconnections were scope exits showed 0 while its outcome chips and the Activity list
    /// showed them.
    /// </summary>
    [TestCase(0, 0, 0)]
    [TestCase(2, 0, 2)]
    [TestCase(0, 3, 3)]
    [TestCase(2, 3, 5)]
    public void TotalDisconnectionsIncludingOutOfScope_Counters_IsTheirSum(int removedFromSource, int outOfScope, int expected)
    {
        var stats = new ActivityRunProfileExecutionStats
        {
            TotalDisconnections = removedFromSource,
            TotalDisconnectedOutOfScope = outOfScope
        };

        Assert.That(stats.TotalDisconnectionsIncludingOutOfScope, Is.EqualTo(expected));
    }
}
