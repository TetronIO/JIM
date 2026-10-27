// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Scheduling;
using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;
using JIM.Models.Utility;
using JIM.Web.Models;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Operations page's Activity tab (#2549) as a whole: which of its three sections are shown, read from the
/// URL so a filtered view can be bookmarked, and the filters that only mean something for finished work being switched
/// off while Completed is hidden.
/// </summary>
[TestFixture]
public class OperationsActivityTabTests : JimComponentTestContext
{
    private Mock<ITaskingRepository> _taskingRepository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private NavigationManager _navigation = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _taskingRepository = new Mock<ITaskingRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        repository.Setup(r => r.Tasking).Returns(_taskingRepository.Object);
        repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        repository.Setup(r => r.Metaverse).Returns(new Mock<IMetaverseRepository>().Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(new JimApplication(repository.Object)));
        Services.AddSingleton<IUiNotificationService>(new Mock<IUiNotificationService>().Object);
        Services.AddSingleton<IWebHostEnvironment>(new FakeWebHostEnvironment());
        Services.AddSingleton(new Mock<IActivityEtaTracker>().Object);
    }

    [SetUp]
    public void SetUp()
    {
        _taskingRepository.Reset();
        _activityRepository.Reset();
        _taskingRepository.Setup(r => r.GetWorkerTaskHeadersAsync()).ReturnsAsync(() => new List<WorkerTaskHeader>());
        _activityRepository.Setup(r => r.GetWorkerTaskActivityFilterOptionsAsync()).ReturnsAsync(new ActivityFilterOptions());
        _activityRepository
            .Setup(r => r.GetScheduleStepOutcomesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync(new Dictionary<Guid, List<ScheduleStepObservation>>());
        _activityRepository
            .Setup(r => r.GetRecentSuccessfulRunProfileDurationsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<int>()))
            .ReturnsAsync(new Dictionary<int, List<TimeSpan>>());
        _activityRepository
            .Setup(r => r.GetActivitiesRangeAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<IEnumerable<ActivityTargetOperationType>?>(),
                It.IsAny<IEnumerable<ActivityOutcomeType>?>(), It.IsAny<IEnumerable<ActivityTargetType>?>(),
                It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>(),
                It.IsAny<IEnumerable<ActivityInitiatorType>?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<IEnumerable<string>?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(),
                It.IsAny<bool?>(), It.IsAny<IEnumerable<Guid>?>(), It.IsAny<bool>()))
            .ReturnsAsync(() => new RangeResultSet<Activity> { Results = [], TotalResults = 0 });
        _navigation = Services.GetRequiredService<NavigationManager>();
    }

    [Test]
    public void OperationsActivityTab_NoSectionsInTheUrl_ShowsAllThreeInTimeOrder()
    {
        _navigation.NavigateTo("/admin/operations?t=activity");

        var cut = Render<OperationsActivityTab>();

        cut.WaitForAssertion(() =>
        {
            var sections = cut.FindAll("section[data-testid]").Select(s => s.GetAttribute("data-testid")).ToList();
            Assert.That(sections, Is.EqualTo(new[] { "activity-queued", "activity-in-progress", "activity-completed" }),
                "later at the top: queued work, then what is running now, then what has finished");
        });
    }

    [Test]
    public void OperationsActivityTab_SectionsInTheUrl_ShowsOnlyThose()
    {
        _navigation.NavigateTo("/admin/operations?t=activity&s=running");

        var cut = Render<OperationsActivityTab>();

        cut.WaitForAssertion(() =>
        {
            var sections = cut.FindAll("section[data-testid]").Select(s => s.GetAttribute("data-testid")).ToList();
            Assert.That(sections, Is.EqualTo(new[] { "activity-in-progress" }));
        });
    }

    [Test]
    public void OperationsActivityTab_NothingChosen_SaysHowToGetSomethingBack()
    {
        _navigation.NavigateTo("/admin/operations?t=activity&s=");

        var cut = Render<OperationsActivityTab>();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.FindAll("section[data-testid]"), Is.Empty);
                Assert.That(cut.Markup, Does.Contain("Choose Queued, In Progress or Completed"));
            }
        });
    }

    [Test]
    public void OperationsActivityTab_CompletedHidden_DisablesTheFiltersOnlyCompletedUses()
    {
        _navigation.NavigateTo("/admin/operations?t=activity&s=queued,running");

        var cut = Render<OperationsActivityTab>();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.FindComponent<MudSelect<ActivityStatus>>().Instance.Disabled, Is.True, "Result means nothing for unfinished work");
                Assert.That(cut.FindComponents<MudCheckBox<bool>>().Single(c => c.Instance.Label == "Has child activities").Instance.Disabled, Is.True);
                Assert.That(cut.FindComponents<MudCheckBox<bool>>().Single(c => c.Instance.Label == "Scheduled only").Instance.Disabled, Is.False,
                    "Scheduled only applies to queued and running work too");
            }
        });
    }

    [Test]
    public void OperationsActivityTab_FiltersInTheUrl_AppliesThemOnArrival()
    {
        _navigation.NavigateTo("/admin/operations?t=activity&scheduled=1&result=Cancelled&by=Infrastructure");

        var cut = Render<OperationsActivityTab>();

        cut.WaitForAssertion(() =>
        {
            var applied = cut.FindComponent<OperationsQueueSections>().Instance.Filters;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied.ScheduledOnly, Is.True);
                Assert.That(applied.Results, Is.EqualTo(new[] { ActivityStatus.Cancelled }));
                Assert.That(applied.InitiatedBy, Is.EqualTo("Infrastructure"));
            }
        });
    }

    [Test]
    public async Task OperationsActivityTab_FilterChanged_WritesTheFiltersToTheUrlAsync()
    {
        _navigation.NavigateTo("/admin/operations?t=activity");
        var cut = Render<OperationsActivityTab>();
        var scheduledOnly = cut.FindComponents<MudCheckBox<bool>>().Single(c => c.Instance.Label == "Scheduled only");

        await cut.InvokeAsync(() => scheduledOnly.Instance.ValueChanged.InvokeAsync(true));

        // Only the filters' own parameters are named, so the Completed list's search and scroll position survive.
        // (The list resets its own scroll position after a filter change, and writes that separately.)
        var update = JSInterop.Invocations
            .Where(i => i.Identifier == "jimInterop.updateQueryString")
            .Select(i => (IReadOnlyDictionary<string, string[]?>)i.Arguments[0]!)
            .Last(p => p.ContainsKey("scheduled"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(update.Keys, Is.EquivalentTo(OperationsActivityFilters.QueryParameterNames));
            Assert.That(update["scheduled"], Is.EqualTo(new[] { "1" }));
        }
    }

    [Test]
    public async Task OperationsActivityTab_SectionToggled_WritesOnlyTheSectionsParameterAsync()
    {
        _navigation.NavigateTo("/admin/operations?t=activity");
        var cut = Render<OperationsActivityTab>();
        var chips = cut.FindComponent<MudChipSet<ActivitySection>>();

        await cut.InvokeAsync(() => chips.Instance.SelectedValuesChanged.InvokeAsync([ActivitySection.Queued]));

        var update = (IReadOnlyDictionary<string, string[]?>)JSInterop.Invocations
            .Last(i => i.Identifier == "jimInterop.updateQueryString").Arguments[0]!;
        Assert.That(update, Is.EqualTo(new Dictionary<string, string[]?> { ["s"] = ["queued"] }));
    }

    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }

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
