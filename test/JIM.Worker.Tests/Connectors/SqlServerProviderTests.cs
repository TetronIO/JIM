// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql.Providers;
using JIM.Models.Core;
using JIM.Utilities;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using System.Data;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers the Microsoft SQL Server dialect behind <see cref="ISqlProvider"/>. These are the pieces that
/// generate SQL text, so a defect here is either a broken query or an injection hole.
/// </summary>
[TestFixture]
public class SqlServerProviderTests
{
    private SqlServerProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _provider = new SqlServerProvider();
    }

    #region Identity

    [Test]
    public void DatabaseType_IsSqlServer()
    {
        Assert.That(_provider.DatabaseType, Is.EqualTo(SqlDatabaseType.SqlServer));
    }

    #endregion

    #region Parameters

    [Test]
    public void ParameterPrefix_IsAtSign()
    {
        Assert.That(_provider.ParameterPrefix, Is.EqualTo("@"), "SQL Server binds parameters by an '@'-prefixed name.");
    }

    [Test]
    public void GetParameterPlaceholder_ValidName_PrefixesWithAtSign()
    {
        Assert.That(_provider.GetParameterPlaceholder("lastAnchor"), Is.EqualTo("@lastAnchor"));
    }

    [TestCase("no spaces allowed")]
    [TestCase("drop; --")]
    [TestCase("")]
    [TestCase("1startsWithADigit")]
    public void GetParameterPlaceholder_HostileName_Throws(string parameterName)
    {
        Assert.Throws<ArgumentException>(() => _provider.GetParameterPlaceholder(parameterName),
            "Parameter names are interpolated into SQL text, so only JIM-generated identifier-shaped names may pass.");
    }

    [Test]
    public void CreateParameter_ValueSupplied_CarriesTheValueAndTheBareName()
    {
        var parameter = _provider.CreateParameter("pageSize", 500);

        Assert.Multiple(() =>
        {
            Assert.That(parameter.ParameterName, Is.EqualTo("pageSize"), "SqlClient accepts the bare name and adds the prefix itself.");
            Assert.That(parameter.Value, Is.EqualTo(500));
        });
    }

    [Test]
    public void CreateParameter_NullValue_UsesDbNull()
    {
        var parameter = _provider.CreateParameter("value", null);

        Assert.That(parameter.Value, Is.EqualTo(DBNull.Value), "ADO.NET represents a SQL NULL as DBNull, never as a CLR null.");
    }

    #endregion

    #region Identifier quoting

    [Test]
    public void QuoteIdentifier_OrdinaryName_WrapsInBrackets()
    {
        Assert.That(_provider.QuoteIdentifier("EMPLOYEE_ID"), Is.EqualTo("[EMPLOYEE_ID]"));
    }

    [Test]
    public void QuoteIdentifier_NameContainingTheClosingBracket_DoublesIt()
    {
        // The classic break-out attempt: a table called: Employees]; DROP TABLE Users--
        var quoted = _provider.QuoteIdentifier("Employees]; DROP TABLE Users--");

        Assert.Multiple(() =>
        {
            Assert.That(quoted, Is.EqualTo("[Employees]]; DROP TABLE Users--]"), "Doubling the closing bracket keeps the whole hostile string inside one quoted identifier.");
            Assert.That(quoted.StartsWith('['), Is.True);
            Assert.That(quoted.EndsWith(']'), Is.True);
        });
    }

    [TestCase("")]
    [TestCase("   ")]
    public void QuoteIdentifier_EmptyName_Throws(string identifier)
    {
        Assert.Throws<ArgumentException>(() => _provider.QuoteIdentifier(identifier), "An empty identifier can never be legitimate and would produce '[]'.");
    }

    [Test]
    public void QuoteIdentifier_NameContainingAControlCharacter_Throws()
    {
        Assert.Throws<ArgumentException>(() => _provider.QuoteIdentifier("Employees\0"),
            "A NUL can truncate the command text downstream, so control characters are refused outright rather than quoted.");
    }

    [Test]
    public void QuoteIdentifier_NameLongerThanTheServerAllows_Throws()
    {
        Assert.Throws<ArgumentException>(() => _provider.QuoteIdentifier(new string('a', 129)),
            "SQL Server identifiers cap at 128 characters; anything longer is not a real object name.");
    }

    [Test]
    public void QualifyObjectName_SchemaSupplied_QuotesBothParts()
    {
        Assert.That(_provider.QualifyObjectName("dbo", "APP_USERS"), Is.EqualTo("[dbo].[APP_USERS]"));
    }

    [Test]
    public void QualifyObjectName_NoSchema_QuotesTheObjectNameOnly()
    {
        Assert.That(_provider.QualifyObjectName(null, "APP_USERS"), Is.EqualTo("[APP_USERS]"));
    }

    #endregion

    #region Keyset pagination

    [Test]
    public void BuildKeysetPageCommandText_FirstPage_SelectsTheTopOfTheOrderedSet()
    {
        var request = new SqlKeysetPageRequest
        {
            SchemaName = "dbo",
            ObjectName = "EMPLOYEES",
            SelectColumns = ["EMPLOYEE_ID", "FIRST_NAME"],
            AnchorColumns = ["EMPLOYEE_ID"],
            PageSizeParameterName = "pageSize"
        };

        var sql = _provider.BuildKeysetPageCommandText(request);

        Assert.That(sql, Is.EqualTo("SELECT TOP (@pageSize) [EMPLOYEE_ID], [FIRST_NAME] FROM [dbo].[EMPLOYEES] ORDER BY [EMPLOYEE_ID]"));
    }

    [Test]
    public void BuildKeysetPageCommandText_SubsequentPage_FiltersBeyondTheLastAnchor()
    {
        var request = new SqlKeysetPageRequest
        {
            SchemaName = "dbo",
            ObjectName = "EMPLOYEES",
            SelectColumns = ["EMPLOYEE_ID", "FIRST_NAME"],
            AnchorColumns = ["EMPLOYEE_ID"],
            PageSizeParameterName = "pageSize",
            LastAnchorParameterNames = ["lastAnchor0"]
        };

        var sql = _provider.BuildKeysetPageCommandText(request);

        Assert.That(sql, Is.EqualTo("SELECT TOP (@pageSize) [EMPLOYEE_ID], [FIRST_NAME] FROM [dbo].[EMPLOYEES] WHERE [EMPLOYEE_ID] > @lastAnchor0 ORDER BY [EMPLOYEE_ID]"));
    }

    [Test]
    public void BuildKeysetPageCommandText_AnyPage_NeverUsesOffset()
    {
        var request = new SqlKeysetPageRequest
        {
            ObjectName = "EMPLOYEES",
            SelectColumns = ["EMPLOYEE_ID"],
            AnchorColumns = ["EMPLOYEE_ID"],
            PageSizeParameterName = "pageSize",
            LastAnchorParameterNames = ["lastAnchor0"]
        };

        var sql = _provider.BuildKeysetPageCommandText(request);

        Assert.That(sql, Does.Not.Contain("OFFSET").IgnoreCase,
            "OFFSET re-scans every skipped row, so a 500,000-row import degrades quadratically; keyset paging is the contract.");
    }

    [Test]
    public void BuildKeysetPageCommandText_CompositeAnchor_ComparesLexicographically()
    {
        var request = new SqlKeysetPageRequest
        {
            ObjectName = "EMPLOYEES",
            SelectColumns = ["COMPANY_ID", "EMPLOYEE_ID"],
            AnchorColumns = ["COMPANY_ID", "EMPLOYEE_ID"],
            PageSizeParameterName = "pageSize",
            LastAnchorParameterNames = ["lastAnchor0", "lastAnchor1"]
        };

        var sql = _provider.BuildKeysetPageCommandText(request);

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("WHERE ([COMPANY_ID] > @lastAnchor0 OR ([COMPANY_ID] = @lastAnchor0 AND [EMPLOYEE_ID] > @lastAnchor1))"),
                "SQL Server has no row-value comparison, so a composite anchor must expand into the equivalent OR chain.");
            Assert.That(sql, Does.Contain("ORDER BY [COMPANY_ID], [EMPLOYEE_ID]"),
                "The ordering must match the comparison exactly or pages overlap or skip rows.");
        });
    }

    [Test]
    public void BuildKeysetPageCommandText_AnchorParameterCountMismatch_Throws()
    {
        var request = new SqlKeysetPageRequest
        {
            ObjectName = "EMPLOYEES",
            SelectColumns = ["COMPANY_ID", "EMPLOYEE_ID"],
            AnchorColumns = ["COMPANY_ID", "EMPLOYEE_ID"],
            PageSizeParameterName = "pageSize",
            LastAnchorParameterNames = ["lastAnchor0"]
        };

        Assert.Throws<ArgumentException>(() => _provider.BuildKeysetPageCommandText(request),
            "A partial anchor would silently produce a wrong page boundary, so it must fail loudly instead.");
    }

    [Test]
    public void BuildKeysetPageCommandText_NoAnchorColumns_Throws()
    {
        var request = new SqlKeysetPageRequest
        {
            ObjectName = "EMPLOYEES",
            SelectColumns = ["EMPLOYEE_ID"],
            AnchorColumns = [],
            PageSizeParameterName = "pageSize"
        };

        Assert.Throws<ArgumentException>(() => _provider.BuildKeysetPageCommandText(request),
            "Keyset paging has no meaning without a stable ordering key.");
    }

    #endregion

    #region Generated keys

    [Test]
    public void GeneratedKeyRetrieval_IsResultSet()
    {
        Assert.That(_provider.GeneratedKeyRetrieval, Is.EqualTo(SqlGeneratedKeyRetrieval.ResultSet),
            "SQL Server returns an OUTPUT clause's value as a result set, not through an output parameter.");
    }

    [Test]
    public void BuildInsertReturningGeneratedKeyCommandText_IdentityColumn_UsesOutputInserted()
    {
        var command = new SqlInsertCommand
        {
            SchemaName = "dbo",
            ObjectName = "APP_USERS",
            Columns = [new SqlColumnParameter("NAME", "p0"), new SqlColumnParameter("EMAIL", "p1")],
            GeneratedKeyColumn = "ID",
            GeneratedKeyParameterName = "generatedKey"
        };

        var sql = _provider.BuildInsertReturningGeneratedKeyCommandText(command);

        Assert.That(sql, Is.EqualTo("INSERT INTO [dbo].[APP_USERS] ([NAME], [EMAIL]) OUTPUT INSERTED.[ID] VALUES (@p0, @p1)"));
    }

    [Test]
    public void CreateGeneratedKeyParameter_AnyKeyType_ReturnsNull()
    {
        Assert.That(_provider.CreateGeneratedKeyParameter("generatedKey", AttributeDataType.Number), Is.Null,
            "The generated key arrives in a result set here, so there is no output parameter to bind.");
    }

    #endregion

    #region Connections

    [Test]
    public void ConnectivityTestCommandText_IsATrivialQuery()
    {
        Assert.That(_provider.ConnectivityTestCommandText, Is.EqualTo("SELECT 1"));
    }

    [Test]
    public void BuildConnectionString_DiscreteSettings_ProducesTheExpectedConnectionString()
    {
        var settings = new SqlConnectionSettings
        {
            Host = "sql.example.local",
            Port = 1433,
            DatabaseName = "HR",
            Username = "jim_reader",
            Password = "s3cret",
            UseTls = true,
            ConnectionTimeoutSeconds = 20
        };

        var builder = new SqlConnectionStringBuilder(_provider.BuildConnectionString(settings));

        Assert.Multiple(() =>
        {
            Assert.That(builder.DataSource, Is.EqualTo("sql.example.local,1433"), "SQL Server expresses the port as a comma-separated suffix on the data source.");
            Assert.That(builder.InitialCatalog, Is.EqualTo("HR"));
            Assert.That(builder.UserID, Is.EqualTo("jim_reader"));
            Assert.That(builder.Password, Is.EqualTo("s3cret"));
            Assert.That(builder.ConnectTimeout, Is.EqualTo(20));
            Assert.That(builder.Encrypt, Is.EqualTo(SqlConnectionEncryptOption.Mandatory), "TLS enabled means encryption is required, not merely offered.");
            Assert.That(builder.TrustServerCertificate, Is.False, "A blanket trust-server-certificate toggle is explicitly not an acceptable substitute for real trust anchors.");
        });
    }

    [Test]
    public void BuildConnectionString_Always_DisablesConnectionPooling()
    {
        var settings = new SqlConnectionSettings { Host = "sql.example.local", DatabaseName = "HR" };

        var builder = new SqlConnectionStringBuilder(_provider.BuildConnectionString(settings));

        Assert.That(builder.Pooling, Is.False,
            "A pool outlives the Connector that filled it: it holds sessions open on the database long after a run, and can re-handshake using a trust anchor file JIM has already deleted.");
    }

    [Test]
    public void BuildConnectionString_NoPort_OmitsThePortSuffix()
    {
        var settings = new SqlConnectionSettings { Host = "sql.example.local", DatabaseName = "HR" };

        var builder = new SqlConnectionStringBuilder(_provider.BuildConnectionString(settings));

        Assert.That(builder.DataSource, Is.EqualTo("sql.example.local"), "Omitting the port lets the client use the default instance discovery.");
    }

    [TestCase("sql.example.local;Password=oops")]
    [TestCase("sql.example.local\"")]
    [TestCase("sql.example.local ")]
    [TestCase("")]
    public void BuildConnectionString_HostileHost_Throws(string host)
    {
        var settings = new SqlConnectionSettings { Host = host, DatabaseName = "HR" };

        Assert.Throws<ArgumentException>(() => _provider.BuildConnectionString(settings),
            "The host reaches a connection string, so it is validated rather than trusted to be escaped downstream.");
    }

    [Test]
    public void CreateConnection_ValidConnectionString_ReturnsAClosedSqlConnection()
    {
        var settings = new SqlConnectionSettings { Host = "sql.example.local", DatabaseName = "HR" };

        using var connection = _provider.CreateConnection(_provider.BuildConnectionString(settings));

        Assert.Multiple(() =>
        {
            Assert.That(connection, Is.InstanceOf<SqlConnection>());
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed), "Creating a connection must not open it; the caller owns the lifetime.");
        });
    }

    [Test]
    public void CreateCommand_ValidText_CarriesTheTextAndTheConnection()
    {
        var settings = new SqlConnectionSettings { Host = "sql.example.local", DatabaseName = "HR" };
        using var connection = _provider.CreateConnection(_provider.BuildConnectionString(settings));

        using var command = _provider.CreateCommand(connection, "SELECT 1");

        Assert.Multiple(() =>
        {
            Assert.That(command.CommandText, Is.EqualTo("SELECT 1"));
            Assert.That(command.Connection, Is.SameAs(connection));
        });
    }

    #endregion

    #region GUID byte order

    [Test]
    public void ConvertToGuid_UniqueIdentifierValue_PassesItThrough()
    {
        var expected = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");

        Assert.That(_provider.ConvertToGuid(expected), Is.EqualTo(expected),
            "SqlClient already materialises 'uniqueidentifier' as a Guid, so there is nothing left to reorder.");
    }

    [Test]
    public void ConvertToGuid_BinaryValue_ReadsItAsMicrosoftByteOrder()
    {
        var expected = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
        var bytes = IdentifierParser.ToMicrosoftBytes(expected);

        Assert.That(_provider.ConvertToGuid(bytes), Is.EqualTo(expected),
            "SQL Server's 'uniqueidentifier' is little-endian in its first three components, unlike Oracle's RAW(16).");
    }

    [Test]
    public void ConvertFromGuid_AnyGuid_StaysAGuid()
    {
        var guid = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");

        Assert.That(_provider.ConvertFromGuid(guid), Is.EqualTo(guid),
            "SqlClient binds a Guid directly to a 'uniqueidentifier' parameter; converting to bytes would transpose it.");
    }

    #endregion

    #region Schema catalogue queries

    [Test]
    public void TablesCommandText_QueriesTheCatalogueForBaseTables()
    {
        var sql = _provider.TablesCommandText;

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("INFORMATION_SCHEMA.TABLES"));
            Assert.That(sql, Does.Contain("BASE TABLE"), "Views are enumerated separately so schema discovery can tell them apart.");
            Assert.That(sql, Does.Contain(SqlCatalogueColumns.SchemaName), "Both dialects alias to the same result column names so schema discovery stays dialect-free.");
            Assert.That(sql, Does.Contain(SqlCatalogueColumns.ObjectName));
        });
    }

    [Test]
    public void ViewsCommandText_QueriesTheCatalogueForViews()
    {
        Assert.That(_provider.ViewsCommandText, Does.Contain("INFORMATION_SCHEMA.VIEWS"));
    }

    [Test]
    public void ColumnsCommandText_FiltersBySchemaAndObjectNameAsParameters()
    {
        var sql = _provider.ColumnsCommandText;

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("INFORMATION_SCHEMA.COLUMNS"));
            Assert.That(sql, Does.Contain("@" + SqlCatalogueParameters.SchemaName), "Catalogue filters are values, so they must be bound as parameters.");
            Assert.That(sql, Does.Contain("@" + SqlCatalogueParameters.ObjectName));
        });
    }

    [Test]
    public void PrimaryKeyColumnsCommandText_SelectsPrimaryKeyConstraintColumnsInOrder()
    {
        var sql = _provider.PrimaryKeyColumnsCommandText;

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("PRIMARY KEY"));
            Assert.That(sql, Does.Contain("ORDER BY"), "A composite key's column order is part of the anchor's meaning.");
            Assert.That(sql, Does.Contain("@" + SqlCatalogueParameters.ObjectName));
        });
    }

    [Test]
    public void ForeignKeyColumnsCommandText_ExposesBothSidesOfEachConstraint()
    {
        var sql = _provider.ForeignKeyColumnsCommandText;

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("sys.foreign_keys"));
            Assert.That(sql, Does.Contain(SqlCatalogueColumns.ReferencedTable), "Reference suggestions need the referenced table and column, not just the owning column.");
            Assert.That(sql, Does.Contain(SqlCatalogueColumns.ReferencedColumn));
        });
    }

    #endregion
}
