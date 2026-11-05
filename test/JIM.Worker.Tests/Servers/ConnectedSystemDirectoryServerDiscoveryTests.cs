// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Connectors.LDAP;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.TestSupport;
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

        mockRepository.Setup(r => r.ServiceSettings).Returns(InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled());
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

    #region GetDirectoryServerDiscoveriesAsync

    [Test]
    public async Task GetDirectoryServerDiscoveriesAsync_FileConnector_IsEmptyAsync()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(CreateFileConnectorConnectedSystem());

        var discoveries = await _jim.ConnectedSystems.GetDirectoryServerDiscoveriesAsync(ConnectedSystemId);

        Assert.That(discoveries, Is.Empty);
    }

    [Test]
    public async Task GetDirectoryServerDiscoveriesAsync_LdapConnector_OffersDomainControllersAndExchangeServersEachForItsOwnSettingAsync()
    {
        var connectedSystem = CreateLdapConnectorConnectedSystem();
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(connectedSystem);

        var discoveries = await _jim.ConnectedSystems.GetDirectoryServerDiscoveriesAsync(ConnectedSystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(discoveries.Select(d => (d.TargetSettingName, d.ServerTerm)), Is.EqualTo(new[]
            {
                ("Preferred Domain Controller", "domain controllers"),
                (LdapConnectorConstants.SETTING_EXCHANGE_SERVER, "Exchange servers")
            }));

            // Each target must be a free-text setting the Connector really declares: the portal only places the
            // action beside a String field, so naming anything else would leave the action nowhere.
            foreach (var discovery in discoveries)
            {
                var target = connectedSystem.SettingValues.SingleOrDefault(sv => sv.Setting.Name == discovery.TargetSettingName);
                Assert.That(target?.Setting.Type, Is.EqualTo(ConnectedSystemSettingType.String), discovery.TargetSettingName);
            }
        }
    }

    [Test]
    public async Task GetDirectoryServerDiscoveriesAsync_TargetSettingBehindAFlagThatIsOff_LeavesThatDiscoveryOutAsync()
    {
        // Exchange server is behind the Exchange recipients flag (#2021): while it is off, the setting is never offered,
        // so neither is its Discover action.
        UseFeatureFlags(on: false);
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(CreateLdapConnectorConnectedSystem());

        var discoveries = await _jim.ConnectedSystems.GetDirectoryServerDiscoveriesAsync(ConnectedSystemId);

        Assert.That(discoveries.Select(d => d.TargetSettingName), Is.EqualTo(new[] { "Preferred Domain Controller" }));
    }

    [Test]
    public async Task GetDirectoryServerDiscoveriesAsync_ConnectorTargetingAnotherSetting_ReturnsThatConnectorsTargetAsync()
    {
        // A second Connector implementing discovery, whose target setting shares nothing with the LDAP
        // Connector's: what comes back must be what the Connector declares, not an LDAP name.
        UseStubConnector(new ConnectorDirectoryServerDiscovery("Preferred Server", "directory servers"));

        var discoveries = await _jim.ConnectedSystems.GetDirectoryServerDiscoveriesAsync(ConnectedSystemId);

        Assert.That(discoveries.Select(d => (d.TargetSettingName, d.ServerTerm)), Is.EqualTo(new[] { ("Preferred Server", "directory servers") }));
    }

    [Test]
    public async Task GetDirectoryServerDiscoveriesAsync_UnknownConnectorDefinition_IsEmptyAsync()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(CreateUnknownConnectorConnectedSystem("Nonexistent Connector"));

        var discoveries = await _jim.ConnectedSystems.GetDirectoryServerDiscoveriesAsync(ConnectedSystemId);

        Assert.That(discoveries, Is.Empty);
    }

    [Test]
    public async Task GetDirectoryServerDiscoveriesAsync_UnknownConnectedSystemId_IsEmptyAsync()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync((ConnectedSystem?)null);

        var discoveries = await _jim.ConnectedSystems.GetDirectoryServerDiscoveriesAsync(ConnectedSystemId);

        Assert.That(discoveries, Is.Empty);
    }

    #endregion

    #region GetConnectedSystemDirectoryServersAsync for a setting

    [Test]
    public async Task GetConnectedSystemDirectoryServersAsync_ForASetting_AsksTheConnectorForThatSettingsServersAsync()
    {
        var connector = UseStubConnector(
            new ConnectorDirectoryServerDiscovery("Preferred Server", "directory servers"),
            new ConnectorDirectoryServerDiscovery("Mail Server", "mail servers"));
        connector.Setup(c => c.GetDirectoryServersAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), "Mail Server", It.IsAny<Serilog.ILogger>()))
            .ReturnsAsync(new ConnectorDirectoryServerDiscoveryResult(null, [new ConnectorDirectoryServer { HostName = "mail1.example.org", Version = "Version 15.2" }]));

        var servers = (await _jim.ConnectedSystems.GetConnectedSystemDirectoryServersAsync(ConnectedSystemId, "Mail Server")).Servers;

        Assert.That(servers.Select(s => (s.HostName, s.Version)), Is.EqualTo(new[] { ("mail1.example.org", (string?)"Version 15.2") }));
    }

    [Test]
    public async Task GetConnectedSystemDirectoryServersAsync_NoSettingNamed_AsksForTheFirstDiscoveryAsync()
    {
        var connector = UseStubConnector(
            new ConnectorDirectoryServerDiscovery("Preferred Server", "directory servers"),
            new ConnectorDirectoryServerDiscovery("Mail Server", "mail servers"));
        connector.Setup(c => c.GetDirectoryServersAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), "Preferred Server", It.IsAny<Serilog.ILogger>()))
            .ReturnsAsync(new ConnectorDirectoryServerDiscoveryResult(null, [new ConnectorDirectoryServer { HostName = "dc1.example.org" }]));

        var servers = (await _jim.ConnectedSystems.GetConnectedSystemDirectoryServersAsync(ConnectedSystemId)).Servers;

        Assert.That(servers.Single().HostName, Is.EqualTo("dc1.example.org"));
    }

    [Test]
    public void GetConnectedSystemDirectoryServersAsync_SettingWithNoDiscovery_ThrowsNotSupportedNamingIt()
    {
        UseStubConnector(new ConnectorDirectoryServerDiscovery("Preferred Server", "directory servers"));

        Assert.That(async () => await _jim.ConnectedSystems.GetConnectedSystemDirectoryServersAsync(ConnectedSystemId, "Host"),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("Host"));
    }

    #endregion

    private void UseFeatureFlags(bool on)
    {
        var mockRepository = new Mock<IRepository>();
        mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        mockRepository.Setup(r => r.ServiceSettings).Returns(on
            ? InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled()
            : new InMemoryServiceSettingsRepository());
        _jim.Dispose();
        _jim = new JimApplication(mockRepository.Object);
    }

    /// <summary>
    /// A Connector whose discoveries are the ones given, each target a String setting the Connected System carries.
    /// </summary>
    private Mock<IConnectorDirectoryServers> UseStubConnector(params ConnectorDirectoryServerDiscovery[] discoveries)
    {
        var connector = new Mock<IConnector>();
        connector.Setup(c => c.Name).Returns(StubDiscoveryConnectorName);
        var directoryServers = connector.As<IConnectorDirectoryServers>();
        directoryServers.Setup(c => c.DirectoryServerDiscoveries).Returns(discoveries);
        var connectorFactory = new Mock<IConnectorFactory>();
        connectorFactory
            .Setup(f => f.Create(StubDiscoveryConnectorName, It.IsAny<ICredentialProtection?>(), It.IsAny<ICertificateProvider?>()))
            .Returns(connector.Object);
        _jim.ConnectedSystems.ConnectorFactory = connectorFactory.Object;

        var connectedSystem = CreateUnknownConnectorConnectedSystem(StubDiscoveryConnectorName);
        var id = 100;
        foreach (var name in discoveries.Select(d => d.TargetSettingName).Append("Host"))
        {
            var setting = new ConnectorDefinitionSetting { Id = id++, Name = name, Type = ConnectedSystemSettingType.String };
            connectedSystem.ConnectorDefinition.Settings.Add(setting);
            connectedSystem.SettingValues.Add(new ConnectedSystemSettingValue { Setting = setting });
        }

        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(connectedSystem);
        return directoryServers;
    }

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
