// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Connectors.LDAP;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using System.Text.Json;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Covers <see cref="JIM.Application.Servers.ConnectedSystemServer.GetConnectedSystemDetectedCapabilitiesAsync"/>
/// (issue #231): the app-layer method resolves the Connected System's Connector and dispatches to it, without
/// JIM.Application ever needing to understand the persisted connector data itself.
/// </summary>
[TestFixture]
public class ConnectedSystemDetectedCapabilitiesTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _jim = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    [Test]
    public async Task GetConnectedSystemDetectedCapabilitiesAsync_UnknownConnectedSystemId_ReturnsNullAsync()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync((ConnectedSystem?)null);

        var result = await _jim.ConnectedSystems.GetConnectedSystemDetectedCapabilitiesAsync(999);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetConnectedSystemDetectedCapabilitiesAsync_ConnectorWithoutDetectedCapabilitiesSupport_ReturnsNullAsync()
    {
        // The File Connector does not implement IConnectorDetectedCapabilities. Null (rather than an empty
        // list) tells the UI to hide the Directory Capabilities card entirely: an empty list means "supported,
        // nothing detected yet", which would show a misleading "will appear after a connection" hint on
        // Connectors that will never detect anything.
        var connectedSystem = CreateConnectedSystem(ConnectorConstants.FileConnectorName, persistedConnectorData: null);
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(connectedSystem.Id, It.IsAny<bool>()))
            .ReturnsAsync(connectedSystem);

        var result = await _jim.ConnectedSystems.GetConnectedSystemDetectedCapabilitiesAsync(connectedSystem.Id);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetConnectedSystemDetectedCapabilitiesAsync_LdapConnectorNoPersistedDataYet_ReturnsEmptyListAsync()
    {
        var connectedSystem = CreateConnectedSystem(ConnectorConstants.LdapConnectorName, persistedConnectorData: null);
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(connectedSystem.Id, It.IsAny<bool>()))
            .ReturnsAsync(connectedSystem);

        var result = await _jim.ConnectedSystems.GetConnectedSystemDetectedCapabilitiesAsync(connectedSystem.Id);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetConnectedSystemDetectedCapabilitiesAsync_LdapConnectorWithPersistedData_ReturnsMappedCapabilitiesAsync()
    {
        var rootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.OpenLDAP,
            VendorName = "OpenLDAP Foundation",
            DnsHostName = "ldap1.example.org"
        };
        var connectedSystem = CreateConnectedSystem(ConnectorConstants.LdapConnectorName, JsonSerializer.Serialize(rootDse));
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(connectedSystem.Id, It.IsAny<bool>()))
            .ReturnsAsync(connectedSystem);

        var result = await _jim.ConnectedSystems.GetConnectedSystemDetectedCapabilitiesAsync(connectedSystem.Id);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Select(c => c.Name), Is.EqualTo(new[] { "Directory Type", "Vendor", "DNS Host Name", "Paging" }));
        Assert.That(result!.Single(c => c.Name == "Directory Type").Value, Is.EqualTo("OpenLDAP"));
        Assert.That(result!.Single(c => c.Name == "Vendor").Value, Is.EqualTo("OpenLDAP Foundation"));
    }

    private static ConnectedSystem CreateConnectedSystem(string connectorName, string? persistedConnectorData)
    {
        return new ConnectedSystem
        {
            Id = 7,
            Name = "Test Connected System",
            ConnectorDefinition = new ConnectorDefinition { Name = connectorName },
            PersistedConnectorData = persistedConnectorData
        };
    }
}
