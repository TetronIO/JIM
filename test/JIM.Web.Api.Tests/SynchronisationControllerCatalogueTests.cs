// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Covers the catalogue endpoints under <c>/connected-systems/{id}/catalogue</c> (issue #2570): every way a read
/// can fail is a clean 4xx or a 502 naming the source, never a 500, and a successful read is shaped for a script.
/// What the SQL Connector reads from a database is covered by <c>SqlConnectorCatalogueTests</c> (JIM.Worker.Tests).
/// </summary>
[TestFixture]
public class SynchronisationControllerCatalogueTests
{
    private const int ConnectedSystemId = 42;
    private const string StubConnectorName = "Stub Catalogue Connector";

    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IConnectorCatalogue> _catalogue = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        var mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);

        _application = new JimApplication(mockRepository.Object);

        var connector = new Mock<IConnector>();
        connector.Setup(c => c.Name).Returns(StubConnectorName);
        _catalogue = connector.As<IConnectorCatalogue>();
        _catalogue.Setup(c => c.Catalogue).Returns(new ConnectorCatalogueDeclaration("Object Types"));
        var connectorFactory = new Mock<IConnectorFactory>();
        connectorFactory
            .Setup(f => f.Create(StubConnectorName, It.IsAny<ICredentialProtection?>(), It.IsAny<ICertificateProvider?>()))
            .Returns(connector.Object);
        connectorFactory
            .Setup(f => f.Create(ConnectorConstants.FileConnectorName, It.IsAny<ICredentialProtection?>(), It.IsAny<ICertificateProvider?>()))
            .Returns(new FileConnector());
        _application.ConnectedSystems.ConnectorFactory = connectorFactory.Object;

        _controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            _application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object);
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    #region GET catalogue

    [Test]
    public async Task GetConnectedSystemCatalogueAsync_UnknownConnectedSystem_ReturnsNotFoundAsync()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync((ConnectedSystem?)null);

        var result = await _controller.GetConnectedSystemCatalogueAsync(ConnectedSystemId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetConnectedSystemCatalogueAsync_ConnectorWithoutACatalogue_ReturnsBadRequestAsync()
    {
        SetUpConnectedSystem(ConnectorConstants.FileConnectorName);

        var result = await _controller.GetConnectedSystemCatalogueAsync(ConnectedSystemId);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task GetConnectedSystemCatalogueAsync_Succeeds_ListsEachObjectWithItsKindAsync()
    {
        SetUpConnectedSystem(StubConnectorName);
        _catalogue
            .Setup(c => c.GetCatalogueObjectsAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), It.IsAny<Serilog.ILogger>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ConnectorCatalogueObject { SchemaName = "HR", Name = "EMPLOYEES", Kind = ConnectorCatalogueObjectKind.Table },
                new ConnectorCatalogueObject { SchemaName = "HR", Name = "V_EMPLOYEES", Kind = ConnectorCatalogueObjectKind.View }
            ]);

        var result = await _controller.GetConnectedSystemCatalogueAsync(ConnectedSystemId);

        var objects = ((result as OkObjectResult)?.Value as IEnumerable<ConnectedSystemCatalogueObjectDto>)?.ToList();
        Assert.That(objects, Has.Count.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objects![0].SchemaName, Is.EqualTo("HR"));
            Assert.That(objects[0].Name, Is.EqualTo("EMPLOYEES"));
            Assert.That(objects[0].Kind, Is.EqualTo("Table"));
            Assert.That(objects[1].Kind, Is.EqualTo("View"));
        }
    }

    [Test]
    public async Task GetConnectedSystemCatalogueAsync_SettingsIncomplete_ReturnsBadRequestAsync()
    {
        SetUpConnectedSystem(StubConnectorName);
        _catalogue
            .Setup(c => c.GetCatalogueObjectsAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), It.IsAny<Serilog.ILogger>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidSettingValuesException("Choose a Database Type before browsing the database."));

        var result = await _controller.GetConnectedSystemCatalogueAsync(ConnectedSystemId);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>(), "settings the administrator has not finished are theirs to fix, not a fault in the source");
    }

    [Test]
    public async Task GetConnectedSystemCatalogueAsync_SourceUnreachable_ReturnsBadGatewayAsync()
    {
        SetUpConnectedSystem(StubConnectorName);
        _catalogue
            .Setup(c => c.GetCatalogueObjectsAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), It.IsAny<Serilog.ILogger>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("The server did not respond."));

        var result = await _controller.GetConnectedSystemCatalogueAsync(ConnectedSystemId);

        Assert.That((result as ObjectResult)?.StatusCode, Is.EqualTo(StatusCodes.Status502BadGateway));
    }

    #endregion

    #region GET catalogue/object

    [Test]
    public async Task GetConnectedSystemCatalogueObjectAsync_Succeeds_ReturnsColumnsAndKeysAsync()
    {
        SetUpConnectedSystem(StubConnectorName);
        _catalogue
            .Setup(c => c.GetCatalogueObjectAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), "HR", "EMPLOYEES", It.IsAny<Serilog.ILogger>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorCatalogueObjectDetail
            {
                SchemaName = "HR",
                Name = "EMPLOYEES",
                Kind = ConnectorCatalogueObjectKind.Table,
                Columns =
                [
                    new ConnectorCatalogueColumn { Name = "EMPLOYEE_ID", DataTypeName = "int" },
                    new ConnectorCatalogueColumn { Name = "MANAGER_ID", DataTypeName = "int", IsNullable = true }
                ],
                PrimaryKey = ["EMPLOYEE_ID"],
                ForeignKeys =
                [
                    new ConnectorCatalogueForeignKey { ConstraintName = "FK_MANAGER", Columns = ["MANAGER_ID"], ReferencedSchemaName = "HR", ReferencedObjectName = "EMPLOYEES", ReferencedColumns = ["EMPLOYEE_ID"] }
                ]
            });

        var result = await _controller.GetConnectedSystemCatalogueObjectAsync(ConnectedSystemId, "HR", "EMPLOYEES");

        var detail = (result as OkObjectResult)?.Value as ConnectedSystemCatalogueObjectDetailDto;
        Assert.That(detail, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail!.Kind, Is.EqualTo("Table"));
            Assert.That(detail.Columns.Select(c => c.Name), Is.EqualTo(new[] { "EMPLOYEE_ID", "MANAGER_ID" }));
            Assert.That(detail.Columns[1].IsNullable, Is.True);
            Assert.That(detail.PrimaryKey, Is.EqualTo(new[] { "EMPLOYEE_ID" }));
            Assert.That(detail.ForeignKeys.Single().ReferencedObjectName, Is.EqualTo("EMPLOYEES"));
            Assert.That(detail.ForeignKeys.Single().ReferencedColumns, Is.EqualTo(new[] { "EMPLOYEE_ID" }));
        }
    }

    [Test]
    public async Task GetConnectedSystemCatalogueObjectAsync_NoSuchObject_ReturnsNotFoundNamingItAsync()
    {
        SetUpConnectedSystem(StubConnectorName);
        _catalogue
            .Setup(c => c.GetCatalogueObjectAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), "HR", "SALARIES", It.IsAny<Serilog.ILogger>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConnectorCatalogueObjectDetail?)null);

        var result = await _controller.GetConnectedSystemCatalogueObjectAsync(ConnectedSystemId, "HR", "SALARIES");

        var notFound = result as NotFoundObjectResult;
        Assert.That(notFound, Is.Not.Null);
        Assert.That((notFound!.Value as ApiErrorResponse)?.Message, Does.Contain("SALARIES"));
    }

    [Test]
    public async Task GetConnectedSystemCatalogueObjectAsync_NameInMoreThanOneSchema_ReturnsBadRequestAsync()
    {
        SetUpConnectedSystem(StubConnectorName);
        _catalogue
            .Setup(c => c.GetCatalogueObjectAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), null, "EMPLOYEES", It.IsAny<Serilog.ILogger>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("'EMPLOYEES' exists in more than one schema (HR, ARCHIVE). Say which schema to read it from."));

        var result = await _controller.GetConnectedSystemCatalogueObjectAsync(ConnectedSystemId, null, "EMPLOYEES");

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task GetConnectedSystemCatalogueObjectAsync_NoName_ReturnsBadRequestAsync()
    {
        SetUpConnectedSystem(StubConnectorName);

        var result = await _controller.GetConnectedSystemCatalogueObjectAsync(ConnectedSystemId, "HR", " ");

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    #endregion

    #region POST catalogue/statement

    [Test]
    public async Task DescribeConnectedSystemCatalogueStatementAsync_Succeeds_ReturnsTheStatementsColumnsAsync()
    {
        SetUpConnectedSystem(StubConnectorName);
        const string statement = "SELECT EMPLOYEE_ID FROM HR.EMPLOYEES";
        _catalogue
            .Setup(c => c.DescribeStatementAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), statement, It.IsAny<Serilog.ILogger>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorCatalogueObjectDetail
            {
                Kind = ConnectorCatalogueObjectKind.Statement,
                Columns = [new ConnectorCatalogueColumn { Name = "EMPLOYEE_ID", DataTypeName = "int" }]
            });

        var result = await _controller.DescribeConnectedSystemCatalogueStatementAsync(ConnectedSystemId, new DescribeCatalogueStatementRequest { Statement = statement });

        var detail = (result as OkObjectResult)?.Value as ConnectedSystemCatalogueObjectDetailDto;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail?.Kind, Is.EqualTo("Statement"));
            Assert.That(detail?.Columns.Single().Name, Is.EqualTo("EMPLOYEE_ID"));
        }
    }

    [Test]
    public async Task DescribeConnectedSystemCatalogueStatementAsync_StatementRefused_ReturnsBadRequestAsync()
    {
        SetUpConnectedSystem(StubConnectorName);
        _catalogue
            .Setup(c => c.DescribeStatementAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), It.IsAny<string>(), It.IsAny<Serilog.ILogger>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("The statement does not begin with SELECT or WITH."));

        var result = await _controller.DescribeConnectedSystemCatalogueStatementAsync(ConnectedSystemId, new DescribeCatalogueStatementRequest { Statement = "DELETE FROM HR.EMPLOYEES" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task DescribeConnectedSystemCatalogueStatementAsync_NoStatement_ReturnsBadRequestAsync()
    {
        SetUpConnectedSystem(StubConnectorName);

        var result = await _controller.DescribeConnectedSystemCatalogueStatementAsync(ConnectedSystemId, new DescribeCatalogueStatementRequest { Statement = "" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    #endregion

    private void SetUpConnectedSystem(string connectorName)
    {
        var connectorDefinition = new ConnectorDefinition { Id = 1, Name = connectorName };
        if (connectorName == ConnectorConstants.FileConnectorName)
            _application.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(new FileConnector(), connectorDefinition);

        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(() => new ConnectedSystem
            {
                Id = ConnectedSystemId,
                Name = "HR Database",
                ConnectorDefinition = connectorDefinition,
                SettingValues = connectorDefinition.Settings.Select(s => new ConnectedSystemSettingValue { Setting = s, StringValue = s.DefaultStringValue }).ToList()
            });
    }
}
