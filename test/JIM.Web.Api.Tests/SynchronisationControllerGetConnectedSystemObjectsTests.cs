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
using JIM.Models.Staging.DTOs;
using JIM.Models.Utility;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for SynchronisationController.GetConnectedSystemObjectsAsync, the paginated Connected
/// System Object list endpoint (issue #154, Phase 2).
/// </summary>
[TestFixture]
public class SynchronisationControllerGetConnectedSystemObjectsTests
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

    [Test]
    public async Task GetConnectedSystemObjectsAsync_ConnectedSystemExists_ReturnsPaginatedHeadersAsync()
    {
        // Arrange
        var system = new ConnectedSystem { Id = 42, Name = "Active Directory" };
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(42, It.IsAny<bool>())).ReturnsAsync(system);

        var headers = new List<ConnectedSystemObjectHeader>
        {
            new() { Id = Guid.NewGuid(), ConnectedSystemId = 42, DisplayName = "Alice" },
            new() { Id = Guid.NewGuid(), ConnectedSystemId = 42, DisplayName = "Bob" }
        };
        var pagedResult = new PagedResultSet<ConnectedSystemObjectHeader>
        {
            Results = headers,
            TotalResults = 2,
            CurrentPage = 1,
            PageSize = 50
        };
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemObjectHeadersAsync(
                42, 1, 50, null, null, true, null, null, null))
            .ReturnsAsync(pagedResult);

        // Act
        var result = await _controller.GetConnectedSystemObjectsAsync(42, page: 1, pageSize: 50);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var response = (PaginatedResponse<ConnectedSystemObjectHeader>)((OkObjectResult)result).Value!;
        Assert.That(response.TotalCount, Is.EqualTo(2));
        Assert.That(response.Items, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task GetConnectedSystemObjectsAsync_ConnectedSystemDoesNotExist_ReturnsNotFoundAsync()
    {
        // Arrange
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(999, It.IsAny<bool>())).ReturnsAsync((ConnectedSystem?)null);

        // Act
        var result = await _controller.GetConnectedSystemObjectsAsync(999, page: 1, pageSize: 50);

        // Assert
        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        var error = (ApiErrorResponse)((NotFoundObjectResult)result).Value!;
        Assert.That(error.Code, Is.EqualTo(ApiErrorCodes.NotFound));
    }

    [Test]
    public async Task GetConnectedSystemObjectsAsync_FiltersProvided_PassesThemToRepositoryAsync()
    {
        // Arrange
        var system = new ConnectedSystem { Id = 7, Name = "HR" };
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(7, It.IsAny<bool>())).ReturnsAsync(system);

        var pagedResult = new PagedResultSet<ConnectedSystemObjectHeader>
        {
            Results = new List<ConnectedSystemObjectHeader>(),
            TotalResults = 0,
            CurrentPage = 2,
            PageSize = 10
        };

        var statusFilter = new List<ConnectedSystemObjectStatus> { ConnectedSystemObjectStatus.Obsolete };
        var objectTypeFilter = new List<int> { 3 };
        var joinTypeFilter = new List<ConnectedSystemObjectJoinType> { ConnectedSystemObjectJoinType.NotJoined };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemObjectHeadersAsync(
                7, 2, 10, "alice", "DisplayName", false, statusFilter, objectTypeFilter, joinTypeFilter))
            .ReturnsAsync(pagedResult);

        // Act
        var result = await _controller.GetConnectedSystemObjectsAsync(
            7, page: 2, pageSize: 10, search: "alice", sortBy: "DisplayName", sortDescending: false,
            status: statusFilter, objectTypeId: objectTypeFilter, joinType: joinTypeFilter);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockConnectedSystemRepo.Verify(r => r.GetConnectedSystemObjectHeadersAsync(
            7, 2, 10, "alice", "DisplayName", false, statusFilter, objectTypeFilter, joinTypeFilter), Times.Once);
    }
}
