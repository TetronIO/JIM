// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
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
/// The REST surface for previewing a Connected System's Object Matching change (#1457).
///
/// Three things are worth pinning. Omitting the rules previews the stored ones, while an EMPTY array is a real
/// proposal that removes every rule and leaves nothing able to join. Omitting the mode keeps the stored mode,
/// because a caller editing rules should not have to restate it, and sending a different one is the mode switch
/// this surface exists to cover. And the proposal crosses a JSON queue on its way to the worker carrying rule
/// order, source order and case sensitivity, each of which changes which identity an account joins to, so any of
/// them lost in transit would have the preview answer for a configuration nobody proposed.
/// </summary>
[TestFixture]
public class SynchronisationControllerObjectMatchingPreviewTests
{
    private const int ConnectedSystemId = 2;
    private const int CsoTypeId = 9;
    private const int MvoTypeId = 3;
    private const int EmployeeIdAttributeId = 101;
    private const int MailAttributeId = 102;
    private const int EmployeeIdMetaverseAttributeId = 201;
    private const int MailMetaverseAttributeId = 202;

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
    private ConnectedSystem _connectedSystem = null!;
    private ConnectedSystemObjectType _csoType = null!;

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

        // A stored rule deliberately away from the defaults, so an omitted proposal merged to a default rather
        // than to the stored configuration is caught.
        _csoType = new ConnectedSystemObjectType
        {
            Id = CsoTypeId,
            Name = "User",
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = EmployeeIdAttributeId, Name = "employeeID", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = MailAttributeId, Name = "mail", Type = AttributeDataType.Text }
            ],
            ObjectMatchingRules =
            [
                new ObjectMatchingRule
                {
                    Id = 700,
                    Order = 2,
                    CaseSensitive = true,
                    ConnectedSystemObjectTypeId = CsoTypeId,
                    MetaverseObjectTypeId = MvoTypeId,
                    TargetMetaverseAttributeId = EmployeeIdMetaverseAttributeId,
                    Sources = [new ObjectMatchingRuleSource { Id = 1, Order = 0, ConnectedSystemAttributeId = EmployeeIdAttributeId }]
                }
            ]
        };

        _connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "HR",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem
        };

        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(() => _connectedSystem);
        _connectedSystemRepo.Setup(r => r.GetObjectTypesAsync(ConnectedSystemId)).ReturnsAsync(() => [_csoType]);
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync(ConnectedSystemId, false, It.IsAny<bool>())).ReturnsAsync(() => []);
        _connectedSystemRepo.Setup(r => r.GetUnjoinedConnectedSystemObjectCountOfTypeAsync(ConnectedSystemId, CsoTypeId)).ReturnsAsync(0);
        _connectedSystemRepo.Setup(r => r.GetUnjoinedConnectedSystemObjectIdsOfTypeAsync(ConnectedSystemId, CsoTypeId)).ReturnsAsync([]);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypesAsync(It.IsAny<bool>())).ReturnsAsync(() =>
        [
            new MetaverseObjectType
            {
                Id = MvoTypeId,
                Name = "Person",
                Attributes =
                [
                    new MetaverseAttribute { Id = EmployeeIdMetaverseAttributeId, Name = "Employee ID", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued },
                    new MetaverseAttribute { Id = MailMetaverseAttributeId, Name = "Email", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued }
                ]
            }
        ]);

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
    public async Task StartObjectMatchingPreview_UnknownConnectedSystem_ReturnsNotFoundAsync()
    {
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(99, It.IsAny<bool>())).ReturnsAsync((ConnectedSystem?)null);

        var result = await _controller.StartObjectMatchingPreviewAsync(99, new StartObjectMatchingPreviewRequest());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task StartObjectMatchingPreview_OmittedRules_PreviewsTheStoredConfigurationAsync()
    {
        var result = await _controller.StartObjectMatchingPreviewAsync(ConnectedSystemId, new StartObjectMatchingPreviewRequest());

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var proposal = QueuedProposal();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.Mode, Is.EqualTo(ObjectMatchingRuleMode.ConnectedSystem));
            Assert.That(proposal.Rules, Has.Count.EqualTo(1));
            Assert.That(proposal.Rules[0].Order, Is.EqualTo(2), "the stored order, not the default");
            Assert.That(proposal.Rules[0].CaseSensitive, Is.True, "the stored case sensitivity, not the default");
            Assert.That(proposal.Rules[0].TargetMetaverseAttributeId, Is.EqualTo(EmployeeIdMetaverseAttributeId));
            Assert.That(proposal.Rules[0].Sources[0].ConnectedSystemAttributeId, Is.EqualTo(EmployeeIdAttributeId));
        }
    }

    [Test]
    public async Task StartObjectMatchingPreview_EmptyRulesArray_ProposesRemovingEveryRuleAsync()
    {
        // The distinction this endpoint exists to keep: an empty array proposes a system that joins nothing, which
        // is not the same as proposing no change at all.
        var result = await _controller.StartObjectMatchingPreviewAsync(ConnectedSystemId,
            new StartObjectMatchingPreviewRequest { Rules = [] });

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        Assert.That(QueuedProposal().Rules, Is.Empty);
    }

    [Test]
    public async Task StartObjectMatchingPreview_OmittedMode_KeepsTheStoredModeAsync()
    {
        _connectedSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;

        await _controller.StartObjectMatchingPreviewAsync(ConnectedSystemId,
            new StartObjectMatchingPreviewRequest { Rules = [] });

        Assert.That(QueuedProposal().Mode, Is.EqualTo(ObjectMatchingRuleMode.SyncRule));
    }

    [Test]
    public async Task StartObjectMatchingPreview_ModeSwitch_CrossesTheQueueAsync()
    {
        await _controller.StartObjectMatchingPreviewAsync(ConnectedSystemId,
            new StartObjectMatchingPreviewRequest { Mode = ObjectMatchingRuleMode.SyncRule });

        Assert.That(QueuedProposal().Mode, Is.EqualTo(ObjectMatchingRuleMode.SyncRule));
    }

    [Test]
    public async Task StartObjectMatchingPreview_ProposedRule_CrossesTheQueueIntactAsync()
    {
        // Rule order, source order and case sensitivity each decide which identity an account joins to, so all
        // three have to survive serialisation to the worker.
        await _controller.StartObjectMatchingPreviewAsync(ConnectedSystemId, new StartObjectMatchingPreviewRequest
        {
            Rules =
            [
                new ObjectMatchingRuleRequest
                {
                    Order = 5,
                    ConnectedSystemObjectTypeId = CsoTypeId,
                    MetaverseObjectTypeId = MvoTypeId,
                    TargetMetaverseAttributeId = MailMetaverseAttributeId,
                    CaseSensitive = true,
                    Sources = [new ObjectMatchingRuleSourceRequest { Order = 3, ConnectedSystemAttributeId = MailAttributeId }]
                }
            ]
        });

        var rule = QueuedProposal().Rules.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rule.Order, Is.EqualTo(5));
            Assert.That(rule.CaseSensitive, Is.True);
            Assert.That(rule.TargetMetaverseAttributeId, Is.EqualTo(MailMetaverseAttributeId));
            Assert.That(rule.MetaverseObjectTypeId, Is.EqualTo(MvoTypeId));
            Assert.That(rule.Sources.Single().Order, Is.EqualTo(3));
            Assert.That(rule.Sources.Single().ConnectedSystemAttributeId, Is.EqualTo(MailAttributeId));
        }
    }

    [Test]
    public async Task StartObjectMatchingPreview_ExpressionSource_IsBlockedRatherThanEvaluatedAsync()
    {
        // Accepted on the wire and refused by validation, so the caller is told why rather than having their
        // proposal silently reshaped into one that matches on nothing. A blocked proposal is never queued: an
        // evaluation of it could only answer for a configuration that cannot work.
        var result = await _controller.StartObjectMatchingPreviewAsync(ConnectedSystemId, new StartObjectMatchingPreviewRequest
        {
            Rules =
            [
                new ObjectMatchingRuleRequest
                {
                    Order = 0,
                    ConnectedSystemObjectTypeId = CsoTypeId,
                    MetaverseObjectTypeId = MvoTypeId,
                    TargetMetaverseAttributeId = MailMetaverseAttributeId,
                    Sources = [new ObjectMatchingRuleSourceRequest { Order = 0, Expression = "Lower(cs[\"mail\"])" }]
                }
            ]
        });

        var response = (ConfigurationChangePreviewStartResponse)((AcceptedAtRouteResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsBlocked, Is.True,
                "an expression source cannot be matched on, and the caller is told so");
            Assert.That(response.ValidationFindings.Any(f => f.Severity == PreviewValidationSeverity.Blocking && f.Message.Contains("expression")), Is.True,
                "the finding names what is wrong rather than merely refusing");
            Assert.That(_queuedWorkerTasks.OfType<ConfigurationChangePreviewWorkerTask>(), Is.Empty,
                "a blocked proposal is never evaluated");
        }
    }

    private ObjectMatchingProposal QueuedProposal()
    {
        var task = _queuedWorkerTasks.OfType<ConfigurationChangePreviewWorkerTask>().SingleOrDefault();
        Assert.That(task, Is.Not.Null, "the preview should have been queued for evaluation");
        return JsonSerializer.Deserialize<ObjectMatchingProposal>(task!.ProposedConfigurationPayload)!;
    }
}
