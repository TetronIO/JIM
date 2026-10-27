// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Scheduling;
using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;
using JIM.Web.Models;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Services;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Operations page Activity tab's Queued and In progress sections (#2549), which replaced the Queue tab.
/// What these pin is what an administrator depends on: running work and waiting work land in their own sections, the
/// next run sits nearest In progress, parallel runs are shown together under their Schedule Execution, a Schedule
/// Execution keeps the actions it is the only home for, the sections follow the queue live without rebuilding a row
/// under the reader's cursor, and the filters and toggles above the tab reach these sections.
/// </summary>
[TestFixture]
public class OperationsQueueSectionsTests : JimComponentTestContext
{
    private Mock<ITaskingRepository> _taskingRepository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private FakeUiNotificationService _notifications = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _taskingRepository = new Mock<ITaskingRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        repository.Setup(r => r.Tasking).Returns(_taskingRepository.Object);
        repository.Setup(r => r.Activity).Returns(_activityRepository.Object);

        _notifications = new FakeUiNotificationService();

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(new JimApplication(repository.Object)));
        Services.AddSingleton<IUiNotificationService>(_notifications);
        Services.AddSingleton<IWebHostEnvironment>(new FakeWebHostEnvironment());
        Services.AddSingleton(new Mock<IActivityEtaTracker>().Object);
    }

    /// <summary>
    /// NUnit reuses one fixture instance across the fixture and bUnit builds its service provider once, so the
    /// mocks are shared; reset them or arrangements and recorded calls leak between tests.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _taskingRepository.Reset();
        _activityRepository.Reset();
        _activityRepository
            .Setup(r => r.GetScheduleStepOutcomesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync(new Dictionary<Guid, List<ScheduleStepObservation>>());
        _activityRepository
            .Setup(r => r.GetRecentSuccessfulRunProfileDurationsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<int>()))
            .ReturnsAsync(new Dictionary<int, List<TimeSpan>>());
    }

    /// <summary>
    /// Arranges the one read the sections make, answering with whatever the supplied list holds at the time. A copy
    /// is returned per read because the real repository hands back fresh headers each time, and the component detects
    /// change by comparing what it holds against what it was just given.
    /// </summary>
    private void ArrangeQueue(List<WorkerTaskHeader> queue) =>
        _taskingRepository.Setup(r => r.GetWorkerTaskHeadersAsync()).ReturnsAsync(() => [.. queue]);

    private static WorkerTaskHeader Task(
        string connectedSystem,
        string runProfile = "Full Import",
        WorkerTaskStatus status = WorkerTaskStatus.Queued,
        Guid? scheduleExecutionId = null,
        string? scheduleName = null,
        int? stepIndex = null,
        int minutesAgo = 0,
        WorkerTaskExecutionMode mode = WorkerTaskExecutionMode.Sequential) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"{connectedSystem} - {runProfile}",
        Type = "Synchronisation",
        Status = status,
        Timestamp = DateTime.UtcNow.AddMinutes(-minutesAgo),
        ConnectedSystemName = connectedSystem,
        RunProfileName = runProfile,
        ExecutionMode = mode,
        ScheduleExecutionId = scheduleExecutionId,
        ScheduleExecutionName = scheduleName,
        ScheduleStepIndex = stepIndex,
        ScheduleTotalSteps = scheduleExecutionId.HasValue ? 3 : null,
        ScheduleCurrentStepIndex = scheduleExecutionId.HasValue ? 0 : null
    };

    private static string SectionText(IRenderedComponent<OperationsQueueSections> cut, string testId) =>
        cut.FindAll($"[data-testid='{testId}']").SingleOrDefault()?.TextContent ?? string.Empty;

    /// <summary>
    /// Waits for a condition the component reaches off the renderer's own thread. <c>WaitForAssertion</c> re-checks
    /// only when a render happens, and the refresh a notification triggers runs on a background task whose renders do
    /// not reliably reach the rendered component, so it times out on a condition that has long since been met.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
            await System.Threading.Tasks.Task.Delay(50);
    }

    [Test]
    public void OperationsQueueSections_RunningAndWaitingWork_LandInTheirOwnSections()
    {
        ArrangeQueue([Task("HR System", status: WorkerTaskStatus.Processing), Task("LDAP Directory")]);

        var cut = Render<OperationsQueueSections>();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(SectionText(cut, "activity-in-progress"), Does.Contain("HR System"));
                Assert.That(SectionText(cut, "activity-in-progress"), Does.Not.Contain("LDAP Directory"));
                Assert.That(SectionText(cut, "activity-queued"), Does.Contain("LDAP Directory"));
                Assert.That(SectionText(cut, "activity-queued"), Does.Not.Contain("HR System"));
            }
        });
    }

    [Test]
    public void OperationsQueueSections_SeveralQueued_PutsTheNextRunLastSoItSitsAboveInProgress()
    {
        ArrangeQueue([Task("Newer", minutesAgo: 1), Task("Older", minutesAgo: 10)]);

        var cut = Render<OperationsQueueSections>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-testid='activity-queued'] tr.jim-activity-row");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rows, Has.Count.EqualTo(2));
                Assert.That(rows[^1].TextContent, Does.Contain("Older").And.Contain("Next"),
                    "the worker takes the oldest first, so it is the next run and sits nearest In progress");
                Assert.That(rows[0].TextContent, Does.Contain("Newer").And.Contain("2nd"));
            }
        });
    }

    [Test]
    public void OperationsQueueSections_ParallelStepRunning_ShowsItsTasksTogetherUnderOneScheduleHeader()
    {
        var execution = Guid.NewGuid();
        ArrangeQueue(
        [
            Task("Glitterband EMEA", "Full Sync", WorkerTaskStatus.Processing, execution, "Regional Sync Cycle", 1, mode: WorkerTaskExecutionMode.Parallel),
            Task("Yellowstone APAC", "Full Sync", WorkerTaskStatus.Processing, execution, "Regional Sync Cycle", 1, mode: WorkerTaskExecutionMode.Parallel)
        ]);

        var cut = Render<OperationsQueueSections>();

        cut.WaitForAssertion(() =>
        {
            var section = cut.Find("[data-testid='activity-in-progress']");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(section.QuerySelectorAll("tr.jim-activity-group-row"), Has.Length.EqualTo(1),
                    "both runs belong to one Schedule Execution, so one header covers them");
                Assert.That(section.QuerySelectorAll("tr.jim-activity-row"), Has.Length.EqualTo(2));
                Assert.That(cut.FindComponents<WorkerTaskProgress>(), Has.Count.EqualTo(2),
                    "every running task shows its own full progress, not just the first");
                Assert.That(section.TextContent, Does.Contain("2 tasks running in parallel"));
            }
        });
    }

    [Test]
    public void OperationsQueueSections_ScheduleExecutionHeader_KeepsItsRailAndItsActions()
    {
        var execution = Guid.NewGuid();
        ArrangeQueue([Task("HR System", status: WorkerTaskStatus.Processing, scheduleExecutionId: execution, scheduleName: "Nightly Full Sync", stepIndex: 0)]);

        var cut = Render<OperationsQueueSections>();

        cut.WaitForAssertion(() =>
        {
            var header = cut.Find("tr.jim-activity-group-row");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(header.TextContent, Does.Contain("Nightly Full Sync"), "the header must still name its Schedule");
                Assert.That(cut.HasComponent<ScheduleStepRail>(), Is.True, "the Schedule Execution's step rail is the header's own");
                Assert.That(header.QuerySelectorAll("a"), Has.Length.EqualTo(1), "View execution must still link to the Schedule Execution");
                Assert.That(header.QuerySelectorAll("a")[0].GetAttribute("href"),
                    Is.EqualTo($"/admin/operations/schedule-executions/{execution}"));
                Assert.That(header.QuerySelectorAll("button"), Has.Length.EqualTo(1), "Cancel Schedule must survive");
            }
        });
    }

    [Test]
    public async Task OperationsQueueSections_ProgressNotification_ShowsTheNewFiguresAsync()
    {
        var runningTask = Task("HR System", status: WorkerTaskStatus.Processing);
        runningTask.ObjectsToProcess = 12500;
        runningTask.ObjectsProcessed = 8320;
        var queue = new List<WorkerTaskHeader> { runningTask };
        ArrangeQueue(queue);

        var cut = Render<OperationsQueueSections>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("8,320 / 12,500")));

        // The worker reports progress: the header is replaced wholesale, exactly as the refresh does it.
        var advanced = Task("HR System", status: WorkerTaskStatus.Processing);
        advanced.Id = runningTask.Id;
        advanced.ObjectsToProcess = 12500;
        advanced.ObjectsProcessed = 11000;
        queue[0] = advanced;
        _notifications.RaiseActivityProgressChanged(Guid.NewGuid());

        await WaitUntilAsync(() => cut.Markup.Contains("11,000 / 12,500", StringComparison.Ordinal));

        Assert.That(cut.Markup, Does.Contain("11,000 / 12,500"), "a progress notification must reach the running row");
    }

    [Test]
    public async Task OperationsQueueSections_ProgressNotification_UpdatesTheRowInPlaceRatherThanRebuildingItAsync()
    {
        // Progress arrives many times a second during a large import, and a row rebuilt between a mouse press and its
        // release never receives the click: the task's link could not be clicked through to its Activity.
        var runningTask = Task("HR System", status: WorkerTaskStatus.Processing);
        runningTask.ActivityId = Guid.NewGuid();
        runningTask.ObjectsToProcess = 12500;
        runningTask.ObjectsProcessed = 8320;
        var queue = new List<WorkerTaskHeader> { runningTask };
        ArrangeQueue(queue);

        var cut = Render<OperationsQueueSections>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("8,320 / 12,500")));
        var progressBefore = cut.FindComponent<WorkerTaskProgress>().Instance;

        var advanced = Task("HR System", status: WorkerTaskStatus.Processing);
        advanced.Id = runningTask.Id;
        advanced.ActivityId = runningTask.ActivityId;
        advanced.ObjectsToProcess = 12500;
        advanced.ObjectsProcessed = 11000;
        queue[0] = advanced;
        _notifications.RaiseActivityProgressChanged(Guid.NewGuid());

        await WaitUntilAsync(() => cut.Markup.Contains("11,000 / 12,500", StringComparison.Ordinal));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("11,000 / 12,500"), "the row must still show the new figures");
            Assert.That(cut.FindComponent<WorkerTaskProgress>().Instance, Is.SameAs(progressBefore),
                "a progress-only change must update the existing row, not rebuild it under the reader's cursor");
        }
    }

    [Test]
    public async Task OperationsQueueSections_RunFinishes_LeavesInProgressAndTheNextOneMovesInAsync()
    {
        var finishing = Task("HR System", status: WorkerTaskStatus.Processing);
        var next = Task("LDAP Directory");
        var queue = new List<WorkerTaskHeader> { finishing, next };
        ArrangeQueue(queue);

        var cut = Render<OperationsQueueSections>();
        cut.WaitForAssertion(() => Assert.That(SectionText(cut, "activity-in-progress"), Does.Contain("HR System")));

        queue.Remove(finishing);
        next.Status = WorkerTaskStatus.Processing;
        _notifications.RaiseWorkerTaskChanged(new WorkerTaskChangeNotification());

        await WaitUntilAsync(() => SectionText(cut, "activity-in-progress").Contains("LDAP Directory", StringComparison.Ordinal));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Not.Contain("HR System"), "a finished run must leave In progress");
            Assert.That(SectionText(cut, "activity-in-progress"), Does.Contain("LDAP Directory"));
            Assert.That(SectionText(cut, "activity-queued"), Does.Contain("Nothing is queued."));
        }
    }

    [Test]
    public void OperationsQueueSections_NothingQueuedOrRunning_SaysSoRatherThanDrawingEmptyTables()
    {
        ArrangeQueue([]);

        var cut = Render<OperationsQueueSections>();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(SectionText(cut, "activity-queued"), Does.Contain("Nothing is queued."));
                Assert.That(SectionText(cut, "activity-in-progress"), Does.Contain("the worker is idle"));
                Assert.That(cut.FindAll("table"), Is.Empty);
            }
        });
    }

    [Test]
    public void OperationsQueueSections_Filtered_ShowsOnlyMatchingWorkAndKeepsTheTrueRunPosition()
    {
        ArrangeQueue([Task("Glitterband EMEA", minutesAgo: 10), Task("Yellowstone APAC", minutesAgo: 1)]);
        var filters = OperationsActivityFilters.None with { ConnectedSystems = ["Yellowstone APAC"] };

        var cut = Render<OperationsQueueSections>(p => p.Add(c => c.Filters, filters));

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-testid='activity-queued'] tr.jim-activity-row");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rows, Has.Count.EqualTo(1));
                Assert.That(rows[0].TextContent, Does.Contain("Yellowstone APAC").And.Contain("2nd"),
                    "hiding the run ahead of it does not make it the next run");
            }
        });
    }

    [Test]
    public void OperationsQueueSections_SectionTurnedOff_IsNotDrawn()
    {
        ArrangeQueue([Task("HR System", status: WorkerTaskStatus.Processing), Task("LDAP Directory")]);

        var cut = Render<OperationsQueueSections>(p => p.Add(c => c.ShowQueued, false));

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.FindAll("[data-testid='activity-queued']"), Is.Empty);
                Assert.That(SectionText(cut, "activity-in-progress"), Does.Contain("HR System"));
            }
        });
    }

    [Test]
    public void OperationsQueueSections_Loaded_ReportsHowManyAreQueuedAndRunning()
    {
        ArrangeQueue([Task("HR System", status: WorkerTaskStatus.Processing), Task("LDAP Directory"), Task("AD")]);
        (int Queued, int Running)? reported = null;

        Render<OperationsQueueSections>(p => p.Add(c => c.CountsChanged, counts => reported = counts));

        Assert.That(reported, Is.EqualTo((2, 1)));
    }

    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }

    /// <summary>
    /// A notification relay the test drives by hand, standing in for the PostgreSQL NOTIFY listener. Real-time is
    /// reported as available so the polling fallback settles on its slow interval and cannot race a test.
    /// </summary>
    private sealed class FakeUiNotificationService : IUiNotificationService
    {
        public event Action<WorkerTaskChangeNotification>? WorkerTaskChanged;

        public event Action<Guid>? ActivityProgressChanged;

        public event Action<bool>? RealTimeAvailabilityChanged;

        public bool IsRealTimeAvailable => true;

        public void RaiseActivityProgressChanged(Guid activityId) => ActivityProgressChanged?.Invoke(activityId);

        public void RaiseWorkerTaskChanged(WorkerTaskChangeNotification notification) => WorkerTaskChanged?.Invoke(notification);

        // Declared by the interface; referencing it here keeps the compiler from warning about an event that is
        // never raised.
        public void RaiseRealTimeAvailabilityChanged(bool available) => RealTimeAvailabilityChanged?.Invoke(available);
    }

    /// <summary>
    /// The sections read the hosting environment to decide whether the demo-data query string is honoured; reported
    /// as Production here so the tests exercise the real load path.
    /// </summary>
    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "JIM.Web.Tests";

        public string WebRootPath { get; set; } = string.Empty;

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
