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
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST create endpoints for Object Matching Rules refuse a rule whose scope the Connected System's matching
/// mode would never consult (#1569). Before this, POST sync-rules/{id}/matching-rules happily created a
/// Synchronisation Rule scoped rule on a system in simple matching mode; the rule was silently inert and
/// synchronisation joined nothing, with no error anywhere. The refusal is a 400 naming the active mode and the
/// remedy, which is also what the PowerShell cmdlets surface. These tests also pin that the application layer's
/// InvalidDataException reaches the caller as a 400 rather than an unhandled 500.
/// </summary>
[TestFixture]
public class SynchronisationControllerObjectMatchingRuleTests
{
    private const int ConnectedSystemId = 2;
    private const int CsoTypeId = 9;
    private const int MvoTypeId = 3;
    private const int EmployeeIdAttributeId = 101;
    private const int EmployeeIdMetaverseAttributeId = 201;
    private const int SyncRuleId = 40;

    private Mock<IRepository> _repository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IApiKeyRepository> _apiKeyRepo = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private ConnectedSystem _connectedSystem = null!;
    private ConnectedSystemObjectType _csoType = null!;
    private SyncRule _syncRule = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _apiKeyRepo = new Mock<IApiKeyRepository>();

        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repository.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);
        _repository.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repository.Setup(r => r.ApiKeys).Returns(_apiKeyRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _connectedSystemRepo.Setup(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>())).Returns(Task.CompletedTask);

        _csoType = new ConnectedSystemObjectType
        {
            Id = CsoTypeId,
            Name = "User",
            ConnectedSystemId = ConnectedSystemId,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = EmployeeIdAttributeId, Name = "employeeID", Type = AttributeDataType.Text }
            ]
        };

        _connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "HR",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem,
            ObjectTypes = [_csoType]
        };
        _csoType.ConnectedSystem = _connectedSystem;

        _syncRule = new SyncRule
        {
            Id = SyncRuleId,
            Name = "Import Users",
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystem = _connectedSystem,
            ConnectedSystemObjectTypeId = CsoTypeId,
            ConnectedSystemObjectType = _csoType,
            MetaverseObjectTypeId = MvoTypeId
        };

        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(() => _connectedSystem);
        _connectedSystemRepo.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(() => _syncRule);

        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(MvoTypeId, false)).ReturnsAsync(
            new MetaverseObjectType { Id = MvoTypeId, Name = "Person" });
        _metaverseRepo.Setup(r => r.GetMetaverseAttributesAsync(It.IsAny<bool>())).ReturnsAsync(
        [
            new MetaverseAttribute { Id = EmployeeIdMetaverseAttributeId, Name = "Employee ID", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued }
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
    public async Task CreateSyncRuleObjectMatchingRule_SystemInSimpleMode_ReturnsBadRequestNamingTheModeAsync()
    {
        _connectedSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem;

        var result = await _controller.CreateSyncRuleObjectMatchingRuleAsync(SyncRuleId, SyncRuleScopedRequest());

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>(),
            "a Synchronisation Rule scoped rule is never consulted in simple matching mode and must be refused");
        var error = ((BadRequestObjectResult)result).Value as ApiErrorResponse;
        Assert.That(error!.Message, Does.Contain("simple matching mode"));
        _connectedSystemRepo.Verify(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()), Times.Never);
    }

    [Test]
    public async Task CreateSyncRuleObjectMatchingRule_SystemInAdvancedMode_CreatesTheRuleAsync()
    {
        _connectedSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;

        var result = await _controller.CreateSyncRuleObjectMatchingRuleAsync(SyncRuleId, SyncRuleScopedRequest());

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        _connectedSystemRepo.Verify(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()), Times.Once);
    }

    [Test]
    public async Task CreateObjectMatchingRule_SystemInAdvancedMode_ReturnsBadRequestNamingTheModeAsync()
    {
        _connectedSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;

        var result = await _controller.CreateObjectMatchingRuleAsync(ConnectedSystemId, TypeScopedRequest());

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>(),
            "a type-scoped rule is never consulted in advanced matching mode and must be refused");
        var error = ((BadRequestObjectResult)result).Value as ApiErrorResponse;
        Assert.That(error!.Message, Does.Contain("advanced matching mode"));
        _connectedSystemRepo.Verify(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()), Times.Never);
    }

    [Test]
    public async Task CreateObjectMatchingRule_SystemInSimpleMode_CreatesTheRuleAsync()
    {
        _connectedSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem;

        var result = await _controller.CreateObjectMatchingRuleAsync(ConnectedSystemId, TypeScopedRequest());

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        _connectedSystemRepo.Verify(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()), Times.Once);
    }

    [Test]
    public async Task SwitchObjectMatchingMode_ApiKeyAuthenticated_SwitchesInsteadOfFailingAttributionAsync()
    {
        // The endpoint's auth gate accepts an API key, but it previously handed the application layer a null user,
        // and activity attribution (rightly) refuses an unattributed Activity. Switch-JIMMatchingMode therefore
        // never worked under API key authentication, which is how automation authenticates.
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync(ConnectedSystemId, true)).ReturnsAsync([]);
        _connectedSystemRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);

        var result = await _controller.SwitchObjectMatchingModeAsync(ConnectedSystemId,
            new SwitchObjectMatchingModeRequest { Mode = ObjectMatchingRuleMode.SyncRule });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var switchResult = (JIM.Models.Staging.DTOs.ObjectMatchingModeSwitchResult)((OkObjectResult)result).Value!;
        Assert.That(switchResult.Success, Is.True);
        Assert.That(switchResult.NewMode, Is.EqualTo(ObjectMatchingRuleMode.SyncRule));
    }

    private static CreateSyncRuleObjectMatchingRuleRequest SyncRuleScopedRequest() => new()
    {
        TargetMetaverseAttributeId = EmployeeIdMetaverseAttributeId,
        Sources = [new CreateObjectMatchingRuleSourceRequest { Order = 0, ConnectedSystemAttributeId = EmployeeIdAttributeId }]
    };

    private static CreateObjectMatchingRuleRequest TypeScopedRequest() => new()
    {
        ConnectedSystemObjectTypeId = CsoTypeId,
        MetaverseObjectTypeId = MvoTypeId,
        TargetMetaverseAttributeId = EmployeeIdMetaverseAttributeId,
        Sources = [new CreateObjectMatchingRuleSourceRequest { Order = 0, ConnectedSystemAttributeId = EmployeeIdAttributeId }]
    };
}
