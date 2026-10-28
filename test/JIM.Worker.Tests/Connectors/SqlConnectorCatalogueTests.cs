// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers the SQL Connector's catalogue: what it tells an administrator the database contains, so the Object
/// Types document can be written by choosing rather than by recalling exact names.
/// </summary>
/// <remarks>
/// Runs against <see cref="FakeSqlProvider"/>, which answers the same catalogue queries schema discovery asks.
/// What the real SQL Server and Oracle catalogues return for those queries is covered by the provider tests and
/// Scenario 016.
/// </remarks>
[TestFixture]
public class SqlConnectorCatalogueTests
{
    private ILogger _logger = null!;
    private FakeSqlProvider _provider = null!;
    private SqlConnector _connector = null!;
    private List<ConnectedSystemSettingValue> _settings = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
        _provider = new FakeSqlProvider();
        _connector = new SqlConnector { ProviderFactory = _ => _provider };
        _settings = SqlConnectorSettingValues.CreateSqlServer(_connector);
    }

    [TearDown]
    public void TearDown()
    {
        _connector.Dispose();
        (_logger as IDisposable)?.Dispose();
    }

    [Test]
    public void Catalogue_NamesTheObjectTypesSetting()
    {
        IConnectorCatalogue catalogue = _connector;

        Assert.That(catalogue.Catalogue.DocumentSettingName, Is.EqualTo(SqlConnectorConstants.SettingObjectTypes));
    }

    [Test]
    public async Task GetCatalogueObjectsAsync_TablesAndViews_ListsEachWithItsKindAsync()
    {
        _provider.Catalogue.AddTable("HR", "EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int"));
        _provider.Catalogue.AddView("HR", "V_EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int"));

        var objects = await _connector.GetCatalogueObjectsAsync(_settings, _logger);

        Assert.That(objects.Select(o => (o.SchemaName, o.Name, o.Kind)), Is.EquivalentTo(new[]
        {
            ((string?)"HR", "EMPLOYEES", ConnectorCatalogueObjectKind.Table),
            ((string?)"HR", "V_EMPLOYEES", ConnectorCatalogueObjectKind.View)
        }));
    }

    [Test]
    public async Task GetCatalogueObjectAsync_ATable_ReturnsItsColumnsAndPrimaryKeyInKeyOrderAsync()
    {
        _provider.Catalogue.AddTable("HR", "ASSIGNMENTS",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("ROLE_CODE", "nvarchar", MaxLength: 20, IsNullable: false),
            new FakeCatalogueColumn("WEIGHT", "decimal", Precision: 5, Scale: 2));
        _provider.Catalogue.AddPrimaryKey("HR", "ASSIGNMENTS", "ROLE_CODE", "EMPLOYEE_ID");

        var detail = await _connector.GetCatalogueObjectAsync(_settings, "HR", "ASSIGNMENTS", _logger);

        Assert.That(detail, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail!.Kind, Is.EqualTo(ConnectorCatalogueObjectKind.Table));
            Assert.That(detail.Columns.Select(c => c.Name), Is.EqualTo(new[] { "EMPLOYEE_ID", "ROLE_CODE", "WEIGHT" }));
            Assert.That(detail.PrimaryKey, Is.EqualTo(new[] { "ROLE_CODE", "EMPLOYEE_ID" }), "key order, not column order, is what makes a composite anchor reproducible");
            Assert.That(detail.Columns[1].MaxLength, Is.EqualTo(20));
            Assert.That(detail.Columns[2].Precision, Is.EqualTo(5));
            Assert.That(detail.Columns[2].Scale, Is.EqualTo(2));
            Assert.That(detail.Columns[0].IsNullable, Is.False);
        }
    }

    [Test]
    public async Task GetCatalogueObjectAsync_ACompositeForeignKey_IsOneEntryWithItsColumnsPairedAsync()
    {
        _provider.Catalogue.AddTable("HR", "ASSIGNMENT_NOTES",
            new FakeCatalogueColumn("NOTE_ID", "int"),
            new FakeCatalogueColumn("EMPLOYEE_ID", "int"),
            new FakeCatalogueColumn("ROLE_CODE", "nvarchar"),
            new FakeCatalogueColumn("AUTHOR_ID", "int"));
        _provider.Catalogue.AddForeignKey("HR", "ASSIGNMENT_NOTES", new FakeCatalogueForeignKey("FK_NOTES_ASSIGNMENT", "ROLE_CODE", "HR", "ASSIGNMENTS", "ROLE_CODE"));
        _provider.Catalogue.AddForeignKey("HR", "ASSIGNMENT_NOTES", new FakeCatalogueForeignKey("FK_NOTES_ASSIGNMENT", "EMPLOYEE_ID", "HR", "ASSIGNMENTS", "EMPLOYEE_ID"));
        _provider.Catalogue.AddForeignKey("HR", "ASSIGNMENT_NOTES", new FakeCatalogueForeignKey("FK_NOTES_AUTHOR", "AUTHOR_ID", "HR", "EMPLOYEES", "EMPLOYEE_ID"));

        var detail = await _connector.GetCatalogueObjectAsync(_settings, "HR", "ASSIGNMENT_NOTES", _logger);

        Assert.That(detail!.ForeignKeys, Has.Count.EqualTo(2));
        var composite = detail.ForeignKeys.Single(fk => fk.ConstraintName == "FK_NOTES_ASSIGNMENT");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(composite.Columns, Is.EqualTo(new[] { "ROLE_CODE", "EMPLOYEE_ID" }));
            Assert.That(composite.ReferencedColumns, Is.EqualTo(new[] { "ROLE_CODE", "EMPLOYEE_ID" }));
            Assert.That(composite.ReferencedSchemaName, Is.EqualTo("HR"));
            Assert.That(composite.ReferencedObjectName, Is.EqualTo("ASSIGNMENTS"));
        }
    }

    [Test]
    public async Task GetCatalogueObjectAsync_AView_IsAViewWithNoPrimaryKeyAsync()
    {
        _provider.Catalogue.AddView("HR", "V_EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int"));

        var detail = await _connector.GetCatalogueObjectAsync(_settings, "HR", "V_EMPLOYEES", _logger);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail!.Kind, Is.EqualTo(ConnectorCatalogueObjectKind.View));
            Assert.That(detail.PrimaryKey, Is.Empty);
        }
    }

    [Test]
    public async Task GetCatalogueObjectAsync_NoSchemaGiven_FindsTheOnlyObjectOfThatNameAsync()
    {
        _provider.Catalogue.AddTable("HR", "EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int"));
        _provider.Catalogue.AddPrimaryKey("HR", "EMPLOYEES", "EMPLOYEE_ID");

        var detail = await _connector.GetCatalogueObjectAsync(_settings, null, "employees", _logger);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail!.SchemaName, Is.EqualTo("HR"));
            Assert.That(detail.Name, Is.EqualTo("EMPLOYEES"), "the name comes back as the database spells it");
            Assert.That(detail.PrimaryKey, Is.EqualTo(new[] { "EMPLOYEE_ID" }), "keys are read with the schema the name resolved to");
        }
    }

    [Test]
    public void GetCatalogueObjectAsync_NoSchemaGivenAndTheNameIsInTwo_IsRefusedNamingBoth()
    {
        _provider.Catalogue.AddTable("HR", "EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int"));
        _provider.Catalogue.AddTable("ARCHIVE", "EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int"));

        var exception = Assert.ThrowsAsync<ArgumentException>(() => _connector.GetCatalogueObjectAsync(_settings, null, "EMPLOYEES", _logger));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Message, Does.Contain("HR"));
            Assert.That(exception.Message, Does.Contain("ARCHIVE"));
        }
    }

    [Test]
    public async Task GetCatalogueObjectAsync_AnObjectTheAccountCannotSee_ReturnsNullAsync()
    {
        _provider.Catalogue.AddTable("HR", "EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int"));

        var detail = await _connector.GetCatalogueObjectAsync(_settings, "HR", "SALARIES", _logger);

        Assert.That(detail, Is.Null);
    }

    [Test]
    public async Task DescribeStatementAsync_AQuery_ReturnsItsColumnsWithoutReadingRowsAsync()
    {
        // The fake asserts the statement is run schema-only, so this also proves no row is read.
        const string statement = "SELECT EMPLOYEE_ID, GIVEN_NAME FROM HR.EMPLOYEES";
        _provider.Catalogue.AddSelectStatement(statement,
            new FakeCatalogueColumn("EMPLOYEE_ID", "int"),
            new FakeCatalogueColumn("GIVEN_NAME", "nvarchar"));

        var detail = await _connector.DescribeStatementAsync(_settings, statement, _logger);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail.Kind, Is.EqualTo(ConnectorCatalogueObjectKind.Statement));
            Assert.That(detail.Columns.Select(c => c.Name), Is.EqualTo(new[] { "EMPLOYEE_ID", "GIVEN_NAME" }));
            Assert.That(detail.PrimaryKey, Is.Empty);
        }
    }

    [Test]
    public void DescribeStatementAsync_NotAQuery_IsRefusedBeforeItReachesTheDatabase()
    {
        var exception = Assert.ThrowsAsync<ArgumentException>(() => _connector.DescribeStatementAsync(_settings, "DELETE FROM HR.EMPLOYEES", _logger));

        Assert.That(exception!.Message, Does.Contain("SELECT"));
    }

    [Test]
    public void DescribeStatementAsync_TwoStatements_IsRefused()
    {
        Assert.ThrowsAsync<ArgumentException>(() => _connector.DescribeStatementAsync(_settings, "SELECT 1 FROM HR.EMPLOYEES; DROP TABLE HR.EMPLOYEES", _logger));
    }
}
