// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Connectors.Sql;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Covers the application-layer dispatch for reading a Connected System's catalogue (issue #2570): a Connector
/// without <see cref="IConnectorCatalogue"/> is reported as unsupported, an unknown Connected System distinctly
/// from that, and settings typed on screen but not yet saved reach the Connector, so an administrator can browse
/// the database they are configuring before saving it. What the SQL Connector reads from a database is covered
/// by <see cref="JIM.Worker.Tests.Connectors.SqlConnectorCatalogueTests"/>.
/// </summary>
[TestFixture]
public class ConnectedSystemCatalogueTests
{
    private const int ConnectedSystemId = 7;
    private const int HostSettingId = 41;
    private const string StubCatalogueConnectorName = "Stub Catalogue Connector";

    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IConnectorCatalogue> _catalogue = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);

        _jim = new JimApplication(mockRepository.Object);

        var connector = new Mock<IConnector>();
        connector.Setup(c => c.Name).Returns(StubCatalogueConnectorName);
        _catalogue = connector.As<IConnectorCatalogue>();
        _catalogue.Setup(c => c.Catalogue).Returns(new ConnectorCatalogueDeclaration("Object Types"));

        var connectorFactory = new Mock<IConnectorFactory>();
        connectorFactory
            .Setup(f => f.Create(StubCatalogueConnectorName, It.IsAny<ICredentialProtection?>(), It.IsAny<ICertificateProvider?>()))
            .Returns(connector.Object);
        connectorFactory
            .Setup(f => f.Create(ConnectorConstants.SqlConnectorName, It.IsAny<ICredentialProtection?>(), It.IsAny<ICertificateProvider?>()))
            .Returns(new SqlConnector());
        connectorFactory
            .Setup(f => f.Create(ConnectorConstants.FileConnectorName, It.IsAny<ICredentialProtection?>(), It.IsAny<ICertificateProvider?>()))
            .Returns(new JIM.Connectors.File.FileConnector());
        _jim.ConnectedSystems.ConnectorFactory = connectorFactory.Object;
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    #region GetCatalogueDeclarationAsync

    [Test]
    public async Task GetCatalogueDeclarationAsync_SqlConnector_NamesItsObjectTypesSettingAsync()
    {
        SetUpConnectedSystem(ConnectorConstants.SqlConnectorName);

        var declaration = await _jim.ConnectedSystems.GetCatalogueDeclarationAsync(ConnectedSystemId);

        Assert.That(declaration?.DocumentSettingName, Is.EqualTo(SqlConnectorConstants.SettingObjectTypes));
    }

    [Test]
    public async Task GetCatalogueDeclarationAsync_FileConnector_ReturnsNullAsync()
    {
        SetUpConnectedSystem(ConnectorConstants.FileConnectorName);

        var declaration = await _jim.ConnectedSystems.GetCatalogueDeclarationAsync(ConnectedSystemId);

        Assert.That(declaration, Is.Null);
    }

    [Test]
    public async Task GetCatalogueDeclarationAsync_UnknownConnectedSystemId_ReturnsNullAsync()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync((ConnectedSystem?)null);

        var declaration = await _jim.ConnectedSystems.GetCatalogueDeclarationAsync(ConnectedSystemId);

        Assert.That(declaration, Is.Null);
    }

    #endregion

    #region Catalogue reads

    [Test]
    public void GetCatalogueObjectsAsync_UnknownConnectedSystemId_ThrowsArgumentException()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync((ConnectedSystem?)null);

        var exception = Assert.ThrowsAsync<ArgumentException>(() => _jim.ConnectedSystems.GetCatalogueObjectsAsync(ConnectedSystemId));

        Assert.That(exception!.Message, Does.Contain(ConnectedSystemId.ToString()));
    }

    [Test]
    public void GetCatalogueObjectsAsync_FileConnector_ThrowsNotSupportedException()
    {
        SetUpConnectedSystem(ConnectorConstants.FileConnectorName);

        var exception = Assert.ThrowsAsync<NotSupportedException>(() => _jim.ConnectedSystems.GetCatalogueObjectsAsync(ConnectedSystemId));

        Assert.That(exception!.Message, Does.Contain(ConnectorConstants.FileConnectorName));
    }

    [Test]
    public async Task GetCatalogueObjectsAsync_DraftSettings_ReachTheConnectorInPlaceOfTheSavedOnesAsync()
    {
        SetUpConnectedSystem(StubCatalogueConnectorName);
        List<ConnectedSystemSettingValue>? received = null;
        _catalogue
            .Setup(c => c.GetCatalogueObjectsAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), It.IsAny<ILogger>(), It.IsAny<CancellationToken>()))
            .Callback<List<ConnectedSystemSettingValue>, ILogger, CancellationToken>((settings, _, _) => received = settings)
            .ReturnsAsync([new ConnectorCatalogueObject { SchemaName = "HR", Name = "EMPLOYEES", Kind = ConnectorCatalogueObjectKind.Table }]);

        var objects = await _jim.ConnectedSystems.GetCatalogueObjectsAsync(ConnectedSystemId,
            [new ConnectedSystemSettingValueDraft { SettingId = HostSettingId, StringValue = "db02.example.com" }]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(objects.Single().Name, Is.EqualTo("EMPLOYEES"));
            Assert.That(received!.Single(sv => sv.Setting.Id == HostSettingId).StringValue, Is.EqualTo("db02.example.com"),
                "the catalogue an administrator browses must be the one they are configuring, not the one last saved");
        }
    }

    [Test]
    public async Task GetCatalogueObjectAsync_PassesTheSchemaAndNameThroughAsync()
    {
        SetUpConnectedSystem(StubCatalogueConnectorName);
        var detail = new ConnectorCatalogueObjectDetail { SchemaName = "HR", Name = "EMPLOYEES", Kind = ConnectorCatalogueObjectKind.Table, PrimaryKey = ["EMPLOYEE_ID"] };
        _catalogue
            .Setup(c => c.GetCatalogueObjectAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), "HR", "EMPLOYEES", It.IsAny<ILogger>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(detail);

        var result = await _jim.ConnectedSystems.GetCatalogueObjectAsync(ConnectedSystemId, "HR", "EMPLOYEES");

        Assert.That(result, Is.SameAs(detail));
    }

    [Test]
    public async Task DescribeCatalogueStatementAsync_PassesTheStatementThroughAsync()
    {
        SetUpConnectedSystem(StubCatalogueConnectorName);
        const string statement = "SELECT EMPLOYEE_ID FROM HR.EMPLOYEES";
        var detail = new ConnectorCatalogueObjectDetail { Kind = ConnectorCatalogueObjectKind.Statement };
        _catalogue
            .Setup(c => c.DescribeStatementAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), statement, It.IsAny<ILogger>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(detail);

        var result = await _jim.ConnectedSystems.DescribeCatalogueStatementAsync(ConnectedSystemId, statement);

        Assert.That(result, Is.SameAs(detail));
    }

    [Test]
    public void DescribeCatalogueStatementAsync_FileConnector_ThrowsNotSupportedException()
    {
        SetUpConnectedSystem(ConnectorConstants.FileConnectorName);

        Assert.ThrowsAsync<NotSupportedException>(() => _jim.ConnectedSystems.DescribeCatalogueStatementAsync(ConnectedSystemId, "SELECT 1"));
    }

    #endregion

    private void SetUpConnectedSystem(string connectorName)
    {
        var connectorDefinition = new ConnectorDefinition { Name = connectorName };
        var host = new ConnectorDefinitionSetting { Id = HostSettingId, Name = "Host", Type = ConnectedSystemSettingType.String };
        connectorDefinition.Settings.Add(host);

        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(new ConnectedSystem
            {
                Id = ConnectedSystemId,
                Name = "HR Database",
                ConnectorDefinition = connectorDefinition,
                SettingValues = [new ConnectedSystemSettingValue { Setting = host, StringValue = "db01.example.com" }]
            });
    }
}
