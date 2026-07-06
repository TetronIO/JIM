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
/// Tests for the API Key configuration change-history REST endpoints on <see cref="ApiKeysController"/>: the paged
/// history list, the single-version detail, and the compare endpoint, including the not-found cases. API Keys are
/// Guid-keyed, exercising the Guid-keyed read path at the controller layer (mirroring
/// <see cref="CertificateChangeHistoryApiTests"/>). Also covers that a mutation's optional ChangeReason (supplied in
/// the request body for Create/Update, and as a query parameter for Delete) reaches the recorded audit Activity.
/// </summary>
[TestFixture]
public class ApiKeyChangeHistoryApiTests
{
    private static readonly Guid ApiKeyId = Guid.Parse("9a2b4c6d-1e3f-4a5b-8c7d-2e4f6a8b0c1d");

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IApiKeyRepository> _apiKeyRepo = null!;
    private Mock<ISecurityRepository> _securityRepo = null!;
    private JimApplication _application = null!;
    private ApiKeysController _controller = null!;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTime When = new(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _apiKeyRepo = new Mock<IApiKeyRepository>();
        _securityRepo = new Mock<ISecurityRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ApiKeys).Returns(_apiKeyRepo.Object);
        _repo.Setup(r => r.Security).Returns(_securityRepo.Object);
        _application = new JimApplication(_repo.Object);
        _controller = new ApiKeysController(new Mock<ILogger<ApiKeysController>>().Object, _application)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task GetApiKeyChangeHistoryAsync_ReturnsPaginatedItemsAsync()
    {
        var rows = new List<ConfigurationChangeActivityData>
        {
            Data(2, ActivityTargetOperationType.Update, ApiKeySnapJson(isEnabled: false)),
            Data(1, ActivityTargetOperationType.Create, ApiKeySnapJson(isEnabled: true))
        };
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.ApiKey, ApiKeyId)).ReturnsAsync(2);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.ApiKey, ApiKeyId, 0, It.IsAny<int>())).ReturnsAsync(rows);

        var payload = await OkPayload<PaginatedResponse<ConfigurationChangeHistoryItem>>(
            _controller.GetApiKeyChangeHistoryAsync(ApiKeyId, new PaginationRequest()));

        Assert.That(payload.TotalCount, Is.EqualTo(2));
        Assert.That(payload.Items.Count(), Is.EqualTo(2));
        Assert.That(payload.Items.First().Summary, Is.EqualTo("Enabled"), "v2 changed the enabled flag versus v1");
        Assert.That(payload.Items.Last().Summary, Is.EqualTo("Created"), "v1 is the creation");
    }

    [Test]
    public async Task GetApiKeyChangeHistoryAsync_ReturnsEmptyWhenNoHistoryAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.ApiKey, ApiKeyId)).ReturnsAsync(0);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.ApiKey, ApiKeyId, 0, It.IsAny<int>()))
            .ReturnsAsync(new List<ConfigurationChangeActivityData>());

        var payload = await OkPayload<PaginatedResponse<ConfigurationChangeHistoryItem>>(
            _controller.GetApiKeyChangeHistoryAsync(ApiKeyId, new PaginationRequest()));

        Assert.That(payload.TotalCount, Is.EqualTo(0));
        Assert.That(payload.Items, Is.Empty);
    }

    [Test]
    public async Task GetApiKeyChangeAsync_ReturnsDetailWithDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.ApiKey, ApiKeyId, 2))
            .ReturnsAsync(Data(2, ActivityTargetOperationType.Update, ApiKeySnapJson(isEnabled: false)));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType.ApiKey, ApiKeyId, 2))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, ApiKeySnapJson(isEnabled: true)));

        var detail = await OkPayload<ConfigurationChangeDetail>(_controller.GetApiKeyChangeAsync(ApiKeyId, 2));

        Assert.That(detail.Version, Is.EqualTo(2));
        Assert.That(detail.Snapshot.ObjectGuidId, Is.EqualTo(ApiKeyId));
        Assert.That(detail.Snapshot.ObjectName, Is.EqualTo("CI/CD Pipeline Key"));
        Assert.That(detail.Diff.ModifiedCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task GetApiKeyChangeAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<Guid>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.GetApiKeyChangeAsync(ApiKeyId, 99);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task CompareApiKeyChangesAsync_ReturnsDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.ApiKey, ApiKeyId, 1))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, ApiKeySnapJson(isEnabled: true)));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.ApiKey, ApiKeyId, 3))
            .ReturnsAsync(Data(3, ActivityTargetOperationType.Update, ApiKeySnapJson(isEnabled: false)));

        var diff = await OkPayload<ConfigurationDiff>(_controller.CompareApiKeyChangesAsync(ApiKeyId, 1, 3));

        Assert.That(diff.OldVersion, Is.EqualTo(1));
        Assert.That(diff.NewVersion, Is.EqualTo(3));
        Assert.That(diff.ModifiedCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task CompareApiKeyChangesAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<Guid>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.CompareApiKeyChangesAsync(ApiKeyId, 1, 3);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task CreateAsync_WithChangeReason_RecordsReasonOnAuditActivityAsync()
    {
        var request = new ApiKeyCreateRequestDto
        {
            Name = "New Key",
            RoleIds = new List<int>(),
            ChangeReason = "Provisioning key for the nightly export pipeline (CHG0123)"
        };

        Activity? capturedActivity = null;
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);
        _apiKeyRepo.Setup(r => r.CreateAsync(It.IsAny<ApiKey>()))
            .ReturnsAsync((ApiKey key) => key);

        var result = await _controller.CreateAsync(request);

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.ChangeReason, Is.EqualTo("Provisioning key for the nightly export pipeline (CHG0123)"));
        Assert.That(capturedActivity.TargetType, Is.EqualTo(ActivityTargetType.ApiKey));
        Assert.That(capturedActivity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
    }

    [Test]
    public async Task DeleteAsync_WithChangeReasonQueryParameter_RecordsReasonOnAuditActivityAsync()
    {
        var id = Guid.NewGuid();
        var existingKey = new ApiKey { Id = id, Name = "Old Key", KeyPrefix = "jim_ak_test", Roles = new List<Role>() };

        Activity? capturedActivity = null;
        _apiKeyRepo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(existingKey);
        _apiKeyRepo.Setup(r => r.DeleteAsync(id)).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);

        var result = await _controller.DeleteAsync(id, "No longer required (CHG0124)");

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.ChangeReason, Is.EqualTo("No longer required (CHG0124)"));
        Assert.That(capturedActivity.TargetType, Is.EqualTo(ActivityTargetType.ApiKey));
        Assert.That(capturedActivity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Delete));
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

    private string ApiKeySnapJson(bool isEnabled) =>
        ConfigurationSnapshotService.Serialise(_application.ConfigurationSnapshots.CreateSnapshot(
            new ApiKey
            {
                Id = ApiKeyId,
                Name = "CI/CD Pipeline Key",
                Description = "Used by the nightly export pipeline",
                KeyPrefix = "jim_ak_test",
                IsEnabled = isEnabled,
                Roles = new List<Role>()
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
