using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
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
    private Mock<ILogger<MetaverseController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private MetaverseController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockLogger = new Mock<ILogger<MetaverseController>>();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new MetaverseController(_mockLogger.Object, _application);

        // Set up API key authentication context for the controller
        var claims = new List<Claim>
        {
            new Claim("auth_method", "api_key"),
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
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(1))
            .ReturnsAsync(connectedSystem1);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(2))
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
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(999))
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
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(1))
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
}
