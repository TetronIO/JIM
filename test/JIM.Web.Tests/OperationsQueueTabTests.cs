// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Scheduling;
using JIM.Models.Staging.DTOs;
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
/// Covers the Operations queue now that each Schedule Execution's tasks stream through their own virtualised
/// grid. MudBlazor's grouping and its virtualisation are mutually exclusive branches, so the queue could not
/// become one grid without losing the grouping that carries the Schedule Execution's rail, its collapse and its
/// actions; a grid per group is what keeps both. What these pin is what the reader depends on and what a
/// conversion silently breaks: the queue's shape, that a live notification reaches the affected group's grid
/// rather than merely repainting around it, that the group header kept the actions it is the only home for, and
/// that an empty queue still says so.
/// </summary>
[TestFixture]
public class OperationsQueueTabTests : JimComponentTestContext
{
    private Mock<ITaskingRepository> _taskingRepository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private FakeUiNotificationService _notifications = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _taskingRepository = new Mock<ITaskingRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        repository.Setup(r => r.Tasking).Returns(_taskingRepository.Object);
        repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);

        _notifications = new FakeUiNotificationService();

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(new JimApplication(repository.Object)));
        Services.AddSingleton<IUiNotificationService>(_notifications);
        Services.AddSingleton<IWebHostEnvironment>(new FakeWebHostEnvironment());
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
        _connectedSystemRepository.Reset();
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemHeadersAsync()).ReturnsAsync([]);
        _activityRepository
            .Setup(r => r.GetScheduleStepOutcomesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync(new Dictionary<Guid, List<ScheduleStepObservation>>());
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        // The tab holds a polling loop and grids that talk to JavaScript; NUnit reuses the fixture instance across
        // tests, so each rendered component is disposed here rather than left running into the next test.
        await DisposeComponentsAsync();
    }

    /// <summary>
    /// Arranges the one read the queue makes, answering with whatever the supplied list holds at the time. A copy
    /// is returned per read because the real repository hands back fresh headers each time, and the tab detects
    /// change by comparing what it holds against what it was just given; sharing one list would make every read
    /// report that nothing had moved.
    /// </summary>
    private void ArrangeQueue(List<WorkerTaskHeader> queue) =>
        _taskingRepository.Setup(r => r.GetWorkerTaskHeadersAsync()).ReturnsAsync(() => [.. queue]);

    private static WorkerTaskHeader Task(
        string name,
        WorkerTaskStatus status = WorkerTaskStatus.Queued,
        Guid? scheduleExecutionId = null,
        string? scheduleName = null,
        int? stepIndex = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Type = "Synchronisation",
        Status = status,
        Timestamp = DateTime.UtcNow,
        ScheduleExecutionId = scheduleExecutionId,
        ScheduleExecutionName = scheduleName,
        ScheduleStepIndex = stepIndex,
        ScheduleTotalSteps = scheduleExecutionId.HasValue ? 3 : null,
        ScheduleCurrentStepIndex = scheduleExecutionId.HasValue ? 0 : null
    };

    private static int GridCount(IRenderedComponent<OperationsQueueTab> cut) =>
        cut.FindComponents<VirtualisedDataGrid<WorkerTaskHeader>>().Count;

    /// <summary>
    /// Waits for a condition the tab reaches off the renderer's own thread. <c>WaitForAssertion</c> re-checks only
    /// when a render happens, and the refresh a notification triggers runs on a background task whose renders do
    /// not reliably reach the rendered component, so it times out on a condition that has long since been met.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
            await System.Threading.Tasks.Task.Delay(50);
    }

    [Test]
    public void OperationsQueueTab_QueueHoldingTwoSchedulesAndLooseTasks_RendersAGridPerScheduleAndOneForTheRest()
    {
        var nightly = Guid.NewGuid();
        var cloud = Guid.NewGuid();
        ArrangeQueue(
        [
            Task("HR System - Full Import", WorkerTaskStatus.Processing, nightly, "Nightly Full Sync", 0),
            Task("AD - Delta Sync", WorkerTaskStatus.WaitingForPreviousStep, nightly, "Nightly Full Sync", 1),
            Task("Azure AD - Export", WorkerTaskStatus.Queued, cloud, "Cloud Provisioning", 0),
            Task("LDAP Directory - Full Import")
        ]);

        var cut = Render<OperationsQueueTab>();

        cut.WaitForAssertion(() => Assert.That(GridCount(cut), Is.EqualTo(3),
            "the queue draws a grid per Schedule Execution plus one for the tasks running outside a Schedule"));
    }

    [Test]
    public void OperationsQueueTab_ScheduleExecutionGroup_KeepsItsRailAndItsActions()
    {
        var execution = Guid.NewGuid();
        ArrangeQueue([Task("HR System - Full Import", WorkerTaskStatus.Processing, execution, "Nightly Full Sync", 0)]);

        var cut = Render<OperationsQueueTab>();

        cut.WaitForAssertion(() =>
        {
            // The Cancel action's label lives in a tooltip, which is only in the DOM once it is hovered, so the
            // header's own action block is what the assertion counts: a link out to the Schedule Execution and a
            // button beside it. Both are the group header's alone; no row carries either.
            var actions = cut.Find(".jim-queue-group-actions");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("Nightly Full Sync"), "the group header must still name its Schedule");
                Assert.That(cut.HasComponent<ScheduleStepRail>(), Is.True, "the Schedule Execution's step rail is the header's own");
                Assert.That(cut.Markup, Does.Contain("View execution"));
                Assert.That(actions.QuerySelectorAll("a"), Has.Length.EqualTo(1), "View execution must still link to the Schedule Execution");
                Assert.That(actions.QuerySelectorAll("a")[0].GetAttribute("href"),
                    Is.EqualTo($"/admin/operations/schedule-executions/{execution}"));
                Assert.That(actions.QuerySelectorAll("button"), Has.Length.EqualTo(1), "Cancel Schedule must survive the conversion");
            }
        });
    }

    [Test]
    public void OperationsQueueTab_GroupHeaderClicked_CollapsesThatScheduleAndLeavesTheRest()
    {
        var nightly = Guid.NewGuid();
        var cloud = Guid.NewGuid();
        ArrangeQueue(
        [
            Task("HR System - Full Import", WorkerTaskStatus.Processing, nightly, "Nightly Full Sync", 0),
            Task("Azure AD - Export", WorkerTaskStatus.Queued, cloud, "Cloud Provisioning", 0)
        ]);

        var cut = Render<OperationsQueueTab>();
        cut.WaitForAssertion(() => Assert.That(GridCount(cut), Is.EqualTo(2)));

        cut.Find(".jim-queue-group-toggle").Click();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(GridCount(cut), Is.EqualTo(1), "collapsing a group must put away its rows");
                Assert.That(cut.Markup, Does.Contain("Nightly Full Sync"),
                    "the collapsed group keeps its header; it is the only way back to its rows");
            }
        });
    }

    [Test]
    public async Task OperationsQueueTab_ProgressNotification_ReachesTheGridOfTheGroupThatChangedAsync()
    {
        // The queue moving under the reader is the whole point of the screen. A virtualised grid caches the rows
        // it has fetched, so repainting the tab around it leaves the reader watching a figure that stopped.
        var execution = Guid.NewGuid();
        var runningTask = Task("HR System - Full Import", WorkerTaskStatus.Processing, execution, "Nightly Full Sync", 0);
        runningTask.ObjectsToProcess = 12500;
        runningTask.ObjectsProcessed = 8320;
        var queue = new List<WorkerTaskHeader> { runningTask };
        ArrangeQueue(queue);

        var cut = Render<OperationsQueueTab>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("8,320 / 12,500")));

        // The worker reports progress: the header is replaced wholesale, exactly as the refresh does it.
        var advanced = Task("HR System - Full Import", WorkerTaskStatus.Processing, execution, "Nightly Full Sync", 0);
        advanced.Id = runningTask.Id;
        advanced.ObjectsToProcess = 12500;
        advanced.ObjectsProcessed = 11000;
        queue[0] = advanced;
        _notifications.RaiseActivityProgressChanged(Guid.NewGuid());

        await WaitUntilAsync(() => cut.Markup.Contains("11,000 / 12,500", StringComparison.Ordinal));

        Assert.That(cut.Markup, Does.Contain("11,000 / 12,500"),
            "a progress notification must reload the affected group's grid; its rows are cached and would otherwise stay frozen");
    }

    [Test]
    public async Task OperationsQueueTab_NewScheduleQueued_AppearsWithoutTheReaderActingAsync()
    {
        var nightly = Guid.NewGuid();
        var queue = new List<WorkerTaskHeader>
        {
            Task("HR System - Full Import", WorkerTaskStatus.Processing, nightly, "Nightly Full Sync", 0)
        };
        ArrangeQueue(queue);

        var cut = Render<OperationsQueueTab>();
        cut.WaitForAssertion(() => Assert.That(GridCount(cut), Is.EqualTo(1)));

        queue.Add(Task("Azure AD - Export", WorkerTaskStatus.Queued, Guid.NewGuid(), "Cloud Provisioning", 0));
        _notifications.RaiseWorkerTaskChanged(new WorkerTaskChangeNotification());

        await WaitUntilAsync(() => GridCount(cut) == 2);

        Assert.That(GridCount(cut), Is.EqualTo(2),
            "a Schedule queued while the reader is watching must appear as its own block, unprompted");
    }

    [Test]
    public void OperationsQueueTab_NothingQueued_SaysSoRatherThanDrawingAnEmptyTable()
    {
        ArrangeQueue([]);

        var cut = Render<OperationsQueueTab>();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("There are no tasks queued."));
                Assert.That(GridCount(cut), Is.Zero);
            }
        });
    }

    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }

    /// <summary>
    /// A notification relay the test drives by hand, standing in for the PostgreSQL NOTIFY listener. Real-time is
    /// reported as available so the tab's polling fallback settles on its slow interval and cannot race a test.
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
    /// The queue reads the hosting environment to decide whether its demo-data query string is honoured; reported
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
