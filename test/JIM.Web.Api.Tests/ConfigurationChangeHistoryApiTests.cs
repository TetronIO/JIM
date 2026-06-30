// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the configuration change-history REST endpoints on <see cref="SynchronisationController"/>: the paged
/// history list, the single-version detail, and the compare endpoint, for both Connected Systems and Synchronisation
/// Rules, including the not-found cases.
/// </summary>
[TestFixture]
public class ConfigurationChangeHistoryApiTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTime When = new(2026, 6, 26, 12, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _application = new JimApplication(_repo.Object);
        _controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            _application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task GetConnectedSystemChangeHistoryAsync_ReturnsPaginatedItemsAsync()
    {
        var rows = new List<ConfigurationChangeActivityData>
        {
            Data(2, ActivityTargetOperationType.Update, CsSnapJson("v2")),
            Data(1, ActivityTargetOperationType.Create, CsSnapJson("v1"))
        };
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.ConnectedSystem, 9)).ReturnsAsync(2);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.ConnectedSystem, 9, 0, It.IsAny<int>())).ReturnsAsync(rows);

        var payload = await OkPayload<PaginatedResponse<ConfigurationChangeHistoryItem>>(
            _controller.GetConnectedSystemChangeHistoryAsync(9, new PaginationRequest()));

        Assert.That(payload.TotalCount, Is.EqualTo(2));
        Assert.That(payload.Items.Count(), Is.EqualTo(2));
        Assert.That(payload.Items.First().Summary, Is.EqualTo("Description"), "v2 changed the description versus v1");
    }

    [Test]
    public async Task GetConnectedSystemChangeAsync_ReturnsDetailWithDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.ConnectedSystem, 9, 2))
            .ReturnsAsync(Data(2, ActivityTargetOperationType.Update, CsSnapJson("v2")));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType.ConnectedSystem, 9, 2))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, CsSnapJson("v1")));

        var detail = await OkPayload<ConfigurationChangeDetail>(_controller.GetConnectedSystemChangeAsync(9, 2));

        Assert.That(detail.Version, Is.EqualTo(2));
        Assert.That(detail.Snapshot.ObjectName, Is.EqualTo("AD"));
        Assert.That(detail.Diff.ModifiedCount, Is.EqualTo(1));
    }

    [Test]
    public async Task GetConnectedSystemChangeAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.GetConnectedSystemChangeAsync(9, 99);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task CompareConnectedSystemChangesAsync_ReturnsDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.ConnectedSystem, 9, 1))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, CsSnapJson("a")));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.ConnectedSystem, 9, 3))
            .ReturnsAsync(Data(3, ActivityTargetOperationType.Update, CsSnapJson("c")));

        var diff = await OkPayload<ConfigurationDiff>(_controller.CompareConnectedSystemChangesAsync(9, 1, 3));

        Assert.That(diff.OldVersion, Is.EqualTo(1));
        Assert.That(diff.NewVersion, Is.EqualTo(3));
        Assert.That(diff.ModifiedCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CompareConnectedSystemChangesAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.CompareConnectedSystemChangesAsync(9, 1, 3);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetSyncRuleChangeHistoryAsync_ReturnsPaginatedItemsAsync()
    {
        var rows = new List<ConfigurationChangeActivityData> { Data(1, ActivityTargetOperationType.Create, SyncRuleSnapJson("HR Inbound")) };
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.SyncRule, 55)).ReturnsAsync(1);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.SyncRule, 55, 0, It.IsAny<int>())).ReturnsAsync(rows);

        var payload = await OkPayload<PaginatedResponse<ConfigurationChangeHistoryItem>>(
            _controller.GetSyncRuleChangeHistoryAsync(55, new PaginationRequest()));

        Assert.That(payload.TotalCount, Is.EqualTo(1));
        Assert.That(payload.Items.Single().Summary, Is.EqualTo("Created"));
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

    private string CsSnapJson(string description) =>
        ConfigurationSnapshotService.Serialise(_application.ConfigurationSnapshots.CreateSnapshot(
            new ConnectedSystem { Id = 9, Name = "AD", ConnectorDefinitionId = 4, Description = description }, HashKey));

    private string SyncRuleSnapJson(string name) =>
        ConfigurationSnapshotService.Serialise(_application.ConfigurationSnapshots.CreateSnapshot(
            new SyncRule { Id = 55, Name = name, Direction = SyncRuleDirection.Export }, HashKey));

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
