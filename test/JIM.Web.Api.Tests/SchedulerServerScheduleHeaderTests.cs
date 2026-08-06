// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Scheduling;
using JIM.Models.Scheduling.DTOs;
using JIM.Models.Utility;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests that the Schedules list is served as ScheduleHeader projections (issue #1196), so the portal and the REST API
/// both receive each Schedule's most recent execution outcome without materialising Schedule entities and their steps.
/// </summary>
[TestFixture]
public class SchedulerServerScheduleHeaderTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockSchedulingRepository = new Mock<ISchedulingRepository>();
        _mockRepository.Setup(r => r.Scheduling).Returns(_mockSchedulingRepository.Object);
        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task GetScheduleHeadersAsync_ReturnsHeadersWithLastExecutionOutcomeAsync()
    {
        var header = new ScheduleHeader
        {
            Id = Guid.NewGuid(),
            Name = "Nightly Sync",
            StepCount = 6,
            LastExecutionId = Guid.NewGuid(),
            LastExecutionStatus = ScheduleExecutionStatus.Failed,
            LastExecutionCurrentStepIndex = 2,
            LastExecutionTotalSteps = 6
        };
        _mockSchedulingRepository.Setup(r => r.GetScheduleHeadersAsync(1, 20, null, null, false))
            .ReturnsAsync(new PagedResultSet<ScheduleHeader>
            {
                Results = new List<ScheduleHeader> { header },
                TotalResults = 1,
                CurrentPage = 1,
                PageSize = 20
            });

        var result = await _application.Scheduler.GetScheduleHeadersAsync(1, 20);

        Assert.That(result.Results, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Results[0].LastExecutionStatus, Is.EqualTo(ScheduleExecutionStatus.Failed));
            Assert.That(result.Results[0].LastExecutionCurrentStepIndex, Is.EqualTo(2));
            Assert.That(result.Results[0].StepCount, Is.EqualTo(6));
        }
    }

    [Test]
    public async Task GetScheduleHeadersAsync_PassesPagingSearchAndSortToRepositoryAsync()
    {
        _mockSchedulingRepository.Setup(r => r.GetScheduleHeadersAsync(3, 25, "nightly", "name", true))
            .ReturnsAsync(new PagedResultSet<ScheduleHeader>
            {
                Results = new List<ScheduleHeader>(),
                TotalResults = 0,
                CurrentPage = 3,
                PageSize = 25
            });

        await _application.Scheduler.GetScheduleHeadersAsync(3, 25, "nightly", "name", true);

        _mockSchedulingRepository.Verify(r => r.GetScheduleHeadersAsync(3, 25, "nightly", "name", true), Times.Once);
    }
}
