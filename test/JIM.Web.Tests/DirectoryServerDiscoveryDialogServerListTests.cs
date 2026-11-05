// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Discover dialog's list of servers, for the setting it was opened from (#2021, D6): a Connector that discovers
/// servers for more than one setting is asked for that setting's, and a Version column appears when the servers have
/// versions (Exchange servers do; domain controllers do not). A stub Connector stands in for a directory.
/// </summary>
[TestFixture]
public class DirectoryServerDiscoveryDialogServerListTests : JimComponentTestContext
{
    private const int ConnectedSystemId = 7;
    private const string StubConnectorName = "Stub Discovery Connector";

    private Mock<IConnectorDirectoryServers> _discovery = null!;

    protected override void ConfigureAdditionalServices()
    {
        var connector = new Mock<IConnector>();
        connector.Setup(c => c.Name).Returns(StubConnectorName);
        _discovery = connector.As<IConnectorDirectoryServers>();
        _discovery.Setup(c => c.DirectoryServerDiscoveries).Returns(
        [
            new ConnectorDirectoryServerDiscovery("Preferred Server", "directory servers"),
            new ConnectorDirectoryServerDiscovery("Mail Server", "mail servers")
        ]);
        _discovery.Setup(c => c.GetDirectoryServersAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), "Preferred Server", It.IsAny<Serilog.ILogger>()))
            .ReturnsAsync(new ConnectorDirectoryServerDiscoveryResult(null, [new ConnectorDirectoryServer { HostName = "dc01.example.local", Site = "London" }]));
        _discovery.Setup(c => c.GetDirectoryServersAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), "Mail Server", It.IsAny<Serilog.ILogger>()))
            .ReturnsAsync(new ConnectorDirectoryServerDiscoveryResult("example.local",
                [new ConnectorDirectoryServer { HostName = "mail01.example.local", Site = "London", Version = "Version 15.2 (Build 1544.4)" }]));
        var connectorFactory = new Mock<IConnectorFactory>();
        connectorFactory.Setup(f => f.Create(StubConnectorName, It.IsAny<ICredentialProtection?>(), It.IsAny<ICertificateProvider?>()))
            .Returns(connector.Object);

        var settings = new List<ConnectorDefinitionSetting>
        {
            new() { Id = 1, Name = "Preferred Server", Type = ConnectedSystemSettingType.String },
            new() { Id = 2, Name = "Mail Server", Type = ConnectedSystemSettingType.String }
        };
        var connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        connectedSystemRepository
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(() => new ConnectedSystem
            {
                Id = ConnectedSystemId,
                Name = "Stub",
                ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = StubConnectorName, Settings = settings },
                SettingValues = settings.Select(s => new ConnectedSystemSettingValue { Setting = s }).ToList()
            });
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(connectedSystemRepository.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object, connectorFactory.Object));
    }

    [Test]
    public void DirectoryServerDiscoveryDialog_ForASetting_ListsThatSettingsServersWithTheirVersions()
    {
        var provider = ShowDialog("Mail Server", "mail servers");

        var row = provider.Find(".jim-directory-server-row");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.TextContent, Does.Contain("mail01.example.local").And.Contain("London").And.Contain("Version 15.2 (Build 1544.4)"));
            Assert.That(provider.FindAll("th").Select(th => th.TextContent), Does.Contain("Version"), "a Version column, since the servers have versions");
        }
    }

    [Test]
    public void DirectoryServerDiscoveryDialog_ServersWithoutVersions_HasNoVersionColumn()
    {
        var provider = ShowDialog("Preferred Server", "directory servers");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Find(".jim-directory-server-row").TextContent, Does.Contain("dc01.example.local"));
            Assert.That(provider.FindAll("th").Select(th => th.TextContent), Does.Not.Contain("Version"));
        }
    }

    [Test]
    public void DirectoryServerDiscoveryDialog_ForestNamedOnceDiscoveryFinishes_TitleSaysWhichForestWasSearched()
    {
        // As in the portal, the dialog first renders while discovery runs, and learns the forest only when it finishes:
        // the title, which the dialog's frame draws, must follow.
        var discovered = new TaskCompletionSource<ConnectorDirectoryServerDiscoveryResult>();
        _discovery.Setup(c => c.GetDirectoryServersAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), "Mail Server", It.IsAny<Serilog.ILogger>()))
            .Returns(discovered.Task);
        var provider = ShowDialog("Mail Server", "mail servers", awaitRows: false, awaitNothing: true);
        provider.WaitForState(() => provider.FindAll("[data-testid='jim-directory-server-discovery-title']").Count > 0);

        discovered.SetResult(new ConnectorDirectoryServerDiscoveryResult("example.local", [new ConnectorDirectoryServer { HostName = "mail01.example.local" }]));
        provider.WaitForState(() => provider.FindAll(".jim-directory-server-row").Count > 0);

        provider.WaitForAssertion(() =>
            Assert.That(provider.Find("[data-testid='jim-directory-server-discovery-title']").TextContent, Does.Contain("example.local")));
    }

    [Test]
    public void DirectoryServerDiscoveryDialog_NoForestNamed_TitleNamesNone()
    {
        var provider = ShowDialog("Preferred Server", "directory servers");

        Assert.That(provider.Find("[data-testid='jim-directory-server-discovery-title']").TextContent, Does.Not.Contain(" in "));
    }

    [Test]
    public void DirectoryServerDiscoveryDialog_NoServersInANamedForest_SaysWhichForestHasNone()
    {
        _discovery.Setup(c => c.GetDirectoryServersAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), "Mail Server", It.IsAny<Serilog.ILogger>()))
            .ReturnsAsync(new ConnectorDirectoryServerDiscoveryResult("example.local", []));

        var provider = ShowDialog("Mail Server", "mail servers", awaitRows: false);

        Assert.That(provider.Find("[data-testid='jim-directory-server-discovery-empty']").TextContent, Does.Contain("example.local"));
    }

    private IRenderedComponent<MudDialogProvider> ShowDialog(string targetSettingName, string serverTerm, bool awaitRows = true, bool awaitNothing = false)
    {
        var provider = Render<MudDialogProvider>();
        var parameters = new DialogParameters<DirectoryServerDiscoveryDialog>
        {
            { x => x.ConnectedSystemId, ConnectedSystemId },
            { x => x.TargetSettingName, targetSettingName },
            { x => x.ServerTerm, serverTerm }
        };
        provider.InvokeAsync(() => Services.GetRequiredService<IDialogService>().ShowAsync<DirectoryServerDiscoveryDialog>("Discover", parameters));
        if (awaitNothing)
            return provider;
        provider.WaitForState(() => awaitRows
            ? provider.FindAll(".jim-directory-server-row").Count > 0
            : provider.FindAll("[data-testid='jim-directory-server-discovery-empty']").Count > 0);
        return provider;
    }

    /// <summary>
    /// A fresh <see cref="JimApplication"/> per call, since the dialog disposes each one it creates.
    /// </summary>
    private sealed class FakeJimApplicationFactory(IRepository repository, IConnectorFactory connectorFactory) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository, connectorFactory: connectorFactory);
    }
}
