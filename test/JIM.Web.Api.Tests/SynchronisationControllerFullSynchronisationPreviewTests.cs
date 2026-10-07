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
using System.Text.Json;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST surface for starting a Full Synchronisation preview (#1530 Phase 6). The portal starts the same preview
/// from the configuration notice and the Run Profiles tab; scripts start it here, read it back through the shared
/// <c>/previews</c> endpoints, and cite it when running the Full Synchronisation (Phase 4).
/// </summary>
[TestFixture]
public class SynchronisationControllerFullSynchronisationPreviewTests
{
    private const int ConnectedSystemId = 3;
    private const int Population = 12_847;

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
    private List<ConnectedSystemRunProfile> _runProfiles = null!;
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
        _runProfiles =
        [
            new ConnectedSystemRunProfile { Id = 30, Name = "Full Synchronisation", RunType = ConnectedSystemRunType.FullSynchronisation, ConnectedSystemId = ConnectedSystemId }
        ];

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                if (a.Id == Guid.Empty)
                    a.Id = Guid.NewGuid();
                _activities[a.Id] = a;
            })
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
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
            .ReturnsAsync(() => new ConnectedSystem { Id = ConnectedSystemId, Name = "Yellowstone HR", Status = ConnectedSystemStatus.Active });
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemRunProfilesAsync(ConnectedSystemId)).ReturnsAsync(() => _runProfiles);
        _syncRepo.Setup(r => r.GetAllSyncRulesAsync(It.IsAny<bool>())).ReturnsAsync(new List<SyncRule>());
        _syncRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(ConnectedSystemId)).ReturnsAsync(Population);

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

    [Test]
    public async Task StartFullSynchronisationPreview_UnknownConnectedSystem_ReturnsNotFoundAsync()
    {
        var result = await _controller.StartConnectedSystemFullSynchronisationPreviewAsync(99, null);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task StartFullSynchronisationPreview_StartsAFullSynchronisationPreviewOfThatSystemAsync()
    {
        var result = await _controller.StartConnectedSystemFullSynchronisationPreviewAsync(ConnectedSystemId, null);

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var queued = _queuedWorkerTasks.OfType<ConfigurationChangePreviewWorkerTask>().Single();
        var activity = _activities.Values.Single(a => a.TargetOperationType == ActivityTargetOperationType.Preview);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queued.Surface, Is.EqualTo(ConfigurationChangePreviewSurface.ConnectedSystemFullSynchronisation));
            Assert.That(queued.TargetId, Is.EqualTo(ConnectedSystemId));
            Assert.That(Proposal(queued).MaxObjects, Is.Null, "no cap was asked for, so every object is evaluated");
            Assert.That(activity.ConnectedSystemId, Is.EqualTo(ConnectedSystemId),
                "a run of this system can cite the preview only if the preview names the system it previewed");
            Assert.That(activity.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
        }
    }

    [Test]
    public async Task StartFullSynchronisationPreview_WithACap_ProposesThatCapAsync()
    {
        var result = await _controller.StartConnectedSystemFullSynchronisationPreviewAsync(ConnectedSystemId,
            new StartConnectedSystemFullSynchronisationPreviewRequest { MaxObjects = 500 });

        var response = StartResponse(result);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Proposal(_queuedWorkerTasks.OfType<ConfigurationChangePreviewWorkerTask>().Single()).MaxObjects, Is.EqualTo(500));
            Assert.That(response.ValidationFindings.Select(f => f.Severity), Does.Contain(PreviewValidationSeverity.Warning),
                "a capped preview warns that its counts describe only the objects it evaluated");
            Assert.That(response.EstimatedAffectedObjects, Is.EqualTo(500));
        }
    }

    [Test]
    public async Task StartFullSynchronisationPreview_CapBelowOne_IsBlockedAndEvaluatesNothingAsync()
    {
        var result = await _controller.StartConnectedSystemFullSynchronisationPreviewAsync(ConnectedSystemId,
            new StartConnectedSystemFullSynchronisationPreviewRequest { MaxObjects = 0 });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(StartResponse(result).IsBlocked, Is.True);
            Assert.That(_queuedWorkerTasks, Is.Empty);
        }
    }

    [Test]
    public async Task StartFullSynchronisationPreview_NoFullSynchronisationRunProfile_IsBlockedAndEvaluatesNothingAsync()
    {
        _runProfiles = [new ConnectedSystemRunProfile { Id = 31, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport, ConnectedSystemId = ConnectedSystemId }];

        var result = await _controller.StartConnectedSystemFullSynchronisationPreviewAsync(ConnectedSystemId, null);

        var response = StartResponse(result);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsBlocked, Is.True);
            Assert.That(response.ValidationFindings.Single().Message, Does.Contain("no Full Synchronisation Run Profile"));
            Assert.That(_queuedWorkerTasks, Is.Empty);
        }
    }

    [Test]
    public async Task StartFullSynchronisationPreview_EveryRowRequested_IsHonouredAsync()
    {
        await _controller.StartConnectedSystemFullSynchronisationPreviewAsync(ConnectedSystemId,
            new StartConnectedSystemFullSynchronisationPreviewRequest { DeltaPersistence = ConfigurationChangePreviewDeltaPersistence.Full });

        Assert.That(_previews.Values.Single().RequestedDeltaPersistence, Is.EqualTo(ConfigurationChangePreviewDeltaPersistence.Full));
    }

    [Test]
    public async Task StartFullSynchronisationPreview_SaysHowLongItShouldTakeAsync()
    {
        // The portal states this before a large preview starts; a script deciding how long to wait needs it too.
        var result = await _controller.StartConnectedSystemFullSynchronisationPreviewAsync(ConnectedSystemId, null);

        Assert.That(StartResponse(result).EstimatedDuration, Is.EqualTo(TimeSpan.FromSeconds(Population / 50d)),
            "with no Full Synchronisation on record, the estimate is the population at the reference rate");
    }

    private static ConfigurationChangePreviewStartResponse StartResponse(IActionResult result)
    {
        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        return (ConfigurationChangePreviewStartResponse)((AcceptedAtRouteResult)result).Value!;
    }

    private static ConnectedSystemFullSynchronisationProposal Proposal(ConfigurationChangePreviewWorkerTask task) =>
        JsonSerializer.Deserialize<ConnectedSystemFullSynchronisationProposal>(task.ProposedConfigurationPayload)!;
}
