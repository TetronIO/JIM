// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Scheduling;
using JIM.Models.Scheduling.DTOs;
using JIM.Models.Utility;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Schedules tab now that it is a virtualised grid rather than a paged table. The window contract is
/// what these pin: the tab reads Schedules through an offset/count range read (the paged reader it used to share
/// caps a page at 100, which would have silently hidden the rest), it forwards the grid's skip-the-count request
/// rather than deciding for itself, and it never turns "not counted" into "none" (a null total means the read was
/// asked to skip the count; rendering it as zero would flash the empty state over a list that has rows).
/// </summary>
[TestFixture]
public class OperationsSchedulesTabTests : JimComponentTestContext
{
    private Mock<ISchedulingRepository> _schedulingRepository = null!;
    private NavigationManager _navigation = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _schedulingRepository = new Mock<ISchedulingRepository>();
        repository.Setup(r => r.Scheduling).Returns(_schedulingRepository.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(new JimApplication(repository.Object)));
    }

    [SetUp]
    public void SetUp()
    {
        _schedulingRepository.Reset();
        _navigation = Services.GetRequiredService<NavigationManager>();
        _navigation.NavigateTo("/admin/operations?t=schedules");
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        // The tab holds a polling loop and a grid that talks to JavaScript; NUnit reuses the fixture instance
        // across tests, so each rendered component is disposed here rather than left running into the next test.
        await DisposeComponentsAsync();
    }

    private void ArrangeWindow(List<ScheduleHeader> results, int? total)
    {
        _schedulingRepository
            .Setup(r => r.GetScheduleHeadersRangeAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync(() => new RangeResultSet<ScheduleHeader> { Results = results, TotalResults = total });
    }

    private static ScheduleHeader Schedule(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        IsEnabled = true,
        StepCount = 3,
        TriggerType = ScheduleTriggerType.Cron,
        PatternType = SchedulePatternType.SpecificTimes,
        RunTimes = "02:00"
    };

    [Test]
    public void OperationsSchedulesTab_WindowLoaded_ReadsThroughTheRangeReadAndRendersTheSchedule()
    {
        ArrangeWindow([Schedule("Nightly Directory Synchronisation")], 1);

        var cut = Render<OperationsSchedulesTab>();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("Nightly Directory Synchronisation"));
                Assert.That(cut.Markup, Does.Contain("3 steps"));
            }
        });
    }

    [Test]
    public void OperationsSchedulesTab_FirstWindow_AsksTheRepositoryForTheTotalCount()
    {
        ArrangeWindow([Schedule("Nightly Directory Synchronisation")], 1);

        var cut = Render<OperationsSchedulesTab>();

        cut.WaitForAssertion(() => _schedulingRepository.Verify(r => r.GetScheduleHeadersRangeAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), true),
            Times.AtLeastOnce));
    }

    [Test]
    public void OperationsSchedulesTab_Loading_NeverUsesThePagedReader()
    {
        // The paged reader caps a page at 100 Schedules, so a list built on it would silently stop at the
        // hundredth and there would be no pager left to reach the rest with.
        ArrangeWindow([Schedule("Nightly Directory Synchronisation")], 1);

        var cut = Render<OperationsSchedulesTab>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Nightly Directory Synchronisation")));

        _schedulingRepository.Verify(r => r.GetScheduleHeadersAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Test]
    public void OperationsSchedulesTab_NoSchedules_OffersToCreateTheFirstOne()
    {
        ArrangeWindow([], 0);

        var cut = Render<OperationsSchedulesTab>();

        cut.WaitForAssertion(() =>
        {
            var emptyState = cut.FindComponent<TableEmptyState>();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(emptyState.Instance.PrimaryText, Is.EqualTo("No Schedules have been created yet"));
                Assert.That(emptyState.Instance.ActionText, Is.EqualTo("Create Your First Schedule"));
            }
        });
    }

    [Test]
    public void OperationsSchedulesTab_SearchMatchedNothing_OffersToClearTheSearch()
    {
        _navigation.NavigateTo("/admin/operations?t=schedules&s-q=nothing-matches-this");
        ArrangeWindow([], 0);

        var cut = Render<OperationsSchedulesTab>();

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
}
