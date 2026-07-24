// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.ExampleData.DTOs;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests the human-readable persistence-progress message that the example data template
/// execution surfaces on the Activity record. The message is what a user sees on the Activity
/// Detail page while a long-running data-generation persist is in flight; it must include batch
/// progress and a rolling ETA so users can tell the operation is moving and roughly when it
/// will finish.
/// </summary>
[TestFixture]
public class ExampleDataServerProgressFormattingTests
{
    [Test]
    public void FormatPersistenceProgressMessage_FirstBatch_OmitsEtaBecauseRateNotYetEstablished()
    {
        var progress = new PersistenceProgress
        {
            TotalObjects = 10500,
            ObjectsPersisted = 500,
            BatchIndex = 1,
            BatchCount = 21,
            Elapsed = TimeSpan.FromSeconds(3)
        };

        var message = ExampleDataServer.FormatPersistenceProgressMessage(progress);

        Assert.That(message, Does.Contain("batch 1/21"));
        Assert.That(message, Does.Not.Contain("("),
            "The raw object counter is no longer in the message; the progress bar's overall count conveys quantity");
        Assert.That(message, Does.Not.Contain("ETA"),
            "First batch has no measured rate yet, so no ETA should be shown");
    }

    [Test]
    public void FormatPersistenceProgressMessage_MidRun_ShowsBatchAndEtaInSeconds()
    {
        // 2,000 objects done in 2 seconds → 1ms each → 8,500 left → 8.5s ETA
        var progress = new PersistenceProgress
        {
            TotalObjects = 10500,
            ObjectsPersisted = 2000,
            BatchIndex = 4,
            BatchCount = 21,
            Elapsed = TimeSpan.FromSeconds(2)
        };

        var message = ExampleDataServer.FormatPersistenceProgressMessage(progress);

        Assert.That(message, Does.Contain("batch 4/21"));
        Assert.That(message, Does.Not.Contain("("),
            "The raw object counter is no longer in the message; the progress bar's overall count conveys quantity");
        Assert.That(message, Does.Contain("ETA"));
        Assert.That(message, Does.Contain("s"),
            "Sub-minute ETA should be rendered in seconds");
    }

    [Test]
    public void FormatPersistenceProgressMessage_MidRun_ShowsEtaInMinutesAndSeconds()
    {
        // 1,000 objects in 30s → 30ms each → 9,500 left → 285s ETA = 4m 45s
        var progress = new PersistenceProgress
        {
            TotalObjects = 10500,
            ObjectsPersisted = 1000,
            BatchIndex = 2,
            BatchCount = 21,
            Elapsed = TimeSpan.FromSeconds(30)
        };

        var message = ExampleDataServer.FormatPersistenceProgressMessage(progress);

        Assert.That(message, Does.Contain("ETA 04m 45s"));
    }

    [Test]
    public void FormatPersistenceProgressMessage_FinalBatch_OmitsEtaBecauseNothingLeft()
    {
        var progress = new PersistenceProgress
        {
            TotalObjects = 10500,
            ObjectsPersisted = 10500,
            BatchIndex = 21,
            BatchCount = 21,
            Elapsed = TimeSpan.FromSeconds(45)
        };

        var message = ExampleDataServer.FormatPersistenceProgressMessage(progress);

        Assert.That(message, Does.Contain("batch 21/21"));
        Assert.That(message, Does.Not.Contain("("),
            "The raw object counter is no longer in the message; the progress bar's overall count conveys quantity");
        Assert.That(message, Does.Not.Contain("ETA"),
            "Final batch is complete so no ETA is needed");
    }

    [Test]
    public void FormatPersistenceProgressMessage_LongRun_ShowsEtaInHoursAndMinutes()
    {
        // 100 objects in 60s → 600ms each → 99,900 left → 59,940s ETA ≈ 16h 39m
        var progress = new PersistenceProgress
        {
            TotalObjects = 100000,
            ObjectsPersisted = 100,
            BatchIndex = 2,
            BatchCount = 1000,
            Elapsed = TimeSpan.FromSeconds(60)
        };

        var message = ExampleDataServer.FormatPersistenceProgressMessage(progress);

        Assert.That(message, Does.Contain("ETA"));
        Assert.That(message, Does.Match(@"ETA \d+h \d{2}m"),
            "Multi-hour ETA should be rendered as Xh YYm");
    }
}
