// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers where the settings tab places the Discover action: beside whichever setting the Connected System's
/// Connector names as its directory server discovery target, never beside a setting picked out by an LDAP name
/// the portal knows. The Connector here is a stub whose target shares nothing with the LDAP Connector's, and the
/// Connected System also carries a setting with the LDAP Connector's name as a decoy.
/// </summary>
[TestFixture]
public class ConnectedSystemSettingsTabDirectoryServerDiscoveryTests : JimComponentTestContext
{
    private const int ConnectedSystemId = 7;
    private const string StubConnectorName = "Stub Discovery Connector";
    private const string TargetSettingName = "Preferred Server";
    private const string LdapSettingName = "Preferred Domain Controller";

    protected override void ConfigureAdditionalServices()
    {
        var connector = new Mock<IConnector>();
        connector.Setup(c => c.Name).Returns(StubConnectorName);
        connector.As<IConnectorDirectoryServers>()
            .Setup(c => c.DirectoryServerDiscovery)
            .Returns(new ConnectorDirectoryServerDiscovery(TargetSettingName, "directory servers"));
        var connectorFactory = new Mock<IConnectorFactory>();
        connectorFactory
            .Setup(f => f.Create(StubConnectorName, It.IsAny<ICredentialProtection?>(), It.IsAny<ICertificateProvider?>()))
            .Returns(connector.Object);

        var connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        connectedSystemRepository
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(CreateConnectedSystem);
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(connectedSystemRepository.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object, connectorFactory.Object));
    }

    [Test]
    public void SettingsTab_ConnectorTargetingAnotherSetting_PlacesDiscoverBesideThatSettingOnly()
    {
        var cut = Render<ConnectedSystemSettingsTab>(p => p
            .Add(c => c.ConnectedSystem, CreateConnectedSystem())
            .Add(c => c.SettingCategories, [ConnectedSystemSettingCategory.Connectivity]));

        cut.WaitForState(() => cut.FindComponents<ConnectedSystemSettingField>().Any(f => f.Instance.ShowDirectoryServerDiscovery));

        var fieldsWithDiscover = cut.FindComponents<ConnectedSystemSettingField>()
            .Where(f => f.Instance.ShowDirectoryServerDiscovery)
            .Select(f => f.Instance.SettingValue.Setting.Name)
            .ToList();
        Assert.That(fieldsWithDiscover, Is.EqualTo(new[] { TargetSettingName }));
    }

    [Test]
    public void SettingsTab_ConnectorTargetingAnotherSetting_HandsTheConnectorsTermToTheField()
    {
        var cut = Render<ConnectedSystemSettingsTab>(p => p
            .Add(c => c.ConnectedSystem, CreateConnectedSystem())
            .Add(c => c.SettingCategories, [ConnectedSystemSettingCategory.Connectivity]));

        cut.WaitForState(() => cut.FindComponents<ConnectedSystemSettingField>().Any(f => f.Instance.ShowDirectoryServerDiscovery));

        var field = cut.FindComponents<ConnectedSystemSettingField>().Single(f => f.Instance.ShowDirectoryServerDiscovery);
        Assert.That(field.Instance.DirectoryServerTerm, Is.EqualTo("directory servers"));
    }

    private static ConnectedSystem CreateConnectedSystem()
    {
        var settings = new List<ConnectorDefinitionSetting>
        {
            new() { Id = 1, Name = "Host", Required = true, Type = ConnectedSystemSettingType.String, Category = ConnectedSystemSettingCategory.Connectivity },
            new() { Id = 2, Name = LdapSettingName, Type = ConnectedSystemSettingType.String, Category = ConnectedSystemSettingCategory.Connectivity },
            new() { Id = 3, Name = TargetSettingName, Type = ConnectedSystemSettingType.String, Category = ConnectedSystemSettingCategory.Connectivity }
        };

        var connectorDefinition = new ConnectorDefinition { Id = 1, Name = StubConnectorName, Settings = settings };
        return new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Stub",
            ConnectorDefinition = connectorDefinition,
            SettingValues = settings.Select(s => new ConnectedSystemSettingValue
            {
                Id = s.Id,
                Setting = s,
                StringValue = s.Name == "Host" ? "server01.example.local" : null
            }).ToList()
        };
    }

    /// <summary>
    /// A fresh <see cref="JimApplication"/> per call, since the tab disposes each one it creates.
    /// </summary>
    private sealed class FakeJimApplicationFactory(IRepository repository, IConnectorFactory connectorFactory) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository, connectorFactory: connectorFactory);
    }
}
