// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the Clear Connector Space API endpoint on SynchronisationController. The endpoint queues a
/// <see cref="ClearConnectedSystemObjectsWorkerTask"/> (the same task the portal queues) and returns 202
/// Accepted with tracking ids, exactly like <c>ExecuteRunProfileAsync</c>, instead of running the deletion
/// inline with no Activity.
/// </summary>
[TestFixture]
public class SynchronisationControllerClearTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<ITaskingRepository> _mockTaskingRepo = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepo = null!;
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
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockTaskingRepo = new Mock<ITaskingRepository>();
        _mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepo.Object);
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepo.Object);

        // Simulate the database generating the Activity's id at persistence time, so the queued task's
        // Activity id is observable in the response the controller builds (mirrors
        // SynchronisationControllerConnectedSystemDeletionTests, which established this pattern for the
        // sibling queued-deletion endpoint).
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
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    private ConnectedSystem SetUpConnectedSystem(int id = 1, string name = "Test System")
    {
        var connectedSystem = new ConnectedSystem { Id = id, Name = name, Status = ConnectedSystemStatus.Active };
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(id, It.IsAny<bool>()))
            .ReturnsAsync(connectedSystem);
        return connectedSystem;
    }

    /// <summary>
    /// Authenticates the controller as an interactive user, wiring up the SSO claim-to-Metaverse-Object
    /// resolution that <c>GetCurrentUserAsync</c> depends on.
    /// </summary>
    private MetaverseObject SetUpUserAuthentication(Guid userId, string userName)
    {
        var ssoAttribute = new MetaverseAttribute { Id = 1, Name = "SsoId" };
        _mockServiceSettingsRepo.Setup(r => r.GetServiceSettingsAsync()).ReturnsAsync(new ServiceSettings
        {
            SSOUniqueIdentifierClaimType = "sub",
            SSOUniqueIdentifierMetaverseAttribute = ssoAttribute
        });

        var userType = new MetaverseObjectType { Id = 1, Name = "User" };
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(It.IsAny<string>(), false, It.IsAny<bool>()))
            .ReturnsAsync(userType);

        var user = new MetaverseObject { Id = userId, Type = userType };
        // NameOrId falls back to Id.ToString() when Name is unset; the test only needs it to round-trip.
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectByTypeAndAttributeAsync(userType, ssoAttribute, It.IsAny<string>()))
            .ReturnsAsync(user);

        var claims = new List<Claim>
        {
            new Claim("sub", userId.ToString()),
            new Claim(ClaimTypes.Name, userName)
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return user;
    }

    private ApiKey SetUpApiKeyAuthentication(Guid apiKeyId, string apiKeyName)
    {
        var testApiKey = new ApiKey
        {
            Id = apiKeyId,
            Name = apiKeyName,
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(testApiKey);

        var claims = new List<Claim>
        {
            new Claim("auth_method", "api_key"),
            new Claim(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new Claim(ClaimTypes.Name, apiKeyName)
        };
        var identity = new ClaimsIdentity(claims, "ApiKey");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return testApiKey;
    }

    [Test]
    public async Task ClearConnectorSpace_UserAuthenticated_QueuesTaskAndReturns202Async()
    {
        var connectedSystem = SetUpConnectedSystem();
        var userId = Guid.NewGuid();
        SetUpUserAuthentication(userId, "Alice Admin");

        ClearConnectedSystemObjectsWorkerTask? queuedTask = null;
        _mockTaskingRepo.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(t => queuedTask = t as ClearConnectedSystemObjectsWorkerTask)
            .Returns(Task.CompletedTask);

        var result = await _controller.ClearConnectorSpaceAsync(connectedSystem.Id, deleteChangeHistory: true);

        Assert.That(result, Is.InstanceOf<AcceptedResult>());
        var accepted = (AcceptedResult)result;
        var dto = accepted.Value as ConnectorSpaceClearResponse;
        Assert.That(dto, Is.Not.Null);
        Assert.That(queuedTask, Is.Not.Null, "a Clear Connector Space request must queue a ClearConnectedSystemObjectsWorkerTask");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queuedTask!.ConnectedSystemId, Is.EqualTo(connectedSystem.Id));
            Assert.That(queuedTask.DeleteChangeHistory, Is.True);
            Assert.That(queuedTask.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
            Assert.That(queuedTask.InitiatedById, Is.EqualTo(userId));
            Assert.That(dto!.ActivityId, Is.EqualTo(_generatedActivityId));
            Assert.That(dto.TaskId, Is.EqualTo(queuedTask.Id));
            Assert.That(dto.Message, Does.Contain(connectedSystem.Name));
        }

        // The old synchronous inline path must never run: the clear happens only when the worker
        // processes the queued task.
        _mockConnectedSystemRepo.Verify(
            r => r.DeleteAllConnectedSystemObjectsAndDependenciesAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Test]
    public async Task ClearConnectorSpace_ApiKeyAuthenticated_QueuesTaskAndReturns202Async()
    {
        var connectedSystem = SetUpConnectedSystem();
        var apiKeyId = Guid.NewGuid();
        SetUpApiKeyAuthentication(apiKeyId, "TestApiKey");

        ClearConnectedSystemObjectsWorkerTask? queuedTask = null;
        _mockTaskingRepo.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(t => queuedTask = t as ClearConnectedSystemObjectsWorkerTask)
            .Returns(Task.CompletedTask);

        var result = await _controller.ClearConnectorSpaceAsync(connectedSystem.Id, deleteChangeHistory: false);

        Assert.That(result, Is.InstanceOf<AcceptedResult>());
        var dto = ((AcceptedResult)result).Value as ConnectorSpaceClearResponse;
        Assert.That(dto, Is.Not.Null);
        Assert.That(queuedTask, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queuedTask!.ConnectedSystemId, Is.EqualTo(connectedSystem.Id));
            Assert.That(queuedTask.DeleteChangeHistory, Is.False);
            Assert.That(queuedTask.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
            Assert.That(queuedTask.InitiatedById, Is.EqualTo(apiKeyId));
            Assert.That(dto!.ActivityId, Is.EqualTo(_generatedActivityId));
            Assert.That(dto.TaskId, Is.EqualTo(queuedTask.Id));
        }

        _mockConnectedSystemRepo.Verify(
            r => r.DeleteAllConnectedSystemObjectsAndDependenciesAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Test]
    public async Task ClearConnectorSpace_UnknownSystem_Returns404Async()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(999, It.IsAny<bool>()))
            .ReturnsAsync((ConnectedSystem?)null);
        SetUpApiKeyAuthentication(Guid.NewGuid(), "TestApiKey");

        var result = await _controller.ClearConnectorSpaceAsync(999);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockTaskingRepo.Verify(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()), Times.Never);
    }

    /// <summary>
    /// TaskingServer.CreateWorkerTaskAsync fences a Deleting Connected System against a queued Clear the same
    /// way it already fences a Run Profile execution (#809): a clear racing a Synchronised Deprovisioning run
    /// must not proceed. This is what gives <c>!result.Success</c> a genuine trigger for a Clear task; the
    /// controller must turn that refusal into a 400, exactly like <c>ExecuteRunProfileAsync</c> does.
    /// </summary>
    [Test]
    public async Task ClearConnectorSpace_QueueRefused_Returns400Async()
    {
        var connectedSystem = SetUpConnectedSystem();
        connectedSystem.Status = ConnectedSystemStatus.Deleting;
        SetUpApiKeyAuthentication(Guid.NewGuid(), "TestApiKey");

        var result = await _controller.ClearConnectorSpaceAsync(connectedSystem.Id);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockTaskingRepo.Verify(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()), Times.Never,
            "a refused task must never reach persistence");
    }
}
