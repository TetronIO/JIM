// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql.Providers;
using JIM.Models.Core;
using JIM.Utilities;
using NUnit.Framework;
using Oracle.ManagedDataAccess.Client;
using System.Data;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers the Oracle Database dialect behind <see cref="ISqlProvider"/>. Oracle differs from SQL Server
/// in every dimension the seam exists to hide: bind prefix, quoting, row limiting, generated-key
/// retrieval and catalogue views.
/// </summary>
[TestFixture]
public class OracleProviderTests
{
    private OracleProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _provider = new OracleProvider();
    }

    #region Identity

    [Test]
    public void DatabaseType_IsOracle()
    {
        Assert.That(_provider.DatabaseType, Is.EqualTo(SqlDatabaseType.Oracle));
    }

    #endregion

    #region Parameters

    [Test]
    public void ParameterPrefix_IsColon()
    {
        Assert.That(_provider.ParameterPrefix, Is.EqualTo(":"), "Oracle binds parameters by a ':'-prefixed name.");
    }

    [Test]
    public void GetParameterPlaceholder_ValidName_PrefixesWithColon()
    {
        Assert.That(_provider.GetParameterPlaceholder("lastAnchor"), Is.EqualTo(":lastAnchor"));
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parameter.ParameterName, Is.EqualTo("pageSize"));
            Assert.That(parameter.Value, Is.EqualTo(500));
        }
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
    public void QuoteIdentifier_OrdinaryName_WrapsInDoubleQuotes()
    {
        Assert.That(_provider.QuoteIdentifier("EMPLOYEE_ID"), Is.EqualTo("\"EMPLOYEE_ID\""));
    }

    [Test]
    public void QuoteIdentifier_NameContainingADoubleQuote_DoublesIt()
    {
        var quoted = _provider.QuoteIdentifier("EMPLOYEES\"; DROP TABLE USERS--");

        Assert.That(quoted, Is.EqualTo("\"EMPLOYEES\"\"; DROP TABLE USERS--\""),
            "Doubling the closing quote keeps the whole hostile string inside one quoted identifier.");
    }

    [TestCase("")]
    [TestCase("   ")]
    public void QuoteIdentifier_EmptyName_Throws(string identifier)
    {
        Assert.Throws<ArgumentException>(() => _provider.QuoteIdentifier(identifier), "An empty identifier can never be legitimate.");
    }

    [Test]
    public void QuoteIdentifier_NameContainingAControlCharacter_Throws()
    {
        Assert.Throws<ArgumentException>(() => _provider.QuoteIdentifier("EMPLOYEES\r\n"),
            "Control characters have no place in a real object name and complicate the command text downstream.");
    }

    [Test]
    public void QualifyObjectName_SchemaSupplied_QuotesBothParts()
    {
        Assert.That(_provider.QualifyObjectName("HR", "EMPLOYEES"), Is.EqualTo("\"HR\".\"EMPLOYEES\""));
    }

    #endregion

    #region Keyset pagination

    [Test]
    public void BuildKeysetPageCommandText_FirstPage_FetchesTheFirstRowsOfTheOrderedSet()
    {
        var request = new SqlKeysetPageRequest
        {
            SchemaName = "HR",
            ObjectName = "EMPLOYEES",
            SelectColumns = ["EMPLOYEE_ID", "FIRST_NAME"],
            AnchorColumns = ["EMPLOYEE_ID"],
            PageSizeParameterName = "pageSize"
        };

        var sql = _provider.BuildKeysetPageCommandText(request);

        Assert.That(sql, Is.EqualTo("SELECT \"EMPLOYEE_ID\", \"FIRST_NAME\" FROM \"HR\".\"EMPLOYEES\" ORDER BY \"EMPLOYEE_ID\" FETCH FIRST :pageSize ROWS ONLY"));
    }

    [Test]
    public void BuildKeysetPageCommandText_SubsequentPage_FiltersBeyondTheLastAnchor()
    {
        var request = new SqlKeysetPageRequest
        {
            SchemaName = "HR",
            ObjectName = "EMPLOYEES",
            SelectColumns = ["EMPLOYEE_ID", "FIRST_NAME"],
            AnchorColumns = ["EMPLOYEE_ID"],
            PageSizeParameterName = "pageSize",
            LastAnchorParameterNames = ["lastAnchor0"]
        };

        var sql = _provider.BuildKeysetPageCommandText(request);

        Assert.That(sql, Is.EqualTo("SELECT \"EMPLOYEE_ID\", \"FIRST_NAME\" FROM \"HR\".\"EMPLOYEES\" WHERE \"EMPLOYEE_ID\" > :lastAnchor0 ORDER BY \"EMPLOYEE_ID\" FETCH FIRST :pageSize ROWS ONLY"));
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sql, Does.Contain("WHERE (\"COMPANY_ID\" > :lastAnchor0 OR (\"COMPANY_ID\" = :lastAnchor0 AND \"EMPLOYEE_ID\" > :lastAnchor1))"),
                "Oracle supports row-value comparison only for equality, so a composite anchor must expand into the equivalent OR chain.");
            Assert.That(sql, Does.Contain("ORDER BY \"COMPANY_ID\", \"EMPLOYEE_ID\""),
                "The ordering must match the comparison exactly or pages overlap or skip rows.");
        }
    }

    [Test]
    public void BuildKeysetPageCommandText_WatermarkPageWithARelatedTable_AlsoSelectsParentsWhoseRelatedRowsChanged()
    {
        var request = new SqlKeysetPageRequest
        {
            SchemaName = "HR",
            ObjectName = "EMPLOYEES",
            SelectColumns = ["EMPLOYEE_ID", "FIRST_NAME"],
            AnchorColumns = ["EMPLOYEE_ID"],
            PageSizeParameterName = "pageSize",
            ChangeColumn = "LAST_MODIFIED",
            ChangeParameterName = "watermark",
            RelatedChangeSources =
            [
                new SqlRelatedChangeSource
                {
                    SchemaName = "HR",
                    TableName = "EMPLOYEE_PHONES",
                    JoinColumns = ["EMPLOYEE_ID"],
                    WatermarkColumn = "ROW_CHANGED",
                    WatermarkParameterName = "relatedWatermark0"
                }
            ]
        };

        var sql = _provider.BuildKeysetPageCommandText(request);

        Assert.That(sql, Is.EqualTo(
            "SELECT \"EMPLOYEE_ID\", \"FIRST_NAME\" FROM \"HR\".\"EMPLOYEES\" \"JIM_SOURCE\" " +
            "WHERE (\"LAST_MODIFIED\" > :watermark OR EXISTS (SELECT 1 FROM \"HR\".\"EMPLOYEE_PHONES\" \"JIM_RELATED0\" " +
            "WHERE \"JIM_RELATED0\".\"EMPLOYEE_ID\" = \"JIM_SOURCE\".\"EMPLOYEE_ID\" AND \"JIM_RELATED0\".\"ROW_CHANGED\" > :relatedWatermark0)) " +
            "ORDER BY \"EMPLOYEE_ID\" FETCH FIRST :pageSize ROWS ONLY"),
            "A membership added or revoked never moves the parent row's own watermark, so the parent has to be selectable on its related table's evidence too.");
    }

    [Test]
    public void BuildKeysetPageCommandText_WatermarkPageWithTwoRelatedTables_OrsOneExistsPerRelatedTable()
    {
        var request = new SqlKeysetPageRequest
        {
            ObjectName = "EMPLOYEES",
            SelectColumns = ["EMPLOYEE_ID"],
            AnchorColumns = ["EMPLOYEE_ID"],
            PageSizeParameterName = "pageSize",
            ChangeColumn = "LAST_MODIFIED",
            ChangeParameterName = "watermark",
            RelatedChangeSources =
            [
                new SqlRelatedChangeSource
                {
                    TableName = "EMPLOYEE_PHONES",
                    JoinColumns = ["EMPLOYEE_ID"],
                    WatermarkColumn = "ROW_CHANGED",
                    WatermarkParameterName = "relatedWatermark0"
                },
                new SqlRelatedChangeSource
                {
                    TableName = "EMPLOYEE_GROUPS",
                    JoinColumns = ["EMPLOYEE_ID"],
                    WatermarkColumn = "ROW_CHANGED",
                    WatermarkParameterName = "relatedWatermark1"
                }
            ]
        };

        var sql = _provider.BuildKeysetPageCommandText(request);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sql, Does.Contain("EXISTS (SELECT 1 FROM \"EMPLOYEE_PHONES\" \"JIM_RELATED0\" WHERE \"JIM_RELATED0\".\"EMPLOYEE_ID\" = \"JIM_SOURCE\".\"EMPLOYEE_ID\" AND \"JIM_RELATED0\".\"ROW_CHANGED\" > :relatedWatermark0)"));
            Assert.That(sql, Does.Contain("EXISTS (SELECT 1 FROM \"EMPLOYEE_GROUPS\" \"JIM_RELATED1\" WHERE \"JIM_RELATED1\".\"EMPLOYEE_ID\" = \"JIM_SOURCE\".\"EMPLOYEE_ID\" AND \"JIM_RELATED1\".\"ROW_CHANGED\" > :relatedWatermark1)"),
                "Each related table carries its own watermark, so each one gets its own correlated subquery rather than being folded into a single join.");
            Assert.That(sql, Does.Not.Contain("JOIN"),
                "A join would return one parent row per matching related row, which is how a page silently turns into duplicate objects.");
        }
    }

    #endregion

    #region Generated keys

    [Test]
    public void GeneratedKeyRetrieval_IsOutputParameter()
    {
        Assert.That(_provider.GeneratedKeyRetrieval, Is.EqualTo(SqlGeneratedKeyRetrieval.OutputParameter),
            "Oracle's RETURNING clause hands the generated value back through a bound output parameter.");
    }

    [Test]
    public void BuildInsertReturningGeneratedKeyCommandText_SequenceBackedKey_UsesReturningInto()
    {
        var command = new SqlInsertCommand
        {
            SchemaName = "HR",
            ObjectName = "APP_USERS",
            Columns = [new SqlColumnParameter("NAME", "p0"), new SqlColumnParameter("EMAIL", "p1")],
            GeneratedKeyColumn = "ID",
            GeneratedKeyParameterName = "generatedKey"
        };

        var sql = _provider.BuildInsertReturningGeneratedKeyCommandText(command);

        Assert.That(sql, Is.EqualTo("INSERT INTO \"HR\".\"APP_USERS\" (\"NAME\", \"EMAIL\") VALUES (:p0, :p1) RETURNING \"ID\" INTO :generatedKey"));
    }

    [Test]
    public void CreateGeneratedKeyParameter_NumericKey_ReturnsABoundOutputParameter()
    {
        var parameter = _provider.CreateGeneratedKeyParameter("generatedKey", AttributeDataType.Number);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parameter, Is.Not.Null, "Oracle needs a real output parameter to receive the RETURNING value.");
            Assert.That(parameter!.ParameterName, Is.EqualTo("generatedKey"));
            Assert.That(parameter.Direction, Is.EqualTo(ParameterDirection.Output));
        }
    }

    [Test]
    public void CreateGeneratedKeyParameter_TextKey_IsSizedSoTheValueFits()
    {
        var parameter = _provider.CreateGeneratedKeyParameter("generatedKey", AttributeDataType.Text);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parameter, Is.Not.Null);
            Assert.That(parameter!.Size, Is.GreaterThan(0),
                "An unsized output parameter silently truncates a returned string key to nothing on ODP.NET.");
        }
    }

    #endregion

    #region Connections

    [Test]
    public void ConnectivityTestCommandText_IsATrivialQuery()
    {
        Assert.That(_provider.ConnectivityTestCommandText, Is.EqualTo("SELECT 1 FROM DUAL"),
            "Oracle has no bare SELECT without a FROM clause.");
    }

    [Test]
    public void BuildConnectionString_ServiceName_BuildsAServiceNameDescriptor()
    {
        var settings = new SqlConnectionSettings
        {
            Host = "oracle.example.local",
            Port = 1521,
            ServiceName = "HRPDB",
            Username = "jim_reader",
            Password = "s3cret",
            ConnectionTimeoutSeconds = 20
        };

        var builder = new OracleConnectionStringBuilder(_provider.BuildConnectionString(settings));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(builder.DataSource, Does.Contain("(PROTOCOL=TCP)"), "Without TLS the listener is addressed over plain TCP.");
            Assert.That(builder.DataSource, Does.Contain("(HOST=oracle.example.local)"));
            Assert.That(builder.DataSource, Does.Contain("(PORT=1521)"));
            Assert.That(builder.DataSource, Does.Contain("(SERVICE_NAME=HRPDB)"));
            Assert.That(builder.UserID, Is.EqualTo("jim_reader"));
            Assert.That(builder.Password, Is.EqualTo("s3cret"));
            Assert.That(builder.ConnectionTimeout, Is.EqualTo(20));
        }
    }

    [Test]
    public void BuildConnectionString_Sid_BuildsASidDescriptor()
    {
        var settings = new SqlConnectionSettings { Host = "oracle.example.local", Port = 1521, Sid = "ORCL" };

        var builder = new OracleConnectionStringBuilder(_provider.BuildConnectionString(settings));

        Assert.That(builder.DataSource, Does.Contain("(SID=ORCL)"), "Older estates address a database by SID rather than service name.");
    }

    [Test]
    public void BuildConnectionString_Always_DisablesConnectionPooling()
    {
        var settings = new SqlConnectionSettings { Host = "oracle.example.local", Port = 1521, ServiceName = "HRPDB" };

        var builder = new OracleConnectionStringBuilder(_provider.BuildConnectionString(settings));

        Assert.That(builder.Pooling, Is.False,
            "A pool outlives the Connector that filled it: it holds sessions open on the database long after a run, and JIM opens one connection per operation rather than one per object, so pooling buys nothing to offset that.");
    }

    [Test]
    public void BuildConnectionString_Tcps_UsesTheEncryptedProtocol()
    {
        var settings = new SqlConnectionSettings { Host = "oracle.example.local", Port = 2484, ServiceName = "HRPDB", Encryption = SqlConnectionEncryption.Tls };

        var builder = new OracleConnectionStringBuilder(_provider.BuildConnectionString(settings));

        Assert.That(builder.DataSource, Does.Contain("(PROTOCOL=TCPS)"), "Oracle Net expresses TLS as the TCPS protocol in the address descriptor.");
    }

    [Test]
    public void BuildConnectionString_NativeNetworkEncryption_UsesTheOrdinaryListenerAndProtocol()
    {
        // Native Network Encryption is negotiated on an ordinary TCP connection to the ordinary
        // listener; it is not TLS and has no separate listener of its own.
        var settings = new SqlConnectionSettings { Host = "oracle.example.local", ServiceName = "HRPDB", Encryption = SqlConnectionEncryption.OracleNativeNetworkEncryption };

        var builder = new OracleConnectionStringBuilder(_provider.BuildConnectionString(settings));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(builder.DataSource, Does.Contain("(PROTOCOL=TCP)"));
            Assert.That(builder.DataSource, Does.Not.Contain("TCPS"));
            Assert.That(builder.DataSource, Does.Contain("(PORT=1521)"), "TCPS has its own listener port; Native Network Encryption uses the ordinary one.");
        }
    }

    [Test]
    public void GetDefaultPort_DependsOnWhetherTheTransportIsTcps()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_provider.GetDefaultPort(SqlConnectionEncryption.Tls), Is.EqualTo(2484), "TCPS listens on its own port.");
            Assert.That(_provider.GetDefaultPort(SqlConnectionEncryption.OracleNativeNetworkEncryption), Is.EqualTo(1521));
            Assert.That(_provider.GetDefaultPort(SqlConnectionEncryption.None), Is.EqualTo(1521));
        }
    }

    [Test]
    public void ConfigureConnection_NativeNetworkEncryption_RequiresStrongEncryptionAndIntegrityOnTheConnection()
    {
        // These are the driver's per-connection Oracle Advanced Networking settings. Their process-wide
        // equivalents on OracleConfiguration are static, so one Connected System's choice would decide
        // every other Connected System's connections; these instance properties do not.
        var settings = new SqlConnectionSettings { Host = "oracle.example.local", ServiceName = "HRPDB", Encryption = SqlConnectionEncryption.OracleNativeNetworkEncryption };
        using var connection = (OracleConnection)_provider.CreateConnection(_provider.BuildConnectionString(settings));

        _provider.ConfigureConnection(connection, settings);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connection.SqlNetEncryptionClient, Is.EqualTo("REQUIRED"),
                "Anything weaker lets the connection fall back to plain text without saying so, which is exactly what Mandatory rules out on Microsoft SQL Server.");
            Assert.That(connection.SqlNetEncryptionTypesClient, Is.EqualTo("AES256, AES192, AES128"),
                "Naming only the AES algorithms is what keeps DES and RC4 out, whatever the driver's own weak-crypto default is.");
            Assert.That(connection.SqlNetCryptoChecksumClient, Is.EqualTo("REQUIRED"),
                "Encryption without integrity protection leaves the traffic malleable; Oracle estates configure the pair together.");
            Assert.That(connection.SqlNetCryptoChecksumTypesClient, Is.EqualTo("SHA512, SHA384, SHA256"));
        }
    }

    [Test]
    public void ConfigureConnection_Tcps_LeavesTheNativeNetworkEncryptionSettingsUntouched()
    {
        // TCPS already encrypts the transport. Asking for Native Network Encryption on top of it is what
        // SQLNET.IGNORE_ANO_ENCRYPTION_FOR_TCPS exists to unpick, and that setting is process-wide only;
        // never asking for both avoids the interaction rather than reaching for a global to resolve it.
        var settings = new SqlConnectionSettings { Host = "oracle.example.local", ServiceName = "HRPDB", Encryption = SqlConnectionEncryption.Tls };
        using var connection = (OracleConnection)_provider.CreateConnection(_provider.BuildConnectionString(settings));

        _provider.ConfigureConnection(connection, settings);

        using (Assert.EnterMultipleScope())
        {
            // An untouched setting reads back from the driver as null rather than as an empty string.
            // Either way nothing has been asked for, which is what this asserts.
            Assert.That(connection.SqlNetEncryptionClient, Is.Null.Or.Empty);
            Assert.That(connection.SqlNetCryptoChecksumClient, Is.Null.Or.Empty);
        }
    }

    [Test]
    public void ConfigureConnection_NoEncryption_LeavesTheNativeNetworkEncryptionSettingsUntouched()
    {
        // Unset means the driver's own default, which accepts encryption a server insists on rather than
        // refusing it. "None" is JIM declining to require encryption, not JIM refusing it.
        var settings = new SqlConnectionSettings { Host = "oracle.example.local", ServiceName = "HRPDB", Encryption = SqlConnectionEncryption.None };
        using var connection = (OracleConnection)_provider.CreateConnection(_provider.BuildConnectionString(settings));

        _provider.ConfigureConnection(connection, settings);

        using (Assert.EnterMultipleScope())
        {
            // An untouched setting reads back from the driver as null rather than as an empty string.
            // Either way nothing has been asked for, which is what this asserts.
            Assert.That(connection.SqlNetEncryptionClient, Is.Null.Or.Empty);
            Assert.That(connection.SqlNetCryptoChecksumClient, Is.Null.Or.Empty);
        }
    }

    [Test]
    public void BuildConnectionString_NeitherServiceNameNorSid_Throws()
    {
        var settings = new SqlConnectionSettings { Host = "oracle.example.local", Port = 1521 };

        Assert.Throws<ArgumentException>(() => _provider.BuildConnectionString(settings),
            "An Oracle connect descriptor cannot identify a database without one of the two.");
    }

    [TestCase("oracle.example.local)(x=1")]
    [TestCase("oracle.example.local ")]
    [TestCase("")]
    public void BuildConnectionString_HostileHost_Throws(string host)
    {
        var settings = new SqlConnectionSettings { Host = host, Port = 1521, ServiceName = "HRPDB" };

        Assert.Throws<ArgumentException>(() => _provider.BuildConnectionString(settings),
            "The host is placed inside an Oracle Net descriptor, which is parsed structurally; unbalanced parentheses would rewrite the address.");
    }

    [Test]
    public void BuildConnectionString_HostileServiceName_Throws()
    {
        var settings = new SqlConnectionSettings { Host = "oracle.example.local", Port = 1521, ServiceName = "HRPDB)(SOMETHING=else" };

        Assert.Throws<ArgumentException>(() => _provider.BuildConnectionString(settings),
            "The service name is placed inside an Oracle Net descriptor and must not be able to close it.");
    }

    [Test]
    public void CreateConnection_ValidConnectionString_ReturnsAClosedOracleConnection()
    {
        var settings = new SqlConnectionSettings { Host = "oracle.example.local", Port = 1521, ServiceName = "HRPDB" };

        using var connection = _provider.CreateConnection(_provider.BuildConnectionString(settings));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connection, Is.InstanceOf<OracleConnection>());
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed), "Creating a connection must not open it; the caller owns the lifetime.");
        }
    }

    [Test]
    public void CreateCommand_AnyText_BindsParametersByName()
    {
        var settings = new SqlConnectionSettings { Host = "oracle.example.local", Port = 1521, ServiceName = "HRPDB" };
        using var connection = _provider.CreateConnection(_provider.BuildConnectionString(settings));

        using var command = _provider.CreateCommand(connection, "SELECT 1 FROM DUAL");

        Assert.That(((OracleCommand)command).BindByName, Is.True,
            "ODP.NET binds positionally by default, so a named parameter reused twice in one statement would bind to the wrong value.");
    }

    #endregion

    #region GUID byte order

    [Test]
    public void ConvertToGuid_Raw16Bytes_ReadsThemAsBigEndian()
    {
        var expected = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
        var raw16 = IdentifierParser.ToRfc4122Bytes(expected);

        var result = _provider.ConvertToGuid(raw16);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(expected));
            Assert.That(raw16[0], Is.EqualTo(0x55),
                "The fixture is only meaningful while Oracle's RAW(16) layout genuinely differs from the Microsoft one, whose first byte here is 0x00.");
        }
    }

    [Test]
    public void ConvertToGuid_MicrosoftOrderedBytes_ProducesTheTransposedValue()
    {
        var original = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
        var microsoftOrdered = IdentifierParser.ToMicrosoftBytes(original);

        var result = _provider.ConvertToGuid(microsoftOrdered);

        Assert.That(result, Is.EqualTo(IdentifierParser.FromRfc4122Bytes(microsoftOrdered)),
            "Oracle always means big-endian, so feeding it Microsoft-ordered bytes must visibly transpose rather than silently agree.");
    }

    [Test]
    public void ConvertFromGuid_AnyGuid_ProducesRfc4122Bytes()
    {
        var guid = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");

        var result = _provider.ConvertFromGuid(guid);

        Assert.That(result, Is.EqualTo(IdentifierParser.ToRfc4122Bytes(guid)),
            "Exporting Microsoft-ordered bytes into RAW(16) would write a different identifier from the one JIM holds.");
    }

    [Test]
    public void ConvertToGuid_StringValue_ParsesTheCanonicalForm()
    {
        var result = _provider.ConvertToGuid("550e8400-e29b-41d4-a716-446655440000");

        Assert.That(result, Is.EqualTo(Guid.Parse("550e8400-e29b-41d4-a716-446655440000")),
            "Some estates store identifiers as VARCHAR2 rather than RAW(16).");
    }

    #endregion

    #region Schema catalogue queries

    [Test]
    public void TablesCommandText_QueriesTheAllTablesView()
    {
        var sql = _provider.TablesCommandText;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sql, Does.Contain("ALL_TABLES"));
            Assert.That(sql, Does.Contain(SqlCatalogueColumns.SchemaName), "Both dialects alias to the same result column names so schema discovery stays dialect-free.");
            Assert.That(sql, Does.Contain(SqlCatalogueColumns.ObjectName));
        }
    }

    [Test]
    public void ViewsCommandText_QueriesTheAllViewsView()
    {
        Assert.That(_provider.ViewsCommandText, Does.Contain("ALL_VIEWS"));
    }

    [Test]
    public void ColumnsCommandText_FiltersBySchemaAndObjectNameAsParameters()
    {
        var sql = _provider.ColumnsCommandText;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sql, Does.Contain("ALL_TAB_COLUMNS"));
            Assert.That(sql, Does.Contain(":" + SqlCatalogueParameters.SchemaName), "Catalogue filters are values, so they must be bound as parameters.");
            Assert.That(sql, Does.Contain(":" + SqlCatalogueParameters.ObjectName));
        }
    }

    [Test]
    public void PrimaryKeyColumnsCommandText_SelectsPrimaryKeyConstraintColumnsInOrder()
    {
        var sql = _provider.PrimaryKeyColumnsCommandText;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sql, Does.Contain("ALL_CONSTRAINTS"));
            Assert.That(sql, Does.Contain("'P'"), "Oracle records a primary key as constraint type 'P'.");
            Assert.That(sql, Does.Contain("ORDER BY"), "A composite key's column order is part of the anchor's meaning.");
        }
    }

    [Test]
    public void ForeignKeyColumnsCommandText_ExposesBothSidesOfEachConstraint()
    {
        var sql = _provider.ForeignKeyColumnsCommandText;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sql, Does.Contain("'R'"), "Oracle records a referential constraint as type 'R'.");
            Assert.That(sql, Does.Contain(SqlCatalogueColumns.ReferencedTable), "Reference suggestions need the referenced table and column, not just the owning column.");
            Assert.That(sql, Does.Contain(SqlCatalogueColumns.ReferencedColumn));
        }
    }

    #endregion
}
