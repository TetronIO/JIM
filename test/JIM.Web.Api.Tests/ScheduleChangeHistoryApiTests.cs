// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Scheduling;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the Schedule configuration change-history REST endpoints on <see cref="SchedulesController"/>: the paged
/// history list, the single-version detail, and the compare endpoint, including the not-found cases. Schedules are
/// Guid-keyed, so these exercise the Guid-keyed read path end to end at the controller layer.
/// </summary>
[TestFixture]
public class ScheduleChangeHistoryApiTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private JimApplication _application = null!;
    private SchedulesController _controller = null!;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTime When = new(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid ScheduleId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _application = new JimApplication(_repo.Object);
        _controller = new SchedulesController(new Mock<ILogger<SchedulesController>>().Object, _application)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task GetScheduleChangeHistoryAsync_ReturnsPaginatedItemsAsync()
    {
        var rows = new List<ConfigurationChangeActivityData>
        {
            Data(2, ActivityTargetOperationType.Update, ScheduleSnapJson("v2")),
            Data(1, ActivityTargetOperationType.Create, ScheduleSnapJson("v1"))
        };
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.Schedule, ScheduleId)).ReturnsAsync(2);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.Schedule, ScheduleId, 0, It.IsAny<int>())).ReturnsAsync(rows);

        var payload = await OkPayload<PaginatedResponse<ConfigurationChangeHistoryItem>>(
            _controller.GetScheduleChangeHistoryAsync(ScheduleId, new PaginationRequest()));

        Assert.That(payload.TotalCount, Is.EqualTo(2));
        Assert.That(payload.Items.Count(), Is.EqualTo(2));
        Assert.That(payload.Items.First().Summary, Is.EqualTo("Description"), "v2 changed the description versus v1");
        Assert.That(payload.Items.Last().Summary, Is.EqualTo("Created"), "v1 is the creation");
    }

    [Test]
    public async Task GetScheduleChangeAsync_ReturnsDetailWithDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.Schedule, ScheduleId, 2))
            .ReturnsAsync(Data(2, ActivityTargetOperationType.Update, ScheduleSnapJson("v2")));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType.Schedule, ScheduleId, 2))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, ScheduleSnapJson("v1")));

        var detail = await OkPayload<ConfigurationChangeDetail>(_controller.GetScheduleChangeAsync(ScheduleId, 2));

        Assert.That(detail.Version, Is.EqualTo(2));
        Assert.That(detail.Snapshot.ObjectName, Is.EqualTo("Nightly Sync"));
        Assert.That(detail.Snapshot.ObjectGuidId, Is.EqualTo(ScheduleId));
        Assert.That(detail.Diff.ModifiedCount, Is.EqualTo(1));
    }

    [Test]
    public async Task GetScheduleChangeAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<Guid>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.GetScheduleChangeAsync(ScheduleId, 99);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task CompareScheduleChangesAsync_ReturnsDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.Schedule, ScheduleId, 1))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, ScheduleSnapJson("a")));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.Schedule, ScheduleId, 3))
            .ReturnsAsync(Data(3, ActivityTargetOperationType.Update, ScheduleSnapJson("c")));

        var diff = await OkPayload<ConfigurationDiff>(_controller.CompareScheduleChangesAsync(ScheduleId, 1, 3));

        Assert.That(diff.OldVersion, Is.EqualTo(1));
        Assert.That(diff.NewVersion, Is.EqualTo(3));
        Assert.That(diff.ModifiedCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CompareScheduleChangesAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<Guid>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.CompareScheduleChangesAsync(ScheduleId, 1, 3);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private static async Task<T> OkPayload<T>(Task<IActionResult> action) where T : class
    {
        var result = await action;
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var payload = ((OkObjectResult)result).Value as T;
        Assert.That(payload, Is.Not.Null);
        return payload!;
    }

    private string ScheduleSnapJson(string description) =>
        ConfigurationSnapshotService.Serialise(_application.ConfigurationSnapshots.CreateSnapshot(
            new Schedule { Id = ScheduleId, Name = "Nightly Sync", Description = description, IsEnabled = true }, HashKey));

    private static ConfigurationChangeActivityData Data(int version, ActivityTargetOperationType operation, string snapshotJson) => new()
    {
        ActivityId = Guid.NewGuid(),
        Version = version,
        Operation = operation,
        InitiatedByType = ActivityInitiatorType.User,
        InitiatedByName = "Tester",
        When = When,
        SnapshotJson = snapshotJson
    };
}
