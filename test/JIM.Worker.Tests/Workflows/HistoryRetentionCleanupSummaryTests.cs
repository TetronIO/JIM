// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// The summary a History Retention Cleanup pass leaves on its Activity (issue #1118, requirement 30).
/// <para>
/// This is the whole visible output of a pass that ran correctly, so it is worth asserting directly: retention
/// used to run on a timer inside the worker's idle loop, where the only record of it was a log line nobody was
/// reading. An administrator asking "is retention working?" reads this message, and it has to answer both
/// halves of that question, including the case where the honest answer is "there was nothing to remove".
/// </para>
/// </summary>
[TestFixture]
public class HistoryRetentionCleanupSummaryTests
{
    [Test]
    public void DescribeRetentionCleanup_NothingRemoved_SaysSoRatherThanListingZeroes()
    {
        var summary = Worker.DescribeRetentionCleanup(new ChangeHistoryServer.ChangeHistoryCleanupResult());

        Assert.That(summary, Is.EqualTo("Nothing had reached its retention period."),
            "\"there was nothing to remove\" and \"the pass never ran\" must not look alike");
    }

    [Test]
    public void DescribeRetentionCleanup_RecordsRemoved_NamesEveryClassItTouched()
    {
        var summary = Worker.DescribeRetentionCleanup(new ChangeHistoryServer.ChangeHistoryCleanupResult
        {
            CsoChangesDeleted = 12,
            MvoChangesDeleted = 3,
            PreviewsDeleted = 2,
            ActivitiesDeleted = 40,
            ConfigurationChangeActivitiesDeleted = 1,
            SecurityEventActivitiesDeleted = 5,
            InitialPasswordWorkRecordsDeleted = 7,
            PasswordEventActivitiesDeleted = 9,
            PasswordQueueRecordsDeleted = 4
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary, Does.StartWith("Removed "));
            Assert.That(summary, Does.Contain("12 Connected System Object changes"));
            Assert.That(summary, Does.Contain("3 Metaverse Object changes"));
            Assert.That(summary, Does.Contain("2 configuration change previews"));
            Assert.That(summary, Does.Contain("40 Activities"));
            Assert.That(summary, Does.Contain("1 configuration change Activity"));
            Assert.That(summary, Does.Contain("5 security event Activities"));
            Assert.That(summary, Does.Contain("7 initial password records"));
            Assert.That(summary, Does.Contain("9 Password Synchronisation Activities"));
            Assert.That(summary, Does.Contain("4 queued password changes"));
        }
    }

    [Test]
    public void DescribeRetentionCleanup_OnlySomeClassesTouched_OmitsTheRestRatherThanReportingZero()
    {
        var summary = Worker.DescribeRetentionCleanup(new ChangeHistoryServer.ChangeHistoryCleanupResult
        {
            PasswordQueueRecordsDeleted = 1
        });

        Assert.That(summary, Is.EqualTo("Removed 1 queued password change."),
            "a pass that only trimmed the password queue should say that, singular, and nothing else");
    }
}
