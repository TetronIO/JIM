// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using System.Linq;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Covers <c>GET /connected-systems/{id}/directory-servers</c> (issue #1167): an unknown Connected System id and
/// a Connector that does not support directory server discovery are both reported as clean 4xx responses, never
/// a 500. The success (200) path needs a live Active Directory / Samba AD directory to discover against, so it
/// is not covered here; <see cref="ConnectedSystemDirectoryServerDtoTests"/> covers the response shape a
/// successful discovery maps to, and <c>ConnectedSystemDirectoryServerDiscoveryTests</c> /
/// <c>LdapConnectorDirectoryServerDiscoveryTests</c> (JIM.Worker.Tests) cover the dispatch and DN-mapping logic
/// behind it.
/// </summary>
[TestFixture]
public class SynchronisationControllerDirectoryServersTests
{
    private const int ConnectedSystemId = 42;

    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        var mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);

        _application = new JimApplication(mockRepository.Object);

        _controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            _application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task GetConnectedSystemDirectoryServersAsync_UnknownConnectedSystem_ReturnsNotFoundAsync()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync((ConnectedSystem?)null);

        var result = await _controller.GetConnectedSystemDirectoryServersAsync(ConnectedSystemId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetConnectedSystemDirectoryServersAsync_ConnectorWithoutTheCapability_ReturnsBadRequestAsync()
    {
        // The File Connector does not implement IConnectorDirectoryServers.
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(CreateFileConnectorConnectedSystem());

        var result = await _controller.GetConnectedSystemDirectoryServersAsync(ConnectedSystemId);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    private ConnectedSystem CreateFileConnectorConnectedSystem()
    {
        var connectorDefinition = new ConnectorDefinition { Id = 1, Name = ConnectorConstants.FileConnectorName };
        _application.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(new FileConnector(), connectorDefinition);

        return new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Test File System",
            ConnectorDefinition = connectorDefinition,
            SettingValues = connectorDefinition.Settings.Select(s => new ConnectedSystemSettingValue
            {
                Setting = s,
                StringValue = s.DefaultStringValue
            }).ToList()
        };
    }
}
