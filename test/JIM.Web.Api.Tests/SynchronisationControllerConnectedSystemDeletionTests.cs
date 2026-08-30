// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Web.Controllers.Api;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Tasking;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the Connected System DELETE endpoint's deletion-mode choice (#809, Phase 3): Synchronised
/// Deprovisioning is the DEFAULT and always answers 202 Accepted with the tracking DTO; the immediate mode
/// (<c>synchronisedDeprovisioning=false</c>) keeps the 200/202 split. On a fenced system (Status = Deleting)
/// the failed-run exits apply: deprovisioning mode is the retry (re-queue, or attach to the persisted task),
/// immediate mode is finish-immediately (complete the deletion, abandoning the remaining run).
/// </summary>
[TestFixture]
public class SynchronisationControllerConnectedSystemDeletionTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<ITaskingRepository> _mockTaskingRepo = null!;
    private Mock<ILogger<SynchronisationController>> _mockLogger = null!;
    private Mock<ICredentialProtectionService> _mockCredentialProtection = null!;
    private IExpressionEvaluator _expressionEvaluator = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private Guid _generatedActivityId;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockTaskingRepo = new Mock<ITaskingRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepo.Object);

        // Simulate the database generating the Activity's id at persistence time, so the queued task's
        // Activity id is observable in the response the controller builds.
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
        var testApiKey = new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId))
            .ReturnsAsync(testApiKey);

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
    /// A Connected System mocked at the repository, with no running sync and no persisted deletion task.
    /// </summary>
    private ConnectedSystem SetUpConnectedSystem(int id = 1, ConnectedSystemStatus status = ConnectedSystemStatus.Active)
    {
        var connectedSystem = new ConnectedSystem { Id = id, Name = "HR System", Status = status };
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(id, It.IsAny<bool>()))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetRunningSyncTaskAsync(id))
            .ReturnsAsync((SynchronisationWorkerTask?)null);
        return connectedSystem;
    }

    [Test]
    public async Task DeleteConnectedSystemAsync_DefaultMode_QueuesSynchronisedDeprovisioningAndReturns202Async()
    {
        // Synchronised Deprovisioning is the DEFAULT on every surface (PRD decision): a parameterless
        // DELETE must queue the deprovisioning run and answer 202 with the tracking DTO.
        var connectedSystem = SetUpConnectedSystem();
        DeleteConnectedSystemWorkerTask? queuedTask = null;
        _mockTaskingRepo.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(t => queuedTask = t as DeleteConnectedSystemWorkerTask)
            .Returns(Task.CompletedTask);

        var result = await _controller.DeleteConnectedSystemAsync(1);

        Assert.That(result, Is.InstanceOf<AcceptedResult>(), "the default mode must always queue and answer 202");
        var accepted = (AcceptedResult)result;
        var dto = accepted.Value as ConnectedSystemDeletionResult;
        Assert.That(dto, Is.Not.Null);
        Assert.That(queuedTask, Is.Not.Null, "a deprovisioning run must queue a DeleteConnectedSystemWorkerTask");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queuedTask!.SynchronisedDeprovisioning, Is.True,
                "the default mode is Synchronised Deprovisioning, not the immediate deletion");
            Assert.That(queuedTask!.ConnectedSystemId, Is.EqualTo(1));
            Assert.That(dto!.Outcome, Is.EqualTo(DeletionOutcome.QueuedAsBackgroundJob));
            Assert.That(dto!.ActivityId, Is.EqualTo(_generatedActivityId), "the tracking DTO must carry the Activity id");
            Assert.That(dto!.WorkerTaskId, Is.EqualTo(queuedTask!.Id));
            Assert.That(connectedSystem.Status, Is.EqualTo(ConnectedSystemStatus.Deleting), "the system must be fenced at queue time");
        }
    }

    [Test]
    public async Task DeleteConnectedSystemAsync_ImmediateModeOnLargeSystem_QueuesImmediateTaskAndReturns202Async()
    {
        SetUpConnectedSystem();
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1))
            .ReturnsAsync(5000);
        DeleteConnectedSystemWorkerTask? queuedTask = null;
        _mockTaskingRepo.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(t => queuedTask = t as DeleteConnectedSystemWorkerTask)
            .Returns(Task.CompletedTask);

        var result = await _controller.DeleteConnectedSystemAsync(1, synchronisedDeprovisioning: false);

        Assert.That(result, Is.InstanceOf<AcceptedResult>());
        Assert.That(queuedTask, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queuedTask!.SynchronisedDeprovisioning, Is.False,
                "the immediate mode must keep today's queued bulk deletion, not a deprovisioning run");
            Assert.That(queuedTask!.AbandonsDeprovisioningRun, Is.False,
                "an unfenced immediate deletion abandons nothing");
        }
    }

    [Test]
    public async Task DeleteConnectedSystemAsync_DeprovisioningModeOnFencedSystem_ReQueuesRetryAndReturns202Async()
    {
        // The RETRY exit: a fenced system whose failed run left no persisted task re-queues a fresh
        // deprovisioning task (the run resumes from where the data stands).
        SetUpConnectedSystem(status: ConnectedSystemStatus.Deleting);
        _mockTaskingRepo.Setup(r => r.GetDeleteConnectedSystemWorkerTaskAsync(1))
            .ReturnsAsync((DeleteConnectedSystemWorkerTask?)null);
        DeleteConnectedSystemWorkerTask? queuedTask = null;
        _mockTaskingRepo.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(t => queuedTask = t as DeleteConnectedSystemWorkerTask)
            .Returns(Task.CompletedTask);

        var result = await _controller.DeleteConnectedSystemAsync(1);

        Assert.That(result, Is.InstanceOf<AcceptedResult>(), "the retry must re-queue and answer 202, not refuse");
        Assert.That(queuedTask, Is.Not.Null);
        Assert.That(queuedTask!.SynchronisedDeprovisioning, Is.True);
    }

    [Test]
    public async Task DeleteConnectedSystemAsync_DeprovisioningModeOnFencedSystemWithPersistedTask_Returns202WithExistingIdsAsync()
    {
        // The RETRY exit against a surviving task (worker crash recovery): attach to it, never queue a second.
        SetUpConnectedSystem(status: ConnectedSystemStatus.Deleting);
        var existingActivityId = Guid.NewGuid();
        var existingTask = new DeleteConnectedSystemWorkerTask(1, evaluateMvoDeletionRules: true, deleteChangeHistory: false, synchronisedDeprovisioning: true)
        {
            Activity = new Activity { Id = existingActivityId }
        };
        _mockTaskingRepo.Setup(r => r.GetDeleteConnectedSystemWorkerTaskAsync(1))
            .ReturnsAsync(existingTask);

        var result = await _controller.DeleteConnectedSystemAsync(1);

        Assert.That(result, Is.InstanceOf<AcceptedResult>());
        var dto = ((AcceptedResult)result).Value as ConnectedSystemDeletionResult;
        Assert.That(dto, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto!.WorkerTaskId, Is.EqualTo(existingTask.Id), "the retry must report the persisted task");
            Assert.That(dto!.ActivityId, Is.EqualTo(existingActivityId));
        }
        _mockTaskingRepo.Verify(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()), Times.Never,
            "no second task may be queued while one is persisted");
    }

    [Test]
    public async Task DeleteConnectedSystemAsync_ImmediateModeOnFencedSystem_QueuesAbandoningTaskAndReturns202Async()
    {
        // The FINISH-IMMEDIATELY exit: the immediate delete on a fenced system proceeds (rather than
        // refusing), abandoning the remaining deprovisioning work, and the queued task carries the
        // abandonment marker so the Activity records it and a failure keeps the fence.
        SetUpConnectedSystem(status: ConnectedSystemStatus.Deleting);
        _mockTaskingRepo.Setup(r => r.GetDeleteConnectedSystemWorkerTaskAsync(1))
            .ReturnsAsync((DeleteConnectedSystemWorkerTask?)null);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1))
            .ReturnsAsync(5000);
        DeleteConnectedSystemWorkerTask? queuedTask = null;
        _mockTaskingRepo.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(t => queuedTask = t as DeleteConnectedSystemWorkerTask)
            .Returns(Task.CompletedTask);

        var result = await _controller.DeleteConnectedSystemAsync(1, synchronisedDeprovisioning: false);

        Assert.That(result, Is.InstanceOf<AcceptedResult>(), "finish-immediately must proceed, not refuse");
        Assert.That(queuedTask, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queuedTask!.SynchronisedDeprovisioning, Is.False);
            Assert.That(queuedTask!.AbandonsDeprovisioningRun, Is.True,
                "the task must record that it abandons a deprovisioning run");
            Assert.That(queuedTask!.Activity?.Message, Does.Contain("abandoned"),
                "the queued Activity must record the abandonment for the audit trail");
        }
    }

    [Test]
    public async Task DeleteConnectedSystemAsync_WithNonExistentSystem_ReturnsBadRequestAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(999, It.IsAny<bool>()))
            .ReturnsAsync((ConnectedSystem?)null);

        var result = await _controller.DeleteConnectedSystemAsync(999);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }
}
