// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Connectors;
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
/// Tests for SynchronisationController.GetConnectedSystemCapabilitiesAsync (issue #231): the REST surface of
/// the Directory Capabilities card, returning 404 for an unknown Connected System, an empty list when the
/// Connector has detected nothing (or does not support detection), and the mapped facts otherwise.
/// </summary>
[TestFixture]
public class SynchronisationControllerGetConnectedSystemCapabilitiesTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
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
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);

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
    public async Task GetConnectedSystemCapabilitiesAsync_UnknownConnectedSystem_ReturnsNotFoundAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(99, It.IsAny<bool>())).ReturnsAsync((ConnectedSystem?)null);

        var result = await _controller.GetConnectedSystemCapabilitiesAsync(99);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetConnectedSystemCapabilitiesAsync_NoDataDetectedYet_ReturnsEmptyListAsync()
    {
        var system = new ConnectedSystem
        {
            Id = 5,
            Name = "Directory",
            ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = ConnectorConstants.LdapConnectorName },
            PersistedConnectorData = null
        };
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(5, It.IsAny<bool>())).ReturnsAsync(system);

        var result = await _controller.GetConnectedSystemCapabilitiesAsync(5);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var dtos = (IEnumerable<ConnectorCapabilityDto>)((OkObjectResult)result).Value!;
        Assert.That(dtos, Is.Empty);
    }

    [Test]
    public async Task GetConnectedSystemCapabilitiesAsync_DataDetected_ReturnsMappedCapabilitiesAsync()
    {
        // Mirrors LdapConnectorRootDse's JSON shape without depending on the internal type (JIM.Web.Api.Tests
        // is not granted InternalsVisibleTo by JIM.Connectors, unlike JIM.Worker.Tests). DirectoryType 0 is
        // ActiveDirectory.
        const string persistedConnectorData = """{"DnsHostName":"dc1.contoso.local","DirectoryType":0,"VendorName":"Microsoft"}""";
        var system = new ConnectedSystem
        {
            Id = 6,
            Name = "Active Directory",
            ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = ConnectorConstants.LdapConnectorName },
            PersistedConnectorData = persistedConnectorData
        };
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(6, It.IsAny<bool>())).ReturnsAsync(system);

        var result = await _controller.GetConnectedSystemCapabilitiesAsync(6);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var dtos = ((IEnumerable<ConnectorCapabilityDto>)((OkObjectResult)result).Value!).ToList();
        Assert.That(dtos.Select(d => d.Name), Is.EqualTo(new[] { "Directory Type", "Vendor", "DNS Host Name", "Paging" }));
        Assert.That(dtos.Single(d => d.Name == "Directory Type").Value, Is.EqualTo("Active Directory"));
        Assert.That(dtos.Single(d => d.Name == "Paging").Value, Is.EqualTo("Supported"));
    }
}
