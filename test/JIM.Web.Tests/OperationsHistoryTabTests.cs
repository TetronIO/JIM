// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Tasking;
using JIM.Models.Utility;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Services;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Operations History tab now that it is a virtualised grid rather than a paged table. Three things
/// are worth pinning, because each fails silently: the tab must forward the grid's skip-the-count request rather
/// than deciding for itself (a hardcoded false leaves the list with no total forever, and a hardcoded true
/// re-counts the whole match set on every scroll window); a real-time notification must reach the grid, because
/// the rows the virtualiser holds are cached and a repaint alone leaves a running execution frozen; and the two
/// empty states have to be told apart, because "your search matched nothing" and "nothing has run yet" have
/// different ways out.
/// </summary>
[TestFixture]
public class OperationsHistoryTabTests : JimComponentTestContext
{
    private Mock<IActivityRepository> _activityRepository = null!;
    private FakeUiNotificationService _notifications = null!;
    private NavigationManager _navigation = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        repository.Setup(r => r.Activity).Returns(_activityRepository.Object);

        _notifications = new FakeUiNotificationService();

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(new JimApplication(repository.Object)));
        Services.AddSingleton<IUiNotificationService>(_notifications);
    }

    /// <summary>
    /// NUnit reuses one fixture instance across the whole fixture and the bUnit service provider is built once in
    /// the base constructor, so the mock is shared; reset it or arrangements and recorded calls leak between tests.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _activityRepository.Reset();
        _activityRepository.Setup(r => r.GetWorkerTaskActivityFilterOptionsAsync())
            .ReturnsAsync(new ActivityFilterOptions());
        _navigation = Services.GetRequiredService<NavigationManager>();
        _navigation.NavigateTo("/admin/operations?t=history");
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        // The tab holds a polling loop and a grid that talks to JavaScript; NUnit reuses the fixture instance
        // across tests, so each rendered component is disposed here rather than left running into the next test.
        await DisposeComponentsAsync();
    }

    /// <summary>
    /// Arranges the one range read the tab makes, returning the supplied executions and total for every window.
    /// </summary>
    private void ArrangeWindow(List<Activity> results, int? total)
    {
        _activityRepository
            .Setup(r => r.GetActivitiesRangeAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<IEnumerable<ActivityTargetOperationType>?>(),
                It.IsAny<IEnumerable<ActivityOutcomeType>?>(), It.IsAny<IEnumerable<ActivityTargetType>?>(),
                It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>(),
                It.IsAny<IEnumerable<ActivityInitiatorType>?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<IEnumerable<string>?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(),
                It.IsAny<bool?>(), It.IsAny<IEnumerable<Guid>?>(), It.IsAny<bool>()))
            .ReturnsAsync(() => new RangeResultSet<Activity> { Results = results, TotalResults = total });
    }

    private static Activity RunProfileExecution(string connectedSystem, string runProfile, ActivityStatus status) => new()
    {
        Id = Guid.NewGuid(),
        TargetType = ActivityTargetType.ConnectedSystemRunProfile,
        TargetOperationType = ActivityTargetOperationType.Execute,
        TargetContext = connectedSystem,
        TargetName = runProfile,
        Status = status
    };

    [Test]
    public void OperationsHistoryTab_FirstWindow_AsksTheRepositoryForTheTotalCount()
    {
        // The grid decides when a count is worth paying for and says so on the request; the tab must forward that
        // decision. Hardcoding it either way is invisible in the markup: false leaves the toolbar count and the
        // scroll area sized from nothing, true re-counts the whole match set once per scroll window.
        ArrangeWindow([RunProfileExecution("HR System", "Full Import", ActivityStatus.Complete)], 1);

        var cut = Render<OperationsHistoryTab>();

        cut.WaitForAssertion(() => _activityRepository.Verify(r => r.GetActivitiesRangeAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
            It.IsAny<Guid?>(), It.IsAny<IEnumerable<ActivityTargetOperationType>?>(),
            It.IsAny<IEnumerable<ActivityOutcomeType>?>(), It.IsAny<IEnumerable<ActivityTargetType>?>(),
            It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>(),
            It.IsAny<IEnumerable<ActivityInitiatorType>?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<IEnumerable<string>?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(),
            It.IsAny<bool?>(), It.IsAny<IEnumerable<Guid>?>(), true), Times.AtLeastOnce));
    }

    [Test]
    public void OperationsHistoryTab_WindowLoaded_RendersTheExecutionsRows()
    {
        ArrangeWindow([RunProfileExecution("HR System", "Full Import", ActivityStatus.Complete)], 1);

        var cut = Render<OperationsHistoryTab>();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("HR System"));
                Assert.That(cut.Markup, Does.Contain("Full Import"));
            }
        });
    }

    /// <summary>
    /// How many windows the grid has asked the repository to count. A counted read is the grid reloading itself
    /// (the toolbar count and the scroll area are both sized from that total); the tab's own change-detection
    /// probe deliberately asks for no count, so this separates "the grid re-read" from "the tab looked".
    /// </summary>
    private int CountedWindowReads() => WindowReads(counted: true);

    private int WindowReads(bool? counted = null) => _activityRepository.Invocations
        .Count(i => i.Method.Name == nameof(IActivityRepository.GetActivitiesRangeAsync)
                    && (counted == null || (bool)i.Arguments[^1]! == counted));

    /// <summary>
    /// Waits for a condition the tab reaches off the renderer's own thread. <c>WaitForAssertion</c> re-checks only
    /// when a render happens, and the refresh a notification triggers runs on a background task whose renders do
    /// not reliably reach the rendered component, so it times out on a condition that has long since been met.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
            await Task.Delay(50);
    }

    [Test]
    public async Task OperationsHistoryTab_ActivityProgressNotification_ReloadsTheGridWithoutTheReaderActingAsync()
    {
        // The queue changing is the whole reason this tab exists. The virtualiser caches the rows it has fetched,
        // so a notification that only repaints the component leaves the reader looking at the previous run: the
        // grid itself has to be told to re-read, and to re-count, because an execution finishing changes both.
        var executions = new List<Activity> { RunProfileExecution("HR System", "Full Import", ActivityStatus.InProgress) };
        ArrangeWindow(executions, 1);

        var cut = Render<OperationsHistoryTab>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Full Import")));
        var countedReadsBeforeNotification = CountedWindowReads();

        // A second execution finishes while the reader is looking at the list.
        executions.Insert(0, RunProfileExecution("Payroll", "Delta Import", ActivityStatus.Complete));
        _notifications.RaiseActivityProgressChanged(Guid.NewGuid());

        await WaitUntilAsync(() => CountedWindowReads() > countedReadsBeforeNotification);

        Assert.That(CountedWindowReads(), Is.GreaterThan(countedReadsBeforeNotification),
            "a progress notification must reload the grid's windows and re-count them; the rows and the count it holds are both stale");
    }

    [Test]
    public async Task OperationsHistoryTab_UnchangedData_DoesNotReloadTheGridAsync()
    {
        // The sibling half of the contract: notifications and the polling fallback both arrive constantly, and a
        // reload on every one of them would re-count the whole match set for nothing.
        ArrangeWindow([RunProfileExecution("HR System", "Full Import", ActivityStatus.Complete)], 1);

        var cut = Render<OperationsHistoryTab>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Full Import")));
        var readsBeforeNotification = WindowReads();
        var countedReadsBeforeNotification = CountedWindowReads();

        _notifications.RaiseActivityProgressChanged(Guid.NewGuid());

        // Wait for the notification's own probe to run and decide against reloading.
        await WaitUntilAsync(() => WindowReads() > readsBeforeNotification);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(WindowReads(), Is.GreaterThan(readsBeforeNotification), "the change-detection probe never ran");
            Assert.That(CountedWindowReads(), Is.EqualTo(countedReadsBeforeNotification),
                "nothing changed, so the grid must not re-read and re-count its windows");
        }
    }

    [Test]
    public void OperationsHistoryTab_NothingHasRun_SaysSoRatherThanOfferingToClearASearch()
    {
        ArrangeWindow([], 0);

        var cut = Render<OperationsHistoryTab>();

        cut.WaitForAssertion(() =>
        {
            var emptyState = cut.FindComponent<TableEmptyState>();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(emptyState.Instance.PrimaryText, Is.EqualTo("Nothing has run yet"));
                Assert.That(emptyState.Instance.ActionText, Is.Null,
                    "there is nothing to clear when the list has simply never been populated");
            }
        });
    }

    [Test]
    public void OperationsHistoryTab_SearchMatchedNothing_OffersToClearTheSearch()
    {
        // The grid reads its own search from the URL under this tab's parameter prefix, so a deep link into a
        // search that matches nothing must produce the search-shaped empty state, not the never-run one.
        _navigation.NavigateTo("/admin/operations?t=history&h-q=nothing-matches-this");
        ArrangeWindow([], 0);

        var cut = Render<OperationsHistoryTab>();

        cut.WaitForAssertion(() =>
        {
            var emptyState = cut.FindComponent<TableEmptyState>();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(emptyState.Instance.PrimaryText, Does.Contain("nothing-matches-this"));
                Assert.That(emptyState.Instance.ActionText, Is.EqualTo("Clear Search"));
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

        // Declared by the interface; referencing them here keeps the compiler from warning about events that
        // are never raised.
        public void RaiseWorkerTaskChanged(WorkerTaskChangeNotification notification) => WorkerTaskChanged?.Invoke(notification);

        public void RaiseRealTimeAvailabilityChanged(bool available) => RealTimeAvailabilityChanged?.Invoke(available);
    }
}
