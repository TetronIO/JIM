using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Models.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for Sync Rule Mapping CRUD endpoints.
/// </summary>
[TestFixture]
public class SynchronisationControllerMappingTests
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
    private JIM.Models.Security.ApiKey _testApiKey = null!;

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

    #region GetSyncRuleMappingsAsync tests

    [Test]
    public async Task GetSyncRuleMappingsAsync_WithValidSyncRule_ReturnsOkResult()
    {
        var syncRuleId = 1;
        var syncRule = new SyncRule { Id = syncRuleId, Name = "Test Rule" };

        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(syncRuleId))
            .ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingsAsync(syncRuleId))
            .ReturnsAsync(new List<SyncRuleMapping>());

        var result = await _controller.GetSyncRuleMappingsAsync(syncRuleId);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetSyncRuleMappingsAsync_WithNonExistentSyncRule_ReturnsNotFound()
    {
        var syncRuleId = 999;

        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(syncRuleId))
            .ReturnsAsync((SyncRule?)null);

        var result = await _controller.GetSyncRuleMappingsAsync(syncRuleId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetSyncRuleMappingsAsync_WithMappings_ReturnsMappingDtos()
    {
        var syncRuleId = 1;
        var syncRule = new SyncRule { Id = syncRuleId, Name = "Test Rule" };
        var mvAttr = new MetaverseAttribute { Id = 5, Name = "DisplayName" };
        var mapping = new SyncRuleMapping
        {
            Id = 10,
            Created = DateTime.UtcNow,
            SyncRule = syncRule,
            TargetMetaverseAttribute = mvAttr,
            TargetMetaverseAttributeId = 5
        };

        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(syncRuleId))
            .ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingsAsync(syncRuleId))
            .ReturnsAsync(new List<SyncRuleMapping> { mapping });

        var result = await _controller.GetSyncRuleMappingsAsync(syncRuleId) as OkObjectResult;
        var dtos = (result?.Value as IEnumerable<SyncRuleMappingDto>)?.ToList();

        Assert.That(dtos, Is.Not.Null);
        Assert.That(dtos!.Count, Is.EqualTo(1));
        Assert.That(dtos[0].Id, Is.EqualTo(10));
        Assert.That(dtos[0].TargetMetaverseAttributeId, Is.EqualTo(5));
        Assert.That(dtos[0].TargetMetaverseAttributeName, Is.EqualTo("DisplayName"));
    }

    [Test]
    public async Task GetSyncRuleMappingsAsync_WithEmptyMappings_ReturnsEmptyList()
    {
        var syncRuleId = 1;
        var syncRule = new SyncRule { Id = syncRuleId, Name = "Test Rule" };

        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(syncRuleId))
            .ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingsAsync(syncRuleId))
            .ReturnsAsync(new List<SyncRuleMapping>());

        var result = await _controller.GetSyncRuleMappingsAsync(syncRuleId) as OkObjectResult;
        var dtos = (result?.Value as IEnumerable<SyncRuleMappingDto>)?.ToList();

        Assert.That(dtos, Is.Not.Null);
        Assert.That(dtos!.Count, Is.EqualTo(0));
    }

    #endregion

    #region GetSyncRuleMappingAsync tests

    [Test]
    public async Task GetSyncRuleMappingAsync_WithValidIds_ReturnsOkResult()
    {
        var syncRuleId = 1;
        var mappingId = 10;
        var syncRule = new SyncRule { Id = syncRuleId, Name = "Test Rule" };
        var mapping = new SyncRuleMapping
        {
            Id = mappingId,
            SyncRule = syncRule
        };

        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(syncRuleId))
            .ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingAsync(mappingId))
            .ReturnsAsync(mapping);

        var result = await _controller.GetSyncRuleMappingAsync(syncRuleId, mappingId);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetSyncRuleMappingAsync_WithNonExistentSyncRule_ReturnsNotFound()
    {
        var syncRuleId = 999;
        var mappingId = 10;

        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(syncRuleId))
            .ReturnsAsync((SyncRule?)null);

        var result = await _controller.GetSyncRuleMappingAsync(syncRuleId, mappingId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetSyncRuleMappingAsync_WithNonExistentMapping_ReturnsNotFound()
    {
        var syncRuleId = 1;
        var mappingId = 999;
        var syncRule = new SyncRule { Id = syncRuleId, Name = "Test Rule" };

        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(syncRuleId))
            .ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingAsync(mappingId))
            .ReturnsAsync((SyncRuleMapping?)null);

        var result = await _controller.GetSyncRuleMappingAsync(syncRuleId, mappingId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetSyncRuleMappingAsync_WithMappingFromDifferentRule_ReturnsNotFound()
    {
        var syncRuleId = 1;
        var mappingId = 10;
        var syncRule = new SyncRule { Id = syncRuleId, Name = "Test Rule" };
        var differentSyncRule = new SyncRule { Id = 2, Name = "Different Rule" };
        var mapping = new SyncRuleMapping
        {
            Id = mappingId,
            SyncRule = differentSyncRule
        };

        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(syncRuleId))
            .ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingAsync(mappingId))
            .ReturnsAsync(mapping);

        var result = await _controller.GetSyncRuleMappingAsync(syncRuleId, mappingId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetSyncRuleMappingAsync_ReturnsMappingDto()
    {
        var syncRuleId = 1;
        var mappingId = 10;
        var syncRule = new SyncRule { Id = syncRuleId, Name = "Test Rule" };
        var mvAttr = new MetaverseAttribute { Id = 5, Name = "Email" };
        var mapping = new SyncRuleMapping
        {
            Id = mappingId,
            SyncRule = syncRule,
            TargetMetaverseAttribute = mvAttr,
            TargetMetaverseAttributeId = 5
        };

        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(syncRuleId))
            .ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingAsync(mappingId))
            .ReturnsAsync(mapping);

        var result = await _controller.GetSyncRuleMappingAsync(syncRuleId, mappingId) as OkObjectResult;
        var dto = result?.Value as SyncRuleMappingDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Id, Is.EqualTo(10));
        Assert.That(dto.TargetMetaverseAttributeId, Is.EqualTo(5));
        Assert.That(dto.TargetMetaverseAttributeName, Is.EqualTo("Email"));
    }

    #endregion

    #region DeleteSyncRuleMappingAsync tests

    [Test]
    public async Task DeleteSyncRuleMappingAsync_WithValidIds_ReturnsNoContent()
    {
        var syncRuleId = 1;
        var mappingId = 10;
        var syncRule = new SyncRule { Id = syncRuleId, Name = "Test Rule" };
        var mapping = new SyncRuleMapping
        {
            Id = mappingId,
            SyncRule = syncRule
        };

        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(syncRuleId))
            .ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingAsync(mappingId))
            .ReturnsAsync(mapping);

        var result = await _controller.DeleteSyncRuleMappingAsync(syncRuleId, mappingId);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task DeleteSyncRuleMappingAsync_WithNonExistentSyncRule_ReturnsNotFound()
    {
        var syncRuleId = 999;
        var mappingId = 10;

        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(syncRuleId))
            .ReturnsAsync((SyncRule?)null);

        var result = await _controller.DeleteSyncRuleMappingAsync(syncRuleId, mappingId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task DeleteSyncRuleMappingAsync_WithNonExistentMapping_ReturnsNotFound()
    {
        var syncRuleId = 1;
        var mappingId = 999;
        var syncRule = new SyncRule { Id = syncRuleId, Name = "Test Rule" };

        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(syncRuleId))
            .ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingAsync(mappingId))
            .ReturnsAsync((SyncRuleMapping?)null);

        var result = await _controller.DeleteSyncRuleMappingAsync(syncRuleId, mappingId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task DeleteSyncRuleMappingAsync_WithMappingFromDifferentRule_ReturnsNotFound()
    {
        var syncRuleId = 1;
        var mappingId = 10;
        var syncRule = new SyncRule { Id = syncRuleId, Name = "Test Rule" };
        var differentSyncRule = new SyncRule { Id = 2, Name = "Different Rule" };
        var mapping = new SyncRuleMapping
        {
            Id = mappingId,
            SyncRule = differentSyncRule
        };

        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(syncRuleId))
            .ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingAsync(mappingId))
            .ReturnsAsync(mapping);

        var result = await _controller.DeleteSyncRuleMappingAsync(syncRuleId, mappingId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task DeleteSyncRuleMappingAsync_CallsRepositoryDelete()
    {
        var syncRuleId = 1;
        var mappingId = 10;
        var syncRule = new SyncRule { Id = syncRuleId, Name = "Test Rule" };
        var mapping = new SyncRuleMapping
        {
            Id = mappingId,
            SyncRule = syncRule
        };

        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(syncRuleId))
            .ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingAsync(mappingId))
            .ReturnsAsync(mapping);

        await _controller.DeleteSyncRuleMappingAsync(syncRuleId, mappingId);

        _mockConnectedSystemRepo.Verify(r => r.DeleteSyncRuleMappingAsync(mapping), Times.Once);
    }

    #endregion
}
