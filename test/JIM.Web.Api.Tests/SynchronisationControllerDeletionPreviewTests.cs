// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST surface for previewing a Connected System's deletion and for recording the preview behind the deletion
/// (#134). The portal and PowerShell record a preview the same way, so the audit trail answers "did they look first?"
/// whichever surface the deletion came from; the one rule the boundary owns is that a deletion may only cite a preview
/// of deleting that same system.
/// </summary>
[TestFixture]
public class SynchronisationControllerDeletionPreviewTests
{
    private const int ConnectedSystemId = 3;
    private const int OtherConnectedSystemId = 4;

    private Mock<IRepository> _repository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IApiKeyRepository> _apiKeyRepo = null!;
    private Mock<IConfigurationChangePreviewRepository> _previewRepo = null!;
    private Mock<ITaskingRepository> _taskingRepo = null!;
    private Mock<ISyncRepository> _syncRepo = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private List<WorkerTask> _queuedWorkerTasks = null!;
    private readonly Dictionary<Guid, Activity> _activities = [];
    private readonly Dictionary<Guid, ConfigurationChangePreview> _previews = [];

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _apiKeyRepo = new Mock<IApiKeyRepository>();
        _previewRepo = new Mock<IConfigurationChangePreviewRepository>();
        _taskingRepo = new Mock<ITaskingRepository>();
        _syncRepo = new Mock<ISyncRepository>();

        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repository.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repository.Setup(r => r.ApiKeys).Returns(_apiKeyRepo.Object);
        _repository.Setup(r => r.ConfigurationChangePreviews).Returns(_previewRepo.Object);
        _repository.Setup(r => r.Tasking).Returns(_taskingRepo.Object);
        _repository.Setup(r => r.ServiceSettings).Returns(new Mock<IServiceSettingsRepository>().Object);

        _queuedWorkerTasks = [];
        _activities.Clear();
        _previews.Clear();

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                if (a.Id == Guid.Empty)
                    a.Id = Guid.NewGuid();
                _activities[a.Id] = a;
            })
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        // Nothing has happened since the preview started, so the cited preview is current (#2022).
        _activityRepo.Setup(r => r.GetPreviewStalenessSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(new ConfigurationChangePreviewStaleness(null, null));
        _activityRepo.Setup(r => r.GetActivityAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _activities.GetValueOrDefault(id));
        _previewRepo.Setup(r => r.CreatePreviewAsync(It.IsAny<ConfigurationChangePreview>()))
            .Callback<ConfigurationChangePreview>(p => _previews[p.ActivityId] = p)
            .Returns(Task.CompletedTask);
        _previewRepo.Setup(r => r.UpdatePreviewAsync(It.IsAny<ConfigurationChangePreview>())).Returns(Task.CompletedTask);
        _previewRepo.Setup(r => r.GetPreviewAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _previews.GetValueOrDefault(id));
        _taskingRepo.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(t => _queuedWorkerTasks.Add(t))
            .Returns(Task.CompletedTask);

        _connectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(() => new ConnectedSystem { Id = ConnectedSystemId, Name = "Old HR System", Status = ConnectedSystemStatus.Active });
        _syncRepo.Setup(r => r.GetAllSyncRulesAsync(It.IsAny<bool>())).ReturnsAsync(new List<SyncRule>());
        _syncRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(ConnectedSystemId)).ReturnsAsync(12_847);

        // The deletion preview reads what the deprovisioning run reads, through the sync repository.
        _application = new JimApplication(_repository.Object, syncRepository: _syncRepo.Object);
        _controller = new SynchronisationController(new Mock<ILogger<SynchronisationController>>().Object, _application,
            new DynamicExpressoEvaluator(), new Mock<ICredentialProtectionService>().Object);

        var apiKeyId = Guid.NewGuid();
        _apiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        });

        var identity = new ClaimsIdentity(
        [
            new Claim("auth_method", "api_key"),
            new Claim(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new Claim(ClaimTypes.Name, "TestApiKey")
        ], "ApiKey");

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    // -----------------------------------------------------------------------------------------------------------------
    // Starting the preview
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task StartDeletionPreview_UnknownConnectedSystem_ReturnsNotFoundAsync()
    {
        var result = await _controller.StartConnectedSystemDeletionPreviewAsync(99, null);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task StartDeletionPreview_StartsADeletionPreviewOfThatSystemAsync()
    {
        var result = await _controller.StartConnectedSystemDeletionPreviewAsync(ConnectedSystemId, null);

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var queued = _queuedWorkerTasks.OfType<ConfigurationChangePreviewWorkerTask>().Single();
        var activity = _activities.Values.Single(a => a.TargetOperationType == ActivityTargetOperationType.Preview);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queued.Surface, Is.EqualTo(ConfigurationChangePreviewSurface.ConnectedSystemDeletion));
            Assert.That(queued.TargetId, Is.EqualTo(ConnectedSystemId));
            Assert.That(activity.ConnectedSystemId, Is.EqualTo(ConnectedSystemId),
                "the preview is found from the system it previewed, which is how a deletion is checked against it");
            Assert.That(activity.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
        }
    }

    [Test]
    public async Task StartDeletionPreview_EveryRowRequested_IsHonouredAsync()
    {
        await _controller.StartConnectedSystemDeletionPreviewAsync(ConnectedSystemId,
            new StartConnectedSystemDeletionPreviewRequest { DeltaPersistence = ConfigurationChangePreviewDeltaPersistence.Full });

        Assert.That(_previews.Values.Single().RequestedDeltaPersistence, Is.EqualTo(ConfigurationChangePreviewDeltaPersistence.Full));
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Citing it on the deletion
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task DeleteConnectedSystem_CitingADeletionPreviewOfThatSystem_RecordsItAsync()
    {
        var previewActivityId = SeedPreview(ConfigurationChangePreviewSurface.ConnectedSystemDeletion, ConnectedSystemId);

        var result = await _controller.DeleteConnectedSystemAsync(ConnectedSystemId, previewActivityId: previewActivityId);

        Assert.That(result, Is.InstanceOf<AcceptedResult>());
        Assert.That(_queuedWorkerTasks.OfType<DeleteConnectedSystemWorkerTask>().Single().PreviewActivityId, Is.EqualTo(previewActivityId));
    }

    [Test]
    public async Task DeleteConnectedSystem_CitingAnotherSystemsPreview_IsRefusedAndDeletesNothingAsync()
    {
        var previewActivityId = SeedPreview(ConfigurationChangePreviewSurface.ConnectedSystemDeletion, OtherConnectedSystemId);

        var result = await _controller.DeleteConnectedSystemAsync(ConnectedSystemId, previewActivityId: previewActivityId);

        AssertRefused(result);
    }

    [Test]
    public async Task DeleteConnectedSystem_CitingAPreviewOfADifferentChange_IsRefusedAndDeletesNothingAsync()
    {
        // A schema selection preview of the same system answers a different question entirely.
        var previewActivityId = SeedPreview(ConfigurationChangePreviewSurface.ConnectedSystemSchema, ConnectedSystemId);

        var result = await _controller.DeleteConnectedSystemAsync(ConnectedSystemId, previewActivityId: previewActivityId);

        AssertRefused(result);
    }

    [Test]
    public async Task DeleteConnectedSystem_CitingNoSuchPreview_IsRefusedAndDeletesNothingAsync()
    {
        var result = await _controller.DeleteConnectedSystemAsync(ConnectedSystemId, previewActivityId: Guid.NewGuid());

        AssertRefused(result);
    }

    private void AssertRefused(IActionResult result)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(_queuedWorkerTasks, Is.Empty, "a refused citation must not let the deletion through");
            _connectedSystemRepo.Verify(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()), Times.Never,
                "nor fence the system");
        }
    }

    private Guid SeedPreview(ConfigurationChangePreviewSurface surface, int connectedSystemId)
    {
        var activity = new Activity
        {
            Id = Guid.CreateVersion7(),
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Preview,
            ConnectedSystemId = connectedSystemId
        };
        _activities[activity.Id] = activity;
        _previews[activity.Id] = new ConfigurationChangePreview { ActivityId = activity.Id, Surface = surface };
        return activity.Id;
    }
}
