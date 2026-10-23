// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Covers <c>GET /connected-systems/{id}/directory-servers</c> (issue #1167): an unknown Connected System id and
/// a Connector that does not support directory server discovery are both reported as clean 4xx responses, never
/// a 500, and a successful discovery (through a stub Connector) names the setting each server fills.
/// <see cref="ConnectedSystemDirectoryServerDtoTests"/> covers the response shape, and <c>ConnectedSystemDirectoryServerDiscoveryTests</c> /
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

    [Test]
    public async Task GetConnectedSystemDirectoryServersAsync_DiscoverySucceeds_NamesTheSettingEachServerFillsAsync()
    {
        // A script needs the setting's id, not just its name, because Set-JIMConnectedSystem keys settings by id.
        const string stubConnectorName = "Stub Discovery Connector";
        const int targetSettingId = 9;
        var connector = new Mock<IConnector>();
        connector.Setup(c => c.Name).Returns(stubConnectorName);
        var discovery = connector.As<IConnectorDirectoryServers>();
        discovery.Setup(c => c.DirectoryServerDiscovery).Returns(new ConnectorDirectoryServerDiscovery("Preferred Server", "directory servers"));
        discovery.Setup(c => c.GetDirectoryServersAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), It.IsAny<Serilog.ILogger>()))
            .ReturnsAsync([new ConnectorDirectoryServer { HostName = "server01.example.local", Site = "London" }]);
        var connectorFactory = new Mock<IConnectorFactory>();
        connectorFactory
            .Setup(f => f.Create(stubConnectorName, It.IsAny<ICredentialProtection?>(), It.IsAny<ICertificateProvider?>()))
            .Returns(connector.Object);
        _application.ConnectedSystems.ConnectorFactory = connectorFactory.Object;

        var settings = new List<ConnectorDefinitionSetting>
        {
            new() { Id = 8, Name = "Host", Type = ConnectedSystemSettingType.String, Category = ConnectedSystemSettingCategory.Connectivity },
            new() { Id = targetSettingId, Name = "Preferred Server", Type = ConnectedSystemSettingType.String, Category = ConnectedSystemSettingCategory.Connectivity }
        };
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(() => new ConnectedSystem
            {
                Id = ConnectedSystemId,
                Name = "Stub",
                ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = stubConnectorName, Settings = settings },
                SettingValues = settings.Select(s => new ConnectedSystemSettingValue { Setting = s }).ToList()
            });

        var result = await _controller.GetConnectedSystemDirectoryServersAsync(ConnectedSystemId);

        var servers = ((result as OkObjectResult)?.Value as IEnumerable<ConnectedSystemDirectoryServerDto>)?.ToList();
        Assert.That(servers, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(servers![0].HostName, Is.EqualTo("server01.example.local"));
            Assert.That(servers![0].TargetSettingId, Is.EqualTo(targetSettingId));
            Assert.That(servers![0].TargetSettingName, Is.EqualTo("Preferred Server"));
        }
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
