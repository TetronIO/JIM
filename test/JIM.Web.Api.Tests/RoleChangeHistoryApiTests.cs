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
using JIM.Models.Security;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the Role configuration change-history REST endpoints on <see cref="SecurityController"/>: the paged
/// history list, the single-version detail, and the compare endpoint, including the not-found cases. Roles are
/// int-keyed, exercising the int-keyed read path at the controller layer (mirroring
/// <see cref="MetaverseChangeHistoryApiTests"/>).
/// </summary>
[TestFixture]
public class RoleChangeHistoryApiTests
{
    private const int RoleId = 3;

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<ISecurityRepository> _securityRepo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private JimApplication _application = null!;
    private SecurityController _controller = null!;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTime When = new(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _securityRepo = new Mock<ISecurityRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.Security).Returns(_securityRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);
        _application = new JimApplication(_repo.Object);
        _controller = new SecurityController(new Mock<ILogger<SecurityController>>().Object, _application)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task GetRoleChangeHistoryAsync_ReturnsPaginatedItemsAsync()
    {
        var rows = new List<ConfigurationChangeActivityData>
        {
            Data(2, ActivityTargetOperationType.Update, RoleSnapJson(builtIn: true)),
            Data(1, ActivityTargetOperationType.Create, RoleSnapJson(builtIn: false))
        };
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.Role, RoleId)).ReturnsAsync(2);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.Role, RoleId, 0, It.IsAny<int>())).ReturnsAsync(rows);

        var payload = await OkPayload<PaginatedResponse<ConfigurationChangeHistoryItem>>(
            _controller.GetRoleChangeHistoryAsync(RoleId, new PaginationRequest()));

        Assert.That(payload.TotalCount, Is.EqualTo(2));
        Assert.That(payload.Items.Count(), Is.EqualTo(2));
        Assert.That(payload.Items.Last().Summary, Is.EqualTo("Created"), "v1 is the creation");
    }

    [Test]
    public async Task GetRoleChangeHistoryAsync_ReturnsEmptyWhenNoHistoryAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.Role, RoleId)).ReturnsAsync(0);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.Role, RoleId, 0, It.IsAny<int>()))
            .ReturnsAsync(new List<ConfigurationChangeActivityData>());

        var payload = await OkPayload<PaginatedResponse<ConfigurationChangeHistoryItem>>(
            _controller.GetRoleChangeHistoryAsync(RoleId, new PaginationRequest()));

        Assert.That(payload.TotalCount, Is.EqualTo(0));
        Assert.That(payload.Items, Is.Empty);
    }

    [Test]
    public async Task GetRoleChangeAsync_ReturnsDetailWithDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.Role, RoleId, 2))
            .ReturnsAsync(Data(2, ActivityTargetOperationType.Update, RoleSnapJson(builtIn: true)));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType.Role, RoleId, 2))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, RoleSnapJson(builtIn: false)));

        var detail = await OkPayload<ConfigurationChangeDetail>(_controller.GetRoleChangeAsync(RoleId, 2));

        Assert.That(detail.Version, Is.EqualTo(2));
        Assert.That(detail.Snapshot.ObjectId, Is.EqualTo(RoleId));
        Assert.That(detail.Snapshot.ObjectName, Is.EqualTo("Auditor"));
        Assert.That(detail.Diff.ModifiedCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task GetRoleChangeAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.GetRoleChangeAsync(RoleId, 99);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task CompareRoleChangesAsync_ReturnsDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.Role, RoleId, 1))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, RoleSnapJson(builtIn: false)));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.Role, RoleId, 3))
            .ReturnsAsync(Data(3, ActivityTargetOperationType.Update, RoleSnapJson(builtIn: true)));

        var diff = await OkPayload<ConfigurationDiff>(_controller.CompareRoleChangesAsync(RoleId, 1, 3));

        Assert.That(diff.OldVersion, Is.EqualTo(1));
        Assert.That(diff.NewVersion, Is.EqualTo(3));
        Assert.That(diff.ModifiedCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task CompareRoleChangesAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.CompareRoleChangesAsync(RoleId, 1, 3);

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

    private string RoleSnapJson(bool builtIn) =>
        ConfigurationSnapshotService.Serialise(_application.ConfigurationSnapshots.CreateSnapshot(
            new Role
            {
                Id = RoleId,
                Name = "Auditor",
                BuiltIn = builtIn,
                StaticMembers = new List<MetaverseObject>()
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
