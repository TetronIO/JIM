// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Models.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Proves the REST API refuses to let a credential attribute become managed or be wired into an Attribute Flow.
/// This is the surface that matters most: both PowerShell and the portal's automation callers go through it, so a
/// UI-only guard would be trivially bypassable.
/// </summary>
[TestFixture]
public class SynchronisationControllerCredentialAttributeTests
{
    private const int ConnectedSystemId = 1;
    private const int ObjectTypeId = 7;
    private const int CredentialAttributeId = 20;
    private const int OrdinaryAttributeId = 21;

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
        _mockConnectedSystemRepo
            .Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<SyncRuleMapping>());

        _mockLogger = new Mock<ILogger<SynchronisationController>>();
        _mockCredentialProtection = new Mock<ICredentialProtectionService>();
        _expressionEvaluator = new DynamicExpressoEvaluator();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new SynchronisationController(_mockLogger.Object, _application, _expressionEvaluator, _mockCredentialProtection.Object);

        var apiKeyId = Guid.NewGuid();
        var testApiKey = new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(testApiKey);

        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new(ClaimTypes.Name, "TestApiKey")
        };
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey")) }
        };

        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
    }

    #region Attribute selection

    [Test]
    public async Task UpdateConnectedSystemAttributeAsync_SelectingCredentialAttribute_ReturnsBadRequestAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem { Id = ConnectedSystemId, Name = "Test System" };
        var attribute = CreateAttribute(CredentialAttributeId, "unicodePwd");
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeAsync(CredentialAttributeId)).ReturnsAsync(attribute);

        // Act
        var result = await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, CredentialAttributeId,
            new UpdateConnectedSystemAttributeRequest { Selected = true });

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(attribute.Selected, Is.False, "The attribute must not have been mutated before the rejection.");
        _mockConnectedSystemRepo.Verify(r => r.UpdateAttributeAsync(It.IsAny<ConnectedSystemObjectTypeAttribute>()), Times.Never);
    }

    [Test]
    public async Task UpdateConnectedSystemAttributeAsync_MakingCredentialAttributeTheExternalId_ReturnsBadRequestAsync()
    {
        // Arrange: IsExternalId force-selects the attribute, so it is a second route to the same outcome.
        var connectedSystem = new ConnectedSystem { Id = ConnectedSystemId, Name = "Test System" };
        var attribute = CreateAttribute(CredentialAttributeId, "unicodePwd");
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeAsync(CredentialAttributeId)).ReturnsAsync(attribute);

        // Act
        var result = await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, CredentialAttributeId,
            new UpdateConnectedSystemAttributeRequest { IsExternalId = true });

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdateConnectedSystemAttributeAsync_SelectingOrdinaryAttribute_ReturnsOkAsync()
    {
        // Arrange: the control case. pwdLastSet matches the credential-like-name warning heuristic but must remain
        // fully selectable.
        var connectedSystem = new ConnectedSystem { Id = ConnectedSystemId, Name = "Test System" };
        var attribute = CreateAttribute(OrdinaryAttributeId, "pwdLastSet");
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeAsync(OrdinaryAttributeId)).ReturnsAsync(attribute);

        // Act
        var result = await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, OrdinaryAttributeId,
            new UpdateConnectedSystemAttributeRequest { Selected = true });

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(attribute.Selected, Is.True);
    }

    [Test]
    public async Task BulkUpdateConnectedSystemAttributesAsync_SelectingCredentialAttribute_ReportsAnErrorAndDoesNotSelectItAsync()
    {
        // Arrange: the bulk endpoint is the one the portal's schema tab drives, so it needs the same guard.
        var connectedSystem = new ConnectedSystem { Id = ConnectedSystemId, Name = "Test System" };
        var credentialAttribute = CreateAttribute(CredentialAttributeId, "unicodePwd");
        var ordinaryAttribute = CreateAttribute(OrdinaryAttributeId, "displayName");
        var objectType = new ConnectedSystemObjectType
        {
            Id = ObjectTypeId,
            Name = "User",
            ConnectedSystemId = ConnectedSystemId,
            Attributes = [credentialAttribute, ordinaryAttribute]
        };
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(ObjectTypeId)).ReturnsAsync(objectType);

        var request = new BulkUpdateConnectedSystemAttributesRequest
        {
            Attributes = new Dictionary<int, UpdateConnectedSystemAttributeRequest>
            {
                { CredentialAttributeId, new UpdateConnectedSystemAttributeRequest { Selected = true } },
                { OrdinaryAttributeId, new UpdateConnectedSystemAttributeRequest { Selected = true } }
            }
        };

        // Act
        var result = await _controller.BulkUpdateConnectedSystemAttributesAsync(ConnectedSystemId, ObjectTypeId, request);

        // Assert
        var okResult = result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as BulkUpdateConnectedSystemAttributesResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Errors, Is.Not.Null);
        Assert.That(response.Errors!.Any(e => e.AttributeId == CredentialAttributeId), Is.True);
        Assert.That(credentialAttribute.Selected, Is.False);
        Assert.That(ordinaryAttribute.Selected, Is.True, "The rest of the batch must still apply.");
    }

    #endregion

    #region Attribute Flow

    [Test]
    public async Task CreateSyncRuleMappingAsync_ExportTargetIsCredentialAttribute_ReturnsBadRequestAsync()
    {
        // Arrange
        const int syncRuleId = 1;
        var syncRule = new SyncRule { Id = syncRuleId, Name = "Export Rule", Direction = SyncRuleDirection.Export, ConnectedSystemObjectTypeId = ObjectTypeId };
        var credentialAttribute = CreateAttribute(CredentialAttributeId, "unicodePwd");
        var metaverseAttribute = new MetaverseAttribute { Id = 5, Name = "displayName", Type = AttributeDataType.Text };
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(syncRuleId)).ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeAsync(CredentialAttributeId)).ReturnsAsync(credentialAttribute);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(5, It.IsAny<bool>())).ReturnsAsync(metaverseAttribute);

        var request = new CreateSyncRuleMappingRequest
        {
            TargetConnectedSystemAttributeId = CredentialAttributeId,
            Sources = [new CreateSyncRuleMappingSourceRequest { Order = 0, MetaverseAttributeId = 5 }]
        };

        // Act
        var result = await _controller.CreateSyncRuleMappingAsync(syncRuleId, request);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockConnectedSystemRepo.Verify(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_ImportSourceIsCredentialAttribute_ReturnsBadRequestAsync()
    {
        // Arrange
        const int syncRuleId = 2;
        var syncRule = new SyncRule { Id = syncRuleId, Name = "Import Rule", Direction = SyncRuleDirection.Import, ConnectedSystemObjectTypeId = ObjectTypeId };
        var credentialAttribute = CreateAttribute(CredentialAttributeId, "unicodePwd");
        var metaverseAttribute = new MetaverseAttribute { Id = 5, Name = "displayName", Type = AttributeDataType.Text };
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(syncRuleId)).ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeAsync(CredentialAttributeId)).ReturnsAsync(credentialAttribute);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(5, It.IsAny<bool>())).ReturnsAsync(metaverseAttribute);

        var request = new CreateSyncRuleMappingRequest
        {
            TargetMetaverseAttributeId = 5,
            Sources = [new CreateSyncRuleMappingSourceRequest { Order = 0, ConnectedSystemAttributeId = CredentialAttributeId }]
        };

        // Act
        var result = await _controller.CreateSyncRuleMappingAsync(syncRuleId, request);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockConnectedSystemRepo.Verify(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    #endregion

    private static ConnectedSystemObjectTypeAttribute CreateAttribute(int id, string name)
    {
        return new ConnectedSystemObjectTypeAttribute
        {
            Id = id,
            Name = name,
            Type = AttributeDataType.Text,
            ConnectedSystemObjectType = new ConnectedSystemObjectType
            {
                Id = ObjectTypeId,
                Name = "User",
                ConnectedSystemId = ConnectedSystemId
            }
        };
    }
}
