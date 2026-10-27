// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests the typical duration the Operations page uses to estimate when queued work will start (#2549): the median of
/// each Run Profile's most recent successful runs, so one unusually slow or fast run does not skew the estimate.
/// </summary>
[TestFixture]
public class ActivityServerRecentRunDurationTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);
        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task GetMedianRecentRunDurationsAsync_OddCount_ReturnsMiddleValueAsync()
    {
        SetupDurations(1, 50, 10, 30, 20, 40);

        var result = await _application.Activities.GetMedianRecentRunDurationsAsync([1]);

        Assert.That(result[1], Is.EqualTo(TimeSpan.FromMinutes(30)));
    }

    [Test]
    public async Task GetMedianRecentRunDurationsAsync_EvenCount_ReturnsMeanOfMiddleTwoAsync()
    {
        SetupDurations(1, 10, 40, 20, 30);

        var result = await _application.Activities.GetMedianRecentRunDurationsAsync([1]);

        Assert.That(result[1], Is.EqualTo(TimeSpan.FromMinutes(25)));
    }

    [Test]
    public async Task GetMedianRecentRunDurationsAsync_OutlierRun_DoesNotSkewTheEstimateAsync()
    {
        SetupDurations(1, 10, 11, 600, 12, 9);

        var result = await _application.Activities.GetMedianRecentRunDurationsAsync([1]);

        Assert.That(result[1], Is.EqualTo(TimeSpan.FromMinutes(11)));
    }

    [Test]
    public async Task GetMedianRecentRunDurationsAsync_SingleRun_ReturnsThatRunAsync()
    {
        SetupDurations(1, 7);

        var result = await _application.Activities.GetMedianRecentRunDurationsAsync([1]);

        Assert.That(result[1], Is.EqualTo(TimeSpan.FromMinutes(7)));
    }

    [Test]
    public async Task GetMedianRecentRunDurationsAsync_AsksForFiveRunsPerRunProfileByDefaultAsync()
    {
        _mockActivityRepository
            .Setup(r => r.GetRecentSuccessfulRunProfileDurationsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<int>()))
            .ReturnsAsync(new Dictionary<int, List<TimeSpan>>());

        await _application.Activities.GetMedianRecentRunDurationsAsync([1, 2]);

        _mockActivityRepository.Verify(r => r.GetRecentSuccessfulRunProfileDurationsAsync(
            It.Is<IReadOnlyCollection<int>>(ids => ids.OrderBy(i => i).SequenceEqual(new[] { 1, 2 })), 5), Times.Once);
    }

    [Test]
    public async Task GetMedianRecentRunDurationsAsync_RunProfileWithoutHistory_IsAbsentAsync()
    {
        SetupDurations(1, 10);

        var result = await _application.Activities.GetMedianRecentRunDurationsAsync([1, 2]);

        Assert.That(result.ContainsKey(2), Is.False);
    }

    private void SetupDurations(int runProfileId, params int[] minutes)
    {
        _mockActivityRepository
            .Setup(r => r.GetRecentSuccessfulRunProfileDurationsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<int>()))
            .ReturnsAsync(new Dictionary<int, List<TimeSpan>>
            {
                [runProfileId] = minutes.Select(m => TimeSpan.FromMinutes(m)).ToList()
            });
    }
}
