// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Utilities;
using Oracle.ManagedDataAccess.Client;
using System.Data;
using System.Data.Common;

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// The Oracle Database dialect. Uses the fully managed <c>Oracle.ManagedDataAccess.Core</c> driver, so
/// no Oracle client installation is required and the Connector stays air-gap deployable.
/// </summary>
internal class OracleProvider : SqlProviderBase
{
    /// <summary>
    /// The default listener port for each transport, used when the administrator leaves the port unset.
    /// </summary>
    private const int DefaultPort = 1521;

    private const int DefaultTlsPort = 2484;

    /// <summary>
    /// Sized to Oracle's maximum VARCHAR2 in a PL/SQL context. An output parameter with no size
    /// silently returns nothing on ODP.NET, so a returned string key must always be sized.
    /// </summary>
    private const int TextKeyParameterSize = 4000;

    /// <summary>
    /// A GUID is exactly 16 bytes when stored in RAW(16).
    /// </summary>
    private const int GuidKeyParameterSize = 16;

    public override SqlDatabaseType DatabaseType => SqlDatabaseType.Oracle;

    public override string DisplayName => "Oracle Database";

    public override string ParameterPrefix => ":";

    /// <summary>
    /// Oracle has no bare SELECT without a FROM clause; DUAL is its one-row system table.
    /// </summary>
    public override string ConnectivityTestCommandText => "SELECT 1 FROM DUAL";

    /// <summary>
    /// TCPS listens on its own port; Native Network Encryption runs over the ordinary listener, so it
    /// takes the same default port as an unencrypted connection.
    /// </summary>
    public override int GetDefaultPort(SqlConnectionEncryption encryption) =>
        encryption == SqlConnectionEncryption.Tls ? DefaultTlsPort : DefaultPort;

    /// <summary>
    /// TCPS is what Oracle's own documentation and an Oracle administrator call the encrypted transport,
    /// so it is the term JIM uses when reporting a refused certificate on one.
    /// </summary>
    public override string SecureTransportName => "TCPS";

    /// <summary>
    /// False for now, pending verification against a real server rather than as a settled conclusion.
    /// ODP.NET exposes no server certificate validation callback and no per-connection trust anchor; its
    /// only trust configuration is an Oracle wallet (<c>WalletLocation</c>), or the Microsoft Certificate
    /// Store on Windows. Whether JIM can supply that wallet from managed code is genuinely open: the
    /// driver's wallet reader takes a wallet password and has a PEM code path, which points at ordinary
    /// PKCS#12 rather than only the auto-login wallets Oracle's own native tooling produces, and .NET can
    /// write both. Settling it needs a live TCPS listener, so it is a question for the integration test
    /// phase. Until then a TCPS connection validates against the operating system's bundle alone, and JIM
    /// reports exactly which certificate was refused and why, so an administrator knows what to install.
    /// Native Network Encryption is unaffected: it uses no certificate at all, which is one reason it is
    /// the default Oracle encryption mode JIM offers.
    /// </summary>
    public override bool SupportsPinnedServerCertificate => false;

    public override SqlGeneratedKeyRetrieval GeneratedKeyRetrieval => SqlGeneratedKeyRetrieval.OutputParameter;

    protected override char OpenQuote => '"';

    protected override char CloseQuote => '"';

    #region Parameters

    public override DbParameter CreateParameter(string parameterName, object? value)
    {
        SqlIdentifier.ValidateParameterName(parameterName, nameof(parameterName));

        // Properties are set explicitly rather than through a constructor overload: ODP.NET's
        // constructors are heavily overloaded on (string, OracleDbType, object, ...), so an int passed
        // where a size is meant binds as the value instead, silently and without a compiler complaint.
        // ODP.NET accepts the bare name and matches it against the ':'-prefixed placeholder.
        var parameter = new OracleParameter { ParameterName = parameterName };
        parameter.Value = value ?? DBNull.Value;
        return parameter;
    }

    public override DbParameter? CreateGeneratedKeyParameter(string parameterName, AttributeDataType keyType)
    {
        SqlIdentifier.ValidateParameterName(parameterName, nameof(parameterName));

        var (oracleDbType, size) = keyType switch
        {
            AttributeDataType.Number or AttributeDataType.LongNumber or AttributeDataType.Decimal => (OracleDbType.Decimal, 0),
            AttributeDataType.Text => (OracleDbType.Varchar2, TextKeyParameterSize),
            AttributeDataType.Guid => (OracleDbType.Raw, GuidKeyParameterSize),
            _ => throw new NotSupportedException($"An Oracle generated key cannot be returned as a {keyType} value.")
        };

        var parameter = new OracleParameter
        {
            ParameterName = parameterName,
            OracleDbType = oracleDbType,
            Direction = ParameterDirection.Output
        };

        // An output parameter with no size returns nothing at all for the variable-length types, so a
        // returned string or RAW key must always be sized.
        if (size > 0)
            parameter.Size = size;

        return parameter;
    }

    #endregion

    #region Connections

    public override string BuildConnectionString(SqlConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateHost(settings.Host);

        var builder = new OracleConnectionStringBuilder
        {
            DataSource = BuildConnectDescriptor(settings)
        };

        if (!string.IsNullOrWhiteSpace(settings.Username))
            builder.UserID = settings.Username;

        if (!string.IsNullOrEmpty(settings.Password))
            builder.Password = settings.Password;

        if (settings.ConnectionTimeoutSeconds.HasValue)
            builder.ConnectionTimeout = settings.ConnectionTimeoutSeconds.Value;

        return builder.ConnectionString;
    }

    /// <summary>
    /// Turns on Oracle Native Network Encryption for a Connected System configured to use it. It is not a
    /// connection string keyword, so it is applied here, on the hook the Connector calls once the
    /// connection is built and before it is opened.
    /// <para>
    /// These are the driver's per-connection Oracle Advanced Networking properties, deliberately in
    /// preference to their equivalents on <c>OracleConfiguration</c>, which are static: setting those
    /// would let one Connected System's choice decide how every other one connects. Nothing is written
    /// for the other encryption modes, so TCPS keeps the transport it already has and a system configured
    /// for no encryption is left alone.
    /// </para>
    /// </summary>
    public override void ConfigureConnection(DbConnection connection, SqlConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Encryption != SqlConnectionEncryption.OracleNativeNetworkEncryption)
            return;

        var oracleConnection = (OracleConnection)connection;

        // REQUIRED rather than REQUESTED: a system an administrator has configured for encryption should
        // fail to connect rather than quietly fall back to plain text against a server that will not.
        oracleConnection.SqlNetEncryptionClient = "REQUIRED";

        // Naming the AES algorithms explicitly is what keeps DES and RC4 out of the negotiation.
        oracleConnection.SqlNetEncryptionTypesClient = "AES256, AES192, AES128";

        // Encryption without integrity protection leaves the session malleable, and Oracle negotiates the
        // two independently, so an estate configuring one configures both.
        oracleConnection.SqlNetCryptoChecksumClient = "REQUIRED";
        oracleConnection.SqlNetCryptoChecksumTypesClient = "SHA512, SHA384, SHA256";
    }

    public override DbConnection CreateConnection(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new OracleConnection(connectionString);
    }

    public override DbCommand CreateCommand(DbConnection connection, string commandText)
    {
        var command = (OracleCommand)base.CreateCommand(connection, commandText);

        // ODP.NET binds parameters positionally by default, so a statement that names the same bind
        // variable twice (the composite keyset predicate does exactly that) would otherwise take its
        // second value from the wrong parameter.
        command.BindByName = true;
        return command;
    }

    /// <summary>
    /// Builds an Oracle Net connect descriptor. Written out in full rather than as an EZConnect string
    /// because the descriptor is the only form that expresses the SID variant and the TCPS transport
    /// unambiguously. Every value inside it is validated first: Oracle Net parses the descriptor
    /// structurally, so an unbalanced parenthesis in a host or service name would rewrite the address.
    /// </summary>
    private string BuildConnectDescriptor(SqlConnectionSettings settings)
    {
        var protocol = settings.Encryption == SqlConnectionEncryption.Tls ? "TCPS" : "TCP";
        var port = settings.Port ?? GetDefaultPort(settings.Encryption);

        string connectData;
        if (!string.IsNullOrWhiteSpace(settings.ServiceName))
        {
            ValidateDatabaseName(settings.ServiceName, nameof(settings.ServiceName));
            connectData = $"(SERVICE_NAME={settings.ServiceName})";
        }
        else if (!string.IsNullOrWhiteSpace(settings.Sid))
        {
            ValidateDatabaseName(settings.Sid, nameof(settings.Sid));
            connectData = $"(SID={settings.Sid})";
        }
        else
        {
            throw new ArgumentException("An Oracle connection needs either a service name or a SID to identify the database.", nameof(settings));
        }

        return $"(DESCRIPTION=(ADDRESS=(PROTOCOL={protocol})(HOST={settings.Host})(PORT={port}))(CONNECT_DATA={connectData}))";
    }

    #endregion

    #region Import

    public override string BuildKeysetPageCommandText(SqlKeysetPageRequest request)
    {
        ValidateKeysetPageRequest(request);

        var select = $"SELECT {BuildColumnList(request.SelectColumns)}";
        var from = $"FROM {QualifyObjectName(request.SchemaName, request.ObjectName)}";
        var orderBy = BuildAnchorOrderByClause(request.AnchorColumns);

        // The row limiting clause accepts a bind variable, so the page size stays a bound value.
        var fetch = $"FETCH FIRST {GetParameterPlaceholder(request.PageSizeParameterName)} ROWS ONLY";

        return request.IsFirstPage
            ? $"{select} {from} {orderBy} {fetch}"
            : $"{select} {from} WHERE {BuildKeysetPredicate(request.AnchorColumns, request.LastAnchorParameterNames)} {orderBy} {fetch}";
    }

    #endregion

    #region Export

    public override string BuildInsertReturningGeneratedKeyCommandText(SqlInsertCommand command)
    {
        ValidateInsertCommand(command);

        // RETURNING ... INTO writes the generated key into a bound output parameter.
        return $"INSERT INTO {QualifyObjectName(command.SchemaName, command.ObjectName)} " +
               $"({BuildInsertColumnList(command.Columns)}) " +
               $"VALUES ({BuildInsertValueList(command.Columns)}) " +
               $"RETURNING {QuoteIdentifier(command.GeneratedKeyColumn)} INTO {GetParameterPlaceholder(command.GeneratedKeyParameterName)}";
    }

    #endregion

    #region Values

    public override Guid ConvertToGuid(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            // Oracle stores a GUID in RAW(16) big-endian, the RFC 4122 layout. Reading it with the
            // Microsoft layout transposes the first three components without any error being raised.
            byte[] bytes => IdentifierParser.FromRfc4122Bytes(bytes),
            string text => IdentifierParser.FromString(text),
            Guid guid => guid,
            _ => throw new ArgumentException($"An Oracle GUID column returned an unexpected value of type {value.GetType().Name}.", nameof(value))
        };
    }

    public override object ConvertFromGuid(Guid value)
    {
        return IdentifierParser.ToRfc4122Bytes(value);
    }

    #endregion

    #region Schema catalogue

    // ALL_* rather than DBA_* throughout: least-privilege database accounts are the documented
    // deployment guidance, and ALL_* shows exactly what the account can actually read.
    public override string TablesCommandText =>
        $"SELECT OWNER AS {SqlCatalogueColumns.SchemaName}, TABLE_NAME AS {SqlCatalogueColumns.ObjectName} " +
        "FROM ALL_TABLES " +
        "ORDER BY OWNER, TABLE_NAME";

    public override string ViewsCommandText =>
        $"SELECT OWNER AS {SqlCatalogueColumns.SchemaName}, VIEW_NAME AS {SqlCatalogueColumns.ObjectName} " +
        "FROM ALL_VIEWS " +
        "ORDER BY OWNER, VIEW_NAME";

    public override string ColumnsCommandText =>
        $"SELECT COLUMN_NAME AS {SqlCatalogueColumns.ColumnName}, " +
        $"DATA_TYPE AS {SqlCatalogueColumns.DataTypeName}, " +
        $"DATA_LENGTH AS {SqlCatalogueColumns.MaxLength}, " +
        $"DATA_PRECISION AS {SqlCatalogueColumns.NumericPrecision}, " +
        $"DATA_SCALE AS {SqlCatalogueColumns.NumericScale}, " +
        // Normalised to the SQL standard's spelling so the consumer never learns Oracle's 'Y'/'N'.
        $"CASE WHEN NULLABLE = 'Y' THEN 'YES' ELSE 'NO' END AS {SqlCatalogueColumns.IsNullable}, " +
        $"COLUMN_ID AS {SqlCatalogueColumns.OrdinalPosition} " +
        "FROM ALL_TAB_COLUMNS " +
        $"WHERE OWNER = :{SqlCatalogueParameters.SchemaName} AND TABLE_NAME = :{SqlCatalogueParameters.ObjectName} " +
        "ORDER BY COLUMN_ID";

    public override string PrimaryKeyColumnsCommandText =>
        $"SELECT cc.COLUMN_NAME AS {SqlCatalogueColumns.ColumnName}, " +
        $"cc.POSITION AS {SqlCatalogueColumns.OrdinalPosition}, " +
        $"c.CONSTRAINT_NAME AS {SqlCatalogueColumns.ConstraintName} " +
        "FROM ALL_CONSTRAINTS c " +
        "INNER JOIN ALL_CONS_COLUMNS cc ON cc.OWNER = c.OWNER AND cc.CONSTRAINT_NAME = c.CONSTRAINT_NAME " +
        "WHERE c.CONSTRAINT_TYPE = 'P' " +
        $"AND c.OWNER = :{SqlCatalogueParameters.SchemaName} AND c.TABLE_NAME = :{SqlCatalogueParameters.ObjectName} " +
        "ORDER BY cc.POSITION";

    public override string ForeignKeyColumnsCommandText =>
        $"SELECT c.CONSTRAINT_NAME AS {SqlCatalogueColumns.ConstraintName}, " +
        $"cc.COLUMN_NAME AS {SqlCatalogueColumns.ColumnName}, " +
        $"rc.OWNER AS {SqlCatalogueColumns.ReferencedSchema}, " +
        $"rc.TABLE_NAME AS {SqlCatalogueColumns.ReferencedTable}, " +
        $"rcc.COLUMN_NAME AS {SqlCatalogueColumns.ReferencedColumn}, " +
        $"cc.POSITION AS {SqlCatalogueColumns.OrdinalPosition} " +
        "FROM ALL_CONSTRAINTS c " +
        "INNER JOIN ALL_CONS_COLUMNS cc ON cc.OWNER = c.OWNER AND cc.CONSTRAINT_NAME = c.CONSTRAINT_NAME " +
        "INNER JOIN ALL_CONSTRAINTS rc ON rc.OWNER = c.R_OWNER AND rc.CONSTRAINT_NAME = c.R_CONSTRAINT_NAME " +
        "INNER JOIN ALL_CONS_COLUMNS rcc ON rcc.OWNER = rc.OWNER AND rcc.CONSTRAINT_NAME = rc.CONSTRAINT_NAME AND rcc.POSITION = cc.POSITION " +
        "WHERE c.CONSTRAINT_TYPE = 'R' " +
        $"AND c.OWNER = :{SqlCatalogueParameters.SchemaName} AND c.TABLE_NAME = :{SqlCatalogueParameters.ObjectName} " +
        "ORDER BY c.CONSTRAINT_NAME, cc.POSITION";

    #endregion
}
