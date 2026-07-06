// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the UpdateSyncRuleAsync endpoint. Covers the scoping-related
/// action fields (InboundOutOfScopeAction, OutboundDeprovisionAction) that
/// integration test Scenario 10 needs to configure programmatically, and the
/// partial-update semantics of the optional Description field.
/// </summary>
[TestFixture]
public class SynchronisationControllerUpdateSyncRuleTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
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
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockLogger = new Mock<ILogger<SynchronisationController>>();
        _mockCredentialProtection = new Mock<ICredentialProtectionService>();
        _expressionEvaluator = new DynamicExpressoEvaluator();
        _application = new JimApplication(_mockRepository.Object);
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
}
