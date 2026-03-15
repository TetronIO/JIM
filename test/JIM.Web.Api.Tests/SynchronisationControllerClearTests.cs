using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the Clear Connector Space API endpoint on SynchronisationController.
/// Verifies that the deleteChangeHistory query parameter is correctly passed through
/// to the application layer.
/// </summary>
[TestFixture]
public class SynchronisationControllerClearTests
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

        // Set up API key authentication context
        var apiKeyId = Guid.NewGuid();
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

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task ClearConnectorSpaceAsync_WithDeleteChangeHistoryTrue_PassesTrueToApplicationAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Active
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.DeleteAllConnectedSystemObjectsAndDependenciesAsync(1, true)).Returns(Task.CompletedTask);

        // Act
        var result = await _controller.ClearConnectorSpaceAsync(1, deleteChangeHistory: true);

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());
        _mockConnectedSystemRepo.Verify(r => r.DeleteAllConnectedSystemObjectsAndDependenciesAsync(1, true), Times.Once);
    }

    [Test]
    public async Task ClearConnectorSpaceAsync_WithDeleteChangeHistoryFalse_PassesFalseToApplicationAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Active
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.DeleteAllConnectedSystemObjectsAndDependenciesAsync(1, false)).Returns(Task.CompletedTask);

        // Act
        var result = await _controller.ClearConnectorSpaceAsync(1, deleteChangeHistory: false);

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());
        _mockConnectedSystemRepo.Verify(r => r.DeleteAllConnectedSystemObjectsAndDependenciesAsync(1, false), Times.Once);
    }

    [Test]
    public async Task ClearConnectorSpaceAsync_WithDefaultParameter_PassesTrueToApplicationAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Active
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.DeleteAllConnectedSystemObjectsAndDependenciesAsync(1, true)).Returns(Task.CompletedTask);

        // Act — call without specifying deleteChangeHistory, should default to true
        var result = await _controller.ClearConnectorSpaceAsync(1);

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());
        _mockConnectedSystemRepo.Verify(r => r.DeleteAllConnectedSystemObjectsAndDependenciesAsync(1, true), Times.Once);
    }

    [Test]
    public async Task ClearConnectorSpaceAsync_WithNonExistentSystem_ReturnsNotFoundAsync()
    {
        // Arrange
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(999)).ReturnsAsync((ConnectedSystem?)null);

        // Act
        var result = await _controller.ClearConnectorSpaceAsync(999);

        // Assert
        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task ClearConnectorSpaceAsync_WithDeletingStatus_ReturnsBadRequestAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Deleting
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);

        // Act
        var result = await _controller.ClearConnectorSpaceAsync(1);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }
}
