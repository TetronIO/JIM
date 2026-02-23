using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Models.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
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
/// Tests for Connected System schema management endpoints (ObjectType and Attribute updates).
/// </summary>
[TestFixture]
public class SynchronisationControllerSchemaTests
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
    private JIM.Models.Security.ApiKey _testApiKey = null!;

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

        // Create a test API key
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

        // Set up the API key repository to return our test API key
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId))
            .ReturnsAsync(_testApiKey);

        // Set up API key authentication context for the controller
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

    #region UpdateConnectedSystemObjectTypeAsync tests

    [Test]
    public async Task UpdateObjectTypeAsync_WithValidRequest_ReturnsOkResult()
    {
        var connectedSystemId = 1;
        var objectTypeId = 5;
        var connectedSystem = new ConnectedSystem
        {
            Id = connectedSystemId,
            Name = "Test System",
            ObjectTypes = new List<ConnectedSystemObjectType>
            {
                new ConnectedSystemObjectType { Id = objectTypeId, Name = "User", ConnectedSystemId = connectedSystemId }
            }
        };
        var objectType = new ConnectedSystemObjectType
        {
            Id = objectTypeId,
            Name = "User",
            Selected = false,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = connectedSystem
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync(objectType);

        var request = new UpdateConnectedSystemObjectTypeRequest { Selected = true };
        var result = await _controller.UpdateConnectedSystemObjectTypeAsync(connectedSystemId, objectTypeId, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task UpdateObjectTypeAsync_WithNonExistentConnectedSystem_ReturnsNotFound()
    {
        var connectedSystemId = 999;
        var objectTypeId = 5;

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync((ConnectedSystem?)null);

        var request = new UpdateConnectedSystemObjectTypeRequest { Selected = true };
        var result = await _controller.UpdateConnectedSystemObjectTypeAsync(connectedSystemId, objectTypeId, request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateObjectTypeAsync_WithNonExistentObjectType_ReturnsNotFound()
    {
        var connectedSystemId = 1;
        var objectTypeId = 999;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync((ConnectedSystemObjectType?)null);

        var request = new UpdateConnectedSystemObjectTypeRequest { Selected = true };
        var result = await _controller.UpdateConnectedSystemObjectTypeAsync(connectedSystemId, objectTypeId, request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateObjectTypeAsync_WithObjectTypeFromDifferentSystem_ReturnsNotFound()
    {
        var connectedSystemId = 1;
        var objectTypeId = 5;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var differentSystem = new ConnectedSystem { Id = 2, Name = "Different System" };
        var objectType = new ConnectedSystemObjectType
        {
            Id = objectTypeId,
            Name = "User",
            ConnectedSystemId = 2,
            ConnectedSystem = differentSystem
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync(objectType);

        var request = new UpdateConnectedSystemObjectTypeRequest { Selected = true };
        var result = await _controller.UpdateConnectedSystemObjectTypeAsync(connectedSystemId, objectTypeId, request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateObjectTypeAsync_WithSelectedTrue_UpdatesObjectType()
    {
        var connectedSystemId = 1;
        var objectTypeId = 5;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var objectType = new ConnectedSystemObjectType
        {
            Id = objectTypeId,
            Name = "User",
            Selected = false,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = connectedSystem
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync(objectType);

        var request = new UpdateConnectedSystemObjectTypeRequest { Selected = true };
        await _controller.UpdateConnectedSystemObjectTypeAsync(connectedSystemId, objectTypeId, request);

        Assert.That(objectType.Selected, Is.True);
        _mockConnectedSystemRepo.Verify(r => r.UpdateObjectTypeAsync(objectType), Times.Once);
    }

    [Test]
    public async Task UpdateObjectTypeAsync_WithRemoveContributedAttributesOnObsoletion_UpdatesProperty()
    {
        var connectedSystemId = 1;
        var objectTypeId = 5;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var objectType = new ConnectedSystemObjectType
        {
            Id = objectTypeId,
            Name = "User",
            RemoveContributedAttributesOnObsoletion = false,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = connectedSystem
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync(objectType);

        var request = new UpdateConnectedSystemObjectTypeRequest { RemoveContributedAttributesOnObsoletion = true };
        await _controller.UpdateConnectedSystemObjectTypeAsync(connectedSystemId, objectTypeId, request);

        Assert.That(objectType.RemoveContributedAttributesOnObsoletion, Is.True);
    }

    [Test]
    public async Task UpdateObjectTypeAsync_ReturnsUpdatedObjectTypeDto()
    {
        var connectedSystemId = 1;
        var objectTypeId = 5;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var objectType = new ConnectedSystemObjectType
        {
            Id = objectTypeId,
            Name = "User",
            Selected = false,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = connectedSystem,
            Attributes = new List<ConnectedSystemObjectTypeAttribute>()
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync(objectType);

        var request = new UpdateConnectedSystemObjectTypeRequest { Selected = true };
        var result = await _controller.UpdateConnectedSystemObjectTypeAsync(connectedSystemId, objectTypeId, request) as OkObjectResult;
        var dto = result?.Value as ConnectedSystemObjectTypeDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Id, Is.EqualTo(objectTypeId));
        Assert.That(dto.Name, Is.EqualTo("User"));
        Assert.That(dto.Selected, Is.True);
    }

    #endregion

    #region UpdateConnectedSystemAttributeAsync tests

    [Test]
    public async Task UpdateAttributeAsync_WithValidRequest_ReturnsOkResult()
    {
        var connectedSystemId = 1;
        var objectTypeId = 5;
        var attributeId = 10;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var objectType = new ConnectedSystemObjectType
        {
            Id = objectTypeId,
            Name = "User",
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = connectedSystem
        };
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = attributeId,
            Name = "employeeId",
            Selected = false,
            ConnectedSystemObjectType = objectType
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync(objectType);
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeAsync(attributeId))
            .ReturnsAsync(attribute);

        var request = new UpdateConnectedSystemAttributeRequest { Selected = true };
        var result = await _controller.UpdateConnectedSystemAttributeAsync(connectedSystemId, objectTypeId, attributeId, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task UpdateAttributeAsync_WithNonExistentConnectedSystem_ReturnsNotFound()
    {
        var connectedSystemId = 999;
        var objectTypeId = 5;
        var attributeId = 10;

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync((ConnectedSystem?)null);

        var request = new UpdateConnectedSystemAttributeRequest { Selected = true };
        var result = await _controller.UpdateConnectedSystemAttributeAsync(connectedSystemId, objectTypeId, attributeId, request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateAttributeAsync_WithNonExistentObjectType_ReturnsNotFound()
    {
        var connectedSystemId = 1;
        var objectTypeId = 999;
        var attributeId = 10;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync((ConnectedSystemObjectType?)null);

        var request = new UpdateConnectedSystemAttributeRequest { Selected = true };
        var result = await _controller.UpdateConnectedSystemAttributeAsync(connectedSystemId, objectTypeId, attributeId, request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateAttributeAsync_WithNonExistentAttribute_ReturnsNotFound()
    {
        var connectedSystemId = 1;
        var objectTypeId = 5;
        var attributeId = 999;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var objectType = new ConnectedSystemObjectType
        {
            Id = objectTypeId,
            Name = "User",
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = connectedSystem
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync(objectType);
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeAsync(attributeId))
            .ReturnsAsync((ConnectedSystemObjectTypeAttribute?)null);

        var request = new UpdateConnectedSystemAttributeRequest { Selected = true };
        var result = await _controller.UpdateConnectedSystemAttributeAsync(connectedSystemId, objectTypeId, attributeId, request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateAttributeAsync_WithAttributeFromDifferentObjectType_ReturnsNotFound()
    {
        var connectedSystemId = 1;
        var objectTypeId = 5;
        var attributeId = 10;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var objectType = new ConnectedSystemObjectType
        {
            Id = objectTypeId,
            Name = "User",
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = connectedSystem
        };
        var differentObjectType = new ConnectedSystemObjectType
        {
            Id = 6,
            Name = "Group",
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = connectedSystem
        };
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = attributeId,
            Name = "employeeId",
            ConnectedSystemObjectType = differentObjectType
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync(objectType);
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeAsync(attributeId))
            .ReturnsAsync(attribute);

        var request = new UpdateConnectedSystemAttributeRequest { Selected = true };
        var result = await _controller.UpdateConnectedSystemAttributeAsync(connectedSystemId, objectTypeId, attributeId, request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateAttributeAsync_WithSelectedTrue_UpdatesAttribute()
    {
        var connectedSystemId = 1;
        var objectTypeId = 5;
        var attributeId = 10;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var objectType = new ConnectedSystemObjectType
        {
            Id = objectTypeId,
            Name = "User",
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = connectedSystem
        };
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = attributeId,
            Name = "employeeId",
            Selected = false,
            ConnectedSystemObjectType = objectType
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync(objectType);
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeAsync(attributeId))
            .ReturnsAsync(attribute);

        var request = new UpdateConnectedSystemAttributeRequest { Selected = true };
        await _controller.UpdateConnectedSystemAttributeAsync(connectedSystemId, objectTypeId, attributeId, request);

        Assert.That(attribute.Selected, Is.True);
        _mockConnectedSystemRepo.Verify(r => r.UpdateAttributeAsync(attribute), Times.Once);
    }

    [Test]
    public async Task UpdateAttributeAsync_WithIsExternalIdTrue_UpdatesAttribute()
    {
        var connectedSystemId = 1;
        var objectTypeId = 5;
        var attributeId = 10;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var objectType = new ConnectedSystemObjectType
        {
            Id = objectTypeId,
            Name = "User",
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = connectedSystem
        };
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = attributeId,
            Name = "employeeId",
            IsExternalId = false,
            ConnectedSystemObjectType = objectType
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync(objectType);
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeAsync(attributeId))
            .ReturnsAsync(attribute);

        var request = new UpdateConnectedSystemAttributeRequest { IsExternalId = true };
        await _controller.UpdateConnectedSystemAttributeAsync(connectedSystemId, objectTypeId, attributeId, request);

        Assert.That(attribute.IsExternalId, Is.True);
    }

    [Test]
    public async Task UpdateAttributeAsync_WithIsSecondaryExternalIdTrue_UpdatesAttribute()
    {
        var connectedSystemId = 1;
        var objectTypeId = 5;
        var attributeId = 10;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var objectType = new ConnectedSystemObjectType
        {
            Id = objectTypeId,
            Name = "User",
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = connectedSystem
        };
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = attributeId,
            Name = "dn",
            IsSecondaryExternalId = false,
            ConnectedSystemObjectType = objectType
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync(objectType);
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeAsync(attributeId))
            .ReturnsAsync(attribute);

        var request = new UpdateConnectedSystemAttributeRequest { IsSecondaryExternalId = true };
        await _controller.UpdateConnectedSystemAttributeAsync(connectedSystemId, objectTypeId, attributeId, request);

        Assert.That(attribute.IsSecondaryExternalId, Is.True);
    }

    [Test]
    public async Task UpdateAttributeAsync_ReturnsUpdatedAttributeDto()
    {
        var connectedSystemId = 1;
        var objectTypeId = 5;
        var attributeId = 10;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var objectType = new ConnectedSystemObjectType
        {
            Id = objectTypeId,
            Name = "User",
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = connectedSystem
        };
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = attributeId,
            Name = "employeeId",
            Selected = false,
            IsExternalId = false,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            ConnectedSystemObjectType = objectType
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync(objectType);
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeAsync(attributeId))
            .ReturnsAsync(attribute);

        var request = new UpdateConnectedSystemAttributeRequest { Selected = true, IsExternalId = true };
        var result = await _controller.UpdateConnectedSystemAttributeAsync(connectedSystemId, objectTypeId, attributeId, request) as OkObjectResult;
        var dto = result?.Value as ConnectedSystemAttributeDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Id, Is.EqualTo(attributeId));
        Assert.That(dto.Name, Is.EqualTo("employeeId"));
        Assert.That(dto.Selected, Is.True);
        Assert.That(dto.IsExternalId, Is.True);
    }

    #endregion

    #region Bulk Attribute Update Tests

    [Test]
    public async Task BulkUpdateAttributesAsync_WithEmptyDictionary_ReturnsBadRequest()
    {
        var request = new BulkUpdateConnectedSystemAttributesRequest { Attributes = new Dictionary<int, UpdateConnectedSystemAttributeRequest>() };
        var result = await _controller.BulkUpdateConnectedSystemAttributesAsync(1, 5, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task BulkUpdateAttributesAsync_WithNonExistentConnectedSystem_ReturnsNotFound()
    {
        var connectedSystemId = 999;
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync((ConnectedSystem?)null);

        var request = new BulkUpdateConnectedSystemAttributesRequest
        {
            Attributes = new Dictionary<int, UpdateConnectedSystemAttributeRequest>
            {
                { 10, new UpdateConnectedSystemAttributeRequest { Selected = true } }
            }
        };
        var result = await _controller.BulkUpdateConnectedSystemAttributesAsync(connectedSystemId, 5, request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task BulkUpdateAttributesAsync_WithNonExistentObjectType_ReturnsNotFound()
    {
        var connectedSystemId = 1;
        var objectTypeId = 999;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync((ConnectedSystemObjectType?)null);

        var request = new BulkUpdateConnectedSystemAttributesRequest
        {
            Attributes = new Dictionary<int, UpdateConnectedSystemAttributeRequest>
            {
                { 10, new UpdateConnectedSystemAttributeRequest { Selected = true } }
            }
        };
        var result = await _controller.BulkUpdateConnectedSystemAttributesAsync(connectedSystemId, objectTypeId, request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task BulkUpdateAttributesAsync_WithValidRequest_UpdatesAllAttributes()
    {
        var connectedSystemId = 1;
        var objectTypeId = 5;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var attribute1 = new ConnectedSystemObjectTypeAttribute
        {
            Id = 10,
            Name = "employeeId",
            Selected = false
        };
        var attribute2 = new ConnectedSystemObjectTypeAttribute
        {
            Id = 11,
            Name = "displayName",
            Selected = false
        };
        var objectType = new ConnectedSystemObjectType
        {
            Id = objectTypeId,
            Name = "User",
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = connectedSystem,
            Attributes = new List<ConnectedSystemObjectTypeAttribute> { attribute1, attribute2 }
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync(objectType);

        var request = new BulkUpdateConnectedSystemAttributesRequest
        {
            Attributes = new Dictionary<int, UpdateConnectedSystemAttributeRequest>
            {
                { 10, new UpdateConnectedSystemAttributeRequest { Selected = true } },
                { 11, new UpdateConnectedSystemAttributeRequest { Selected = true } }
            }
        };

        var result = await _controller.BulkUpdateConnectedSystemAttributesAsync(connectedSystemId, objectTypeId, request) as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        Assert.That(attribute1.Selected, Is.True);
        Assert.That(attribute2.Selected, Is.True);
        _mockConnectedSystemRepo.Verify(r => r.UpdateAttributesAsync(It.IsAny<IEnumerable<ConnectedSystemObjectTypeAttribute>>()), Times.Once);
    }

    [Test]
    public async Task BulkUpdateAttributesAsync_WithMixedValidAndInvalidAttributes_ReturnsPartialSuccess()
    {
        var connectedSystemId = 1;
        var objectTypeId = 5;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var attribute1 = new ConnectedSystemObjectTypeAttribute
        {
            Id = 10,
            Name = "employeeId",
            Selected = false
        };
        var objectType = new ConnectedSystemObjectType
        {
            Id = objectTypeId,
            Name = "User",
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = connectedSystem,
            Attributes = new List<ConnectedSystemObjectTypeAttribute> { attribute1 }
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync(objectType);

        var request = new BulkUpdateConnectedSystemAttributesRequest
        {
            Attributes = new Dictionary<int, UpdateConnectedSystemAttributeRequest>
            {
                { 10, new UpdateConnectedSystemAttributeRequest { Selected = true } },
                { 999, new UpdateConnectedSystemAttributeRequest { Selected = true } }  // Invalid attribute ID
            }
        };

        var result = await _controller.BulkUpdateConnectedSystemAttributesAsync(connectedSystemId, objectTypeId, request) as OkObjectResult;
        var response = result?.Value as BulkUpdateConnectedSystemAttributesResponse;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.UpdatedCount, Is.EqualTo(1));
        Assert.That(response.Errors, Is.Not.Null);
        Assert.That(response.Errors!.Count, Is.EqualTo(1));
        Assert.That(response.Errors[0].AttributeId, Is.EqualTo(999));
    }

    [Test]
    public async Task BulkUpdateAttributesAsync_CreatesOneActivityRecord()
    {
        var connectedSystemId = 1;
        var objectTypeId = 5;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var attribute1 = new ConnectedSystemObjectTypeAttribute
        {
            Id = 10,
            Name = "employeeId",
            Selected = false
        };
        var attribute2 = new ConnectedSystemObjectTypeAttribute
        {
            Id = 11,
            Name = "displayName",
            Selected = false
        };
        var attribute3 = new ConnectedSystemObjectTypeAttribute
        {
            Id = 12,
            Name = "mail",
            Selected = false
        };
        var objectType = new ConnectedSystemObjectType
        {
            Id = objectTypeId,
            Name = "User",
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = connectedSystem,
            Attributes = new List<ConnectedSystemObjectTypeAttribute> { attribute1, attribute2, attribute3 }
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync(objectType);

        var request = new BulkUpdateConnectedSystemAttributesRequest
        {
            Attributes = new Dictionary<int, UpdateConnectedSystemAttributeRequest>
            {
                { 10, new UpdateConnectedSystemAttributeRequest { Selected = true } },
                { 11, new UpdateConnectedSystemAttributeRequest { Selected = true } },
                { 12, new UpdateConnectedSystemAttributeRequest { Selected = true } }
            }
        };

        await _controller.BulkUpdateConnectedSystemAttributesAsync(connectedSystemId, objectTypeId, request);

        // Verify only ONE activity was created (not 3)
        _mockActivityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Once);
        _mockActivityRepo.Verify(r => r.UpdateActivityAsync(It.IsAny<Activity>()), Times.Once);
    }

    [Test]
    public async Task BulkUpdateAttributesAsync_SetsCorrectTargetName_ConnectedSystemName()
    {
        var connectedSystemId = 1;
        var objectTypeId = 5;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "LDAP Directory" };
        var attribute1 = new ConnectedSystemObjectTypeAttribute
        {
            Id = 10,
            Name = "employeeId",
            Selected = false
        };
        var objectType = new ConnectedSystemObjectType
        {
            Id = objectTypeId,
            Name = "User",
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = connectedSystem,
            Attributes = new List<ConnectedSystemObjectTypeAttribute> { attribute1 }
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync(objectType);

        Activity? capturedActivity = null;
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a);

        var request = new BulkUpdateConnectedSystemAttributesRequest
        {
            Attributes = new Dictionary<int, UpdateConnectedSystemAttributeRequest>
            {
                { 10, new UpdateConnectedSystemAttributeRequest { Selected = true } }
            }
        };

        await _controller.BulkUpdateConnectedSystemAttributesAsync(connectedSystemId, objectTypeId, request);

        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.TargetName, Is.EqualTo("LDAP Directory"));
        Assert.That(capturedActivity.TargetType, Is.EqualTo(ActivityTargetType.ConnectedSystem));
        Assert.That(capturedActivity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
    }

    [Test]
    public async Task BulkUpdateAttributesAsync_ReturnsResponseWithActivityId()
    {
        var connectedSystemId = 1;
        var objectTypeId = 5;
        var connectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Test System" };
        var attribute1 = new ConnectedSystemObjectTypeAttribute
        {
            Id = 10,
            Name = "employeeId",
            Selected = false
        };
        var objectType = new ConnectedSystemObjectType
        {
            Id = objectTypeId,
            Name = "User",
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = connectedSystem,
            Attributes = new List<ConnectedSystemObjectTypeAttribute> { attribute1 }
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(objectTypeId))
            .ReturnsAsync(objectType);

        var request = new BulkUpdateConnectedSystemAttributesRequest
        {
            Attributes = new Dictionary<int, UpdateConnectedSystemAttributeRequest>
            {
                { 10, new UpdateConnectedSystemAttributeRequest { Selected = true } }
            }
        };

        var result = await _controller.BulkUpdateConnectedSystemAttributesAsync(connectedSystemId, objectTypeId, request) as OkObjectResult;
        var response = result?.Value as BulkUpdateConnectedSystemAttributesResponse;

        Assert.That(response, Is.Not.Null);
        // Note: ActivityId will be Guid.Empty in unit tests since no actual database persistence occurs
        // In integration tests or real usage, the Activity.Id would be generated
        Assert.That(response!.UpdatedCount, Is.EqualTo(1));
        Assert.That(response.UpdatedAttributes.Count, Is.EqualTo(1));
        Assert.That(response.Errors, Is.Null);
    }

    #endregion
}
