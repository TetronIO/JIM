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
using JIM.Models.Core;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the Service Setting configuration change-history REST endpoints on
/// <see cref="ServiceSettingsController"/>: the paged history list, the single-version detail, and the compare
/// endpoint, including the not-found cases. Service Settings are string-keyed (by setting key), so these exercise
/// the string-keyed read path end to end at the controller layer.
/// </summary>
[TestFixture]
public class ServiceSettingChangeHistoryApiTests
{
    private const string SettingKey = "History.RetentionPeriod";

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private JimApplication _application = null!;
    private ServiceSettingsController _controller = null!;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTime When = new(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _application = new JimApplication(_repo.Object);
        _controller = new ServiceSettingsController(new Mock<ILogger<ServiceSettingsController>>().Object, _application)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task GetServiceSettingChangeHistoryAsync_ReturnsPaginatedItemsAsync()
    {
        var rows = new List<ConfigurationChangeActivityData>
        {
            Data(2, ActivityTargetOperationType.Update, SettingSnapJson("30.00:00:00")),
            Data(1, ActivityTargetOperationType.Create, SettingSnapJson("90.00:00:00"))
        };
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.ServiceSetting, SettingKey)).ReturnsAsync(2);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.ServiceSetting, SettingKey, 0, It.IsAny<int>())).ReturnsAsync(rows);

        var payload = await OkPayload<PaginatedResponse<ConfigurationChangeHistoryItem>>(
            _controller.GetServiceSettingChangeHistoryAsync(SettingKey, new PaginationRequest()));

        Assert.That(payload.TotalCount, Is.EqualTo(2));
        Assert.That(payload.Items.Count(), Is.EqualTo(2));
        Assert.That(payload.Items.First().Summary, Is.EqualTo("Value, Overridden"), "v2 changed the value and the override flag versus v1");
        Assert.That(payload.Items.Last().Summary, Is.EqualTo("Created"), "v1 is the creation");
    }

    [Test]
    public async Task GetServiceSettingChangeAsync_ReturnsDetailWithDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.ServiceSetting, SettingKey, 2))
            .ReturnsAsync(Data(2, ActivityTargetOperationType.Update, SettingSnapJson("30.00:00:00")));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType.ServiceSetting, SettingKey, 2))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, SettingSnapJson("90.00:00:00")));

        var detail = await OkPayload<ConfigurationChangeDetail>(_controller.GetServiceSettingChangeAsync(SettingKey, 2));

        Assert.That(detail.Version, Is.EqualTo(2));
        Assert.That(detail.Snapshot.ObjectKey, Is.EqualTo(SettingKey));
        Assert.That(detail.Snapshot.ObjectName, Is.EqualTo("History retention period"));
        Assert.That(detail.Diff.ModifiedCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task GetServiceSettingChangeAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.GetServiceSettingChangeAsync(SettingKey, 99);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task CompareServiceSettingChangesAsync_ReturnsDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.ServiceSetting, SettingKey, 1))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, SettingSnapJson("90.00:00:00")));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.ServiceSetting, SettingKey, 3))
            .ReturnsAsync(Data(3, ActivityTargetOperationType.Update, SettingSnapJson("14.00:00:00")));

        var diff = await OkPayload<ConfigurationDiff>(_controller.CompareServiceSettingChangesAsync(SettingKey, 1, 3));

        Assert.That(diff.OldVersion, Is.EqualTo(1));
        Assert.That(diff.NewVersion, Is.EqualTo(3));
        Assert.That(diff.ModifiedCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task CompareServiceSettingChangesAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.CompareServiceSettingChangesAsync(SettingKey, 1, 3);

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

    private string SettingSnapJson(string? value) =>
        ConfigurationSnapshotService.Serialise(_application.ConfigurationSnapshots.CreateSnapshot(
            new ServiceSetting
            {
                Key = SettingKey,
                DisplayName = "History retention period",
                Category = ServiceSettingCategory.Maintenance,
                ValueType = ServiceSettingValueType.TimeSpan,
                DefaultValue = "90.00:00:00",
                Value = value == "90.00:00:00" ? null : value
            }, HashKey));

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
