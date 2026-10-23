// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Covers the application-layer dispatch for Discover Domain Controllers (issue #1167): a Connector that does
/// not implement <see cref="JIM.Models.Interfaces.IConnectorDirectoryServers"/> is reported as unsupported rather
/// than the call bottoming out in a bare <see cref="NotImplementedException"/> or a null reference, and an
/// unknown Connected System id is reported distinctly from an unsupported Connector. The LDAP Connector's own
/// discovery behaviour against a live directory is covered by
/// <see cref="JIM.Worker.Tests.Connectors.LdapConnectorDirectoryServerDiscoveryTests"/> and the DN-parsing tests
/// alongside it; this class only proves the dispatch, not directory connectivity.
/// </summary>
[TestFixture]
public class ConnectedSystemDirectoryServerDiscoveryTests
{
    private const int ConnectedSystemId = 7;
    private const string StubDiscoveryConnectorName = "Stub Discovery Connector";

    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private JimApplication _jim = null!;
    private string _tempCsvPath = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);

        _jim = new JimApplication(mockRepository.Object);

        // FileConnector settings need a file to exist for import modes; the connector reads it as CSV regardless
        // of extension, and this test only needs the Connected System to be constructible.
        _tempCsvPath = Path.GetTempFileName();
        File.WriteAllText(_tempCsvPath, "id,displayName\n1,Test User\n");
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
        if (File.Exists(_tempCsvPath))
            File.Delete(_tempCsvPath);
    }

    #region GetConnectedSystemDirectoryServersAsync

    [Test]
    public void GetConnectedSystemDirectoryServersAsync_UnknownConnectedSystemId_ThrowsArgumentException()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync((ConnectedSystem?)null);

        var exception = Assert.ThrowsAsync<ArgumentException>(async () =>
            await _jim.ConnectedSystems.GetConnectedSystemDirectoryServersAsync(ConnectedSystemId));

        Assert.That(exception!.Message, Does.Contain(ConnectedSystemId.ToString()));
    }

    [Test]
    public void GetConnectedSystemDirectoryServersAsync_FileConnector_ThrowsNotSupportedException()
    {
        // The File Connector does not implement IConnectorDirectoryServers, so discovery is unsupported.
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(CreateFileConnectorConnectedSystem());

        var exception = Assert.ThrowsAsync<NotSupportedException>(async () =>
            await _jim.ConnectedSystems.GetConnectedSystemDirectoryServersAsync(ConnectedSystemId));

        Assert.That(exception!.Message, Does.Contain(ConnectorConstants.FileConnectorName));
    }

    [Test]
    public void GetConnectedSystemDirectoryServersAsync_UnknownConnectorDefinition_ThrowsNotSupportedException()
    {
        const string unknownConnectorName = "Nonexistent Connector";

        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(CreateUnknownConnectorConnectedSystem(unknownConnectorName));

        var exception = Assert.ThrowsAsync<NotSupportedException>(async () =>
            await _jim.ConnectedSystems.GetConnectedSystemDirectoryServersAsync(ConnectedSystemId));

        Assert.That(exception!.Message, Does.Contain(unknownConnectorName));
    }

    #endregion

    #region GetDirectoryServerDiscoveryAsync

    [Test]
    public async Task GetDirectoryServerDiscoveryAsync_FileConnector_ReturnsNullAsync()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(CreateFileConnectorConnectedSystem());

        var discovery = await _jim.ConnectedSystems.GetDirectoryServerDiscoveryAsync(ConnectedSystemId);

        Assert.That(discovery, Is.Null);
    }

    [Test]
    public async Task GetDirectoryServerDiscoveryAsync_LdapConnector_TargetsTheLdapConnectorsOwnSettingAsync()
    {
        var connectedSystem = CreateLdapConnectorConnectedSystem();
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(connectedSystem);

        var discovery = await _jim.ConnectedSystems.GetDirectoryServerDiscoveryAsync(ConnectedSystemId);

        // The target must be a free-text setting the Connector really declares: the portal only places the
        // action beside a String field, so naming anything else would leave the action nowhere.
        Assert.That(discovery, Is.Not.Null);
        var target = connectedSystem.SettingValues.SingleOrDefault(sv => sv.Setting.Name == discovery!.TargetSettingName);
        Assert.That(target?.Setting.Type, Is.EqualTo(ConnectedSystemSettingType.String));
    }

    [Test]
    public async Task GetDirectoryServerDiscoveryAsync_ConnectorTargetingAnotherSetting_ReturnsThatConnectorsTargetAsync()
    {
        // A second Connector implementing discovery, whose target setting shares nothing with the LDAP
        // Connector's: what comes back must be what the Connector declares, not an LDAP name.
        var connector = new Mock<IConnector>();
        connector.Setup(c => c.Name).Returns(StubDiscoveryConnectorName);
        connector.As<IConnectorDirectoryServers>()
            .Setup(c => c.DirectoryServerDiscovery)
            .Returns(new ConnectorDirectoryServerDiscovery("Preferred Server", "directory servers"));
        var connectorFactory = new Mock<IConnectorFactory>();
        connectorFactory
            .Setup(f => f.Create(StubDiscoveryConnectorName, It.IsAny<ICredentialProtection?>(), It.IsAny<ICertificateProvider?>()))
            .Returns(connector.Object);
        _jim.ConnectedSystems.ConnectorFactory = connectorFactory.Object;

        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(CreateUnknownConnectorConnectedSystem(StubDiscoveryConnectorName));

        var discovery = await _jim.ConnectedSystems.GetDirectoryServerDiscoveryAsync(ConnectedSystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(discovery?.TargetSettingName, Is.EqualTo("Preferred Server"));
            Assert.That(discovery?.ServerTerm, Is.EqualTo("directory servers"));
        }
    }

    [Test]
    public async Task GetDirectoryServerDiscoveryAsync_UnknownConnectorDefinition_ReturnsNullAsync()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(CreateUnknownConnectorConnectedSystem("Nonexistent Connector"));

        var discovery = await _jim.ConnectedSystems.GetDirectoryServerDiscoveryAsync(ConnectedSystemId);

        Assert.That(discovery, Is.Null);
    }

    [Test]
    public async Task GetDirectoryServerDiscoveryAsync_UnknownConnectedSystemId_ReturnsNullAsync()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync((ConnectedSystem?)null);

        var discovery = await _jim.ConnectedSystems.GetDirectoryServerDiscoveryAsync(ConnectedSystemId);

        Assert.That(discovery, Is.Null);
    }

    #endregion

    /// <summary>
    /// Builds a Connected System using the LDAP Connector's own setting definitions, mirroring how JIM creates
    /// setting values from a persisted connector definition.
    /// </summary>
    private ConnectedSystem CreateLdapConnectorConnectedSystem()
    {
        var connectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.LdapConnectorName };
        _jim.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(new JIM.Connectors.LDAP.LdapConnector(), connectorDefinition);

        return new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Test AD System",
            ConnectorDefinition = connectorDefinition,
            SettingValues = connectorDefinition.Settings.Select(s => new ConnectedSystemSettingValue
            {
                Setting = s,
                StringValue = s.DefaultStringValue
            }).ToList()
        };
    }

    /// <summary>
    /// Builds a Connected System using the File Connector's own setting definitions, mirroring how JIM creates
    /// setting values from a persisted connector definition.
    /// </summary>
    private ConnectedSystem CreateFileConnectorConnectedSystem()
    {
        var connectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.FileConnectorName };
        _jim.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(new FileConnector(), connectorDefinition);

        var connectedSystem = new ConnectedSystem
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

        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "File Path").StringValue = _tempCsvPath;
        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "Object Type").StringValue = "user";
        return connectedSystem;
    }

    /// <summary>
    /// Builds a Connected System whose Connector Definition does not correspond to any built-in connector.
    /// </summary>
    private static ConnectedSystem CreateUnknownConnectorConnectedSystem(string connectorName)
    {
        var connectorDefinition = new ConnectorDefinition { Name = connectorName };
        var setting = new ConnectorDefinitionSetting { Name = "Dummy Setting", Type = ConnectedSystemSettingType.Text };
        connectorDefinition.Settings.Add(setting);

        return new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Test Unsupported System",
            ConnectorDefinition = connectorDefinition,
            SettingValues =
            [
                new ConnectedSystemSettingValue { Setting = setting, StringValue = "value" }
            ]
        };
    }
}
