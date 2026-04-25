// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Interfaces;
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
/// Tests for SynchronisationController.GetConnectedSystemAsync. Verifies that the returned
/// ConnectedSystemDetailDto.ObjectCount reflects a real database count rather than the
/// in-memory navigation property, which is not loaded by GetConnectedSystemAsync.
/// </summary>
[TestFixture]
public class SynchronisationControllerGetConnectedSystemTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
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
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockLogger = new Mock<ILogger<SynchronisationController>>();
        _mockCredentialProtection = new Mock<ICredentialProtectionService>();
        _expressionEvaluator = new DynamicExpressoEvaluator();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new SynchronisationController(_mockLogger.Object, _application, _expressionEvaluator, _mockCredentialProtection.Object);

        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "Administrator")
        };
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };
    }

    [TearDown]
    public void TearDown()
    {
        _application.Dispose();
    }

    /// <summary>
    /// Reproduces the bug where ConnectedSystemDetailDto.ObjectCount silently returned 0
    /// because GetConnectedSystemAsync did not load the Objects navigation property.
    /// The controller must compute the count via a dedicated query, the same way it already
    /// does for PendingExportCount.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemAsync_ReturnsObjectCountFromCountQueryNotEntityCollectionAsync()
    {
        // Arrange: simulate the real EF behaviour where Objects is not loaded.
        var system = new ConnectedSystem
        {
            Id = 42,
            Name = "Active Directory",
            ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = "LDAP" },
            ObjectTypes = new List<ConnectedSystemObjectType>(),
            Objects = new List<ConnectedSystemObject>() // empty / not loaded
        };
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(42, It.IsAny<bool>())).ReturnsAsync(system);
        _mockConnectedSystemRepo.Setup(r => r.GetPendingExportsCountAsync(42)).ReturnsAsync(0);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(42, null)).ReturnsAsync(12345);

        // Act
        var result = await _controller.GetConnectedSystemAsync(42);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var dto = (ConnectedSystemDetailDto)((OkObjectResult)result).Value!;
        Assert.That(dto.ObjectCount, Is.EqualTo(12345),
            "ObjectCount must be sourced from the dedicated count query, not from the unloaded Objects navigation");
        _mockConnectedSystemRepo.Verify(r => r.GetConnectedSystemObjectCountAsync(42, null), Times.Once);
    }
}
