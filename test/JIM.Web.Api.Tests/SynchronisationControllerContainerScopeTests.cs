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
/// A Container's scope decides how far beneath it objects are imported from, so it has to be settable from the API
/// and readable back out of it: an administrator who scripts JIM cannot be left having to click the portal for one
/// field. These cover the update endpoint's handling of the scope field and the DTO that reports it.
/// </summary>
[TestFixture]
public class SynchronisationControllerContainerScopeTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepo = null!;
    private SynchronisationController _controller = null!;

    private const int ConnectedSystemId = 1;
    private const int ContainerId = 20;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        // Container selection is audited configuration, so the update records an Activity and a snapshot on its
        // way through; without these the call fails before it reaches anything worth asserting on.
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepo.Object);

        var application = new JimApplication(_mockRepository.Object);
        var expressionEvaluator = new DynamicExpressoEvaluator();
        var credentialProtection = new Mock<ICredentialProtectionService>();
        _controller = new SynchronisationController(new Mock<ILogger<SynchronisationController>>().Object, application, expressionEvaluator, credentialProtection.Object);

        var apiKeyId = Guid.NewGuid();
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId, Name = "TestApiKey", KeyHash = "h", KeyPrefix = "t", IsEnabled = true, Created = DateTime.UtcNow
        });

        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new(ClaimTypes.Name, "TestApiKey")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
    }

    [Test]
    public async Task UpdateConnectedSystemContainerAsync_WithOneLevelScope_SetsTheScopeAsync()
    {
        var container = SetUpContainer();

        var result = await _controller.UpdateConnectedSystemContainerAsync(ConnectedSystemId, ContainerId,
            new UpdateConnectedSystemContainerRequest { Scope = ConnectedSystemContainerScope.OneLevel });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(container.Scope, Is.EqualTo(ConnectedSystemContainerScope.OneLevel));
        });
    }

    [Test]
    public async Task UpdateConnectedSystemContainerAsync_WithoutScope_LeavesTheScopeAloneAsync()
    {
        // Omitting a field on a partial update means "leave unchanged"; a caller toggling selection must not
        // silently widen a container back to Subtree.
        var container = SetUpContainer(ConnectedSystemContainerScope.OneLevel);

        await _controller.UpdateConnectedSystemContainerAsync(ConnectedSystemId, ContainerId,
            new UpdateConnectedSystemContainerRequest { Selected = true });

        Assert.That(container.Scope, Is.EqualTo(ConnectedSystemContainerScope.OneLevel));
    }

    [Test]
    public async Task UpdateConnectedSystemContainerAsync_WithScope_ReportsItOnTheResponseAsync()
    {
        SetUpContainer();

        var result = await _controller.UpdateConnectedSystemContainerAsync(ConnectedSystemId, ContainerId,
            new UpdateConnectedSystemContainerRequest { Scope = ConnectedSystemContainerScope.OneLevel });

        var dto = (result as OkObjectResult)?.Value as ConnectedSystemContainerDto;
        Assert.That(dto?.Scope, Is.EqualTo(ConnectedSystemContainerScope.OneLevel));
    }

    [Test]
    public void ConnectedSystemContainerDto_FromEntity_CarriesTheScope()
    {
        var entity = new ConnectedSystemContainer
        {
            Id = ContainerId,
            Name = "OU=Users",
            ExternalId = "OU=Users,DC=example,DC=local",
            Selected = true,
            Scope = ConnectedSystemContainerScope.OneLevel
        };

        var dto = ConnectedSystemContainerDto.FromEntity(entity);

        Assert.That(dto.Scope, Is.EqualTo(ConnectedSystemContainerScope.OneLevel));
    }

    /// <summary>
    /// Wires up a Connected System holding one selected Container, and returns the Container instance the
    /// controller will mutate.
    /// </summary>
    private ConnectedSystemContainer SetUpContainer(ConnectedSystemContainerScope scope = ConnectedSystemContainerScope.Subtree)
    {
        var connectedSystem = new ConnectedSystem { Id = ConnectedSystemId, Name = "Corporate Directory", ConnectorDefinitionId = 1 };
        var container = new ConnectedSystemContainer
        {
            Id = ContainerId,
            Name = "OU=Users",
            ExternalId = "OU=Users,DC=example,DC=local",
            Selected = true,
            Scope = scope,
            ConnectedSystem = connectedSystem
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId)).ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemContainerAsync(ContainerId)).ReturnsAsync(container);

        return container;
    }
}
