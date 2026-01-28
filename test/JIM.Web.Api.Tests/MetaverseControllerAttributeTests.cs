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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for Metaverse Attribute CRUD endpoints (Create, Update, Delete).
/// </summary>
[TestFixture]
public class MetaverseControllerAttributeTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
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
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockLogger = new Mock<ILogger<MetaverseController>>();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new MetaverseController(_mockLogger.Object, _application);

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

    #region CreateAttributeAsync tests

    [Test]
    public async Task CreateAttributeAsync_WithValidRequest_ReturnsCreatedResult()
    {
        MetaverseAttribute? capturedAttribute = null;
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(It.IsAny<string>()))
            .ReturnsAsync((MetaverseAttribute?)null);
        _mockMetaverseRepo.Setup(r => r.CreateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>()))
            .Callback<MetaverseAttribute>(a => capturedAttribute = a);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(It.IsAny<int>()))
            .ReturnsAsync(() => capturedAttribute);

        var request = new CreateMetaverseAttributeRequest
        {
            Name = "CustomAttribute",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        var result = await _controller.CreateAttributeAsync(request);

        Assert.That(result, Is.InstanceOf<CreatedResult>());
    }

    [Test]
    public async Task CreateAttributeAsync_WithExistingName_ReturnsBadRequest()
    {
        var existingAttr = new MetaverseAttribute { Id = 1, Name = "CustomAttribute" };
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync("CustomAttribute"))
            .ReturnsAsync(existingAttr);

        var request = new CreateMetaverseAttributeRequest
        {
            Name = "CustomAttribute",
            Type = AttributeDataType.Text
        };
        var result = await _controller.CreateAttributeAsync(request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task CreateAttributeAsync_CallsRepositoryWithCorrectValues()
    {
        MetaverseAttribute? capturedAttribute = null;
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(It.IsAny<string>()))
            .ReturnsAsync((MetaverseAttribute?)null);
        _mockMetaverseRepo.Setup(r => r.CreateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>()))
            .Callback<MetaverseAttribute>(a => capturedAttribute = a);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(It.IsAny<int>()))
            .ReturnsAsync(() => capturedAttribute);

        var request = new CreateMetaverseAttributeRequest
        {
            Name = "TestAttribute",
            Type = AttributeDataType.Number,
            AttributePlurality = AttributePlurality.MultiValued
        };
        await _controller.CreateAttributeAsync(request);

        Assert.That(capturedAttribute, Is.Not.Null);
        Assert.That(capturedAttribute!.Name, Is.EqualTo("TestAttribute"));
        Assert.That(capturedAttribute.Type, Is.EqualTo(AttributeDataType.Number));
        Assert.That(capturedAttribute.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
    }

    [Test]
    public async Task CreateAttributeAsync_WithObjectTypeIds_AssociatesWithObjectTypes()
    {
        var objectType1 = new MetaverseObjectType { Id = 1, Name = "User" };
        var objectType2 = new MetaverseObjectType { Id = 2, Name = "Group" };

        MetaverseAttribute? capturedAttribute = null;
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(It.IsAny<string>()))
            .ReturnsAsync((MetaverseAttribute?)null);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType1);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(2, false))
            .ReturnsAsync(objectType2);
        _mockMetaverseRepo.Setup(r => r.CreateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>()))
            .Callback<MetaverseAttribute>(a => capturedAttribute = a);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(It.IsAny<int>()))
            .ReturnsAsync(() => capturedAttribute);

        var request = new CreateMetaverseAttributeRequest
        {
            Name = "Department",
            Type = AttributeDataType.Text,
            ObjectTypeIds = new List<int> { 1, 2 }
        };
        await _controller.CreateAttributeAsync(request);

        Assert.That(capturedAttribute, Is.Not.Null);
        Assert.That(capturedAttribute!.MetaverseObjectTypes.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task CreateAttributeAsync_WithInvalidObjectTypeId_ReturnsBadRequest()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(It.IsAny<string>()))
            .ReturnsAsync((MetaverseAttribute?)null);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(999, false))
            .ReturnsAsync((MetaverseObjectType?)null);

        var request = new CreateMetaverseAttributeRequest
        {
            Name = "TestAttribute",
            Type = AttributeDataType.Text,
            ObjectTypeIds = new List<int> { 999 }
        };
        var result = await _controller.CreateAttributeAsync(request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task CreateAttributeAsync_ReturnsCreatedAttributeDto()
    {
        var createdAttr = new MetaverseAttribute
        {
            Id = 10,
            Name = "NewAttribute",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            BuiltIn = false
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(It.IsAny<string>()))
            .ReturnsAsync((MetaverseAttribute?)null);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(It.IsAny<int>()))
            .ReturnsAsync(createdAttr);

        var request = new CreateMetaverseAttributeRequest
        {
            Name = "NewAttribute",
            Type = AttributeDataType.Text
        };
        var result = await _controller.CreateAttributeAsync(request) as CreatedResult;
        var dto = result?.Value as MetaverseAttributeDetailDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Id, Is.EqualTo(10));
        Assert.That(dto.Name, Is.EqualTo("NewAttribute"));
    }

    #endregion

    #region UpdateAttributeAsync tests

    [Test]
    public async Task UpdateAttributeAsync_WithValidRequest_ReturnsOkResult()
    {
        var attribute = new MetaverseAttribute
        {
            Id = 1,
            Name = "OldName",
            Type = AttributeDataType.Text,
            BuiltIn = false
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(1))
            .ReturnsAsync(attribute);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync("NewName"))
            .ReturnsAsync((MetaverseAttribute?)null);

        var request = new UpdateMetaverseAttributeRequest { Name = "NewName" };
        var result = await _controller.UpdateAttributeAsync(1, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task UpdateAttributeAsync_WithNonExistentAttribute_ReturnsNotFound()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(999))
            .ReturnsAsync((MetaverseAttribute?)null);

        var request = new UpdateMetaverseAttributeRequest { Name = "NewName" };
        var result = await _controller.UpdateAttributeAsync(999, request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateAttributeAsync_WithBuiltInAttribute_ReturnsBadRequest()
    {
        var builtInAttribute = new MetaverseAttribute
        {
            Id = 1,
            Name = "DisplayName",
            BuiltIn = true
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(1))
            .ReturnsAsync(builtInAttribute);

        var request = new UpdateMetaverseAttributeRequest { Name = "NewName" };
        var result = await _controller.UpdateAttributeAsync(1, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdateAttributeAsync_WithConflictingName_ReturnsBadRequest()
    {
        var attribute = new MetaverseAttribute
        {
            Id = 1,
            Name = "Attribute1",
            BuiltIn = false
        };
        var conflictingAttribute = new MetaverseAttribute
        {
            Id = 2,
            Name = "Attribute2"
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(1))
            .ReturnsAsync(attribute);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync("Attribute2"))
            .ReturnsAsync(conflictingAttribute);

        var request = new UpdateMetaverseAttributeRequest { Name = "Attribute2" };
        var result = await _controller.UpdateAttributeAsync(1, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdateAttributeAsync_UpdatesName()
    {
        var attribute = new MetaverseAttribute
        {
            Id = 1,
            Name = "OldName",
            BuiltIn = false
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(1))
            .ReturnsAsync(attribute);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync("NewName"))
            .ReturnsAsync((MetaverseAttribute?)null);

        var request = new UpdateMetaverseAttributeRequest { Name = "NewName" };
        await _controller.UpdateAttributeAsync(1, request);

        Assert.That(attribute.Name, Is.EqualTo("NewName"));
        _mockMetaverseRepo.Verify(r => r.UpdateMetaverseAttributeAsync(attribute), Times.Once);
    }

    [Test]
    public async Task UpdateAttributeAsync_UpdatesType()
    {
        var attribute = new MetaverseAttribute
        {
            Id = 1,
            Name = "TestAttr",
            Type = AttributeDataType.Text,
            BuiltIn = false
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(1))
            .ReturnsAsync(attribute);

        var request = new UpdateMetaverseAttributeRequest { Type = AttributeDataType.Number };
        await _controller.UpdateAttributeAsync(1, request);

        Assert.That(attribute.Type, Is.EqualTo(AttributeDataType.Number));
    }

    [Test]
    public async Task UpdateAttributeAsync_UpdatesPlurality()
    {
        var attribute = new MetaverseAttribute
        {
            Id = 1,
            Name = "TestAttr",
            AttributePlurality = AttributePlurality.SingleValued,
            BuiltIn = false
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(1))
            .ReturnsAsync(attribute);

        var request = new UpdateMetaverseAttributeRequest { AttributePlurality = AttributePlurality.MultiValued };
        await _controller.UpdateAttributeAsync(1, request);

        Assert.That(attribute.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
    }

    [Test]
    public async Task UpdateAttributeAsync_WithObjectTypeIds_UpdatesAssociations()
    {
        var objectType1 = new MetaverseObjectType { Id = 1, Name = "User" };
        var objectType2 = new MetaverseObjectType { Id = 2, Name = "Group" };
        var attribute = new MetaverseAttribute
        {
            Id = 1,
            Name = "TestAttr",
            BuiltIn = false,
            MetaverseObjectTypes = new List<MetaverseObjectType> { objectType1 }
        };

        // When ObjectTypeIds is specified, the controller uses GetMetaverseAttributeWithObjectTypesAsync
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeWithObjectTypesAsync(1))
            .ReturnsAsync(attribute);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(2, false))
            .ReturnsAsync(objectType2);
        // The controller also fetches the updated attribute for the response
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(1))
            .ReturnsAsync(attribute);

        var request = new UpdateMetaverseAttributeRequest { ObjectTypeIds = new List<int> { 2 } };
        await _controller.UpdateAttributeAsync(1, request);

        Assert.That(attribute.MetaverseObjectTypes.Count, Is.EqualTo(1));
        Assert.That(attribute.MetaverseObjectTypes.First().Id, Is.EqualTo(2));
    }

    #endregion

    #region DeleteAttributeAsync tests

    [Test]
    public async Task DeleteAttributeAsync_WithValidId_ReturnsNoContent()
    {
        var attribute = new MetaverseAttribute
        {
            Id = 1,
            Name = "CustomAttr",
            BuiltIn = false
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(1))
            .ReturnsAsync(attribute);

        var result = await _controller.DeleteAttributeAsync(1);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task DeleteAttributeAsync_WithNonExistentAttribute_ReturnsNotFound()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(999))
            .ReturnsAsync((MetaverseAttribute?)null);

        var result = await _controller.DeleteAttributeAsync(999);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task DeleteAttributeAsync_WithBuiltInAttribute_ReturnsBadRequest()
    {
        var builtInAttribute = new MetaverseAttribute
        {
            Id = 1,
            Name = "DisplayName",
            BuiltIn = true
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(1))
            .ReturnsAsync(builtInAttribute);

        var result = await _controller.DeleteAttributeAsync(1);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task DeleteAttributeAsync_CallsRepositoryDelete()
    {
        var attribute = new MetaverseAttribute
        {
            Id = 1,
            Name = "CustomAttr",
            BuiltIn = false
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(1))
            .ReturnsAsync(attribute);

        await _controller.DeleteAttributeAsync(1);

        _mockMetaverseRepo.Verify(r => r.DeleteMetaverseAttributeAsync(attribute), Times.Once);
    }

    #endregion
}
