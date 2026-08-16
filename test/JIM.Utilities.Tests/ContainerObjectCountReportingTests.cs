// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using JIM.Models.Staging;
using JIM.Utilities;
using NUnit.Framework;

namespace JIM.Utilities.Tests;

/// <summary>
/// Telling an administrator why a Container has no object count against it (#1276).
/// </summary>
/// <remarks>
/// A count that did not finish is discarded rather than displayed, because figures short of the truth read as whole
/// and understate what deselecting a Container costs. Discarding them silently trades one wrong impression for
/// another though: the tab then looks exactly like a Connected System that cannot count at all, and the
/// administrator has no way to tell "nothing to show" from "we gave up". This is what closes that gap.
/// </remarks>
[TestFixture]
public class ContainerObjectCountReportingTests
{
    [Test]
    public void DescribeIncompleteCounts_EveryPartitionCountedInFull_SaysNothing()
    {
        var outcomes = new[]
        {
            ("dc=corp,dc=local", Complete()),
            ("dc=sales,dc=local", Complete())
        };

        Assert.That(ContainerObjectCounts.DescribeIncompleteCounts(outcomes), Is.Null,
            "a complete count is the ordinary case and warning about it would train administrators to ignore the field");
    }

    [Test]
    public void DescribeIncompleteCounts_NothingCounted_SaysNothing()
    {
        Assert.That(ContainerObjectCounts.DescribeIncompleteCounts([]), Is.Null);
    }

    [Test]
    public void DescribeIncompleteCounts_OnePartitionCutShort_NamesItAndGivesTheConnectorsReason()
    {
        var outcomes = new[] { ("dc=corp,dc=local", Incomplete("The directory stopped the search at its own size limit.")) };

        var message = ContainerObjectCounts.DescribeIncompleteCounts(outcomes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("dc=corp,dc=local"), "the administrator has to know which Partition is affected");
            Assert.That(message, Does.Contain("The directory stopped the search at its own size limit."),
                "the Connector's reason is the actionable part; JIM cannot improve on it");
            Assert.That(message, Does.Contain("not shown"),
                "and that the consequence is no counts at all, rather than the partial ones being displayed");
        }
    }

    [Test]
    public void DescribeIncompleteCounts_SeveralPartitionsStoppedForTheSameReason_StatesThatReasonOnce()
    {
        var outcomes = new[]
        {
            ("dc=corp,dc=local", Incomplete("Counting stopped after 60 seconds so that the hierarchy was not held up.")),
            ("dc=sales,dc=local", Incomplete("Counting stopped after 60 seconds so that the hierarchy was not held up."))
        };

        var message = ContainerObjectCounts.DescribeIncompleteCounts(outcomes)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("dc=corp,dc=local"));
            Assert.That(message, Does.Contain("dc=sales,dc=local"));
            Assert.That(CountOccurrences(message, "Counting stopped after 60 seconds"), Is.EqualTo(1),
                "a directory large enough to exhaust the budget exhausts it in every partition; repeating the reason per partition is noise");
        }
    }

    [Test]
    public void DescribeIncompleteCounts_PartitionsStoppedForDifferentReasons_GivesBothReasons()
    {
        var outcomes = new[]
        {
            ("dc=corp,dc=local", Incomplete("Counting stopped after 60 seconds so that the hierarchy was not held up.")),
            ("dc=sales,dc=local", Incomplete("The directory stopped the search at its own size limit."))
        };

        var message = ContainerObjectCounts.DescribeIncompleteCounts(outcomes)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("Counting stopped after 60 seconds"));
            Assert.That(message, Does.Contain("The directory stopped the search at its own size limit."));
        }
    }

    [Test]
    public void DescribeIncompleteCounts_OnlySomePartitionsCutShort_NamesOnlyThose()
    {
        var outcomes = new[]
        {
            ("dc=corp,dc=local", Complete()),
            ("dc=sales,dc=local", Incomplete("The directory stopped the search at its own size limit."))
        };

        var message = ContainerObjectCounts.DescribeIncompleteCounts(outcomes)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("dc=sales,dc=local"));
            Assert.That(message, Does.Not.Contain("dc=corp,dc=local"),
                "a partition that counted in full still shows its counts, so naming it would misreport what the administrator is looking at");
        }
    }

    [Test]
    public void DescribeIncompleteCounts_ConnectorGaveNoReason_StillWarnsThatCountingDidNotFinish()
    {
        // A Connector is not obliged to explain itself, and the absence of an explanation must not become the
        // absence of a warning: the counts are gone either way, and that is the part the administrator can see.
        var outcomes = new[] { ("dc=corp,dc=local", Incomplete(null)) };

        var message = ContainerObjectCounts.DescribeIncompleteCounts(outcomes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Is.Not.Null);
            Assert.That(message, Does.Contain("dc=corp,dc=local"));
        }
    }

    [Test]
    public void DescribeIncompleteCounts_ConnectorGaveAWhitespaceReason_IsTreatedAsNoReason()
    {
        var outcomes = new[] { ("dc=corp,dc=local", Incomplete("   ")) };

        var message = ContainerObjectCounts.DescribeIncompleteCounts(outcomes)!;

        Assert.That(message.Contains("   "), Is.False, "a blank reason must not be pasted into the sentence as though it said something");
    }

    private static ConnectorContainerObjectCountResult Complete() => new();

    private static ConnectorContainerObjectCountResult Incomplete(string? reason) =>
        new() { Complete = false, IncompleteReason = reason };

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var index = haystack.IndexOf(needle, System.StringComparison.Ordinal); index >= 0;
             index = haystack.IndexOf(needle, index + needle.Length, System.StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
