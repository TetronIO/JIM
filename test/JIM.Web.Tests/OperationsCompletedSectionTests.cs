// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Threading.Tasks;
using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core.DTOs;
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
/// Covers the Operations Activity tab's Completed section (#2549), which replaced the History tab. Four things are
/// worth pinning, because each fails silently: it must never show a run still in progress, which In progress shows
/// instead (twice on one page, at two levels of detail, is what the tab was built to end); it must forward the grid's skip-the-count request rather
/// than deciding for itself (a hardcoded false leaves the list with no total forever, and a hardcoded true
/// re-counts the whole match set on every scroll window); a real-time notification must reach the grid, because
/// the rows the virtualiser holds are cached and a repaint alone leaves a running execution frozen; and the two
/// empty states have to be told apart, because "your search matched nothing" and "nothing has run yet" have
/// different ways out.
/// </summary>
[TestFixture]
public class OperationsCompletedSectionTests : JimComponentTestContext
{
    private Mock<IActivityRepository> _activityRepository = null!;
    private Mock<IMetaverseRepository> _metaverseRepository = null!;
    private FakeUiNotificationService _notifications = null!;
    private NavigationManager _navigation = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        _metaverseRepository = new Mock<IMetaverseRepository>();
        repository.Setup(r => r.Metaverse).Returns(_metaverseRepository.Object);

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
        _metaverseRepository.Reset();
        _activityRepository.Setup(r => r.GetWorkerTaskActivityFilterOptionsAsync())
            .ReturnsAsync(new ActivityFilterOptions());
        _navigation = Services.GetRequiredService<NavigationManager>();
        _navigation.NavigateTo("/admin/operations?t=activity");
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
    public void OperationsCompletedSection_FirstWindow_AsksTheRepositoryForTheTotalCount()
    {
        // The grid decides when a count is worth paying for and says so on the request; the tab must forward that
        // decision. Hardcoding it either way is invisible in the markup: false leaves the toolbar count and the
        // scroll area sized from nothing, true re-counts the whole match set once per scroll window.
        ArrangeWindow([RunProfileExecution("HR System", "Full Import", ActivityStatus.Complete)], 1);

        var cut = Render<OperationsCompletedSection>();

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
    public void OperationsCompletedSection_WindowLoaded_NeverAsksForRunsStillInProgress()
    {
        ArrangeWindow([RunProfileExecution("HR System", "Full Import", ActivityStatus.Complete)], 1);

        var cut = Render<OperationsCompletedSection>();

        cut.WaitForAssertion(() => Assert.That(StatusFiltersRequested(), Is.Not.Empty));
        Assert.That(StatusFiltersRequested(), Has.All.Not.Null.And.All.Not.Contains(ActivityStatus.InProgress),
            "every read must exclude In Progress; In progress shows those runs with their full progress");
    }

    [Test]
    public void OperationsCompletedSection_ResultChosen_AsksForJustThatResult()
    {
        ArrangeWindow([RunProfileExecution("HR System", "Full Import", ActivityStatus.FailedWithError)], 1);
        var filters = JIM.Web.Models.OperationsActivityFilters.None with { Results = [ActivityStatus.FailedWithError] };

        var cut = Render<OperationsCompletedSection>(p => p.Add(c => c.Filters, filters));

        cut.WaitForAssertion(() => Assert.That(StatusFiltersRequested(), Is.Not.Empty));
        Assert.That(StatusFiltersRequested()[0], Is.EqualTo(new[] { ActivityStatus.FailedWithError }));
    }

    /// <summary>The status filter each range read asked the repository for.</summary>
    private List<IEnumerable<ActivityStatus>?> StatusFiltersRequested() => _activityRepository.Invocations
        .Where(i => i.Method.Name == nameof(IActivityRepository.GetActivitiesRangeAsync))
        .Select(i => (IEnumerable<ActivityStatus>?)i.Arguments[9])
        .ToList();

    [Test]
    public void OperationsCompletedSection_WindowLoaded_RendersTheExecutionsRows()
    {
        ArrangeWindow([RunProfileExecution("HR System", "Full Import", ActivityStatus.Complete)], 1);

        var cut = Render<OperationsCompletedSection>();

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
    public async Task OperationsCompletedSection_ActivityProgressNotification_ReloadsTheGridWithoutTheReaderActingAsync()
    {
        // The queue changing is the whole reason this tab exists. The virtualiser caches the rows it has fetched,
        // so a notification that only repaints the component leaves the reader looking at the previous run: the
        // grid itself has to be told to re-read, and to re-count, because an execution finishing changes both.
        var executions = new List<Activity> { RunProfileExecution("HR System", "Full Import", ActivityStatus.InProgress) };
        ArrangeWindow(executions, 1);

        var cut = Render<OperationsCompletedSection>();
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
    public async Task OperationsCompletedSection_UnchangedData_DoesNotReloadTheGridAsync()
    {
        // The sibling half of the contract: notifications and the polling fallback both arrive constantly, and a
        // reload on every one of them would re-count the whole match set for nothing.
        ArrangeWindow([RunProfileExecution("HR System", "Full Import", ActivityStatus.Complete)], 1);

        var cut = Render<OperationsCompletedSection>();
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
    public void OperationsCompletedSection_NothingHasRun_SaysSoRatherThanOfferingToClearASearch()
    {
        ArrangeWindow([], 0);

        var cut = Render<OperationsCompletedSection>();

        cut.WaitForAssertion(() =>
        {
            var emptyState = cut.FindComponent<TableEmptyState>();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(emptyState.Instance.PrimaryText, Is.EqualTo("Nothing has finished yet"));
                Assert.That(emptyState.Instance.ActionText, Is.Null,
                    "there is nothing to clear when the list has simply never been populated");
            }
        });
    }

    [Test]
    public void OperationsCompletedSection_SearchMatchedNothing_OffersToClearTheSearch()
    {
        // The grid reads its own search from the URL under this tab's parameter prefix, so a deep link into a
        // search that matches nothing must produce the search-shaped empty state, not the never-run one.
        _navigation.NavigateTo("/admin/operations?t=activity&h-q=nothing-matches-this");
        ArrangeWindow([], 0);

        var cut = Render<OperationsCompletedSection>();

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

    [Test]
    public void OperationsCompletedSection_ToolBar_IsTheSectionsHeadingWithItsChevronAndPauseButton()
    {
        // One row does the work of three: the section's heading (chevron, icon, name), the grid's count, search and
        // density toggle, and the auto-refresh pause, all above the paper rather than a heading over a toolbar over
        // a column header.
        ArrangeWindow([RunProfileExecution("HR System", "Full Import", ActivityStatus.Complete)], 1);

        var cut = Render<OperationsCompletedSection>();

        cut.WaitForAssertion(() =>
        {
            var toolbar = cut.Find(".jim-virtualised-list-toolbar");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(toolbar.TextContent, Does.Contain("Completed"));
                Assert.That(toolbar.QuerySelector("[aria-expanded]")?.GetAttribute("aria-expanded"), Is.EqualTo("true"));
                Assert.That(toolbar.QuerySelector("[aria-label='Pause auto-refresh']"), Is.Not.Null);
                Assert.That(toolbar.QuerySelector("input[placeholder]"), Is.Not.Null, "the search box is in the same row");
                Assert.That(cut.FindAll(".jim-activity-section-header"), Is.Empty, "no second heading row");
            }
        });
    }

    [Test]
    public async Task OperationsCompletedSection_ChevronClicked_AsksTheTabToToggleAsync()
    {
        ArrangeWindow([], 0);
        var toggled = false;

        var cut = Render<OperationsCompletedSection>(p => p.Add(c => c.ToggleRequested, () => toggled = true));
        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".jim-virtualised-list-toolbar [aria-expanded]"), Has.Count.EqualTo(1)));

        await cut.Find(".jim-virtualised-list-toolbar [aria-expanded]").ClickAsync();

        Assert.That(toggled, Is.True);
    }

    [Test]
    public async Task OperationsCompletedSection_Collapsed_FoldsTheGridAndRefreshesOnlyOnceExpandedAgainAsync()
    {
        // Folded, the section is its heading; nothing under it can be seen, so nothing under it is re-read on every
        // notification. Unfolding re-reads once, so what comes back into view is current.
        ArrangeWindow([RunProfileExecution("HR System", "Full Import", ActivityStatus.Complete)], 1);

        var cut = Render<OperationsCompletedSection>(p => p.Add(c => c.Expanded, false));
        cut.WaitForAssertion(() => Assert.That(cut.FindComponent<MudBlazor.MudCollapse>().Instance.Expanded, Is.False));
        var readsWhileFolded = WindowReads();

        _notifications.RaiseActivityProgressChanged(Guid.NewGuid());
        await Task.Delay(200);
        Assert.That(WindowReads(), Is.EqualTo(readsWhileFolded), "a folded section has nothing to keep current");

        cut.Render(p => p.Add(c => c.Expanded, true));
        await WaitUntilAsync(() => WindowReads() > readsWhileFolded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindComponent<MudBlazor.MudCollapse>().Instance.Expanded, Is.True);
            Assert.That(WindowReads(), Is.GreaterThan(readsWhileFolded), "unfolding checks for what changed meanwhile");
        }
    }

    [Test]
    public async Task OperationsCompletedSection_SearchTyped_SurvivesBeingFoldedAndUnfoldedAsync()
    {
        // The grid stays mounted under the fold. A grid rebuilt on unfolding would re-read the URL as it stood on
        // arrival (its own writes go through history.replaceState, which NavigationManager never sees) and lose
        // the search.
        ArrangeWindow([RunProfileExecution("HR System", "Full Import", ActivityStatus.Complete)], 1);

        var cut = Render<OperationsCompletedSection>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Full Import")));
        var search = cut.FindComponent<SearchField>();
        await cut.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("Full"));

        cut.Render(p => p.Add(c => c.Expanded, false));
        cut.Render(p => p.Add(c => c.Expanded, true));

        Assert.That(cut.FindComponent<VirtualisedDataGrid<Activity>>().Instance.SearchText, Is.EqualTo("Full"));
    }

    [Test]
    public void OperationsCompletedSection_RunThatChangedNothing_SaysSoInsteadOfACountOfZero()
    {
        ArrangeWindow([RunProfileExecution("HR System", "Full Import", ActivityStatus.Complete)], 1);

        var cut = Render<OperationsCompletedSection>();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.FindAll("[data-testid='no-changes']"), Has.Count.EqualTo(1));
                Assert.That(cut.FindAll(".jim-stat-chip"), Is.Empty, "a zero wears no chip");
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

    private static Activity MetaverseObjectActivity(Guid metaverseObjectId) => new()
    {
        Id = Guid.NewGuid(),
        TargetType = ActivityTargetType.MetaverseObject,
        TargetOperationType = ActivityTargetOperationType.Update,
        TargetName = "Priya Shah",
        MetaverseObjectId = metaverseObjectId,
        Status = ActivityStatus.Complete
    };

    [Test]
    public async Task OperationsCompletedSection_MetaverseObjectActivitySelected_LinksTheObjectUnderItsTypesPluralNameAsync()
    {
        // The panel used to link /admin/metaverse/{id}, a route that does not exist (#2151).
        var metaverseObjectId = Guid.NewGuid();
        _metaverseRepository.Setup(r => r.GetMetaverseObjectHeaderAsync(metaverseObjectId))
            .ReturnsAsync(new MetaverseObjectHeader { Id = metaverseObjectId, TypeName = "Person", TypePluralName = "People" });
        ArrangeWindow([MetaverseObjectActivity(metaverseObjectId)], 1);

        var cut = Render<OperationsCompletedSection>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Priya Shah")));
        await cut.FindAll("tbody tr").First(row => row.TextContent.Contains("Priya Shah")).ClickAsync();

        cut.WaitForAssertion(() => Assert.That(
            cut.FindAll("a").Select(a => a.GetAttribute("href")),
            Does.Contain($"/t/people/v/{metaverseObjectId}")));
    }

    [Test]
    public async Task OperationsCompletedSection_ActivityWithAWarningSelected_ShowsEachWarningInThePanelAsync()
    {
        // The panel's status chip said Complete With Warning but not why: the reason was only on the Activity page.
        var activity = RunProfileExecution("Glitterband EMEA", "Export", ActivityStatus.CompleteWithWarning);
        activity.WarningMessage = "Memory was low while this ran.\n\nJIM couldn't probe Panoply AD for values already in use.";
        ArrangeWindow([activity], 1);

        var cut = Render<OperationsCompletedSection>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Glitterband EMEA")));
        await cut.FindAll("tbody tr").First(row => row.TextContent.Contains("Glitterband EMEA")).ClickAsync();

        cut.WaitForAssertion(() =>
        {
            var warnings = cut.FindComponents<MudBlazor.MudAlert>().Where(a => a.Instance.Severity == MudBlazor.Severity.Warning).ToList();
            Assert.That(warnings, Has.Count.EqualTo(1), "the panel must carry the Activity's warning");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(warnings[0].Markup, Does.Contain("Memory was low while this ran."));
                Assert.That(warnings[0].Markup, Does.Contain("JIM couldn&#x27;t probe Panoply AD").Or.Contain("JIM couldn't probe Panoply AD"));
            }
        });
    }

    [Test]
    public async Task OperationsCompletedSection_ActivityWithoutAWarningSelected_ShowsNoWarningAsync()
    {
        ArrangeWindow([RunProfileExecution("Glitterband EMEA", "Export", ActivityStatus.Complete)], 1);

        var cut = Render<OperationsCompletedSection>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Glitterband EMEA")));
        await cut.FindAll("tbody tr").First(row => row.TextContent.Contains("Glitterband EMEA")).ClickAsync();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("View Full Details"), "the panel did not open");
            Assert.That(cut.FindComponents<MudBlazor.MudAlert>().Where(a => a.Instance.Severity == MudBlazor.Severity.Warning), Is.Empty);
        });
    }

    [Test]
    public async Task OperationsCompletedSection_MetaverseObjectActivityForADeletedObject_NamesItWithoutALinkAsync()
    {
        var metaverseObjectId = Guid.NewGuid();
        _metaverseRepository.Setup(r => r.GetMetaverseObjectHeaderAsync(metaverseObjectId))
            .ReturnsAsync((MetaverseObjectHeader?)null);
        ArrangeWindow([MetaverseObjectActivity(metaverseObjectId)], 1);

        var cut = Render<OperationsCompletedSection>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Priya Shah")));
        await cut.FindAll("tbody tr").First(row => row.TextContent.Contains("Priya Shah")).ClickAsync();

        cut.WaitForAssertion(() =>
        {
            _metaverseRepository.Verify(r => r.GetMetaverseObjectHeaderAsync(metaverseObjectId), Times.AtLeastOnce);
            Assert.That(cut.FindAll("a").Select(a => a.GetAttribute("href") ?? string.Empty),
                Has.None.Contains(metaverseObjectId.ToString()));
        });
    }
}
