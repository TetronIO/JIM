// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Expressions;
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
/// The REST surface for previewing a Synchronisation Rule's Attribute Flow (#1437).
///
/// Two things are worth pinning here. The first is the difference between omitting the mappings and sending none:
/// omitted means "as the rule stands", so the preview reports no change, while an EMPTY array is a real proposal
/// that removes every mapping. The second is that the proposal crosses a JSON queue on its way to the worker, and
/// a mapping is more than its target: its source order, its Expression, its Missing Input Behaviour and its
/// Attribute Priority each change what would be written, so any of them lost in transit would have the preview
/// answer for a configuration nobody proposed.
/// </summary>
[TestFixture]
public class SynchronisationControllerAttributeFlowPreviewTests
{
    private const int SyncRuleId = 42;
    private const int ConnectedSystemId = 2;
    private const int CsoTypeId = 9;
    private const int MvEmailAttributeId = 201;

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

        // A stored mapping deliberately away from the defaults, so an omitted proposal merged to a default rather
        // than to the stored configuration is caught.
        _syncRule = new SyncRule
        {
            Id = SyncRuleId,
            Name = "HR Import",
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectTypeId = CsoTypeId,
            Direction = SyncRuleDirection.Import,
            Enabled = true
        };
        var storedMapping = new SyncRuleMapping
        {
            Id = 900,
            SyncRuleId = SyncRuleId,
            TargetMetaverseAttributeId = MvEmailAttributeId,
            Priority = 3,
            CaseNormalisation = InboundCaseNormalisation.Lower
        };
        storedMapping.Sources.Add(new SyncRuleMappingSource { Order = 1, ConnectedSystemAttributeId = 101 });
        _syncRule.AttributeFlowRules.Add(storedMapping);

        _connectedSystemRepo.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(() => _syncRule);
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync(ConnectedSystemId, false, It.IsAny<bool>()))
            .ReturnsAsync(() => [_syncRule]);

        // The Attribute Flow adapter reads every rule across every Connected System to work out who else
        // contributes to the attributes being proposed, which is what decides Attribute Priority.
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync()).ReturnsAsync(() => [_syncRule]);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountOfTypeAsync(ConnectedSystemId, CsoTypeId))
            .ReturnsAsync(0);

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
    public async Task StartAttributeFlowPreview_UnknownSyncRule_ReturnsNotFoundAsync()
    {
        _connectedSystemRepo.Setup(r => r.GetSyncRuleAsync(99)).ReturnsAsync((SyncRule?)null);

        var result = await _controller.StartSyncRuleAttributeFlowPreviewAsync(99, new StartSyncRuleAttributeFlowPreviewRequest());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task StartAttributeFlowPreview_OmittedMappings_PreviewsTheStoredAttributeFlowAsync()
    {
        var result = await _controller.StartSyncRuleAttributeFlowPreviewAsync(SyncRuleId,
            new StartSyncRuleAttributeFlowPreviewRequest());

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var proposal = QueuedProposal();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.Mappings, Has.Count.EqualTo(1));
            Assert.That(proposal.Mappings[0].TargetMetaverseAttributeId, Is.EqualTo(MvEmailAttributeId));
            Assert.That(proposal.Mappings[0].Priority, Is.EqualTo(3), "the stored priority, not the default");
            Assert.That(proposal.Mappings[0].CaseNormalisation, Is.EqualTo(InboundCaseNormalisation.Lower));
        }
    }

    [Test]
    public async Task StartAttributeFlowPreview_EmptyMappingsArray_ProposesRemovingEveryMappingAsync()
    {
        // The distinction this endpoint exists to keep: an empty array proposes a rule that flows nothing, which
        // is not the same as proposing no change at all.
        var result = await _controller.StartSyncRuleAttributeFlowPreviewAsync(SyncRuleId,
            new StartSyncRuleAttributeFlowPreviewRequest { Mappings = [] });

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        Assert.That(QueuedProposal().Mappings, Is.Empty);
    }

    [Test]
    public async Task StartAttributeFlowPreview_ExpressionSource_ReachesEvaluationWithItsMissingInputBehaviourAsync()
    {
        // Missing Input Behaviour decides whether an Expression with an absent input produces a malformed value,
        // nothing at all, or a reported failure, so losing it across the queue would preview a different outcome
        // for exactly the objects the preview exists to find.
        var result = await _controller.StartSyncRuleAttributeFlowPreviewAsync(SyncRuleId, new StartSyncRuleAttributeFlowPreviewRequest
        {
            Mappings =
            [
                new SyncRuleMappingRequest
                {
                    TargetMetaverseAttributeId = MvEmailAttributeId,
                    Priority = 1,
                    Sources =
                    [
                        new SyncRuleMappingSourceRequest
                        {
                            Order = 1,
                            Expression = "cs[\"givenName\"] + \".\" + cs[\"sn\"] + \"@corp.local\"",
                            MissingInputBehaviour = MissingInputBehaviour.FailMapping
                        }
                    ]
                }
            ]
        });

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var source = QueuedProposal().Mappings[0].Sources[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Expression, Does.Contain("givenName"));
            Assert.That(source.MissingInputBehaviour, Is.EqualTo(MissingInputBehaviour.FailMapping));
            Assert.That(QueuedProposal().Mappings[0].Priority, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task StartAttributeFlowPreview_ChainedSources_KeepTheirOrderAsync()
    {
        // Chained sources feed each other, so an order lost in transit produces a different value.
        var result = await _controller.StartSyncRuleAttributeFlowPreviewAsync(SyncRuleId, new StartSyncRuleAttributeFlowPreviewRequest
        {
            Mappings =
            [
                new SyncRuleMappingRequest
                {
                    TargetMetaverseAttributeId = MvEmailAttributeId,
                    Sources =
                    [
                        new SyncRuleMappingSourceRequest { Order = 2, ConnectedSystemAttributeId = 102 },
                        new SyncRuleMappingSourceRequest { Order = 1, ConnectedSystemAttributeId = 101 }
                    ]
                }
            ]
        });

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var sources = QueuedProposal().Mappings[0].Sources;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sources.Select(s => s.Order), Is.EquivalentTo(new[] { 2, 1 }));
            Assert.That(sources.Single(s => s.Order == 1).ConnectedSystemAttributeId, Is.EqualTo(101));
            Assert.That(sources.Single(s => s.Order == 2).ConnectedSystemAttributeId, Is.EqualTo(102));
        }
    }

    [Test]
    public async Task StartAttributeFlowPreview_ExportMapping_KeepsItsConnectedSystemTargetAsync()
    {
        // An export rule writes the other side, so the fixture's rule is turned round for this one; a Connected
        // System target on an IMPORT rule is refused as a blocking finding rather than previewed, which the next
        // test pins.
        _syncRule.Direction = SyncRuleDirection.Export;
        _syncRule.AttributeFlowRules.Clear();

        var result = await _controller.StartSyncRuleAttributeFlowPreviewAsync(SyncRuleId, new StartSyncRuleAttributeFlowPreviewRequest
        {
            Mappings =
            [
                new SyncRuleMappingRequest
                {
                    TargetConnectedSystemAttributeId = 103,
                    InitialExportOnly = true,
                    Sources = [new SyncRuleMappingSourceRequest { Order = 1, MetaverseAttributeId = MvEmailAttributeId }]
                }
            ]
        });

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var mapping = QueuedProposal().Mappings[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mapping.TargetConnectedSystemAttributeId, Is.EqualTo(103));
            Assert.That(mapping.TargetMetaverseAttributeId, Is.Null);
            Assert.That(mapping.InitialExportOnly, Is.True);
            Assert.That(mapping.Sources[0].MetaverseAttributeId, Is.EqualTo(MvEmailAttributeId));
        }
    }

    [Test]
    public async Task StartAttributeFlowPreview_MappingWritesTheWrongSide_IsRefusedRatherThanEvaluatedAsync()
    {
        // A mapping an import rule could never write is a proposal that silently does less than it reads, so it
        // comes back blocked rather than previewed as though it flowed.
        var result = await _controller.StartSyncRuleAttributeFlowPreviewAsync(SyncRuleId, new StartSyncRuleAttributeFlowPreviewRequest
        {
            Mappings =
            [
                new SyncRuleMappingRequest
                {
                    TargetConnectedSystemAttributeId = 103,
                    Sources = [new SyncRuleMappingSourceRequest { Order = 1, MetaverseAttributeId = MvEmailAttributeId }]
                }
            ]
        });

        var accepted = result as AcceptedAtRouteResult;
        Assert.That(accepted, Is.Not.Null);
        var response = accepted!.Value as ConfigurationChangePreviewStartResponse;
        Assert.That(response, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response!.IsBlocked, Is.True);
            Assert.That(_queuedWorkerTasks.OfType<ConfigurationChangePreviewWorkerTask>(), Is.Empty,
                "a blocked proposal must never reach evaluation");
        }
    }

    private SyncRuleAttributeFlowProposal QueuedProposal()
    {
        var task = _queuedWorkerTasks.OfType<ConfigurationChangePreviewWorkerTask>().SingleOrDefault();
        Assert.That(task, Is.Not.Null, "the preview should have been queued for evaluation");
        return JsonSerializer.Deserialize<SyncRuleAttributeFlowProposal>(task!.ProposedConfigurationPayload)!;
    }
}
