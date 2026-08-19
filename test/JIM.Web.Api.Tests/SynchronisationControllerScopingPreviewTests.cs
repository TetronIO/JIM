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
using JIM.Models.Search;
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
/// The REST surface for previewing a Synchronisation Rule's Scoping Criteria (#1436).
///
/// The behaviour worth pinning is the difference between omitting the criteria and sending none. Omitted means
/// "as the rule stands", so the preview reports no change; an EMPTY array is a real and far larger proposal that
/// removes every criterion and hands the rule every object of its type. Collapsing the two would either hide the
/// widest change the Scope tab can make, or invent one nobody asked for, depending on which way it collapsed.
/// </summary>
[TestFixture]
public class SynchronisationControllerScopingPreviewTests
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
        _syncRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria =
            {
                new SyncRuleScopingCriteria
                {
                    MetaverseAttributeId = 201,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "Sales"
                }
            }
        });
        _connectedSystemRepo.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(() => _syncRule);
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync(ConnectedSystemId, false, It.IsAny<bool>()))
            .ReturnsAsync(() => [_syncRule]);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountOfTypeAsync(ConnectedSystemId, CsoTypeId))
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
    public async Task StartScopingPreview_UnknownSyncRule_ReturnsNotFoundAsync()
    {
        _connectedSystemRepo.Setup(r => r.GetSyncRuleAsync(99)).ReturnsAsync((SyncRule?)null);

        var result = await _controller.StartSyncRuleScopingPreviewAsync(99, new StartSyncRuleScopingPreviewRequest());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task StartScopingPreview_OmittedCriteria_PreviewsTheStoredScopeAsync()
    {
        // Asking what the scope already in force would do is a legitimate question, and the answer must be the
        // rule's own criteria rather than an empty proposal, which would read as "remove them all".
        var result = await _controller.StartSyncRuleScopingPreviewAsync(SyncRuleId, new StartSyncRuleScopingPreviewRequest());

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var proposal = QueuedProposal();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.CriteriaGroups, Has.Count.EqualTo(1));
            Assert.That(proposal.CriteriaGroups[0].Criteria[0].StringValue, Is.EqualTo("Sales"));
            Assert.That(proposal.IsUnscoped, Is.False);
        }
    }

    [Test]
    public async Task StartScopingPreview_EmptyCriteriaArray_ProposesRemovingEveryCriterionAsync()
    {
        // The distinction this endpoint exists to keep: an empty array is the widest proposal available, not
        // silence. Reading it as "no change" would hide it entirely.
        var result = await _controller.StartSyncRuleScopingPreviewAsync(SyncRuleId,
            new StartSyncRuleScopingPreviewRequest { CriteriaGroups = [] });

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var proposal = QueuedProposal();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.CriteriaGroups, Is.Empty);
            Assert.That(proposal.IsUnscoped, Is.True);
        }
    }

    [Test]
    public async Task StartScopingPreview_NestedGroups_ReachEvaluationIntactAsync()
    {
        // The proposal crosses a JSON queue on its way to the worker, so a tree that arrives flattened or with its
        // combining rules lost would be evaluated as a different scope than the one submitted.
        var result = await _controller.StartSyncRuleScopingPreviewAsync(SyncRuleId, new StartSyncRuleScopingPreviewRequest
        {
            CriteriaGroups =
            [
                new SyncRuleScopingCriteriaGroupRequest
                {
                    Type = SearchGroupType.All,
                    Criteria = [new SyncRuleScopingCriterionRequest { MetaverseAttributeId = 201, StringValue = "Sales" }],
                    ChildGroups =
                    [
                        new SyncRuleScopingCriteriaGroupRequest
                        {
                            Type = SearchGroupType.Any,
                            Criteria =
                            [
                                new SyncRuleScopingCriterionRequest { MetaverseAttributeId = 202, StringValue = "UK" },
                                new SyncRuleScopingCriterionRequest { MetaverseAttributeId = 202, StringValue = "IE" }
                            ]
                        }
                    ]
                }
            ]
        });

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var proposal = QueuedProposal();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.CriteriaGroups, Has.Count.EqualTo(1));
            Assert.That(proposal.CriteriaGroups[0].Type, Is.EqualTo(SearchGroupType.All));
            Assert.That(proposal.CriteriaGroups[0].ChildGroups, Has.Count.EqualTo(1));
            Assert.That(proposal.CriteriaGroups[0].ChildGroups[0].Type, Is.EqualTo(SearchGroupType.Any));
            Assert.That(proposal.CriteriaGroups[0].ChildGroups[0].Criteria, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public async Task StartScopingPreview_RelativeDateCriterion_KeepsItsRelativeSettingsAsync()
    {
        // A relative date resolves against the moment of evaluation, so losing its unit or direction across the
        // queue would silently turn "90 days ago" into a comparison against nothing.
        var result = await _controller.StartSyncRuleScopingPreviewAsync(SyncRuleId, new StartSyncRuleScopingPreviewRequest
        {
            CriteriaGroups =
            [
                new SyncRuleScopingCriteriaGroupRequest
                {
                    Criteria =
                    [
                        new SyncRuleScopingCriterionRequest
                        {
                            MetaverseAttributeId = 203,
                            ComparisonType = SearchComparisonType.LessThan,
                            ValueMode = DateCriteriaValueMode.Relative,
                            RelativeCount = 90,
                            RelativeUnit = RelativeDateUnit.Days,
                            RelativeDirection = RelativeDateDirection.Ago
                        }
                    ]
                }
            ]
        });

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var criterion = QueuedProposal().CriteriaGroups[0].Criteria[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(criterion.ValueMode, Is.EqualTo(DateCriteriaValueMode.Relative));
            Assert.That(criterion.RelativeCount, Is.EqualTo(90));
            Assert.That(criterion.RelativeUnit, Is.EqualTo(RelativeDateUnit.Days));
            Assert.That(criterion.RelativeDirection, Is.EqualTo(RelativeDateDirection.Ago));
        }
    }

    private SyncRuleScopingProposal QueuedProposal()
    {
        var task = _queuedWorkerTasks.OfType<ConfigurationChangePreviewWorkerTask>().SingleOrDefault();
        Assert.That(task, Is.Not.Null, "the preview should have been queued for evaluation");
        return JsonSerializer.Deserialize<SyncRuleScopingProposal>(task!.ProposedConfigurationPayload)!;
    }
}
