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
/// Tests for the CreateSyncRuleAsync endpoint. Covers persistence of the
/// optional Description field on newly created Synchronisation Rules.
/// </summary>
[TestFixture]
public class SynchronisationControllerCreateSyncRuleTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
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
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
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

        // Common arrangement for a valid create request: the Connected System, its object
        // types and the Metaverse Object Type all resolve successfully.
        var connectedSystem = new ConnectedSystem { Id = 100, Name = "Test CS", ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem };
        var csObjectType = new ConnectedSystemObjectType { Id = 200, Name = "user" };
        var mvObjectType = new MetaverseObjectType { Id = 300, Name = "User" };
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(100, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypesAsync(100)).ReturnsAsync([csObjectType]);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(300, It.IsAny<bool>())).ReturnsAsync(mvObjectType);
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        // Capture the created Synchronisation Rule and serve it back to the controller's
        // post-create retrieval so CreatedAtRoute can build the response header.
        SyncRule? createdSyncRule = null;
        _mockConnectedSystemRepo.Setup(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>()))
            .Callback<SyncRule>(sr => createdSyncRule = sr)
            .Returns(Task.CompletedTask);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(It.IsAny<int>())).ReturnsAsync(() => createdSyncRule);
    }

    private static CreateSyncRuleRequest BuildCreateRequest()
    {
        return new CreateSyncRuleRequest
        {
            Name = "Import Rule",
            ConnectedSystemId = 100,
            ConnectedSystemObjectTypeId = 200,
            MetaverseObjectTypeId = 300,
            Direction = SyncRuleDirection.Import
        };
    }

    [Test]
    public async Task CreateSyncRuleAsync_WithDescription_PersistsValue()
    {
        var request = BuildCreateRequest();
        request.Description = "Flows HR user data into the Metaverse.";

        var result = await _controller.CreateSyncRuleAsync(request);

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        _mockConnectedSystemRepo.Verify(r => r.CreateSyncRuleAsync(
            It.Is<SyncRule>(sr => sr.Description == "Flows HR user data into the Metaverse.")), Times.Once);
    }

    [Test]
    public async Task CreateSyncRuleAsync_WithoutDescription_LeavesDescriptionNull()
    {
        var request = BuildCreateRequest();

        var result = await _controller.CreateSyncRuleAsync(request);

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        _mockConnectedSystemRepo.Verify(r => r.CreateSyncRuleAsync(
            It.Is<SyncRule>(sr => sr.Description == null)), Times.Once);
    }
}
