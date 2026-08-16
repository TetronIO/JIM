// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Security;
using JIM.Models.Utility;
using JIM.Web.Models;
using JIM.Web.Pages.Admin;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the API Key's usage history now that it is a <see cref="VirtualisedDataGrid{TItem}"/> rather than a
/// server-paged table. Three things are worth pinning: that the window read stays scoped to this API Key (a lost
/// filter would show every Activity in the system on a page claiming they are this key's), that the grid's
/// skip-the-count contract reaches the application layer rather than being counted on every scroll, and that the
/// two empty states say different things, because "your search matched nothing" and "this key has never been
/// used" have different ways out.
/// </summary>
[TestFixture]
public class ApiKeyDetailTests : JimComponentTestContext
{
    private static readonly Guid ApiKeyId = Guid.NewGuid();

    private Mock<IApiKeyRepository> _apiKeys = null!;
    private Mock<IActivityRepository> _activities = null!;
    private NavigationManager _navigation = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _apiKeys = new Mock<IApiKeyRepository>();
        _activities = new Mock<IActivityRepository>();
        repository.Setup(r => r.ApiKeys).Returns(_apiKeys.Object);
        repository.Setup(r => r.Activity).Returns(_activities.Object);

        _apiKeys.Setup(r => r.GetByIdAsync(ApiKeyId)).ReturnsAsync(new ApiKey
        {
            Id = ApiKeyId,
            Name = "Provisioning automation",
            KeyPrefix = "jim_abc",
            IsEnabled = true
        });

        _activities
            .Setup(r => r.GetConfigurationChangeActivitiesAsync(
                It.IsAny<ActivityTargetType>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync([]);

        SetupActivities([]);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
    }

    [SetUp]
    public void SetUp()
    {
        _navigation = Services.GetRequiredService<NavigationManager>();
    }

    /// <summary>
    /// Serves the given Activities as the repository's range read does, honouring the skip-the-count contract so
    /// a test can assert on what the grid was handed rather than on the call it happened to make.
    /// </summary>
    private void SetupActivities(List<Activity> activities)
    {
        _activities
            .Setup(r => r.GetActivitiesRangeAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<IEnumerable<ActivityTargetOperationType>?>(),
                It.IsAny<IEnumerable<ActivityOutcomeType>?>(), It.IsAny<IEnumerable<ActivityTargetType>?>(),
                It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>(),
                It.IsAny<IEnumerable<ActivityInitiatorType>?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<IEnumerable<string>?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(),
                It.IsAny<bool?>(), It.IsAny<IEnumerable<Guid>?>(), It.IsAny<bool>()))
            // InvocationFunc rather than a typed lambda: the range read takes twenty parameters, past the arity
            // Moq's generic Returns overloads go up to, and only three of them matter here.
            .Returns(new InvocationFunc(invocation =>
            {
                var startIndex = (int)invocation.Arguments[0]!;
                var count = (int)invocation.Arguments[1]!;
                var includeTotalCount = (bool)invocation.Arguments[19]!;

                return Task.FromResult(new RangeResultSet<Activity>
                {
                    Results = activities.GetRange(
                        Math.Min(startIndex, activities.Count),
                        Math.Min(count, Math.Max(0, activities.Count - startIndex))),
                    TotalResults = includeTotalCount ? activities.Count : null
                });
            }));
    }

    private static List<Activity> BuildActivities(int count)
    {
        var activities = new List<Activity>(count);
        for (var i = 0; i < count; i++)
        {
            activities.Add(new Activity
            {
                Id = Guid.NewGuid(),
                TargetType = ActivityTargetType.ConnectedSystem,
                TargetName = $"target-{i:D3}",
                TargetOperationType = ActivityTargetOperationType.Update,
                Status = ActivityStatus.Complete,
                Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i)
            });
        }

        return activities;
    }

    /// <summary>
    /// Renders the page with the Activity tab open. The tab is selected through the URL rather than by clicking,
    /// because the tabs sync themselves with <c>?t=</c> and a deep link is how the tab is usually reached.
    /// </summary>
    private IRenderedComponent<ApiKeyDetail> RenderActivityTab(string extraQuery = "")
    {
        _navigation.NavigateTo($"/admin/apikeys/{ApiKeyId}?t=activity{extraQuery}");
        return Render<ApiKeyDetail>(p => p.Add(c => c.Id, ApiKeyId));
    }

    private static VirtualisedDataGrid<Activity> Grid(IRenderedComponent<ApiKeyDetail> cut) =>
        cut.FindComponent<VirtualisedDataGrid<Activity>>().Instance;

    [Test]
    public void ApiKeyActivity_IsAVirtualisedGridWithNoPager()
    {
        SetupActivities(BuildActivities(3));

        var cut = RenderActivityTab();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.HasComponent<VirtualisedDataGrid<Activity>>(), Is.True,
                    "the usage history must be shown in the shared virtualised grid, not a hand-rolled table");
                Assert.That(cut.HasComponent<MudBlazor.MudTablePager>(), Is.False,
                    "a virtualised list has no page size to choose and no page controls");
            }
        });
    }

    [Test]
    public async Task ApiKeyActivity_Window_IsScopedToThisApiKeysOwnActivitiesAsync()
    {
        SetupActivities(BuildActivities(3));
        var cut = RenderActivityTab();
        cut.WaitForAssertion(() => Assert.That(cut.HasComponent<VirtualisedDataGrid<Activity>>(), Is.True));

        await Grid(cut).LoadWindow(
            new VirtualisedWindowRequest(0, 25, null, "created", true, IncludeTotalCount: true),
            CancellationToken.None);

        _activities.Verify(r => r.GetActivitiesRangeAsync(
            0, 25, null, "created", true, ApiKeyId,
            It.IsAny<IEnumerable<ActivityTargetOperationType>?>(), It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
            It.IsAny<IEnumerable<ActivityTargetType>?>(), It.IsAny<IEnumerable<ActivityStatus>?>(),
            It.IsAny<bool?>(), It.IsAny<IEnumerable<ActivityInitiatorType>?>(), It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<IEnumerable<string>?>(),
            It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<IEnumerable<Guid>?>(), true), Times.Once,
            "the window must stay filtered to the API Key whose page this is");
    }

    [Test]
    public async Task ApiKeyActivity_WindowRequestSkippingTheCount_ReturnsANullTotalRatherThanZeroAsync()
    {
        SetupActivities(BuildActivities(3));
        var cut = RenderActivityTab();
        cut.WaitForAssertion(() => Assert.That(cut.HasComponent<VirtualisedDataGrid<Activity>>(), Is.True));

        var window = await Grid(cut).LoadWindow(
            new VirtualisedWindowRequest(0, 2, null, "created", true, IncludeTotalCount: false),
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.TotalItems, Is.Null,
                "null is \"not counted\"; a zero here would read as an API Key that has never been used");
            Assert.That(window.Items, Has.Count.EqualTo(2), "the window itself is unaffected by skipping the count");
        }
    }

    [Test]
    public void ApiKeyActivity_WithNoActivities_SaysWhatWouldPutRowsThere()
    {
        SetupActivities([]);

        var cut = RenderActivityTab();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("No Activities have been recorded for this API Key"));
                Assert.That(cut.Markup, Does.Contain("used to call the REST API"),
                    "an empty list must say what would populate it");
                Assert.That(cut.Markup, Does.Not.Contain("Clear Search"),
                    "there is no search to clear, so the button would be a dead affordance");
            }
        });
    }

    [Test]
    public void ApiKeyActivity_WithASearchThatMatchedNothing_OffersToClearIt()
    {
        SetupActivities([]);

        var cut = RenderActivityTab("&q=nothing-matches-this");

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("No Activities match \"nothing-matches-this\""));
                Assert.That(cut.Markup, Does.Contain("Clear Search"),
                    "a search that matched nothing has a way out, and the empty state must offer it");
            }
        });
    }

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
