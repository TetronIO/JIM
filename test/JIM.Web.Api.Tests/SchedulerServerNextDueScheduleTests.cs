// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Scheduling.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests that the Scheduler server exposes the next due Schedule to the portal (#2549), so the Operations page can say
/// when work will next join the queue without the portal reaching past the application layer.
/// </summary>
[TestFixture]
public class SchedulerServerNextDueScheduleTests
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
    public async Task GetNextDueScheduleAsync_ReturnsTheRepositorysAnswerAsync()
    {
        var next = new NextDueSchedule { Id = Guid.NewGuid(), Name = "Weekly Full Reconciliation", NextRunTime = DateTime.UtcNow.AddDays(3) };
        _mockSchedulingRepository.Setup(r => r.GetNextDueScheduleAsync()).ReturnsAsync(next);

        var result = await _application.Scheduler.GetNextDueScheduleAsync();

        Assert.That(result, Is.SameAs(next));
    }

    [Test]
    public async Task GetNextDueScheduleAsync_NoneDue_ReturnsNullAsync()
    {
        _mockSchedulingRepository.Setup(r => r.GetNextDueScheduleAsync()).ReturnsAsync((NextDueSchedule?)null);

        var result = await _application.Scheduler.GetNextDueScheduleAsync();

        Assert.That(result, Is.Null);
    }
}
