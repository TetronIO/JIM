// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Preview;
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
/// The execute endpoint's half of the run link (#1530): an optional request body naming the Full Synchronisation
/// preview the caller read, carried onto the queued run and refused with a 400 when it does not describe the run.
/// The rules themselves are the tasking server's (<c>TaskingServerRunPreviewLinkTests</c>); these prove the endpoint
/// passes the id through, turns a refusal into a 400, and still queues a run posted without a body.
/// </summary>
[TestFixture]
public class SynchronisationControllerRunPreviewLinkTests
{
    private const int ConnectedSystemId = 3;
    private const int RunProfileId = 30;

    private Mock<IRepository> _repository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystems = null!;
    private Mock<IActivityRepository> _activities = null!;
    private Mock<IApiKeyRepository> _apiKeys = null!;
    private Mock<ITaskingRepository> _tasking = null!;
    private Mock<IConfigurationChangePreviewRepository> _previews = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private readonly List<Activity> _createdActivities = [];
    private readonly Dictionary<Guid, Activity> _storedActivities = [];
    private ConfigurationChangePreviewStaleness _staleness = new(null, null);
    private readonly Dictionary<Guid, ConfigurationChangePreview> _storedPreviews = [];

    [SetUp]
    public void SetUp()
    {
        _createdActivities.Clear();
        _storedActivities.Clear();
        _storedPreviews.Clear();

        _repository = new Mock<IRepository>();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        _activities = new Mock<IActivityRepository>();
        _apiKeys = new Mock<IApiKeyRepository>();
        _tasking = new Mock<ITaskingRepository>();
        _previews = new Mock<IConfigurationChangePreviewRepository>();
        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);
        _repository.Setup(r => r.Activity).Returns(_activities.Object);
        _repository.Setup(r => r.ApiKeys).Returns(_apiKeys.Object);
        _repository.Setup(r => r.Tasking).Returns(_tasking.Object);
        _repository.Setup(r => r.ConfigurationChangePreviews).Returns(_previews.Object);

        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "HR",
            Status = ConnectedSystemStatus.Active,
            SettingValues = [],
            ConnectorDefinition = new ConnectorDefinition { Name = "CSV" }
        };
        _connectedSystems.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, false)).ReturnsAsync(connectedSystem);
        _connectedSystems.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _connectedSystems.Setup(r => r.GetConnectedSystemRunProfilesAsync(ConnectedSystemId)).ReturnsAsync(
        [
            new ConnectedSystemRunProfile { Id = RunProfileId, Name = "Full Synchronisation", RunType = ConnectedSystemRunType.FullSynchronisation, ConnectedSystemId = ConnectedSystemId }
        ]);

        _activities.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                a.Id = Guid.NewGuid();
                _createdActivities.Add(a);
            })
            .Returns(Task.CompletedTask);
        // Nothing has happened since the preview started, so the cited preview is current (#2022), unless a test says otherwise.
        _staleness = new ConfigurationChangePreviewStaleness(null, null);
        _activities.Setup(r => r.GetPreviewStalenessSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(() => _staleness);
        _activities.Setup(r => r.GetActivityAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _storedActivities.GetValueOrDefault(id));
        _previews.Setup(r => r.GetPreviewAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _storedPreviews.GetValueOrDefault(id));
        _tasking.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>())).Returns(Task.CompletedTask);

        _application = new JimApplication(_repository.Object);
        _controller = new SynchronisationController(new Mock<ILogger<SynchronisationController>>().Object, _application,
            new DynamicExpressoEvaluator(), new Mock<ICredentialProtectionService>().Object);
        AuthenticateWithApiKey();
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task ExecuteRunProfileAsync_CitingACompletedFullSynchronisationPreview_RecordsItOnTheRunActivityAsync()
    {
        var previewActivityId = StorePreview(ConnectedSystemId);

        var result = await _controller.ExecuteRunProfileAsync(ConnectedSystemId, RunProfileId,
            new ExecuteRunProfileRequest { PreviewActivityId = previewActivityId });

        Assert.That(result, Is.InstanceOf<AcceptedResult>());
        Assert.That(_createdActivities.Single().PreviewActivityId, Is.EqualTo(previewActivityId));
    }

    [Test]
    public async Task ExecuteRunProfileAsync_CitingAnOutOfDatePreview_QueuesTheRunAndWarnsAsync()
    {
        // A script citing a preview that something has since overtaken still gets its run, and is told (#2022).
        var previewActivityId = StorePreview(ConnectedSystemId);
        _staleness = new ConfigurationChangePreviewStaleness(new PreviewOvertakingActivity(Guid.NewGuid(), DateTime.UtcNow,
            ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetOperationType.Execute, "Delta Import", "HR Import"), null);

        var result = await _controller.ExecuteRunProfileAsync(ConnectedSystemId, RunProfileId,
            new ExecuteRunProfileRequest { PreviewActivityId = previewActivityId });

        Assert.That(result, Is.InstanceOf<AcceptedResult>());
        var response = (RunProfileExecutionResponse)((AcceptedResult)result).Value!;
        Assert.That(response.Warnings, Has.Some.Contain("Delta Import"));
    }

    [Test]
    public async Task ExecuteRunProfileAsync_CitingAnotherSystemsPreview_Returns400Async()
    {
        var previewActivityId = StorePreview(connectedSystemId: ConnectedSystemId + 1);

        var result = await _controller.ExecuteRunProfileAsync(ConnectedSystemId, RunProfileId,
            new ExecuteRunProfileRequest { PreviewActivityId = previewActivityId });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var error = ((BadRequestObjectResult)result).Value as ApiErrorResponse;
        Assert.That(error?.Message, Does.Contain("not a Full Synchronisation preview of this Connected System"));
        _tasking.Verify(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()), Times.Never);
    }

    [Test]
    public async Task ExecuteRunProfileAsync_WithoutABody_QueuesTheRunWithNoLinkAsync()
    {
        // Every caller before #1530 posts no body; that must go on queuing the run exactly as before.
        var result = await _controller.ExecuteRunProfileAsync(ConnectedSystemId, RunProfileId);

        Assert.That(result, Is.InstanceOf<AcceptedResult>());
        Assert.That(_createdActivities.Single().PreviewActivityId, Is.Null);
    }

    private Guid StorePreview(int connectedSystemId)
    {
        var activityId = Guid.NewGuid();
        _storedActivities[activityId] = new Activity
        {
            Id = activityId,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Preview,
            ConnectedSystemId = connectedSystemId
        };
        _storedPreviews[activityId] = new ConfigurationChangePreview
        {
            ActivityId = activityId,
            Surface = ConfigurationChangePreviewSurface.ConnectedSystemFullSynchronisation,
            ValidationStatus = ConfigurationChangePreviewStageStatus.Complete,
            ImpactCountsStatus = ConfigurationChangePreviewStageStatus.Complete,
            SummaryStatus = ConfigurationChangePreviewStageStatus.Complete,
            DeltasStatus = ConfigurationChangePreviewStageStatus.Complete
        };
        return activityId;
    }

    private void AuthenticateWithApiKey()
    {
        var apiKeyId = Guid.NewGuid();
        _apiKeys.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new ApiKey
        {
            Id = apiKeyId,
            Name = "Automation",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        });
        var identity = new ClaimsIdentity(new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new(ClaimTypes.Name, "Automation")
        }, "ApiKey");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }
}
