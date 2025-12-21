using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class ApiKeysControllerTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepository = null!;
    private Mock<ISecurityRepository> _mockSecurityRepository = null!;
    private Mock<ILogger<ApiKeysController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private ApiKeysController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockApiKeyRepository = new Mock<IApiKeyRepository>();
        _mockSecurityRepository = new Mock<ISecurityRepository>();
        _mockLogger = new Mock<ILogger<ApiKeysController>>();

        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepository.Object);
        _mockRepository.Setup(r => r.Security).Returns(_mockSecurityRepository.Object);

        _application = new JimApplication(_mockRepository.Object);
        _controller = new ApiKeysController(_mockLogger.Object, _application);
    }

    #region GetAllAsync tests

    [Test]
    public async Task GetAllAsync_ReturnsOkResult()
    {
        _mockApiKeyRepository.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<ApiKey>());

        var result = await _controller.GetAllAsync();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetAllAsync_ReturnsEmptyListWhenNoKeys()
    {
        _mockApiKeyRepository.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<ApiKey>());

        var result = await _controller.GetAllAsync() as OkObjectResult;
        var keys = result?.Value as IEnumerable<ApiKeyDto>;

        Assert.That(keys, Is.Not.Null);
        Assert.That(keys!.Count(), Is.EqualTo(0));
    }

    [Test]
    public async Task GetAllAsync_ReturnsAllKeys()
    {
        var apiKeys = new List<ApiKey>
        {
            new() { Id = Guid.NewGuid(), Name = "Key 1", KeyPrefix = "jim_ak_1234", Roles = new List<Role>() },
            new() { Id = Guid.NewGuid(), Name = "Key 2", KeyPrefix = "jim_ak_5678", Roles = new List<Role>() }
        };
        _mockApiKeyRepository.Setup(r => r.GetAllAsync())
            .ReturnsAsync(apiKeys);

        var result = await _controller.GetAllAsync() as OkObjectResult;
        var keys = result?.Value as IEnumerable<ApiKeyDto>;

        Assert.That(keys, Is.Not.Null);
        Assert.That(keys!.Count(), Is.EqualTo(2));
    }

    #endregion

    #region GetByIdAsync tests

    [Test]
    public async Task GetByIdAsync_WithValidId_ReturnsOkResult()
    {
        var id = Guid.NewGuid();
        var apiKey = new ApiKey { Id = id, Name = "Test Key", KeyPrefix = "jim_ak_test", Roles = new List<Role>() };
        _mockApiKeyRepository.Setup(r => r.GetByIdAsync(id))
            .ReturnsAsync(apiKey);

        var result = await _controller.GetByIdAsync(id);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetByIdAsync_WithValidId_ReturnsCorrectKey()
    {
        var id = Guid.NewGuid();
        var apiKey = new ApiKey { Id = id, Name = "Test Key", KeyPrefix = "jim_ak_test", Roles = new List<Role>() };
        _mockApiKeyRepository.Setup(r => r.GetByIdAsync(id))
            .ReturnsAsync(apiKey);

        var result = await _controller.GetByIdAsync(id) as OkObjectResult;
        var dto = result?.Value as ApiKeyDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Id, Is.EqualTo(id));
        Assert.That(dto.Name, Is.EqualTo("Test Key"));
    }

    [Test]
    public async Task GetByIdAsync_WithInvalidId_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _mockApiKeyRepository.Setup(r => r.GetByIdAsync(id))
            .ReturnsAsync((ApiKey?)null);

        var result = await _controller.GetByIdAsync(id);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    #endregion

    #region CreateAsync tests

    [Test]
    public async Task CreateAsync_WithValidRequest_ReturnsCreatedResult()
    {
        var request = new ApiKeyCreateRequestDto
        {
            Name = "New Key",
            Description = "Test description",
            RoleIds = new List<int>()
        };

        _mockSecurityRepository.Setup(r => r.GetRolesAsync())
            .ReturnsAsync(new List<Role>());

        _mockApiKeyRepository.Setup(r => r.CreateAsync(It.IsAny<ApiKey>()))
            .ReturnsAsync((ApiKey key) => key);

        var result = await _controller.CreateAsync(request);

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
    }

    [Test]
    public async Task CreateAsync_WithValidRequest_ReturnsFullKey()
    {
        var request = new ApiKeyCreateRequestDto
        {
            Name = "New Key",
            Description = "Test description",
            RoleIds = new List<int>()
        };

        _mockSecurityRepository.Setup(r => r.GetRolesAsync())
            .ReturnsAsync(new List<Role>());

        _mockApiKeyRepository.Setup(r => r.CreateAsync(It.IsAny<ApiKey>()))
            .ReturnsAsync((ApiKey key) => key);

        var result = await _controller.CreateAsync(request) as CreatedAtRouteResult;
        var dto = result?.Value as ApiKeyCreateResponseDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Key, Does.StartWith("jim_ak_"));
    }

    [Test]
    public async Task CreateAsync_WithEmptyName_ReturnsBadRequest()
    {
        var request = new ApiKeyCreateRequestDto
        {
            Name = "",
            RoleIds = new List<int>()
        };

        var result = await _controller.CreateAsync(request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task CreateAsync_WithInvalidRoleId_ReturnsBadRequest()
    {
        var request = new ApiKeyCreateRequestDto
        {
            Name = "New Key",
            RoleIds = new List<int> { 999 } // Invalid role ID
        };

        _mockSecurityRepository.Setup(r => r.GetRolesAsync())
            .ReturnsAsync(new List<Role> { new() { Id = 1, Name = "Admin" } });

        var result = await _controller.CreateAsync(request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task CreateAsync_WithValidRoles_AssignsRoles()
    {
        var adminRole = new Role { Id = 1, Name = "Administrator" };
        var request = new ApiKeyCreateRequestDto
        {
            Name = "New Key",
            RoleIds = new List<int> { 1 }
        };

        _mockSecurityRepository.Setup(r => r.GetRolesAsync())
            .ReturnsAsync(new List<Role> { adminRole });

        ApiKey? capturedKey = null;
        _mockApiKeyRepository.Setup(r => r.CreateAsync(It.IsAny<ApiKey>()))
            .Callback<ApiKey>(k => capturedKey = k)
            .ReturnsAsync((ApiKey key) => key);

        await _controller.CreateAsync(request);

        Assert.That(capturedKey, Is.Not.Null);
        Assert.That(capturedKey!.Roles.Count, Is.EqualTo(1));
        Assert.That(capturedKey.Roles[0].Name, Is.EqualTo("Administrator"));
    }

    #endregion

    #region UpdateAsync tests

    [Test]
    public async Task UpdateAsync_WithValidRequest_ReturnsOkResult()
    {
        var id = Guid.NewGuid();
        var existingKey = new ApiKey { Id = id, Name = "Old Name", KeyPrefix = "jim_ak_test", Roles = new List<Role>() };
        var request = new ApiKeyUpdateRequestDto
        {
            Name = "New Name",
            IsEnabled = true,
            RoleIds = new List<int>()
        };

        _mockApiKeyRepository.Setup(r => r.GetByIdAsync(id))
            .ReturnsAsync(existingKey);

        _mockSecurityRepository.Setup(r => r.GetRolesAsync())
            .ReturnsAsync(new List<Role>());

        _mockApiKeyRepository.Setup(r => r.UpdateAsync(It.IsAny<ApiKey>()))
            .ReturnsAsync((ApiKey key) => key);

        var result = await _controller.UpdateAsync(id, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task UpdateAsync_WithInvalidId_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        var request = new ApiKeyUpdateRequestDto { Name = "New Name", RoleIds = new List<int>() };

        _mockApiKeyRepository.Setup(r => r.GetByIdAsync(id))
            .ReturnsAsync((ApiKey?)null);

        var result = await _controller.UpdateAsync(id, request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateAsync_WithEmptyName_ReturnsBadRequest()
    {
        var id = Guid.NewGuid();
        var request = new ApiKeyUpdateRequestDto { Name = "", RoleIds = new List<int>() };

        var result = await _controller.UpdateAsync(id, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    #endregion

    #region DeleteAsync tests

    [Test]
    public async Task DeleteAsync_WithValidId_ReturnsNoContent()
    {
        var id = Guid.NewGuid();
        var existingKey = new ApiKey { Id = id, Name = "Test Key", KeyPrefix = "jim_ak_test", Roles = new List<Role>() };

        _mockApiKeyRepository.Setup(r => r.GetByIdAsync(id))
            .ReturnsAsync(existingKey);

        _mockApiKeyRepository.Setup(r => r.DeleteAsync(id))
            .Returns(Task.CompletedTask);

        var result = await _controller.DeleteAsync(id);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task DeleteAsync_WithInvalidId_ReturnsNotFound()
    {
        var id = Guid.NewGuid();

        _mockApiKeyRepository.Setup(r => r.GetByIdAsync(id))
            .ReturnsAsync((ApiKey?)null);

        var result = await _controller.DeleteAsync(id);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task DeleteAsync_CallsRepositoryDelete()
    {
        var id = Guid.NewGuid();
        var existingKey = new ApiKey { Id = id, Name = "Test Key", KeyPrefix = "jim_ak_test", Roles = new List<Role>() };

        _mockApiKeyRepository.Setup(r => r.GetByIdAsync(id))
            .ReturnsAsync(existingKey);

        _mockApiKeyRepository.Setup(r => r.DeleteAsync(id))
            .Returns(Task.CompletedTask);

        await _controller.DeleteAsync(id);

        _mockApiKeyRepository.Verify(r => r.DeleteAsync(id), Times.Once);
    }

    #endregion
}
