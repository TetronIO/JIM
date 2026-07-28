// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.ExampleData;
using JIM.Models.Security;
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
/// Tests for ExampleDataController's execute-template endpoint (issue #1112 follow-up): execution must be
/// queued as a worker task with a tracking Activity, mirroring the Run Profile execute endpoint, rather than
/// run synchronously inside the HTTP request with no Activity (where failures were silent).
/// </summary>
[TestFixture]
public class ExampleDataControllerExecuteTemplateTests
{
    private const int TemplateId = 1;

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IExampleDataRepository> _mockExampleDataRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<ITaskingRepository> _mockTaskingRepo = null!;
    private Mock<ILogger<ExampleDataController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private ExampleDataController _controller = null!;
    private Guid _apiKeyId;
    private WorkerTask? _queuedWorkerTask;
    private Activity? _createdActivity;

    [SetUp]
    public void SetUp()
    {
        _queuedWorkerTask = null;
        _createdActivity = null;

        _mockRepository = new Mock<IRepository>();
        _mockExampleDataRepo = new Mock<IExampleDataRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockTaskingRepo = new Mock<ITaskingRepository>();
        _mockRepository.Setup(r => r.ExampleData).Returns(_mockExampleDataRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepo.Object);

        _mockExampleDataRepo
            .Setup(r => r.GetTemplateAsync(TemplateId))
            .ReturnsAsync(new ExampleDataTemplate { Id = TemplateId, Name = "Test Template" });

        _mockActivityRepo
            .Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(activity =>
            {
                // The database assigns Activity.Id on insert; simulate that so the response can carry it.
                activity.Id = Guid.NewGuid();
                _createdActivity = activity;
            })
            .Returns(Task.CompletedTask);

        _mockTaskingRepo
            .Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(workerTask => _queuedWorkerTask = workerTask)
            .Returns(Task.CompletedTask);

        _mockLogger = new Mock<ILogger<ExampleDataController>>();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new ExampleDataController(_mockLogger.Object, _application);

        // Authenticate as an API key so the base controller resolves a non-null principal for worker task
        // attribution. Mirrors ExampleDataControllerExampleDataSetCrudTests.
        _apiKeyId = Guid.NewGuid();
        var apiKey = new ApiKey
        {
            Id = _apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(_apiKeyId)).ReturnsAsync(apiKey);

        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, _apiKeyId.ToString()),
            new(ClaimTypes.Name, "TestApiKey")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    [TearDown]
    public void TearDown()
    {
        _application.Dispose();
    }

    [Test]
    public async Task ExecuteTemplateAsync_TemplateExists_QueuesWorkerTaskWithApiKeyAttributionAsync()
    {
        var result = await _controller.ExecuteTemplateAsync(TemplateId);

        Assert.That(result, Is.InstanceOf<AcceptedResult>());
        var response = (ExampleDataTemplateExecutionResponse)((AcceptedResult)result).Value!;

        Assert.That(_queuedWorkerTask, Is.InstanceOf<ExampleDataTemplateWorkerTask>());
        var queuedTask = (ExampleDataTemplateWorkerTask)_queuedWorkerTask!;
        Assert.That(queuedTask.TemplateId, Is.EqualTo(TemplateId));
        Assert.That(queuedTask.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
        Assert.That(queuedTask.InitiatedById, Is.EqualTo(_apiKeyId));
        Assert.That(queuedTask.InitiatedByName, Is.EqualTo("TestApiKey"));

        Assert.That(response.TaskId, Is.EqualTo(queuedTask.Id));
        Assert.That(response.ActivityId, Is.EqualTo(queuedTask.Activity!.Id));
        Assert.That(response.ActivityId, Is.Not.EqualTo(Guid.Empty));
        Assert.That(response.Message, Does.Contain("Test Template"));
    }

    [Test]
    public async Task ExecuteTemplateAsync_TemplateExists_RecordsDataGenerationActivityAsync()
    {
        await _controller.ExecuteTemplateAsync(TemplateId);

        Assert.That(_createdActivity, Is.Not.Null);
        Assert.That(_createdActivity!.TargetType, Is.EqualTo(ActivityTargetType.DataGeneration));
        Assert.That(_createdActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Execute));
        Assert.That(_createdActivity!.ExampleDataTemplateId, Is.EqualTo(TemplateId));
        Assert.That(_createdActivity!.TargetName, Is.EqualTo("Test Template"));
        Assert.That(_createdActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
    }

    [Test]
    public async Task ExecuteTemplateAsync_TemplateExists_DoesNotExecuteGenerationInlineAsync()
    {
        await _controller.ExecuteTemplateAsync(TemplateId);

        // Generation must happen in the worker, not inside the HTTP request: the endpoint must never
        // reach the persistence stage of template execution.
        _mockExampleDataRepo.Verify(
            r => r.CreateMetaverseObjectsAsync(
                It.IsAny<List<JIM.Models.Core.MetaverseObject>>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Func<JIM.Models.ExampleData.DTOs.PersistenceProgress, Task>?>()),
            Times.Never);
    }

    [Test]
    public async Task ExecuteTemplateAsync_TemplateMissing_ReturnsNotFoundAndQueuesNothingAsync()
    {
        _mockExampleDataRepo.Setup(r => r.GetTemplateAsync(999)).ReturnsAsync((ExampleDataTemplate?)null);

        var result = await _controller.ExecuteTemplateAsync(999);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockTaskingRepo.Verify(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()), Times.Never);
    }
}
