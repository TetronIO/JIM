// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Activities;
using JIM.Models.Localisation;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// #2521: a paged import reports its progress on every page, and a Connector returning a page of 100 makes that thousands of
/// writes, each a commit. <see cref="ActivityProgressThrottle"/> keeps what an administrator sees current to within its
/// interval while writing at most once per interval, always writing the latest state, and the final state when it closes.
/// </summary>
[TestFixture]
public class ActivityProgressThrottleTests
{
    private static readonly TimeSpan Interval = ActivityProgressThrottle.DefaultInterval;

    private JIM.InMemoryData.SyncRepository _syncRepo = null!;
    private ManualTimeProvider _clock = null!;
    private Activity _activity = null!;
    private ActivityPhaseReporter _reporter = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        _syncRepo = new JIM.InMemoryData.SyncRepository();
        _clock = new ManualTimeProvider();
        _activity = new Activity { Id = Guid.NewGuid() };
        _reporter = await ActivityPhaseReporter.StartAsync(_syncRepo, _activity, new PhaseDeclaringConnector(),
            new ConnectedSystem { Id = 1, Name = "Directory" },
            new ConnectedSystemRunProfile { Id = 1, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport });
    }

    [Test]
    public async Task ReportAsync_FirstReport_WritesAtOnceAsync()
    {
        await using var throttle = NewThrottle();

        await throttle.ReportAsync("Importing objects from Connected System");

        Assert.That(_syncRepo.ActivityProgressWrites.Select(w => w.Message), Is.EqualTo(new[] { "Importing objects from Connected System" }),
            "The first report starts the administrator's view of this stretch of the run, so it cannot wait");
    }

    [Test]
    public async Task ReportAsync_WithAMessage_WritesTheMessageBesideItsEnglish_AndAStringClearsItAsync()
    {
        await using var throttle = NewThrottle();

        await throttle.ReportAsync(ActivityMessage.Create(nameof(ActivityMessages.ProgressReadingPage), 2));
        var first = _syncRepo.ActivityProgressWrites[^1];

        _clock.Advance(Interval + TimeSpan.FromMilliseconds(1));
        await throttle.ReportAsync("From a Connector");
        var second = _syncRepo.ActivityProgressWrites[^1];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Message, Is.EqualTo("Reading page 2"));
            Assert.That(MessageJson.Deserialise(first.MessageDetailJson)?.ToEnglish(), Is.EqualTo(first.Message));
            Assert.That(second.Message, Is.EqualTo("From a Connector"));
            Assert.That(second.MessageDetailJson, Is.Null, "English that is not JIM's own wording must not keep an earlier message");
        }
    }

    [Test]
    public async Task ReportAsync_ReportsWithinTheInterval_WritesTheLatestOnceTheIntervalHasPassedAsync()
    {
        await using var throttle = NewThrottle();
        await throttle.ReportAsync("Reading page 1");

        _clock.Advance(TimeSpan.FromMilliseconds(100));
        await throttle.ReportAsync("Reading page 2");
        _clock.Advance(TimeSpan.FromMilliseconds(100));
        await throttle.ReportAsync("Reading page 3");

        Assert.That(_syncRepo.ActivityProgressWrites, Has.Count.EqualTo(1), "Reports within the interval are not written as they come");

        _clock.Advance(Interval);

        Assert.That(_syncRepo.ActivityProgressWrites.Select(w => w.Message), Is.EqualTo(new[] { "Reading page 1", "Reading page 3" }),
            "What was reported is written once the interval has passed, even with nothing else reported, and only the latest of it");

        _clock.Advance(Interval * 5);
        Assert.That(_syncRepo.ActivityProgressWrites, Has.Count.EqualTo(2), "Nothing new was reported, so nothing more is written");
    }

    [Test]
    public async Task ReportAsync_ReportAfterTheInterval_WritesAtOnceAsync()
    {
        await using var throttle = NewThrottle();
        await throttle.ReportAsync("Reading page 1");

        _clock.Advance(Interval + TimeSpan.FromMilliseconds(1));
        await throttle.ReportAsync("Reading page 2");

        Assert.That(_syncRepo.ActivityProgressWrites.Select(w => w.Message), Is.EqualTo(new[] { "Reading page 1", "Reading page 2" }));
    }

    [Test]
    public async Task ReportAsync_CountersOnly_WritesTheCountersWithTheMessageAsync()
    {
        await using var throttle = NewThrottle();
        _activity.ObjectsToProcess = 500;
        _activity.ObjectsProcessed = 100;
        await throttle.ReportAsync("Processing imported objects");

        _activity.ObjectsProcessed = 200;
        await throttle.ReportAsync();
        _clock.Advance(Interval);

        var last = _syncRepo.ActivityProgressWrites[^1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(last.ObjectsProcessed, Is.EqualTo(200));
            Assert.That(last.ObjectsToProcess, Is.EqualTo(500));
            Assert.That(last.Message, Is.EqualTo("Processing imported objects"), "A report without a message keeps the message the Activity has");
        }
    }

    [Test]
    public async Task ReportAsync_KeepsTheActivityItselfCurrent_WhateverHasBeenWrittenAsync()
    {
        await using var throttle = NewThrottle();
        await throttle.ReportAsync("Reading page 1");
        await throttle.ReportAsync("Reading page 2");

        Assert.That(_activity.Message, Is.EqualTo("Reading page 2"),
            "Only the writing is paced; the Activity the run holds always carries the latest, as anything writing it in full later relies on");
    }

    [Test]
    public async Task DisposeAsync_WithSomethingUnwritten_WritesItAndSchedulesNothingFurtherAsync()
    {
        var throttle = NewThrottle();
        await throttle.ReportAsync("Reading page 1");
        await throttle.ReportAsync("Reading page 2");

        await throttle.DisposeAsync();

        Assert.That(_syncRepo.ActivityProgressWrites.Select(w => w.Message), Is.EqualTo(new[] { "Reading page 1", "Reading page 2" }),
            "When the stretch of the run ends, ends early or fails, what an administrator sees must be exactly where it got to");
        Assert.That(_clock.ScheduledTimerCount, Is.Zero);

        _clock.Advance(Interval * 5);
        Assert.That(_syncRepo.ActivityProgressWrites, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task FlushAsync_WritesWhatIsUnwrittenAtOnce_AndNothingWhenAllIsWrittenAsync()
    {
        await using var throttle = NewThrottle();
        await throttle.ReportAsync("Reading page 1");
        await throttle.FlushAsync();
        Assert.That(_syncRepo.ActivityProgressWrites, Has.Count.EqualTo(1), "Everything was written already");

        await throttle.ReportAsync("Reading page 2");
        await throttle.FlushAsync();
        Assert.That(_syncRepo.ActivityProgressWrites.Select(w => w.Message), Is.EqualTo(new[] { "Reading page 1", "Reading page 2" }));
    }

    [Test]
    public async Task StepChanges_WithinTheInterval_AreWrittenTogetherWithTheirTimingsAsync()
    {
        var throttle = NewThrottle();
        var writesBefore = _syncRepo.ActivityWriteCount;
        await _reporter.EnterAsync(RunPhaseKeys.ImportFetch);

        // A Connector returning a page at a time: its step, then JIM's, then its step again, page after page.
        for (var page = 0; page < 20; page++)
        {
            await _reporter.EnterConnectorPhaseAsync("fetch", $"Fetching page {page + 1}");
            await _reporter.ExitConnectorPhasesAsync();
            await _reporter.EnterAsync(RunPhaseKeys.ImportProcess);
        }

        Assert.That(_syncRepo.ActivityWriteCount - writesBefore, Is.EqualTo(1),
            "Entering the fetch step is written at once; the sixty step changes after it, all within the interval, wait");

        await throttle.DisposeAsync();

        var written = WrittenPhases();
        var live = _reporter.Phases!.ToDictionary(p => p.Key);
        foreach (var key in new[] { ActivityPhase.QualifyConnectorKey("fetch"), RunPhaseKeys.ImportProcess })
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(written[key].Status, Is.EqualTo(live[key].Status), key);
                Assert.That(written[key].Started, Is.EqualTo(live[key].Started), $"{key} started when it was first entered, and the write says so");
                Assert.That(written[key].Ended, Is.EqualTo(live[key].Ended), key);
            }
        }
    }

    [Test]
    public async Task StepChanges_AfterTheThrottleCloses_AreWrittenAtOnceAgainAsync()
    {
        var throttle = NewThrottle();
        await _reporter.EnterAsync(RunPhaseKeys.ImportFetch);
        await throttle.DisposeAsync();
        var writesBefore = _syncRepo.ActivityWriteCount;

        await _reporter.EnterAsync(RunPhaseKeys.ImportSave);

        Assert.That(_syncRepo.ActivityWriteCount, Is.GreaterThan(writesBefore),
            "Outside the paged stretch every step change is written as it happens, as before");
        Assert.That(_syncRepo.ActivityPhases.Single(p => p.Key == RunPhaseKeys.ImportSave).Status, Is.EqualTo(ActivityPhaseStatus.Active));
    }

    [Test]
    public async Task AWriteFails_TheRunCarriesOnAndTheNextWriteCarriesWhatItLostAsync()
    {
        var throttle = NewThrottle();
        var failNext = true;
        _syncRepo.FailActivityProgressWrite = _ =>
        {
            var fail = failNext;
            failNext = false;
            return fail;
        };

        await _reporter.EnterAsync(RunPhaseKeys.ImportFetch, "Fetching");
        Assert.That(_syncRepo.ActivityProgressWrites, Is.Empty, "The write failed");

        await throttle.DisposeAsync();

        Assert.That(WrittenPhases()[RunPhaseKeys.ImportFetch].Status, Is.EqualTo(ActivityPhaseStatus.Active),
            "A step change whose write failed is written by the next write, not lost");
        Assert.That(_syncRepo.ActivityProgressWrites[^1].Message, Is.EqualTo("Fetching"));
    }

    [Test]
    public async Task DisposeAsync_TheWriterOfItsOwnKeepsFailing_WritesTheFinalStateThroughTheRepositoryAsync()
    {
        var throttle = NewThrottle();
        _syncRepo.FailActivityProgressWrite = _ => true;
        await _reporter.EnterAsync(RunPhaseKeys.ImportFetch, "Fetching");
        await _reporter.EnterConnectorPhaseAsync("fetch");
        await _reporter.ExitConnectorPhasesAsync();
        var writesBefore = _syncRepo.ActivityWriteCount;

        await throttle.DisposeAsync();

        Assert.That(_syncRepo.ActivityWriteCount, Is.GreaterThan(writesBefore + 1), "The writer was tried, then the repository");
        Assert.That(_syncRepo.ActivityPhases.Single(p => p.Key == ActivityPhase.QualifyConnectorKey("fetch")).Status,
            Is.EqualTo(ActivityPhaseStatus.Completed),
            "A step that changed only while the throttle was open is written before it closes, however it has to be");
    }

    [Test]
    public async Task WithoutAWriterOfItsOwn_WritesThroughTheRepository_AtMostOncePerIntervalAsync()
    {
        _syncRepo.ProvidesActivityProgressWriter = false;
        var throttle = NewThrottle();
        var writesBefore = _syncRepo.ActivityWriteCount;

        await throttle.ReportAsync("Reading page 1");
        await throttle.ReportAsync("Reading page 2");
        await throttle.ReportAsync("Reading page 3");
        Assert.That(_syncRepo.ActivityWriteCount - writesBefore, Is.EqualTo(1));

        _clock.Advance(Interval);
        await throttle.ReportAsync("Reading page 4");
        Assert.That(_syncRepo.ActivityWriteCount - writesBefore, Is.EqualTo(2),
            "With no connection of its own nothing may write from a timer, so the next report after the interval writes");

        await throttle.ReportAsync("Reading page 5");
        await throttle.DisposeAsync();

        Assert.That(_syncRepo.ActivityWriteCount - writesBefore, Is.EqualTo(3));
        Assert.That(_syncRepo.Activities[_activity.Id].Message, Is.EqualTo("Reading page 5"));
        Assert.That(_syncRepo.ActivityProgressWrites, Is.Empty);
    }

    private ActivityProgressThrottle NewThrottle() => new(_syncRepo, _activity, _reporter, _clock);

    /// <summary>Each step as the last progress write that carried it recorded it.</summary>
    private Dictionary<string, ActivityPhase> WrittenPhases()
    {
        var written = new Dictionary<string, ActivityPhase>();
        foreach (var phase in _syncRepo.ActivityProgressWrites.SelectMany(w => w.Phases))
            written[phase.Key] = phase;
        return written;
    }

    private sealed class PhaseDeclaringConnector : IConnector, IConnectorPhases
    {
        public string Name => "PhaseDeclaringConnector";
        public string? Description => null;
        public string? Url => null;

        public IReadOnlyList<ConnectorPhase> GetPhases(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile) =>
            [new ConnectorPhase("fetch", "Fetching objects")];
    }
}
