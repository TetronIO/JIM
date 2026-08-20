// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
using JIM.Models.Preview;
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
/// The REST surface for previewing a Synchronisation Rule's behaviour toggles (#1462).
///
/// What an omitted field means is the thing to pin, and it matters more here than on any other preview surface:
/// three of the five toggles are booleans, so a merge that reached for the type default rather than the stored
/// value would silently propose switching them off. A caller asking about Enabled would be told, confidently,
/// what turning off provisioning does as well.
/// </summary>
[TestFixture]
public class SynchronisationControllerBehaviourPreviewTests
{
    private const int SyncRuleId = 42;
    private const int ConnectedSystemId = 2;
    private const int CsoTypeId = 9;

    private Mock<IRepository> _repository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IApiKeyRepository> _apiKeyRepo = null!;
    private Mock<IConfigurationChangePreviewRepository> _previewRepo = null!;
    private Mock<ITaskingRepository> _taskingRepo = null!;
    private Mock<IServiceSettingsRepository> _serviceSettingsRepo = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private List<WorkerTask> _queuedWorkerTasks = null!;
    private Activity? _previewActivity;
    private SyncRule _syncRule = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _apiKeyRepo = new Mock<IApiKeyRepository>();
        _previewRepo = new Mock<IConfigurationChangePreviewRepository>();
        _taskingRepo = new Mock<ITaskingRepository>();
        _serviceSettingsRepo = new Mock<IServiceSettingsRepository>();

        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repository.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);
        _repository.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repository.Setup(r => r.ApiKeys).Returns(_apiKeyRepo.Object);
        _repository.Setup(r => r.ConfigurationChangePreviews).Returns(_previewRepo.Object);
        _repository.Setup(r => r.Tasking).Returns(_taskingRepo.Object);
        _repository.Setup(r => r.ServiceSettings).Returns(_serviceSettingsRepo.Object);

        _queuedWorkerTasks = [];
        _previewActivity = null;

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                if (a.Id == Guid.Empty)
                    a.Id = Guid.NewGuid();
                _previewActivity = a;
            })
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetActivityAsync(It.IsAny<Guid>())).ReturnsAsync(() => _previewActivity);
        _previewRepo.Setup(r => r.CreatePreviewAsync(It.IsAny<ConfigurationChangePreview>())).Returns(Task.CompletedTask);
        _previewRepo.Setup(r => r.UpdatePreviewAsync(It.IsAny<ConfigurationChangePreview>())).Returns(Task.CompletedTask);
        _previewRepo.Setup(r => r.GetPreviewAsync(It.IsAny<Guid>())).ReturnsAsync((ConfigurationChangePreview?)null);
        _taskingRepo.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(t => _queuedWorkerTasks.Add(t))
            .Returns(Task.CompletedTask);

        // A stored configuration deliberately away from the enum defaults, so an omitted field merged to a
        // default rather than to the stored value is caught.
        _syncRule = new SyncRule
        {
            Id = SyncRuleId,
            Name = "Directory Export",
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectTypeId = CsoTypeId,
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            OutboundDeprovisionAction = OutboundDeprovisionAction.Delete,
            InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined
        };
        _connectedSystemRepo.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(() => _syncRule);
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync(ConnectedSystemId, false, It.IsAny<bool>()))
            .ReturnsAsync(() => [_syncRule]);
        _connectedSystemRepo.Setup(r => r.GetJoinedConnectedSystemObjectCountAsync(ConnectedSystemId, CsoTypeId))
            .ReturnsAsync(0);

        // No background runner is registered in a unit fixture, so every preview is queued for JIM.Worker, which
        // makes the queued task's payload the honest record of what was handed to the framework.
        _application = new JimApplication(_repository.Object);
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
    public async Task StartBehaviourPreview_UnknownSyncRule_ReturnsNotFoundAsync()
    {
        _connectedSystemRepo.Setup(r => r.GetSyncRuleAsync(99)).ReturnsAsync((SyncRule?)null);

        var result = await _controller.StartSyncRuleBehaviourPreviewAsync(99, new StartSyncRuleBehaviourPreviewRequest());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task StartBehaviourPreview_OmittedFields_PreviewTheStoredTogglesAsync()
    {
        // The stored rule deliberately holds non-defaults, so a merge reaching for the type default is caught.
        _syncRule.Enabled = true;
        _syncRule.ProjectToMetaverse = true;
        _syncRule.EnforceState = false;

        var result = await _controller.StartSyncRuleBehaviourPreviewAsync(SyncRuleId, new StartSyncRuleBehaviourPreviewRequest());

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var proposal = QueuedProposal();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.Enabled, Is.True);
            Assert.That(proposal.ProjectToMetaverse, Is.True);
            Assert.That(proposal.EnforceState, Is.False, "the stored value, not the property's default of true");
        }
    }

    [Test]
    public async Task StartBehaviourPreview_OneToggleSupplied_MergesTheRestFromTheStoredRuleAsync()
    {
        // The failure this guards against is specific to booleans: silence must never read as "switch it off".
        _syncRule.Enabled = true;
        _syncRule.ProjectToMetaverse = true;

        var result = await _controller.StartSyncRuleBehaviourPreviewAsync(SyncRuleId,
            new StartSyncRuleBehaviourPreviewRequest { Enabled = false });

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var proposal = QueuedProposal();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.Enabled, Is.False);
            Assert.That(proposal.ProjectToMetaverse, Is.True,
                "the unsupplied toggle must carry the stored value through to evaluation");
        }
    }

    [Test]
    public async Task StartBehaviourPreview_UnsetNullableToggle_ResolvesToOffAsync()
    {
        // Null on the rule means off at synchronisation time, so the proposal says off rather than carrying a null
        // for the adapter to interpret.
        _syncRule.ProvisionToConnectedSystem = null;

        await _controller.StartSyncRuleBehaviourPreviewAsync(SyncRuleId, new StartSyncRuleBehaviourPreviewRequest());

        Assert.That(QueuedProposal().ProvisionToConnectedSystem, Is.False);
    }

    [Test]
    public async Task StartBehaviourPreview_DirectionFlip_IsBlockedRatherThanEvaluatedAsync()
    {
        // Accepted on the wire and refused by validation, so the caller is told why. A blocked proposal is never
        // queued: evaluating it could only answer about a configuration that cannot work.
        _syncRule.Direction = SyncRuleDirection.Import;

        var result = await _controller.StartSyncRuleBehaviourPreviewAsync(SyncRuleId,
            new StartSyncRuleBehaviourPreviewRequest { Direction = SyncRuleDirection.Export });

        var response = (ConfigurationChangePreviewStartResponse)((AcceptedAtRouteResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsBlocked, Is.True);
            Assert.That(response.ValidationFindings.Any(f =>
                f.Severity == PreviewValidationSeverity.Blocking && f.Message.Contains("Direction")), Is.True);
            Assert.That(_queuedWorkerTasks.OfType<ConfigurationChangePreviewWorkerTask>(), Is.Empty,
                "a blocked proposal is never evaluated");
        }
    }

    private SyncRuleBehaviourToggleProposal QueuedProposal()
    {
        var task = _queuedWorkerTasks.OfType<ConfigurationChangePreviewWorkerTask>().SingleOrDefault();
        Assert.That(task, Is.Not.Null, "the preview should have been queued for evaluation");
        return JsonSerializer.Deserialize<SyncRuleBehaviourToggleProposal>(task!.ProposedConfigurationPayload)!;
    }
}
