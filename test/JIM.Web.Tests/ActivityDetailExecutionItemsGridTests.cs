// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Utility;
using JIM.Web.Pages;
using JIM.Web.Services;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Activity detail page streams its Run Profile Execution Items from a range read, which an Activity holding
/// millions of items depends on: the window is read at an absolute offset and the match set is counted only when
/// the filters change. Three things in that contract fail silently rather than visibly, so they are pinned here:
/// counting on every window (a full scan per scroll, which looks merely slow), a chip filter that reloads without
/// re-counting (a toolbar count and a scroll area sized for a match set that no longer exists), and an empty state
/// that cannot tell a search matching nothing from an Activity that recorded nothing.
/// </summary>
[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class ActivityDetailExecutionItemsGridTests : JimComponentTestContext
{
    private static readonly Guid ActivityId = Guid.Parse("2f1c4d6e-8b3a-4f21-9c7d-5a0e6b8f1234");

    private Mock<IActivityRepository> _activityRepository = null!;

    /// <summary>
    /// Every execution item window the page asked for, in order, so a test can assert on which of them asked for
    /// the total as well as on the filters they carried.
    /// </summary>
    private readonly List<ExecutionItemWindowRequest> _windowRequests = [];

    private List<ActivityRunProfileExecutionItemHeader> _items = [];

    // The configuration changes the Activity claimed or saved (#2615), read on every load and when the run ends.
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private List<ConfigurationChangeSynchronisationSummary> _synchronisationChanges = [];

    // Read on every load of the Activity, so a test can move the run on between the page's refreshes.
    private ActivityStatus _status = ActivityStatus.Complete;

    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IUserPreferenceService>(new FakeUserPreferenceService());
        Services.AddSingleton(new Mock<IUiNotificationService>().Object);
        Services.AddSingleton(new Mock<IActivityEtaTracker>().Object);
    }

    [SetUp]
    public void SetUp()
    {
        _items =
        [
            new ActivityRunProfileExecutionItemHeader
            {
                Id = Guid.NewGuid(),
                DisplayName = "Tina Turner",
                ExternalIdValue = "CN=tina",
                ConnectedSystemObjectType = "user",
                OutcomeSummary = "CsoAdded:1"
            }
        ];

        _activityRepository = new Mock<IActivityRepository>();

        _activityRepository.Setup(r => r.GetActivityAsync(ActivityId)).ReturnsAsync(() => new Activity
        {
            Id = ActivityId,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Execute,
            TargetName = "Full Import",
            ConnectedSystemRunType = ConnectedSystemRunType.FullImport,
            Status = _status,
            InitiatedByType = ActivityInitiatorType.System
        });

        _activityRepository.Setup(r => r.GetActivityRunProfileExecutionStatsAsync(ActivityId))
            .ReturnsAsync(() => new ActivityRunProfileExecutionStats
            {
                ActivityId = ActivityId,
                TotalCsoAdds = _items.Count,
                TotalObjectChangeCount = _items.Count,
                TotalObjectTypes = 1,
                ObjectTypeCounts = new Dictionary<string, int> { { "user", _items.Count } }
            });

        _activityRepository.Setup(r => r.GetActivityPhasesAsync(ActivityId))
            .ReturnsAsync(new List<ActivityPhase>());

        // No child Activities, so the page renders the execution items grid alone.
        _activityRepository.Setup(r => r.GetChildActivityCountsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new Dictionary<Guid, int>());

        _activityRepository.Setup(r => r.GetActivityRunProfileExecutionItemHeadersRangeAsync(
                ActivityId,
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<IEnumerable<ActivityRunProfileExecutionItemErrorType>?>(),
                It.IsAny<IEnumerable<ActivityRunProfileExecutionItemSyncOutcomeType>?>(),
                It.IsAny<bool>()))
            .ReturnsAsync((
                Guid _,
                int _,
                int _,
                string? searchQuery,
                string? _,
                bool _,
                IEnumerable<string>? _,
                IEnumerable<ActivityRunProfileExecutionItemErrorType>? _,
                IEnumerable<ActivityRunProfileExecutionItemSyncOutcomeType>? outcomeTypeFilter,
                bool includeTotalCount) =>
            {
                _windowRequests.Add(new ExecutionItemWindowRequest(
                    searchQuery, outcomeTypeFilter?.ToList(), includeTotalCount));

                return new RangeResultSet<ActivityRunProfileExecutionItemHeader>
                {
                    Results = _items,
                    TotalResults = includeTotalCount ? _items.Count : null
                };
            });

        _synchronisationChanges = [];
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _connectedSystemRepository.Setup(r => r.GetConfigurationChangeSynchronisationSummariesForActivityAsync(ActivityId, It.IsAny<int>()))
            .ReturnsAsync(() => _synchronisationChanges.ToList());
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemObjectCountAsync(3, 7, null)).ReturnsAsync(12_500);

        var repository = new Mock<IRepository>();
        repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
    }

    [Test]
    public void ActivityDetail_ExecutionItemsUnfiltered_TakeTheirTotalFromTheStatsRatherThanCounting()
    {
        // Counting is a full scan of the match set. Unfiltered, the stats the page has already read hold the same
        // number, so the range read must never be asked to count: at customer scale that scan is the expensive half
        // of every window, and the live refresh would otherwise pay for it every few seconds.
        var cut = RenderActivityDetail();
        var grid = WaitForGrid(cut);

        cut.WaitForAssertion(() => Assert.That(grid.TotalItems, Is.EqualTo(1), "the toolbar count must come from the stats"));
        cut.InvokeAsync(() => grid.RefreshAsync(invalidateTotals: true)).GetAwaiter().GetResult();

        cut.WaitForAssertion(() => Assert.That(_windowRequests, Has.Count.GreaterThan(1)));
        Assert.That(_windowRequests.Any(r => r.IncludeTotalCount), Is.False,
            "an unfiltered window must not count in the database; the stats already answer it");
    }

    [Test]
    public void ActivityDetail_OutcomeTypeFilterChanged_ReCountsTheMatchSet()
    {
        // A chip filter changes which items match, and the stats cannot say how many match a filter, so the range
        // read must count. Reloading without it leaves the toolbar count and the scroll area sized for a match set
        // that no longer exists, and nothing on screen says so.
        var cut = RenderActivityDetail();
        WaitForGrid(cut);
        cut.WaitForAssertion(() => Assert.That(_windowRequests, Is.Not.Empty));

        InvokeOutcomeTypeFilterChanged(cut, [ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded]);

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_windowRequests.Any(r => r.IncludeTotalCount), Is.True,
                    "a chip filter change must re-count: the match set, and so the total, has changed");
                Assert.That(_windowRequests.Last().OutcomeTypeFilter,
                    Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded),
                    "the chosen outcome type must reach the range read, or the chips filter nothing");
            }
        });
    }

    [Test]
    public void ActivityDetail_RunFinishesWhileOpen_ReloadsTheItemsItHadNotShown()
    {
        // The table holds the rows it last fetched. Opened while an import was still running it was empty, and it
        // stayed empty however many items the run then saved, while the stats above it counted them.
        _status = ActivityStatus.InProgress;
        var saved = _items;
        _items = [];

        var cut = RenderActivityDetail();
        var grid = WaitForGrid(cut);
        cut.WaitForAssertion(() => Assert.That(grid.TotalItems, Is.EqualTo(0)));

        _items = saved;
        _status = ActivityStatus.Complete;
        InvokeRefreshActivity(cut);

        cut.WaitForAssertion(() => Assert.That(grid.TotalItems, Is.EqualTo(1),
            "the run finishing must reload the table, whatever the interval since the last reload"));
    }

    [Test]
    public void ActivityDetail_RunningWithNoItemsYet_SaysTheyAreStillToCome()
    {
        // "Recorded no items" reads as final; while the run is going, items are still being saved.
        _status = ActivityStatus.InProgress;
        _items = [];

        var cut = RenderActivityDetail();

        cut.WaitForAssertion(() => Assert.That(cut.FindComponent<TableEmptyState>().Instance.PrimaryText,
            Does.Contain("yet").And.Not.Contain("recorded no")));
    }

    [Test]
    public void ActivityDetail_NoExecutionItemsAndNoSearch_SaysTheActivityRecordedNone()
    {
        _items = [];

        var cut = RenderActivityDetail();

        cut.WaitForAssertion(() =>
        {
            var emptyState = cut.FindComponent<TableEmptyState>().Instance;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(emptyState.PrimaryText, Does.Contain("recorded no Run Profile Execution Items"));
                Assert.That(emptyState.ActionText, Is.Null,
                    "there is nothing to clear when the Activity simply recorded nothing");
            }
        });
    }

    [Test]
    public void ActivityDetail_SearchMatchedNothing_SaysSoAndOffersToClearTheSearch()
    {
        // The same empty table means two different things, and only the message tells them apart: a search term
        // that matched nothing has a way out, an Activity that recorded nothing does not.
        _items = [];

        var cut = RenderActivityDetail();
        var grid = WaitForGrid(cut);
        cut.WaitForAssertion(() => Assert.That(cut.HasComponent<TableEmptyState>(), Is.True));

        var searchField = cut.FindComponents<SearchField>()
            .First(field => field.Instance.Placeholder == "Search name or external ID");
        cut.InvokeAsync(() => searchField.Instance.ValueChanged.InvokeAsync("zzz")).GetAwaiter().GetResult();

        cut.WaitForAssertion(() =>
        {
            var emptyState = cut.FindComponent<TableEmptyState>().Instance;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(emptyState.PrimaryText, Does.Contain("\"zzz\""));
                Assert.That(emptyState.ActionText, Is.EqualTo("Clear Search"));
                Assert.That(grid.SearchText, Is.EqualTo("zzz"));
                Assert.That(_windowRequests.Last().SearchQuery, Is.EqualTo("zzz"),
                    "the search term must reach the range read rather than being filtered client-side");
            }
        });
    }

    [Test]
    public void ActivityDetail_RunThatClaimedChanges_SaysWhichObjectTypesItProcessedInFull()
    {
        _synchronisationChanges = [ClaimedChange(applied: true)];

        var cut = RenderActivityDetail();

        cut.WaitForAssertion(() =>
        {
            var sections = cut.FindAll("[data-testid='jim-processed-in-full-section']");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(sections, Has.Count.EqualTo(1));
                Assert.That(sections[0].TextContent, Does.Contain("12,500"));
            }
        });
    }

    [Test]
    public void ActivityDetail_RunThatClaimedNothing_ShowsNoSection()
    {
        var cut = RenderActivityDetail();
        WaitForGrid(cut);

        Assert.That(cut.FindAll("[data-testid='jim-processed-in-full-section']"), Is.Empty);
    }

    [Test]
    public void ActivityDetail_RunFinishesWhileOpen_ReadsAgainWhetherItAppliedTheChanges()
    {
        // A run applies what it claimed as it completes, so a page opened mid-run shows neither outcome and must read
        // the changes again once the run has ended.
        _status = ActivityStatus.InProgress;
        _synchronisationChanges = [ClaimedChange(applied: false)];
        var cut = RenderActivityDetail();
        cut.WaitForAssertion(() => Assert.That(cut.FindAll("[data-testid='jim-processed-in-full-section']"), Has.Count.EqualTo(1)));
        Assert.That(cut.FindAll("[data-testid='jim-processed-in-full-applied']"), Is.Empty);

        _synchronisationChanges = [ClaimedChange(applied: true)];
        _status = ActivityStatus.Complete;
        InvokeRefreshActivity(cut);

        cut.WaitForAssertion(() => Assert.That(cut.FindAll("[data-testid='jim-processed-in-full-applied']"), Has.Count.EqualTo(1)));
    }

    private static ConfigurationChangeSynchronisationSummary ClaimedChange(bool applied) => new()
    {
        Id = 1,
        ConnectedSystemId = 3,
        ConnectedSystemName = "HR Import",
        ConnectedSystemObjectTypeId = 7,
        ObjectTypeName = "user",
        Reason = "HR Inbound: Scope changed",
        RequestedAt = DateTime.UtcNow.AddHours(-1),
        ClaimedByActivityId = ActivityId,
        AppliedAt = applied ? DateTime.UtcNow : null,
        InitiatedByType = ActivityInitiatorType.System,
        InitiatedByName = "System"
    };

    private IRenderedComponent<ActivityDetail> RenderActivityDetail()
        => Render<ActivityDetail>(parameters => parameters.Add(c => c.Id, ActivityId));

    private static VirtualisedDataGrid<ActivityRunProfileExecutionItemHeader> WaitForGrid(
        IRenderedComponent<ActivityDetail> cut)
    {
        cut.WaitForAssertion(() => Assert.That(
            cut.HasComponent<VirtualisedDataGrid<ActivityRunProfileExecutionItemHeader>>(), Is.True,
            "the Run Profile Execution Items table must be the shared virtualised grid"));
        return cut.FindComponent<VirtualisedDataGrid<ActivityRunProfileExecutionItemHeader>>().Instance;
    }

    /// <summary>
    /// Raises the page's own outcome-type chip handler. The chip set is MudBlazor's, so driving it through the DOM
    /// would assert on a third party's markup; the handler is what this page owns and what must re-count.
    /// </summary>
    private static void InvokeOutcomeTypeFilterChanged(
        IRenderedComponent<ActivityDetail> cut,
        IReadOnlyCollection<ActivityRunProfileExecutionItemSyncOutcomeType> selected)
    {
        var handler = typeof(ActivityDetail).GetMethod(
            "OnOutcomeTypeFilterChanged", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(handler, Is.Not.Null, "Expected private method 'OnOutcomeTypeFilterChanged' to exist on the page.");

        cut.InvokeAsync(() => (Task)handler!.Invoke(cut.Instance, [selected])!).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Runs one tick of the page's live refresh, as a notification or the polling fallback would.
    /// </summary>
    private static void InvokeRefreshActivity(IRenderedComponent<ActivityDetail> cut)
    {
        var refresh = typeof(ActivityDetail).GetMethod("RefreshActivityAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(refresh, Is.Not.Null, "Expected private method 'RefreshActivityAsync' to exist on the page.");

        cut.InvokeAsync(() => (Task)refresh!.Invoke(cut.Instance, [])!).GetAwaiter().GetResult();
    }

    private sealed record ExecutionItemWindowRequest(
        string? SearchQuery,
        IReadOnlyCollection<ActivityRunProfileExecutionItemSyncOutcomeType>? OutcomeTypeFilter,
        bool IncludeTotalCount);

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
