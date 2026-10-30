// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Expressions;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the UpdateSyncRuleAsync endpoint. Covers the scoping-related
/// action fields (InboundOutOfScopeAction, OutboundDeprovisionAction) that
/// integration test Scenario 010 needs to configure programmatically, and the
/// partial-update semantics of the optional Description field.
/// </summary>
[TestFixture]
public class SynchronisationControllerUpdateSyncRuleTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<ILogger<SynchronisationController>> _mockLogger = null!;
    private Mock<ICredentialProtectionService> _mockCredentialProtection = null!;
    private IExpressionEvaluator _expressionEvaluator = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        // Saves read the configuration test cases covering what they save (the save gate, #2055 Phase 4b).
        _mockRepository.Setup(r => r.ConfigurationTests).Returns(new EmptyConfigurationTestRepository());
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockMetaverseRepo.StubExportReviewReads();
        _mockConnectedSystemRepo.StubConfigurationChangeSynchronisationReads();
        _mockRepository.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());
        // The import rules of a Metaverse Object Type, read to find Attribute Flows deriving Metaverse attributes that a
        // change leaves with a missing input (#1750, FR 3): none here unless a test says otherwise.
        _mockConnectedSystemRepo.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>())).ReturnsAsync(() => []);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockLogger = new Mock<ILogger<SynchronisationController>>();
        _mockCredentialProtection = new Mock<ICredentialProtectionService>();
        _expressionEvaluator = new DynamicExpressoEvaluator();
        // A sync repository is passed, as every host passes one: saving an export Synchronisation Rule checks the export
        // queue for changes the save left without authority, which reads through it. An empty in-memory store finds nothing.
        _application = new JimApplication(_mockRepository.Object, syncRepository: new JIM.InMemoryData.SyncRepository());
        _controller = new SynchronisationController(_mockLogger.Object, _application, _expressionEvaluator, _mockCredentialProtection.Object);

        var apiKeyId = Guid.NewGuid();
        var apiKey = new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(apiKey);

        // Every whole-rule save reads the rule's persisted import mapping targets so it can reconcile attribute
        // priority for whatever the save changed (#1199). These tests change rule properties, never mappings, so the
        // rule contributes nothing and no priority list is touched.
        _mockConnectedSystemRepo
            .Setup(r => r.GetImportMappingTargetMetaverseAttributesAsync(It.IsAny<int>()))
            .ReturnsAsync(new Dictionary<int, int>());

        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new(ClaimTypes.Name, "TestApiKey")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    private static SyncRule BuildImportRule(int id)
    {
        var cs = new ConnectedSystem { Id = 100, Name = "Test CS", ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem };
        var cot = new ConnectedSystemObjectType { Id = 200, Name = "user" };
        var mot = new MetaverseObjectType { Id = 300, Name = "User" };
        return new SyncRule
        {
            Id = id,
            Name = "Import Rule",
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = 100,
            ConnectedSystem = cs,
            ConnectedSystemObjectTypeId = 200,
            ConnectedSystemObjectType = cot,
            MetaverseObjectTypeId = 300,
            MetaverseObjectType = mot,
            InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect,
            OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect
        };
    }

    private static SyncRule BuildExportRule(int id)
    {
        var cs = new ConnectedSystem { Id = 100, Name = "Test CS", ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem };
        var cot = new ConnectedSystemObjectType { Id = 200, Name = "user" };
        var mot = new MetaverseObjectType { Id = 300, Name = "User" };
        return new SyncRule
        {
            Id = id,
            Name = "Export Rule",
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = 100,
            ConnectedSystem = cs,
            ConnectedSystemObjectTypeId = 200,
            ConnectedSystemObjectType = cot,
            MetaverseObjectTypeId = 300,
            MetaverseObjectType = mot,
            InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect,
            OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect
        };
    }

    [Test]
    public async Task UpdateSyncRuleAsync_WithInboundOutOfScopeAction_PersistsValue()
    {
        var syncRule = BuildImportRule(1);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(1)).ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        var request = new UpdateSyncRuleRequest
        {
            InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined
        };

        var result = await _controller.UpdateSyncRuleAsync(1, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleAsync(
            It.Is<SyncRule>(sr => sr.InboundOutOfScopeAction == InboundOutOfScopeAction.RemainJoined)), Times.Once);
    }

    [Test]
    public async Task UpdateSyncRuleAsync_WithOutboundDeprovisionAction_PersistsValue()
    {
        var syncRule = BuildExportRule(2);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(2)).ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        var request = new UpdateSyncRuleRequest
        {
            OutboundDeprovisionAction = OutboundDeprovisionAction.Delete
        };

        var result = await _controller.UpdateSyncRuleAsync(2, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleAsync(
            It.Is<SyncRule>(sr => sr.OutboundDeprovisionAction == OutboundDeprovisionAction.Delete)), Times.Once);
    }

    [Test]
    public async Task UpdateSyncRuleAsync_WithoutScopeActionFields_PreservesExistingValues()
    {
        var syncRule = BuildImportRule(3);
        syncRule.InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined;
        syncRule.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(3)).ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        var request = new UpdateSyncRuleRequest
        {
            Name = "Renamed Rule"
        };

        var result = await _controller.UpdateSyncRuleAsync(3, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleAsync(
            It.Is<SyncRule>(sr =>
                sr.InboundOutOfScopeAction == InboundOutOfScopeAction.RemainJoined &&
                sr.OutboundDeprovisionAction == OutboundDeprovisionAction.Delete)), Times.Once);
    }

    [Test]
    public async Task UpdateSyncRuleAsync_WithDescription_PersistsValue()
    {
        var syncRule = BuildImportRule(4);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(4)).ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        var request = new UpdateSyncRuleRequest
        {
            Description = "Flows HR user data into the Metaverse."
        };

        var result = await _controller.UpdateSyncRuleAsync(4, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleAsync(
            It.Is<SyncRule>(sr => sr.Description == "Flows HR user data into the Metaverse.")), Times.Once);
    }

    [Test]
    public async Task UpdateSyncRuleAsync_WithoutDescription_PreservesExistingValue()
    {
        var syncRule = BuildImportRule(5);
        syncRule.Description = "Existing description.";
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(5)).ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        var request = new UpdateSyncRuleRequest
        {
            Name = "Renamed Rule"
        };

        var result = await _controller.UpdateSyncRuleAsync(5, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleAsync(
            It.Is<SyncRule>(sr => sr.Description == "Existing description.")), Times.Once);
    }

    [Test]
    public async Task UpdateSyncRuleAsync_WithEmptyDescription_ClearsValue()
    {
        var syncRule = BuildImportRule(6);
        syncRule.Description = "Existing description.";
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(6)).ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        var request = new UpdateSyncRuleRequest
        {
            Description = ""
        };

        var result = await _controller.UpdateSyncRuleAsync(6, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleAsync(
            It.Is<SyncRule>(sr => sr.Description == null)), Times.Once);
    }
    #region Deletion source warning (#1256)

    /// <summary>
    /// Arranges an import rule from Connected System 100 ("Test CS") into Metaverse Object Type 300, a type deleted When
    /// Authoritative Source Disconnected whose only authoritative source is Connected System 1 ("HR").
    /// </summary>
    private SyncRule ArrangeRuleIntoAuthoritativeSourceType(int id, bool projects)
    {
        var syncRule = BuildImportRule(id);
        syncRule.Enabled = true;
        syncRule.ProjectToMetaverse = projects;
        syncRule.MetaverseObjectType.DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected;
        syncRule.MetaverseObjectType.DeletionTriggerConnectedSystemIds = [1];
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(id)).ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemNamesAsync())
            .ReturnsAsync(new Dictionary<int, string> { [1] = "HR", [100] = "Test CS" });
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(300, It.IsAny<bool>()))
            .ReturnsAsync(syncRule.MetaverseObjectType);
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        return syncRule;
    }

    [Test]
    public async Task UpdateSyncRuleAsync_ProjectionSwitchedOnFromUnlistedSystem_ReturnsDeletionSourceWarningAsync()
    {
        ArrangeRuleIntoAuthoritativeSourceType(20, projects: false);

        var result = await _controller.UpdateSyncRuleAsync(20, new UpdateSyncRuleRequest { ProjectToMetaverse = true }) as OkObjectResult;

        var response = result?.Value as SyncRuleSaveResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.DeletionSourceWarning, Does.Contain("Test CS").And.Contain("HR"),
            "switching projection on for a system that is not an authoritative source must be reported, naming both");
    }

    [Test]
    public async Task UpdateSyncRuleAsync_RuleAlreadyProjecting_ReturnsNoDeletionSourceWarningAsync()
    {
        ArrangeRuleIntoAuthoritativeSourceType(21, projects: true);

        var result = await _controller.UpdateSyncRuleAsync(21, new UpdateSyncRuleRequest { Name = "Renamed" }) as OkObjectResult;

        var response = result?.Value as SyncRuleSaveResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.DeletionSourceWarning, Is.Null, "re-saving a rule that already projected must not repeat the warning");
    }

    #endregion

    #region Awaiting synchronisation (#2615)

    [Test]
    public async Task UpdateSyncRuleAsync_EnabledImportRuleSave_ReportsTheTypeItRecordedWithItsCostAsync()
    {
        var syncRule = BuildImportRule(30);
        syncRule.Enabled = true;
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(30)).ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(100, 200, null)).ReturnsAsync(30_000);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(100, null)).ReturnsAsync(100_000);
        _mockActivityRepo.Setup(r => r.GetLatestCompletedFullSynchronisationAsync(100))
            .ReturnsAsync(new Activity { ExecutionTime = TimeSpan.FromMinutes(50) });

        var result = await _controller.UpdateSyncRuleAsync(30, new UpdateSyncRuleRequest { InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined }) as OkObjectResult;

        var response = result?.Value as SyncRuleSaveResponse;
        Assert.That(response, Is.Not.Null);
        var awaiting = response!.AwaitingSynchronisation.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(awaiting.ConnectedSystemId, Is.EqualTo(100));
            Assert.That(awaiting.ConnectedSystemName, Is.EqualTo("Test CS"));
            Assert.That(awaiting.ConnectedSystemObjectTypeId, Is.EqualTo(200));
            Assert.That(awaiting.ObjectTypeName, Is.EqualTo("user"));
            Assert.That(awaiting.ObjectCount, Is.EqualTo(30_000));
            // 100,000 objects in 50 minutes is 2,000 a minute, so 30,000 take 15 minutes.
            Assert.That(awaiting.EstimatedDuration, Is.EqualTo(TimeSpan.FromMinutes(15)));
        }
    }

    [Test]
    public async Task UpdateSyncRuleAsync_ImportRuleOnASystemThatHasNeverFullySynchronised_ReportsTheTypeWithoutADurationAsync()
    {
        var syncRule = BuildImportRule(31);
        syncRule.Enabled = true;
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(31)).ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(100, 200, null)).ReturnsAsync(12);

        var result = await _controller.UpdateSyncRuleAsync(31, new UpdateSyncRuleRequest { InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined }) as OkObjectResult;

        var awaiting = (result?.Value as SyncRuleSaveResponse)!.AwaitingSynchronisation.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(awaiting.ObjectCount, Is.EqualTo(12));
            Assert.That(awaiting.EstimatedDuration, Is.Null, "with no completed Full Synchronisation there is no speed to estimate from");
        }
    }

    [Test]
    public async Task UpdateSyncRuleAsync_ExportRuleSave_ReportsNothingAwaitingSynchronisationAsync()
    {
        var syncRule = BuildExportRule(32);
        syncRule.Enabled = true;
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(32)).ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        var result = await _controller.UpdateSyncRuleAsync(32, new UpdateSyncRuleRequest { OutboundDeprovisionAction = OutboundDeprovisionAction.Delete }) as OkObjectResult;

        var response = result?.Value as SyncRuleSaveResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.AwaitingSynchronisation, Is.Empty,
            "an export rule's change reaches existing objects through the review the save flags, not through a type awaiting synchronisation");
    }

    [Test]
    public async Task UpdateSyncRuleAsync_ImportRuleSwitchedOffWhileOff_ReportsNothingAwaitingSynchronisationAsync()
    {
        var syncRule = BuildImportRule(33);
        syncRule.Enabled = false;
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(33)).ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        var result = await _controller.UpdateSyncRuleAsync(33, new UpdateSyncRuleRequest { Description = "Still off." }) as OkObjectResult;

        var response = result?.Value as SyncRuleSaveResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.AwaitingSynchronisation, Is.Empty, "a rule that is off and stays off does nothing for the objects of its type");
    }

    #endregion
}
