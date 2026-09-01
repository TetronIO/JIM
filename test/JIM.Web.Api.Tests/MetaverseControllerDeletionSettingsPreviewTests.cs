// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
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
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST surface for previewing a change to a Metaverse Object Type's deletion settings (#1114).
///
/// The load-bearing behaviour here is that the preview describes **the change the update endpoint would make**, not
/// the body the caller sent. Both endpoints treat an omitted field as "leave the stored value alone", so a preview
/// that read an omitted field as null would answer a question about a change nobody was proposing, and would answer
/// it confidently.
/// </summary>
[TestFixture]
public class MetaverseControllerDeletionSettingsPreviewTests
{
    private const int ObjectTypeId = 1;

    private Mock<IRepository> _repository = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IApiKeyRepository> _apiKeyRepo = null!;
    private Mock<IConfigurationChangePreviewRepository> _previewRepo = null!;
    private Mock<ITaskingRepository> _taskingRepo = null!;
    private Mock<IServiceSettingsRepository> _serviceSettingsRepo = null!;
    private JimApplication _application = null!;
    private MetaverseController _controller = null!;
    private List<WorkerTask> _queuedWorkerTasks = null!;
    private Activity? _previewActivity;

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _apiKeyRepo = new Mock<IApiKeyRepository>();
        _previewRepo = new Mock<IConfigurationChangePreviewRepository>();
        _taskingRepo = new Mock<ITaskingRepository>();
        _serviceSettingsRepo = new Mock<IServiceSettingsRepository>();

        _repository.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);
        _repository.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        // The deletion-rule configuration advisory (#1570) reads every Synchronisation Rule when building
        // an object type response; no rules means no advisory.
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync(It.IsAny<bool>())).ReturnsAsync([]);
        _repository.Setup(r => r.ApiKeys).Returns(_apiKeyRepo.Object);
        _repository.Setup(r => r.ConfigurationChangePreviews).Returns(_previewRepo.Object);
        _repository.Setup(r => r.Tasking).Returns(_taskingRepo.Object);
        _repository.Setup(r => r.ServiceSettings).Returns(_serviceSettingsRepo.Object);

        _queuedWorkerTasks = [];
        _previewActivity = null;

        // EF assigns the Activity's Guid on insert and the preview's identity is that Guid, so nothing downstream
        // works without standing in for it here.
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

        // No background runner is registered in a unit fixture, so every preview is queued for JIM.Worker. That is
        // what makes the queued task's payload the honest record of what was handed to the framework.
        _application = new JimApplication(_repository.Object);
        _controller = new MetaverseController(new Mock<ILogger<MetaverseController>>().Object, _application);

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
    public async Task StartDeletionSettingsPreviewAsync_UnknownObjectType_ReturnsNotFoundAsync()
    {
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(99, false)).ReturnsAsync((MetaverseObjectType?)null);

        var result = await _controller.StartDeletionSettingsPreviewAsync(99,
            new StartMetaverseObjectTypeDeletionSettingsPreviewRequest());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task StartDeletionSettingsPreviewAsync_OmittedFields_PreviewsTheStoredValuesAsync()
    {
        SetUpObjectType(new MetaverseObjectType
        {
            Id = ObjectTypeId,
            Name = "User",
            PluralName = "Users",
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            DeletionGracePeriod = TimeSpan.FromDays(30),
            DeletionTriggerConnectedSystemIds = [4],
            DeletionTriggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect
        });

        // Everything omitted: the caller is asking what the settings already in force would do, which is a
        // legitimate question and must not be read as "clear every setting".
        var result = await _controller.StartDeletionSettingsPreviewAsync(ObjectTypeId,
            new StartMetaverseObjectTypeDeletionSettingsPreviewRequest());

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var proposal = QueuedProposal();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.DeletionRule, Is.EqualTo(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected));
            Assert.That(proposal.DeletionGracePeriod, Is.EqualTo(TimeSpan.FromDays(30)));
            Assert.That(proposal.DeletionTriggerConnectedSystemIds, Is.EquivalentTo(new[] { 4 }));
            Assert.That(proposal.DeletionTriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect));
        }
    }

    [Test]
    public async Task StartDeletionSettingsPreviewAsync_SuppliedFields_PreviewsTheProposedValuesAsync()
    {
        SetUpObjectType(new MetaverseObjectType
        {
            Id = ObjectTypeId,
            Name = "User",
            PluralName = "Users",
            DeletionRule = MetaverseObjectDeletionRule.Manual,
            DeletionGracePeriod = null,
            DeletionTriggerConnectedSystemIds = [],
            DeletionTriggerMode = AuthoritativeSourceTriggerMode.AllSourcesDisconnect
        });
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(9, false)).ReturnsAsync(new ConnectedSystem { Id = 9, Name = "HR" });

        var result = await _controller.StartDeletionSettingsPreviewAsync(ObjectTypeId,
            new StartMetaverseObjectTypeDeletionSettingsPreviewRequest
            {
                DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
                DeletionGracePeriod = TimeSpan.FromDays(7),
                DeletionTriggerConnectedSystemIds = [9],
                DeletionTriggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect
            });

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var proposal = QueuedProposal();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.DeletionRule, Is.EqualTo(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected));
            Assert.That(proposal.DeletionGracePeriod, Is.EqualTo(TimeSpan.FromDays(7)));
            Assert.That(proposal.DeletionTriggerConnectedSystemIds, Is.EquivalentTo(new[] { 9 }));
            Assert.That(proposal.DeletionTriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect));
        }
    }

    [Test]
    public async Task StartDeletionSettingsPreviewAsync_ZeroGracePeriod_PreviewsItAsNoGracePeriodAsync()
    {
        SetUpObjectType(new MetaverseObjectType
        {
            Id = ObjectTypeId,
            Name = "User",
            PluralName = "Users",
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            DeletionGracePeriod = TimeSpan.FromDays(30)
        });

        var result = await _controller.StartDeletionSettingsPreviewAsync(ObjectTypeId,
            new StartMetaverseObjectTypeDeletionSettingsPreviewRequest { DeletionGracePeriod = TimeSpan.Zero });

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        Assert.That(QueuedProposal().DeletionGracePeriod, Is.Null,
            "the update endpoint stores zero as null, so a preview that kept the zero would model a different change from the one it is previewing");
    }

    [Test]
    public async Task StartDeletionSettingsPreviewAsync_UnknownTriggerConnectedSystem_ReturnsBadRequestAsync()
    {
        SetUpObjectType(new MetaverseObjectType { Id = ObjectTypeId, Name = "User", PluralName = "Users" });
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(404, false)).ReturnsAsync((ConnectedSystem?)null);

        var result = await _controller.StartDeletionSettingsPreviewAsync(ObjectTypeId,
            new StartMetaverseObjectTypeDeletionSettingsPreviewRequest
            {
                DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
                DeletionTriggerConnectedSystemIds = [404]
            });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>(),
            "a reference to a Connected System that does not exist is a malformed request, not a finding about an otherwise coherent proposal");
        Assert.That(_previewActivity, Is.Null, "nothing should have been started");
    }

    [Test]
    public async Task StartDeletionSettingsPreviewAsync_AuthoritativeRuleWithNoSources_ReturnsTheBlockingFindingAsync()
    {
        SetUpObjectType(new MetaverseObjectType
        {
            Id = ObjectTypeId,
            Name = "User",
            PluralName = "Users",
            DeletionRule = MetaverseObjectDeletionRule.Manual,
            DeletionTriggerConnectedSystemIds = []
        });

        var result = await _controller.StartDeletionSettingsPreviewAsync(ObjectTypeId,
            new StartMetaverseObjectTypeDeletionSettingsPreviewRequest
            {
                DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
                DeletionTriggerConnectedSystemIds = []
            }) as AcceptedAtRouteResult;

        // Deliberately not a 400: the proposal is well-formed and the framework has an answer about it. Refusing the
        // request would deny the caller the one thing they asked for, which is to be told what is wrong with it.
        Assert.That(result, Is.Not.Null);
        var response = result!.Value as ConfigurationChangePreviewStartResponse;
        Assert.That(response, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response!.IsBlocked, Is.True);
            Assert.That(response!.ValidationFindings.Any(f => f.Severity == PreviewValidationSeverity.Blocking), Is.True);
            Assert.That(response!.ActivityId, Is.Not.EqualTo(Guid.Empty), "the caller needs the Activity to read the findings back");
            Assert.That(_queuedWorkerTasks, Is.Empty, "a blocked proposal is never evaluated");
        }
    }

    [Test]
    public async Task StartDeletionSettingsPreviewAsync_FullDataSetRequested_CarriesThatThroughAsync()
    {
        SetUpObjectType(new MetaverseObjectType
        {
            Id = ObjectTypeId,
            Name = "User",
            PluralName = "Users",
            DeletionRule = MetaverseObjectDeletionRule.Manual
        });
        ConfigurationChangePreview? created = null;
        _previewRepo.Setup(r => r.CreatePreviewAsync(It.IsAny<ConfigurationChangePreview>()))
            .Callback<ConfigurationChangePreview>(p => created = p)
            .Returns(Task.CompletedTask);

        await _controller.StartDeletionSettingsPreviewAsync(ObjectTypeId,
            new StartMetaverseObjectTypeDeletionSettingsPreviewRequest
            {
                DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
                DeltaPersistence = ConfigurationChangePreviewDeltaPersistence.Full
            });

        Assert.That(created, Is.Not.Null);
        Assert.That(created!.RequestedDeltaPersistence, Is.EqualTo(ConfigurationChangePreviewDeltaPersistence.Full),
            "the portal asks an administrator this question; an API caller states it in the request and must be honoured the same way");
    }

    [Test]
    public async Task UpdateObjectTypeAsync_WithPreviewActivityId_LinksTheApplyActivityToThePreviewAsync()
    {
        var objectType = new MetaverseObjectType
        {
            Id = ObjectTypeId,
            Name = "User",
            PluralName = "Users",
            DeletionRule = MetaverseObjectDeletionRule.Manual
        };
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(ObjectTypeId, false)).ReturnsAsync(objectType);
        _metaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>())).Returns(Task.CompletedTask);
        Activity? applyActivity = null;
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => applyActivity = a)
            .Returns(Task.CompletedTask);
        var previewActivityId = Guid.NewGuid();

        var result = await _controller.UpdateObjectTypeAsync(ObjectTypeId, new UpdateMetaverseObjectTypeRequest
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            PreviewActivityId = previewActivityId
        });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(applyActivity, Is.Not.Null);
        Assert.That(applyActivity!.PreviewActivityId, Is.EqualTo(previewActivityId));
    }

    #region helpers

    /// <summary>
    /// Wires the reads the adapter makes: the object type itself, and the candidate population it estimates and
    /// evaluates against. An empty population keeps these tests about the request-to-proposal mapping.
    /// </summary>
    private void SetUpObjectType(MetaverseObjectType objectType)
    {
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(objectType.Id, false)).ReturnsAsync(objectType);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectDeletionCandidateCountAsync(objectType.Id)).ReturnsAsync(0);
        _metaverseRepo.Setup(r => r.StreamMetaverseObjectDeletionCandidates(objectType.Id)).Returns(Empty());
    }

    private static async IAsyncEnumerable<MetaverseObjectDeletionCandidate> Empty(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// The proposal as the framework serialised it onto the worker queue: the only observation point that proves
    /// what the adapter will actually evaluate, rather than what the controller intended to hand over.
    /// </summary>
    private MetaverseObjectTypeDeletionSettingsProposal QueuedProposal()
    {
        var task = _queuedWorkerTasks.OfType<ConfigurationChangePreviewWorkerTask>().SingleOrDefault();
        Assert.That(task, Is.Not.Null, "the preview should have been queued for evaluation");
        return JsonSerializer.Deserialize<MetaverseObjectTypeDeletionSettingsProposal>(task!.ProposedConfigurationPayload)!;
    }

    #endregion
}
