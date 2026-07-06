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
/// Tests for the Metaverse Object Type and Metaverse Attribute configuration change-history REST endpoints on
/// <see cref="MetaverseController"/>: the paged history list, the single-version detail, and the compare endpoint,
/// including the not-found cases. Both types are int-keyed, exercising the int-keyed read path at the controller
/// layer for the two new schema target types.
/// </summary>
[TestFixture]
public class MetaverseChangeHistoryApiTests
{
    private const int ObjectTypeId = 42;
    private const int AttributeId = 7;

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private JimApplication _application = null!;
    private MetaverseController _controller = null!;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTime When = new(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _application = new JimApplication(_repo.Object);
        _controller = new MetaverseController(new Mock<ILogger<MetaverseController>>().Object, _application)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task GetObjectTypeChangeHistoryAsync_ReturnsPaginatedItemsAsync()
    {
        var rows = new List<ConfigurationChangeActivityData>
        {
            Data(2, ActivityTargetOperationType.Update, ObjectTypeSnapJson(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected)),
            Data(1, ActivityTargetOperationType.Create, ObjectTypeSnapJson(MetaverseObjectDeletionRule.Manual))
        };
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.MetaverseObjectType, ObjectTypeId)).ReturnsAsync(2);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.MetaverseObjectType, ObjectTypeId, 0, It.IsAny<int>())).ReturnsAsync(rows);

        var payload = await OkPayload<PaginatedResponse<ConfigurationChangeHistoryItem>>(
            _controller.GetObjectTypeChangeHistoryAsync(ObjectTypeId, new PaginationRequest()));

        Assert.That(payload.TotalCount, Is.EqualTo(2));
        Assert.That(payload.Items.Count(), Is.EqualTo(2));
        Assert.That(payload.Items.First().Summary, Is.EqualTo("Deletion rule"), "v2 changed the deletion rule versus v1");
        Assert.That(payload.Items.Last().Summary, Is.EqualTo("Created"), "v1 is the creation");
    }

    [Test]
    public async Task GetObjectTypeChangeAsync_ReturnsDetailWithDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.MetaverseObjectType, ObjectTypeId, 2))
            .ReturnsAsync(Data(2, ActivityTargetOperationType.Update, ObjectTypeSnapJson(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected)));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType.MetaverseObjectType, ObjectTypeId, 2))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, ObjectTypeSnapJson(MetaverseObjectDeletionRule.Manual)));

        var detail = await OkPayload<ConfigurationChangeDetail>(_controller.GetObjectTypeChangeAsync(ObjectTypeId, 2));

        Assert.That(detail.Version, Is.EqualTo(2));
        Assert.That(detail.Snapshot.ObjectId, Is.EqualTo(ObjectTypeId));
        Assert.That(detail.Snapshot.ObjectName, Is.EqualTo("Robot"));
        Assert.That(detail.Diff.ModifiedCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task GetObjectTypeChangeAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.GetObjectTypeChangeAsync(ObjectTypeId, 99);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task CompareObjectTypeChangesAsync_ReturnsDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.MetaverseObjectType, ObjectTypeId, 1))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, ObjectTypeSnapJson(MetaverseObjectDeletionRule.Manual)));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.MetaverseObjectType, ObjectTypeId, 3))
            .ReturnsAsync(Data(3, ActivityTargetOperationType.Update, ObjectTypeSnapJson(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected)));

        var diff = await OkPayload<ConfigurationDiff>(_controller.CompareObjectTypeChangesAsync(ObjectTypeId, 1, 3));

        Assert.That(diff.OldVersion, Is.EqualTo(1));
        Assert.That(diff.NewVersion, Is.EqualTo(3));
        Assert.That(diff.ModifiedCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task GetAttributeChangeHistoryAsync_ReturnsPaginatedItemsAsync()
    {
        var rows = new List<ConfigurationChangeActivityData>
        {
            Data(2, ActivityTargetOperationType.Update, AttributeSnapJson(AttributePlurality.MultiValued)),
            Data(1, ActivityTargetOperationType.Create, AttributeSnapJson(AttributePlurality.SingleValued))
        };
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.MetaverseAttribute, AttributeId)).ReturnsAsync(2);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.MetaverseAttribute, AttributeId, 0, It.IsAny<int>())).ReturnsAsync(rows);

        var payload = await OkPayload<PaginatedResponse<ConfigurationChangeHistoryItem>>(
            _controller.GetAttributeChangeHistoryAsync(AttributeId, new PaginationRequest()));

        Assert.That(payload.TotalCount, Is.EqualTo(2));
        Assert.That(payload.Items.First().Summary, Is.EqualTo("Plurality"), "v2 changed the plurality versus v1");
    }

    [Test]
    public async Task GetAttributeChangeAsync_ReturnsDetailWithDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.MetaverseAttribute, AttributeId, 2))
            .ReturnsAsync(Data(2, ActivityTargetOperationType.Update, AttributeSnapJson(AttributePlurality.MultiValued)));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType.MetaverseAttribute, AttributeId, 2))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, AttributeSnapJson(AttributePlurality.SingleValued)));

        var detail = await OkPayload<ConfigurationChangeDetail>(_controller.GetAttributeChangeAsync(AttributeId, 2));

        Assert.That(detail.Version, Is.EqualTo(2));
        Assert.That(detail.Snapshot.ObjectId, Is.EqualTo(AttributeId));
        Assert.That(detail.Snapshot.ObjectName, Is.EqualTo("Serial Number"));
        Assert.That(detail.Diff.ModifiedCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task CompareAttributeChangesAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.CompareAttributeChangesAsync(AttributeId, 1, 3);

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

    private string ObjectTypeSnapJson(MetaverseObjectDeletionRule deletionRule) =>
        ConfigurationSnapshotService.Serialise(_application.ConfigurationSnapshots.CreateSnapshot(
            new MetaverseObjectType
            {
                Id = ObjectTypeId,
                Name = "Robot",
                PluralName = "Robots",
                DeletionRule = deletionRule,
                Attributes = new List<MetaverseAttribute>
                {
                    new() { Id = 1, Name = "Display Name", Type = AttributeDataType.Text }
                }
            }, HashKey));

    private string AttributeSnapJson(AttributePlurality plurality) =>
        ConfigurationSnapshotService.Serialise(_application.ConfigurationSnapshots.CreateSnapshot(
            new MetaverseAttribute
            {
                Id = AttributeId,
                Name = "Serial Number",
                Type = AttributeDataType.Text,
                AttributePlurality = plurality,
                MetaverseObjectTypes = new List<MetaverseObjectType>
                {
                    new() { Id = ObjectTypeId, Name = "Robot", PluralName = "Robots" }
                }
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
