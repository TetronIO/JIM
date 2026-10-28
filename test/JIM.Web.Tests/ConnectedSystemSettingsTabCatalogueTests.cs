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
using JIM.Web.Pages.Admin.Components.Sql;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Serilog;

namespace JIM.Web.Tests;

/// <summary>
/// Covers where the settings tab places the catalogue-driven Object Types editor (#2570): in place of whichever
/// setting the Connected System's Connector names as the document its catalogue helps write, and nowhere else.
/// The Connector is a stub whose setting shares nothing with the SQL Connector's, so the tab cannot be finding it
/// by a name it knows.
/// </summary>
[TestFixture]
public class ConnectedSystemSettingsTabCatalogueTests : JimComponentTestContext
{
    private const int ConnectedSystemId = 7;
    private const string StubConnectorName = "Stub Catalogue Connector";
    private const string DocumentSettingName = "Source Mapping";

    protected override void ConfigureAdditionalServices()
    {
        var connector = new Mock<IConnector>();
        connector.Setup(c => c.Name).Returns(StubConnectorName);
        var catalogue = connector.As<IConnectorCatalogue>();
        catalogue.Setup(c => c.Catalogue).Returns(new ConnectorCatalogueDeclaration(DocumentSettingName));
        catalogue
            .Setup(c => c.GetCatalogueObjectsAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), It.IsAny<ILogger>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
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
    public void SettingsTab_ConnectorWithACatalogue_EditsTheSettingItNamesWithTheEditor()
    {
        var cut = Render<ConnectedSystemSettingsTab>(p => p
            .Add(c => c.ConnectedSystem, CreateConnectedSystem())
            .Add(c => c.SettingCategories, [ConnectedSystemSettingCategory.Schema]));

        cut.WaitForState(() => cut.HasComponent<SqlObjectTypesEditor>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindComponent<SqlObjectTypesEditor>().Instance.SettingValue.Setting.Name, Is.EqualTo(DocumentSettingName));
            Assert.That(cut.FindComponents<ConnectedSystemSettingField>().Select(f => f.Instance.SettingValue.Setting.Name),
                Is.EqualTo(new[] { "Notes" }), "the document is edited by the editor alone, and every other setting as before");
        }
    }

    private static ConnectedSystem CreateConnectedSystem()
    {
        var settings = new List<ConnectorDefinitionSetting>
        {
            new() { Id = 1, Name = DocumentSettingName, Required = true, Type = ConnectedSystemSettingType.Text, Category = ConnectedSystemSettingCategory.Schema },
            new() { Id = 2, Name = "Notes", Type = ConnectedSystemSettingType.String, Category = ConnectedSystemSettingCategory.Schema }
        };

        var connectorDefinition = new ConnectorDefinition { Id = 1, Name = StubConnectorName, Settings = settings };
        return new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Stub",
            ConnectorDefinition = connectorDefinition,
            SettingValues = settings.Select(s => new ConnectedSystemSettingValue { Id = s.Id, Setting = s }).ToList()
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
