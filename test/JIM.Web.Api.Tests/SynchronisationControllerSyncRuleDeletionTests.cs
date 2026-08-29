// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Tasking;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the Synchronisation Rule DELETE endpoint's recall-or-keep choice (#1537): a delete that queues a
/// value recall returns 202 Accepted with a tracking DTO carrying the recall Activity id and the affected
/// counts; keep, or a rule with no contributed values, deletes synchronously and returns 204 exactly as before.
/// </summary>
[TestFixture]
public class SynchronisationControllerSyncRuleDeletionTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<ITaskingRepository> _mockTaskingRepo = null!;
    private Mock<ILogger<SynchronisationController>> _mockLogger = null!;
    private Mock<ICredentialProtectionService> _mockCredentialProtection = null!;
    private IExpressionEvaluator _expressionEvaluator = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private JIM.Models.Security.ApiKey _testApiKey = null!;
    private Guid _generatedActivityId;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockTaskingRepo = new Mock<ITaskingRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepo.Object);

        // Simulate the database generating the Activity's id at persistence time, so the queued recall
        // task's Activity id is observable in the response the controller builds.
        _generatedActivityId = Guid.NewGuid();
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                if (a.Id == Guid.Empty)
                    a.Id = _generatedActivityId;
            })
            .Returns(Task.CompletedTask);

        _mockLogger = new Mock<ILogger<SynchronisationController>>();
        _mockCredentialProtection = new Mock<ICredentialProtectionService>();
        _expressionEvaluator = new DynamicExpressoEvaluator();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new SynchronisationController(_mockLogger.Object, _application, _expressionEvaluator, _mockCredentialProtection.Object);

        // Create a test API key and authenticate the controller with it.
        var apiKeyId = Guid.NewGuid();
        _testApiKey = new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId))
            .ReturnsAsync(_testApiKey);

        var claims = new List<Claim>
        {
            new Claim("auth_method", "api_key"),
            new Claim(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new Claim(ClaimTypes.Name, "TestApiKey")
        };
        var identity = new ClaimsIdentity(claims, "ApiKey");
        var principal = new ClaimsPrincipal(identity);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    /// <summary>
    /// A rule with contributed values, mocked at the repository the summary count queries run against.
    /// </summary>
    private SyncRule SetUpRuleWithContributedValues(int syncRuleId = 1, int valueCount = 3, int objectCount = 2)
    {
        var syncRule = new SyncRule { Id = syncRuleId, Name = "HR Import Rule", ConnectedSystemId = 7 };
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(syncRuleId))
            .ReturnsAsync(syncRule);
        _mockMetaverseRepo.Setup(r => r.GetContributedValuesSummaryAsync(syncRuleId, null))
            .ReturnsAsync(new ContributedValuesSummary
            {
                Attributes =
                [
                    new ContributedValuesAttributeSummary
                    {
                        AttributeId = 5,
                        AttributeName = "displayName",
                        ValueCount = valueCount,
                        ObjectCount = objectCount
                    }
                ],
                TotalObjects = objectCount
            });
        return syncRule;
    }

    [Test]
    public async Task DeleteSyncRuleAsync_RecallWithContributedValues_ReturnsAcceptedWithTrackingDtoAsync()
    {
        SetUpRuleWithContributedValues();

        var result = await _controller.DeleteSyncRuleAsync(1);

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var accepted = (AcceptedAtRouteResult)result;
        var dto = accepted.Value as SyncRuleDeletionQueuedResponse;
        Assert.That(dto, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted.RouteName, Is.EqualTo("GetActivity"),
                "the 202 must point at the recall Activity so callers can monitor it");
            Assert.That(accepted.RouteValues?["id"], Is.EqualTo(_generatedActivityId));
            Assert.That(dto!.RecallActivityId, Is.EqualTo(_generatedActivityId));
            Assert.That(dto!.AffectedValueCount, Is.EqualTo(3));
            Assert.That(dto!.AffectedObjectCount, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task DeleteSyncRuleAsync_RecallWithContributedValues_DisablesRuleAndQueuesTaskAsync()
    {
        var syncRule = SetUpRuleWithContributedValues();

        DeleteSyncRuleWorkerTask? queuedTask = null;
        _mockTaskingRepo.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(t => queuedTask = t as DeleteSyncRuleWorkerTask)
            .Returns(Task.CompletedTask);

        await _controller.DeleteSyncRuleAsync(1, changeReason: "decommissioning HR");

        Assert.That(queuedTask, Is.Not.Null, "a recall must queue a DeleteSyncRuleWorkerTask");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queuedTask!.SyncRuleId, Is.EqualTo(1));
            Assert.That(queuedTask!.RecallContributedValues, Is.True);
            Assert.That(syncRule.Enabled, Is.False, "the rule must be disabled the moment the recall queues");
            Assert.That(syncRule.DisabledReason,
                Is.EqualTo("Deletion in progress: contributed attribute values are being recalled."));
        }
        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleAsync(syncRule), Times.Once);
        _mockConnectedSystemRepo.Verify(r => r.DeleteSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never,
            "the rule must NOT be deleted synchronously when a recall queues");
    }

    [Test]
    public async Task DeleteSyncRuleAsync_KeepContributedValues_ReturnsNoContentAndDeletesSynchronouslyAsync()
    {
        var syncRule = SetUpRuleWithContributedValues();

        var result = await _controller.DeleteSyncRuleAsync(1, keepContributedValues: true);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        _mockConnectedSystemRepo.Verify(r => r.DeleteSyncRuleAsync(syncRule), Times.Once,
            "keep deletes the rule synchronously, exactly as today");
        _mockTaskingRepo.Verify(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()), Times.Never,
            "keep must queue nothing");
    }

    [Test]
    public async Task DeleteSyncRuleAsync_RecallWithNoContributedValues_ReturnsNoContentAsync()
    {
        var syncRule = new SyncRule { Id = 1, Name = "HR Import Rule", ConnectedSystemId = 7 };
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(1))
            .ReturnsAsync(syncRule);
        _mockMetaverseRepo.Setup(r => r.GetContributedValuesSummaryAsync(1, null))
            .ReturnsAsync(new ContributedValuesSummary());

        var result = await _controller.DeleteSyncRuleAsync(1);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        _mockConnectedSystemRepo.Verify(r => r.DeleteSyncRuleAsync(syncRule), Times.Once);
        _mockTaskingRepo.Verify(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()), Times.Never,
            "a rule contributing nothing has nothing to recall, so nothing queues");
    }

    [Test]
    public async Task DeleteSyncRuleAsync_WithNonExistentSyncRule_ReturnsNotFoundAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(999))
            .ReturnsAsync((SyncRule?)null);

        var result = await _controller.DeleteSyncRuleAsync(999);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }
}
