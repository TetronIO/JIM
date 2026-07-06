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
using JIM.Models.ExampleData;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the Example Data configuration change-history REST endpoints on <see cref="ExampleDataController"/>:
/// the paged history list, the single-version detail, and the compare endpoint (including the not-found cases), for
/// both Example Data Sets and Example Data Templates. Both are int-keyed, exercising the int-keyed read path at the
/// controller layer (mirroring <see cref="CertificateChangeHistoryApiTests"/> and
/// <see cref="PredefinedSearchChangeHistoryApiTests"/>).
/// </summary>
[TestFixture]
public class ExampleDataChangeHistoryApiTests
{
    private const int SetId = 4;
    private const int TemplateId = 8;

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private JimApplication _application = null!;
    private ExampleDataController _controller = null!;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTime When = new(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _application = new JimApplication(_repo.Object);
        _controller = new ExampleDataController(new Mock<ILogger<ExampleDataController>>().Object, _application)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    // -- Example Data Set --------------------------------------------------------------------------------------------

    [Test]
    public async Task GetExampleDataSetChangeHistoryAsync_ReturnsPaginatedItemsAsync()
    {
        var rows = new List<ConfigurationChangeActivityData>
        {
            Data(2, ActivityTargetOperationType.Update, ExampleDataSetSnapJson(valueCount: 3)),
            Data(1, ActivityTargetOperationType.Create, ExampleDataSetSnapJson(valueCount: 2))
        };
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.ExampleDataSet, SetId)).ReturnsAsync(2);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.ExampleDataSet, SetId, 0, It.IsAny<int>())).ReturnsAsync(rows);

        var payload = await OkPayload<PaginatedResponse<ConfigurationChangeHistoryItem>>(
            _controller.GetExampleDataSetChangeHistoryAsync(SetId, new PaginationRequest()));

        Assert.That(payload.TotalCount, Is.EqualTo(2));
        Assert.That(payload.Items.Count(), Is.EqualTo(2));
        Assert.That(payload.Items.Last().Summary, Is.EqualTo("Created"), "v1 is the creation");
    }

    [Test]
    public async Task GetExampleDataSetChangeHistoryAsync_ReturnsEmptyWhenNoHistoryAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.ExampleDataSet, SetId)).ReturnsAsync(0);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.ExampleDataSet, SetId, 0, It.IsAny<int>()))
            .ReturnsAsync(new List<ConfigurationChangeActivityData>());

        var payload = await OkPayload<PaginatedResponse<ConfigurationChangeHistoryItem>>(
            _controller.GetExampleDataSetChangeHistoryAsync(SetId, new PaginationRequest()));

        Assert.That(payload.TotalCount, Is.EqualTo(0));
        Assert.That(payload.Items, Is.Empty);
    }

    [Test]
    public async Task GetExampleDataSetChangeAsync_ReturnsDetailWithDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.ExampleDataSet, SetId, 2))
            .ReturnsAsync(Data(2, ActivityTargetOperationType.Update, ExampleDataSetSnapJson(valueCount: 3)));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType.ExampleDataSet, SetId, 2))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, ExampleDataSetSnapJson(valueCount: 2)));

        var detail = await OkPayload<ConfigurationChangeDetail>(_controller.GetExampleDataSetChangeAsync(SetId, 2));

        Assert.That(detail.Version, Is.EqualTo(2));
        Assert.That(detail.Snapshot.ObjectId, Is.EqualTo(SetId));
        Assert.That(detail.Snapshot.ObjectName, Is.EqualTo("UK Cities"));
        Assert.That(detail.Diff.ModifiedCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task GetExampleDataSetChangeAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.GetExampleDataSetChangeAsync(SetId, 99);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task CompareExampleDataSetChangesAsync_ReturnsDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.ExampleDataSet, SetId, 1))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, ExampleDataSetSnapJson(valueCount: 2)));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.ExampleDataSet, SetId, 3))
            .ReturnsAsync(Data(3, ActivityTargetOperationType.Update, ExampleDataSetSnapJson(valueCount: 5)));

        var diff = await OkPayload<ConfigurationDiff>(_controller.CompareExampleDataSetChangesAsync(SetId, 1, 3));

        Assert.That(diff.OldVersion, Is.EqualTo(1));
        Assert.That(diff.NewVersion, Is.EqualTo(3));
        Assert.That(diff.ModifiedCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task CompareExampleDataSetChangesAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.CompareExampleDataSetChangesAsync(SetId, 1, 3);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    // -- Example Data Template ---------------------------------------------------------------------------------------

    [Test]
    public async Task GetExampleDataTemplateChangeHistoryAsync_ReturnsPaginatedItemsAsync()
    {
        var rows = new List<ConfigurationChangeActivityData>
        {
            Data(2, ActivityTargetOperationType.Update, ExampleDataTemplateSnapJson("Enterprise (revised)")),
            Data(1, ActivityTargetOperationType.Create, ExampleDataTemplateSnapJson("Enterprise"))
        };
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.ExampleDataTemplate, TemplateId)).ReturnsAsync(2);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.ExampleDataTemplate, TemplateId, 0, It.IsAny<int>())).ReturnsAsync(rows);

        var payload = await OkPayload<PaginatedResponse<ConfigurationChangeHistoryItem>>(
            _controller.GetExampleDataTemplateChangeHistoryAsync(TemplateId, new PaginationRequest()));

        Assert.That(payload.TotalCount, Is.EqualTo(2));
        Assert.That(payload.Items.Count(), Is.EqualTo(2));
        Assert.That(payload.Items.Last().Summary, Is.EqualTo("Created"), "v1 is the creation");
    }

    [Test]
    public async Task GetExampleDataTemplateChangeHistoryAsync_ReturnsEmptyWhenNoHistoryAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.ExampleDataTemplate, TemplateId)).ReturnsAsync(0);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.ExampleDataTemplate, TemplateId, 0, It.IsAny<int>()))
            .ReturnsAsync(new List<ConfigurationChangeActivityData>());

        var payload = await OkPayload<PaginatedResponse<ConfigurationChangeHistoryItem>>(
            _controller.GetExampleDataTemplateChangeHistoryAsync(TemplateId, new PaginationRequest()));

        Assert.That(payload.TotalCount, Is.EqualTo(0));
        Assert.That(payload.Items, Is.Empty);
    }

    [Test]
    public async Task GetExampleDataTemplateChangeAsync_ReturnsDetailWithDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.ExampleDataTemplate, TemplateId, 2))
            .ReturnsAsync(Data(2, ActivityTargetOperationType.Update, ExampleDataTemplateSnapJson("Enterprise (revised)")));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType.ExampleDataTemplate, TemplateId, 2))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, ExampleDataTemplateSnapJson("Enterprise")));

        var detail = await OkPayload<ConfigurationChangeDetail>(_controller.GetExampleDataTemplateChangeAsync(TemplateId, 2));

        Assert.That(detail.Version, Is.EqualTo(2));
        Assert.That(detail.Snapshot.ObjectId, Is.EqualTo(TemplateId));
        Assert.That(detail.Snapshot.ObjectName, Is.EqualTo("Enterprise (revised)"));
        Assert.That(detail.Diff.ModifiedCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task GetExampleDataTemplateChangeAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.GetExampleDataTemplateChangeAsync(TemplateId, 99);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task CompareExampleDataTemplateChangesAsync_ReturnsDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.ExampleDataTemplate, TemplateId, 1))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, ExampleDataTemplateSnapJson("Enterprise")));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.ExampleDataTemplate, TemplateId, 3))
            .ReturnsAsync(Data(3, ActivityTargetOperationType.Update, ExampleDataTemplateSnapJson("Enterprise (revised)")));

        var diff = await OkPayload<ConfigurationDiff>(_controller.CompareExampleDataTemplateChangesAsync(TemplateId, 1, 3));

        Assert.That(diff.OldVersion, Is.EqualTo(1));
        Assert.That(diff.NewVersion, Is.EqualTo(3));
        Assert.That(diff.ModifiedCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task CompareExampleDataTemplateChangesAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.CompareExampleDataTemplateChangesAsync(TemplateId, 1, 3);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    // -- helpers -----------------------------------------------------------------------------------------------------

    private static async Task<T> OkPayload<T>(Task<IActionResult> action) where T : class
    {
        var result = await action;
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var payload = ((OkObjectResult)result).Value as T;
        Assert.That(payload, Is.Not.Null);
        return payload!;
    }

    private string ExampleDataSetSnapJson(int valueCount)
    {
        var dataSet = new ExampleDataSet
        {
            Id = SetId,
            Name = "UK Cities",
            Culture = "en-GB",
            Values = Enumerable.Range(0, valueCount).Select(i => new ExampleDataSetValue { StringValue = $"City {i}" }).ToList()
        };
        return ConfigurationSnapshotService.Serialise(_application.ConfigurationSnapshots.CreateSnapshot(dataSet, HashKey));
    }

    private string ExampleDataTemplateSnapJson(string name)
    {
        var template = new ExampleDataTemplate
        {
            Id = TemplateId,
            Name = name
        };
        return ConfigurationSnapshotService.Serialise(_application.ConfigurationSnapshots.CreateSnapshot(template, HashKey));
    }

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
