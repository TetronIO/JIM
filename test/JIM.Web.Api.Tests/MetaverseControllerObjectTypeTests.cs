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
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for Metaverse Object Type endpoints.
/// </summary>
[TestFixture]
public class MetaverseControllerObjectTypeTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<ILogger<MetaverseController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private MetaverseController _controller = null!;
    private JIM.Models.Security.ApiKey _testApiKey = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockLogger = new Mock<ILogger<MetaverseController>>();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new MetaverseController(_mockLogger.Object, _application);

        // Mirror MetaverseControllerAttributeTests: the controller's activity-creating
        // paths (Create*) require an attributable API key principal. Wire one up so the
        // ApiKey overloads on MetaverseServer are taken and ValidateActivity passes.
        var apiKeyId = Guid.NewGuid();
        _testApiKey = new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(_testApiKey);

        var claims = new List<Claim>
        {
            new Claim("auth_method", "api_key"),
            new Claim(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new Claim(ClaimTypes.Name, "TestApiKey")
        };
        var identity = new ClaimsIdentity(claims, "ApiKey");
        var principal = new ClaimsPrincipal(identity);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    #region UpdateObjectTypeAsync tests

    [Test]
    public async Task UpdateObjectTypeAsync_WithValidId_ReturnsOkResult()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionRule = MetaverseObjectDeletionRule.Manual,
            DeletionGracePeriod = null
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);
        _mockMetaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()))
            .Returns(Task.CompletedTask);

        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            DeletionGracePeriod = TimeSpan.FromDays(30)
        };

        var result = await _controller.UpdateObjectTypeAsync(1, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task UpdateObjectTypeAsync_WithInvalidId_ReturnsNotFound()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(999, false))
            .ReturnsAsync((MetaverseObjectType?)null);

        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected
        };

        var result = await _controller.UpdateObjectTypeAsync(999, request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateObjectTypeAsync_UpdatesDeletionRule()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionRule = MetaverseObjectDeletionRule.Manual
        };

        MetaverseObjectType? capturedObjectType = null;
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);
        _mockMetaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()))
            .Callback<MetaverseObjectType>(ot => capturedObjectType = ot)
            .Returns(Task.CompletedTask);

        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected
        };

        await _controller.UpdateObjectTypeAsync(1, request);

        Assert.That(capturedObjectType, Is.Not.Null);
        Assert.That(capturedObjectType!.DeletionRule, Is.EqualTo(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected));
    }

    [Test]
    public async Task UpdateObjectTypeAsync_UpdatesDeletionGracePeriod()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionGracePeriod = null
        };

        MetaverseObjectType? capturedObjectType = null;
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);
        _mockMetaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()))
            .Callback<MetaverseObjectType>(ot => capturedObjectType = ot)
            .Returns(Task.CompletedTask);

        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionGracePeriod = TimeSpan.FromDays(7)
        };

        await _controller.UpdateObjectTypeAsync(1, request);

        Assert.That(capturedObjectType, Is.Not.Null);
        Assert.That(capturedObjectType!.DeletionGracePeriod, Is.EqualTo(TimeSpan.FromDays(7)));
    }

    [Test]
    public async Task UpdateObjectTypeAsync_ZeroGracePeriod_SetsToNull()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionGracePeriod = TimeSpan.FromDays(30)
        };

        MetaverseObjectType? capturedObjectType = null;
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);
        _mockMetaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()))
            .Callback<MetaverseObjectType>(ot => capturedObjectType = ot)
            .Returns(Task.CompletedTask);

        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionGracePeriod = TimeSpan.Zero
        };

        await _controller.UpdateObjectTypeAsync(1, request);

        Assert.That(capturedObjectType, Is.Not.Null);
        Assert.That(capturedObjectType!.DeletionGracePeriod, Is.Null);
    }

    [Test]
    public async Task UpdateObjectTypeAsync_NegativeGracePeriod_ReturnsBadRequest()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users"
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);

        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionGracePeriod = TimeSpan.FromDays(-1)
        };

        var result = await _controller.UpdateObjectTypeAsync(1, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdateObjectTypeAsync_UpdatesDeletionTriggerConnectedSystemIds()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionTriggerConnectedSystemIds = new List<int>()
        };

        var connectedSystem1 = new JIM.Models.Staging.ConnectedSystem { Id = 1, Name = "HR" };
        var connectedSystem2 = new JIM.Models.Staging.ConnectedSystem { Id = 2, Name = "AD" };

        MetaverseObjectType? capturedObjectType = null;
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>()))
            .ReturnsAsync(connectedSystem1);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(2, It.IsAny<bool>()))
            .ReturnsAsync(connectedSystem2);
        _mockMetaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()))
            .Callback<MetaverseObjectType>(ot => capturedObjectType = ot)
            .Returns(Task.CompletedTask);

        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionTriggerConnectedSystemIds = new List<int> { 1, 2 }
        };

        await _controller.UpdateObjectTypeAsync(1, request);

        Assert.That(capturedObjectType, Is.Not.Null);
        Assert.That(capturedObjectType!.DeletionTriggerConnectedSystemIds, Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public async Task UpdateObjectTypeAsync_InvalidConnectedSystemId_ReturnsBadRequest()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users"
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(999, It.IsAny<bool>()))
            .ReturnsAsync((JIM.Models.Staging.ConnectedSystem?)null);

        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionTriggerConnectedSystemIds = new List<int> { 999 }
        };

        var result = await _controller.UpdateObjectTypeAsync(1, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdateObjectTypeAsync_ReturnsUpdatedObjectType()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionRule = MetaverseObjectDeletionRule.Manual,
            DeletionGracePeriod = null,
            DeletionTriggerConnectedSystemIds = new List<int>()
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);
        _mockMetaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()))
            .Returns(Task.CompletedTask);

        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            DeletionGracePeriod = TimeSpan.FromDays(30)
        };

        var result = await _controller.UpdateObjectTypeAsync(1, request) as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        var dto = result!.Value as MetaverseObjectTypeDetailDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Id, Is.EqualTo(1));
        Assert.That(dto.Name, Is.EqualTo("User"));
        Assert.That(dto.DeletionRule, Is.EqualTo(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected));
        Assert.That(dto.DeletionGracePeriod, Is.EqualTo(TimeSpan.FromDays(30)));
    }

    [Test]
    public async Task UpdateObjectTypeAsync_SubDayGracePeriod_SetsCorrectly()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionGracePeriod = null
        };

        MetaverseObjectType? capturedObjectType = null;
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);
        _mockMetaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()))
            .Callback<MetaverseObjectType>(ot => capturedObjectType = ot)
            .Returns(Task.CompletedTask);

        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionGracePeriod = TimeSpan.FromMinutes(1)
        };

        var result = await _controller.UpdateObjectTypeAsync(1, request) as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        Assert.That(capturedObjectType, Is.Not.Null);
        Assert.That(capturedObjectType!.DeletionGracePeriod, Is.EqualTo(TimeSpan.FromMinutes(1)));

        var dto = result!.Value as MetaverseObjectTypeDetailDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.DeletionGracePeriod, Is.EqualTo(TimeSpan.FromMinutes(1)));
    }

    [Test]
    public async Task UpdateObjectTypeAsync_MixedDaysHoursMinutesGracePeriod_SetsCorrectly()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionGracePeriod = null
        };

        MetaverseObjectType? capturedObjectType = null;
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);
        _mockMetaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()))
            .Callback<MetaverseObjectType>(ot => capturedObjectType = ot)
            .Returns(Task.CompletedTask);

        // 2 days, 6 hours, 30 minutes
        var gracePeriod = new TimeSpan(2, 6, 30, 0);
        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionGracePeriod = gracePeriod
        };

        var result = await _controller.UpdateObjectTypeAsync(1, request) as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        Assert.That(capturedObjectType, Is.Not.Null);
        Assert.That(capturedObjectType!.DeletionGracePeriod, Is.EqualTo(gracePeriod));

        var dto = result!.Value as MetaverseObjectTypeDetailDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.DeletionGracePeriod, Is.EqualTo(gracePeriod));
    }

    [Test]
    public async Task UpdateObjectTypeAsync_NullGracePeriodInRequest_DoesNotOverwriteExisting()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionGracePeriod = TimeSpan.FromDays(7)
        };

        MetaverseObjectType? capturedObjectType = null;
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);
        _mockMetaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()))
            .Callback<MetaverseObjectType>(ot => capturedObjectType = ot)
            .Returns(Task.CompletedTask);

        // Request does NOT include DeletionGracePeriod - should leave existing value untouched
        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionRule = MetaverseObjectDeletionRule.Manual
        };

        await _controller.UpdateObjectTypeAsync(1, request);

        Assert.That(capturedObjectType, Is.Not.Null);
        Assert.That(capturedObjectType!.DeletionGracePeriod, Is.EqualTo(TimeSpan.FromDays(7)));
    }

    [Test]
    public async Task UpdateObjectTypeAsync_WhenAuthoritativeSourceDisconnected_WithTriggerIds_Succeeds()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionRule = MetaverseObjectDeletionRule.Manual,
            DeletionTriggerConnectedSystemIds = new List<int>()
        };

        var connectedSystem = new JIM.Models.Staging.ConnectedSystem { Id = 1, Name = "HR" };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>()))
            .ReturnsAsync(connectedSystem);
        _mockMetaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()))
            .Returns(Task.CompletedTask);

        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            DeletionTriggerConnectedSystemIds = new List<int> { 1 }
        };

        var result = await _controller.UpdateObjectTypeAsync(1, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task UpdateObjectTypeAsync_WhenAuthoritativeSourceDisconnected_WithoutTriggerIds_ReturnsBadRequest()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionRule = MetaverseObjectDeletionRule.Manual,
            DeletionTriggerConnectedSystemIds = new List<int>()
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);

        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected
        };

        var result = await _controller.UpdateObjectTypeAsync(1, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var badRequest = result as BadRequestObjectResult;
        var error = badRequest!.Value as ApiErrorResponse;
        Assert.That(error!.Message, Does.Contain("authoritative source"));
    }

    [Test]
    public async Task UpdateObjectTypeAsync_WhenAuthoritativeSourceDisconnected_WithEmptyTriggerIds_ReturnsBadRequest()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionRule = MetaverseObjectDeletionRule.Manual,
            DeletionTriggerConnectedSystemIds = new List<int>()
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);

        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            DeletionTriggerConnectedSystemIds = new List<int>()
        };

        var result = await _controller.UpdateObjectTypeAsync(1, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdateObjectTypeAsync_WhenAuthoritativeSourceDisconnected_UseExistingTriggerIds_Succeeds()
    {
        // Test that changing to WhenAuthoritativeSourceDisconnected works when trigger IDs are already set
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionRule = MetaverseObjectDeletionRule.Manual,
            DeletionTriggerConnectedSystemIds = new List<int> { 1, 2 }
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);
        _mockMetaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()))
            .Returns(Task.CompletedTask);

        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected
        };

        var result = await _controller.UpdateObjectTypeAsync(1, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    #endregion

    #region CreateObjectTypeAsync tests

    [Test]
    public async Task CreateObjectTypeAsync_WithValidRequest_ReturnsCreated()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync("Device", false))
            .ReturnsAsync((MetaverseObjectType?)null);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeByPluralNameAsync("Devices", false))
            .ReturnsAsync((MetaverseObjectType?)null);
        _mockMetaverseRepo.Setup(r => r.CreateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()))
            .Callback<MetaverseObjectType>(o => o.Id = 42)
            .Returns(Task.CompletedTask);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(42, false))
            .ReturnsAsync(new MetaverseObjectType
            {
                Id = 42,
                Name = "Device",
                PluralName = "Devices",
                BuiltIn = false,
                DeletionRule = MetaverseObjectDeletionRule.Manual
            });

        var request = new CreateMetaverseObjectTypeRequest
        {
            Name = "Device",
            PluralName = "Devices"
        };

        var result = await _controller.CreateObjectTypeAsync(request);

        Assert.That(result, Is.InstanceOf<CreatedResult>());
        var created = (CreatedResult)result;
        Assert.That(created.Location, Is.EqualTo("/api/v1/metaverse/object-types/42"));
        Assert.That(created.Value, Is.InstanceOf<MetaverseObjectTypeDetailDto>());
        var dto = (MetaverseObjectTypeDetailDto)created.Value!;
        Assert.That(dto.Name, Is.EqualTo("Device"));
        Assert.That(dto.PluralName, Is.EqualTo("Devices"));
        Assert.That(dto.BuiltIn, Is.False);
    }

    [Test]
    public async Task CreateObjectTypeAsync_DuplicateName_ReturnsBadRequest()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync("User", false))
            .ReturnsAsync(new MetaverseObjectType { Id = 1, Name = "User", PluralName = "Users" });

        var request = new CreateMetaverseObjectTypeRequest
        {
            Name = "User",
            PluralName = "FreshUsers"
        };

        var result = await _controller.CreateObjectTypeAsync(request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockMetaverseRepo.Verify(r => r.CreateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()), Times.Never);
    }

    [Test]
    public async Task CreateObjectTypeAsync_DuplicatePluralName_ReturnsBadRequest()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync("Workstation", false))
            .ReturnsAsync((MetaverseObjectType?)null);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeByPluralNameAsync("Users", false))
            .ReturnsAsync(new MetaverseObjectType { Id = 1, Name = "User", PluralName = "Users" });

        var request = new CreateMetaverseObjectTypeRequest
        {
            Name = "Workstation",
            PluralName = "Users"
        };

        var result = await _controller.CreateObjectTypeAsync(request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockMetaverseRepo.Verify(r => r.CreateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()), Times.Never);
    }

    [Test]
    public async Task CreateObjectTypeAsync_NegativeGracePeriod_ReturnsBadRequest()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync("Device", false))
            .ReturnsAsync((MetaverseObjectType?)null);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeByPluralNameAsync("Devices", false))
            .ReturnsAsync((MetaverseObjectType?)null);

        var request = new CreateMetaverseObjectTypeRequest
        {
            Name = "Device",
            PluralName = "Devices",
            DeletionGracePeriod = TimeSpan.FromDays(-1)
        };

        var result = await _controller.CreateObjectTypeAsync(request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockMetaverseRepo.Verify(r => r.CreateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()), Times.Never);
    }

    [Test]
    public async Task CreateObjectTypeAsync_AuthoritativeSourceWithoutTriggerIds_ReturnsBadRequest()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync("Device", false))
            .ReturnsAsync((MetaverseObjectType?)null);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeByPluralNameAsync("Devices", false))
            .ReturnsAsync((MetaverseObjectType?)null);

        var request = new CreateMetaverseObjectTypeRequest
        {
            Name = "Device",
            PluralName = "Devices",
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected
            // No DeletionTriggerConnectedSystemIds provided
        };

        var result = await _controller.CreateObjectTypeAsync(request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockMetaverseRepo.Verify(r => r.CreateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()), Times.Never);
    }

    [Test]
    public async Task CreateObjectTypeAsync_WithAttributeIds_AssociatesAttributes()
    {
        var attribute1 = new MetaverseAttribute { Id = 5, Name = "Custom1", Type = AttributeDataType.Text };
        var attribute2 = new MetaverseAttribute { Id = 6, Name = "Custom2", Type = AttributeDataType.Number };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync("CustomThing", false))
            .ReturnsAsync((MetaverseObjectType?)null);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeByPluralNameAsync("CustomThings", false))
            .ReturnsAsync((MetaverseObjectType?)null);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(5, false)).ReturnsAsync(attribute1);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(6, false)).ReturnsAsync(attribute2);

        MetaverseObjectType? captured = null;
        _mockMetaverseRepo.Setup(r => r.CreateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()))
            .Callback<MetaverseObjectType>(o => { o.Id = 100; captured = o; })
            .Returns(Task.CompletedTask);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(100, false))
            .ReturnsAsync(() => captured);

        var request = new CreateMetaverseObjectTypeRequest
        {
            Name = "CustomThing",
            PluralName = "CustomThings",
            AttributeIds = new List<int> { 5, 6 }
        };

        var result = await _controller.CreateObjectTypeAsync(request);

        Assert.That(result, Is.InstanceOf<CreatedResult>());
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Attributes, Has.Count.EqualTo(2));
        Assert.That(captured.Attributes.Select(a => a.Id), Is.EquivalentTo(new[] { 5, 6 }));
    }

    [Test]
    public async Task CreateObjectTypeAsync_UnknownAttributeId_ReturnsBadRequest()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync("Device", false))
            .ReturnsAsync((MetaverseObjectType?)null);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeByPluralNameAsync("Devices", false))
            .ReturnsAsync((MetaverseObjectType?)null);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(999, false))
            .ReturnsAsync((MetaverseAttribute?)null);

        var request = new CreateMetaverseObjectTypeRequest
        {
            Name = "Device",
            PluralName = "Devices",
            AttributeIds = new List<int> { 999 }
        };

        var result = await _controller.CreateObjectTypeAsync(request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockMetaverseRepo.Verify(r => r.CreateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()), Times.Never);
    }

    [Test]
    public async Task CreateObjectTypeAsync_AlwaysSetsBuiltInFalse()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync("Sandbox", false))
            .ReturnsAsync((MetaverseObjectType?)null);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeByPluralNameAsync("SandboxThings", false))
            .ReturnsAsync((MetaverseObjectType?)null);

        MetaverseObjectType? captured = null;
        _mockMetaverseRepo.Setup(r => r.CreateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()))
            .Callback<MetaverseObjectType>(o => { o.Id = 7; captured = o; })
            .Returns(Task.CompletedTask);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(7, false))
            .ReturnsAsync(() => captured);

        var request = new CreateMetaverseObjectTypeRequest
        {
            Name = "Sandbox",
            PluralName = "SandboxThings"
        };

        var result = await _controller.CreateObjectTypeAsync(request);

        Assert.That(result, Is.InstanceOf<CreatedResult>());
        Assert.That(captured, Is.Not.Null);
        // BuiltIn must always be false for API-created types. Customers and tests should never
        // be able to create BuiltIn types via the public API - that's reserved for seed data.
        Assert.That(captured!.BuiltIn, Is.False);
    }

    [Test]
    public async Task CreateObjectTypeAsync_AttributesNotProvided_CreatesWithEmptyList()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync("Bare", false))
            .ReturnsAsync((MetaverseObjectType?)null);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeByPluralNameAsync("Bares", false))
            .ReturnsAsync((MetaverseObjectType?)null);

        MetaverseObjectType? captured = null;
        _mockMetaverseRepo.Setup(r => r.CreateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()))
            .Callback<MetaverseObjectType>(o => { o.Id = 8; captured = o; })
            .Returns(Task.CompletedTask);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(8, false))
            .ReturnsAsync(() => captured);

        var request = new CreateMetaverseObjectTypeRequest
        {
            Name = "Bare",
            PluralName = "Bares"
        };

        var result = await _controller.CreateObjectTypeAsync(request);

        Assert.That(result, Is.InstanceOf<CreatedResult>());
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Attributes, Is.Not.Null);
        Assert.That(captured.Attributes, Is.Empty);
    }

    #endregion

    #region MetaverseObjectTypeHeader.FromEntity tests

    [Test]
    public void FromEntity_WithPredefinedSearches_HasPredefinedSearchesIsTrue()
    {
        var entity = new MetaverseObjectType
        {
            Id = 1,
            Name = "Person",
            PluralName = "People",
            BuiltIn = true,
            PredefinedSearches = new List<PredefinedSearch>
            {
                new PredefinedSearch { Id = 1, Name = "All Users" }
            }
        };

        var header = MetaverseObjectTypeHeader.FromEntity(entity);

        Assert.That(header.HasPredefinedSearches, Is.True);
    }

    [Test]
    public void FromEntity_WithEmptyPredefinedSearches_HasPredefinedSearchesIsFalse()
    {
        var entity = new MetaverseObjectType
        {
            Id = 1,
            Name = "Device",
            PluralName = "Devices",
            BuiltIn = false,
            PredefinedSearches = new List<PredefinedSearch>()
        };

        var header = MetaverseObjectTypeHeader.FromEntity(entity);

        Assert.That(header.HasPredefinedSearches, Is.False);
    }

    [Test]
    public void FromEntity_WithNullPredefinedSearches_HasPredefinedSearchesIsFalse()
    {
        var entity = new MetaverseObjectType
        {
            Id = 1,
            Name = "Device",
            PluralName = "Devices",
            BuiltIn = false,
            PredefinedSearches = null!
        };

        var header = MetaverseObjectTypeHeader.FromEntity(entity);

        Assert.That(header.HasPredefinedSearches, Is.False);
    }

    #endregion

    #region Icon mapping tests

    [Test]
    public async Task GetObjectTypeAsync_WithIcon_ReturnsIconInDto()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            BuiltIn = true,
            Icon = "Person",
            Attributes = new List<MetaverseAttribute>()
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);

        var result = await _controller.GetObjectTypeAsync(1) as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        var dto = result!.Value as MetaverseObjectTypeDetailDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Icon, Is.EqualTo("Person"));
    }

    [Test]
    public async Task GetObjectTypeAsync_WithNullIcon_ReturnsNullIconInDto()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "Device",
            PluralName = "Devices",
            BuiltIn = false,
            Icon = null,
            Attributes = new List<MetaverseAttribute>()
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);

        var result = await _controller.GetObjectTypeAsync(1) as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        var dto = result!.Value as MetaverseObjectTypeDetailDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Icon, Is.Null);
    }

    [Test]
    public void MetaverseObjectTypeHeader_FromEntity_MapsIconCorrectly()
    {
        var entity = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            BuiltIn = true,
            Icon = "Person",
            PredefinedSearches = new List<PredefinedSearch>()
        };

        var header = MetaverseObjectTypeHeader.FromEntity(entity);

        Assert.That(header.Icon, Is.EqualTo("Person"));
    }

    [Test]
    public void MetaverseObjectTypeHeader_FromEntity_MapsNullIconCorrectly()
    {
        var entity = new MetaverseObjectType
        {
            Id = 1,
            Name = "Device",
            PluralName = "Devices",
            BuiltIn = false,
            Icon = null,
            PredefinedSearches = new List<PredefinedSearch>()
        };

        var header = MetaverseObjectTypeHeader.FromEntity(entity);

        Assert.That(header.Icon, Is.Null);
    }

    [Test]
    public void MetaverseObjectTypeDetailDto_FromEntity_MapsIconCorrectly()
    {
        var entity = new MetaverseObjectType
        {
            Id = 2,
            Name = "Group",
            PluralName = "Groups",
            BuiltIn = true,
            Icon = "Groups",
            Attributes = new List<MetaverseAttribute>(),
            DeletionTriggerConnectedSystemIds = new List<int>()
        };

        var dto = MetaverseObjectTypeDetailDto.FromEntity(entity);

        Assert.That(dto.Icon, Is.EqualTo("Groups"));
    }

    #endregion
}
