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

        _activityRepository.Setup(r => r.GetActivityAsync(ActivityId)).ReturnsAsync(new Activity
        {
            Id = ActivityId,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Execute,
            TargetName = "Full Import",
            ConnectedSystemRunType = ConnectedSystemRunType.FullImport,
            Status = ActivityStatus.Complete,
            InitiatedByType = ActivityInitiatorType.System
        });

        _activityRepository.Setup(r => r.GetActivityRunProfileExecutionStatsAsync(ActivityId))
            .ReturnsAsync(new ActivityRunProfileExecutionStats
            {
                ActivityId = ActivityId,
                TotalCsoAdds = 1,
                TotalObjectTypes = 1,
                ObjectTypeCounts = new Dictionary<string, int> { { "user", 1 } }
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

        var repository = new Mock<IRepository>();
        repository.Setup(r => r.Activity).Returns(_activityRepository.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
    }

    [Test]
    public void ActivityDetail_ExecutionItems_CountsTheMatchSetOnceRatherThanOnEveryWindow()
    {
        // Counting is a full scan of the match set, so the grid asks for it on the first window and the page must
        // pass that through rather than counting unconditionally. A reload that changes nothing about which items
        // match must not re-count: at customer scale that is one scan per scroll window.
        var cut = RenderActivityDetail();
        var grid = WaitForGrid(cut);

        cut.WaitForAssertion(() => Assert.That(_windowRequests.Any(r => r.IncludeTotalCount), Is.True,
            "the first window must ask for the total; nothing else knows how big the list is"));
        var countedBefore = _windowRequests.Count(r => r.IncludeTotalCount);

        cut.InvokeAsync(() => grid.RefreshAsync()).GetAwaiter().GetResult();

        cut.WaitForAssertion(() => Assert.That(_windowRequests.Count, Is.GreaterThan(0)));
        Assert.That(_windowRequests.Count(r => r.IncludeTotalCount), Is.EqualTo(countedBefore),
            "a reload that leaves the match set alone must not pay for the count again");
    }

    [Test]
    public void ActivityDetail_OutcomeTypeFilterChanged_ReCountsTheMatchSet()
    {
        // A chip filter changes which items match, so the total the toolbar count states and the scroll area is
        // sized from describes a set that no longer exists. Reloading without invalidating it leaves both wrong,
        // and nothing on screen says so.
        var cut = RenderActivityDetail();
        WaitForGrid(cut);

        cut.WaitForAssertion(() => Assert.That(_windowRequests.Any(r => r.IncludeTotalCount), Is.True));
        var countedBefore = _windowRequests.Count(r => r.IncludeTotalCount);

        InvokeOutcomeTypeFilterChanged(cut, [ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded]);

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_windowRequests.Count(r => r.IncludeTotalCount), Is.GreaterThan(countedBefore),
                    "a chip filter change must re-count: the match set, and so the total, has changed");
                Assert.That(_windowRequests.Last().OutcomeTypeFilter,
                    Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded),
                    "the chosen outcome type must reach the range read, or the chips filter nothing");
            }
        });
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

    private sealed record ExecutionItemWindowRequest(
        string? SearchQuery,
        IReadOnlyCollection<ActivityRunProfileExecutionItemSyncOutcomeType>? OutcomeTypeFilter,
        bool IncludeTotalCount);

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
