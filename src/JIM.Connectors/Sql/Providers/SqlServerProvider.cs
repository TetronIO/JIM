// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Utilities;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Data.Common;

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// The Microsoft SQL Server dialect. Uses the managed <c>Microsoft.Data.SqlClient</c> driver, so
/// nothing native is installed and the Connector stays air-gap deployable.
/// </summary>
internal class SqlServerProvider : SqlProviderBase
{
    /// <summary>
    /// The port a default instance listens on. Encryption changes nothing here: SQL Server negotiates
    /// TLS over the same port rather than offering a second one.
    /// </summary>
    private const int DefaultPort = 1433;

    public override SqlDatabaseType DatabaseType => SqlDatabaseType.SqlServer;

    public override string DisplayName => "Microsoft SQL Server";

    public override string ParameterPrefix => "@";

    public override string ConnectivityTestCommandText => "SELECT 1";

    public override int GetDefaultPort(SqlConnectionEncryption encryption) => DefaultPort;

    /// <summary>
    /// <c>Microsoft.Data.SqlClient</c> takes a path to a certificate file and accepts the server's
    /// certificate when it is an exact match for it, which is the only mechanism it offers for trusting
    /// a certificate the operating system's bundle does not already vouch for. There is no validation
    /// callback to install, and <c>TrustServerCertificate</c> is never an acceptable substitute: it
    /// accepts whatever is presented, now and in future.
    /// </summary>
    public override bool SupportsPinnedServerCertificate => true;

    public override SqlGeneratedKeyRetrieval GeneratedKeyRetrieval => SqlGeneratedKeyRetrieval.ResultSet;

    protected override char OpenQuote => '[';

    protected override char CloseQuote => ']';

    /// <summary>
    /// A backslash names an instance on a host ("SERVER\INSTANCE"), so it is legitimate here where it
    /// would not be for Oracle.
    /// </summary>
    protected override string AdditionalAllowedHostCharacters => "\\";

    #region Parameters

    public override DbParameter CreateParameter(string parameterName, object? value)
    {
        SqlIdentifier.ValidateParameterName(parameterName, nameof(parameterName));

        // SqlClient accepts the bare name and adds the '@' itself, so the name is stored unprefixed.
        return new SqlParameter(parameterName, value ?? DBNull.Value);
    }

    public override DbParameter? CreateGeneratedKeyParameter(string parameterName, AttributeDataType keyType)
    {
        // The OUTPUT clause returns the generated key as a result set, so there is nothing to bind.
        return null;
    }

    #endregion

    #region Connections

    public override string BuildConnectionString(SqlConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateHost(settings.Host);

        var builder = new SqlConnectionStringBuilder
        {
            // SQL Server expresses a non-default port as a comma-separated suffix on the data source.
            DataSource = settings.Port.HasValue ? $"{settings.Host},{settings.Port.Value}" : settings.Host,

            // Mandatory means the connection fails rather than silently falling back to plain text, and
            // is what an administrator gets unless they turn encryption off: SqlClient itself has
            // defaulted to it since version 4.0, so a modern estate is already encrypting. Optional is
            // the deliberate opt-out, and still takes encryption where the server offers it.
            Encrypt = settings.Encryption == SqlConnectionEncryption.Tls ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,

            // Never true. A refused server certificate is surfaced to the administrator with its
            // details; trusting whatever the server presents would defeat the certificate store.
            TrustServerCertificate = false,
            ApplicationName = ApplicationName,

            // Off deliberately, and off for both dialects. JIM opens one connection per operation and
            // holds it for that operation's lifetime, rather than one per object, so a pool saves
            // handshakes JIM was never going to make. What it costs is real: the pool is process-wide
            // and outlives the Connector that filled it, leaving sessions open on a customer's database
            // long after a run, and a pooled connection can re-handshake against the trust anchor file
            // this Connector deletes when it is disposed.
            Pooling = false
        };

        if (!string.IsNullOrWhiteSpace(settings.DatabaseName))
            builder.InitialCatalog = settings.DatabaseName;

        if (!string.IsNullOrWhiteSpace(settings.Username))
            builder.UserID = settings.Username;

        if (!string.IsNullOrEmpty(settings.Password))
            builder.Password = settings.Password;

        if (settings.ConnectionTimeoutSeconds.HasValue)
            builder.ConnectTimeout = settings.ConnectionTimeoutSeconds.Value;

        // The one certificate this connection may accept on top of the operating system's own anchors,
        // supplied only after an ordinary attempt was refused and only for a certificate the JIM
        // certificate store vouches for. SqlClient matches it exactly, so a server that later presents
        // a different certificate is refused again rather than silently trusted.
        if (!string.IsNullOrEmpty(settings.PinnedServerCertificatePath))
            builder.ServerCertificate = settings.PinnedServerCertificatePath;

        return builder.ConnectionString;
    }

    public override DbConnection CreateConnection(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new SqlConnection(connectionString);
    }

    #endregion

    #region Import

    public override string BuildKeysetPageCommandText(SqlKeysetPageRequest request)
    {
        ValidateKeysetPageRequest(request);

        // TOP takes a bound parameter, so the page size is a value like any other.
        var select = $"SELECT TOP ({GetParameterPlaceholder(request.PageSizeParameterName)}) {BuildColumnList(request.SelectColumns)}";
        var from = $"FROM {QualifyObjectName(request.SchemaName, request.ObjectName)}";
        var orderBy = BuildAnchorOrderByClause(request.AnchorColumns);

        return request.IsFirstPage
            ? $"{select} {from} {orderBy}"
            : $"{select} {from} WHERE {BuildKeysetPredicate(request.AnchorColumns, request.LastAnchorParameterNames)} {orderBy}";
    }

    #endregion

    #region Export

    public override string BuildInsertReturningGeneratedKeyCommandText(SqlInsertCommand command)
    {
        ValidateInsertCommand(command);

        // The OUTPUT clause sits between the column list and VALUES, and emits the inserted row's
        // generated key as a single-row result set.
        return $"INSERT INTO {QualifyObjectName(command.SchemaName, command.ObjectName)} " +
               $"({BuildInsertColumnList(command.Columns)}) " +
               $"OUTPUT INSERTED.{QuoteIdentifier(command.GeneratedKeyColumn)} " +
               $"VALUES ({BuildInsertValueList(command.Columns)})";
    }

    #endregion

    #region Values

    public override Guid ConvertToGuid(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            Guid guid => guid,

            // SQL Server's uniqueidentifier is little-endian in its first three components, which is
            // the Microsoft GUID layout; reading it as RFC 4122 would transpose them.
            byte[] bytes => IdentifierParser.FromMicrosoftBytes(bytes),
            string text => IdentifierParser.FromString(text),
            _ => throw new ArgumentException($"A SQL Server GUID column returned an unexpected value of type {value.GetType().Name}.", nameof(value))
        };
    }

    public override object ConvertFromGuid(Guid value)
    {
        // SqlClient binds a Guid straight to a uniqueidentifier parameter.
        return value;
    }

    #endregion

    #region Schema catalogue

    public override string TablesCommandText =>
        $"SELECT TABLE_SCHEMA AS {SqlCatalogueColumns.SchemaName}, TABLE_NAME AS {SqlCatalogueColumns.ObjectName} " +
        "FROM INFORMATION_SCHEMA.TABLES " +
        "WHERE TABLE_TYPE = 'BASE TABLE' " +
        "ORDER BY TABLE_SCHEMA, TABLE_NAME";

    public override string ViewsCommandText =>
        $"SELECT TABLE_SCHEMA AS {SqlCatalogueColumns.SchemaName}, TABLE_NAME AS {SqlCatalogueColumns.ObjectName} " +
        "FROM INFORMATION_SCHEMA.VIEWS " +
        "ORDER BY TABLE_SCHEMA, TABLE_NAME";

    public override string ColumnsCommandText =>
        $"SELECT COLUMN_NAME AS {SqlCatalogueColumns.ColumnName}, " +
        $"DATA_TYPE AS {SqlCatalogueColumns.DataTypeName}, " +
        $"CHARACTER_MAXIMUM_LENGTH AS {SqlCatalogueColumns.MaxLength}, " +
        $"NUMERIC_PRECISION AS {SqlCatalogueColumns.NumericPrecision}, " +
        $"NUMERIC_SCALE AS {SqlCatalogueColumns.NumericScale}, " +
        $"IS_NULLABLE AS {SqlCatalogueColumns.IsNullable}, " +
        $"ORDINAL_POSITION AS {SqlCatalogueColumns.OrdinalPosition} " +
        "FROM INFORMATION_SCHEMA.COLUMNS " +
        $"WHERE TABLE_SCHEMA = @{SqlCatalogueParameters.SchemaName} AND TABLE_NAME = @{SqlCatalogueParameters.ObjectName} " +
        "ORDER BY ORDINAL_POSITION";

    public override string PrimaryKeyColumnsCommandText =>
        $"SELECT kcu.COLUMN_NAME AS {SqlCatalogueColumns.ColumnName}, " +
        $"kcu.ORDINAL_POSITION AS {SqlCatalogueColumns.OrdinalPosition}, " +
        $"tc.CONSTRAINT_NAME AS {SqlCatalogueColumns.ConstraintName} " +
        "FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc " +
        "INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu " +
        "ON tc.CONSTRAINT_SCHEMA = kcu.CONSTRAINT_SCHEMA AND tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME " +
        "WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY' " +
        $"AND tc.TABLE_SCHEMA = @{SqlCatalogueParameters.SchemaName} AND tc.TABLE_NAME = @{SqlCatalogueParameters.ObjectName} " +
        "ORDER BY kcu.ORDINAL_POSITION";

    // sys.* rather than INFORMATION_SCHEMA here: the standard views cannot express both sides of a
    // foreign key without a three-way join through REFERENTIAL_CONSTRAINTS, and lose the column
    // pairing on a composite key.
    public override string ForeignKeyColumnsCommandText =>
        $"SELECT fk.name AS {SqlCatalogueColumns.ConstraintName}, " +
        $"pc.name AS {SqlCatalogueColumns.ColumnName}, " +
        $"rs.name AS {SqlCatalogueColumns.ReferencedSchema}, " +
        $"rt.name AS {SqlCatalogueColumns.ReferencedTable}, " +
        $"rc.name AS {SqlCatalogueColumns.ReferencedColumn}, " +
        $"fkc.constraint_column_id AS {SqlCatalogueColumns.OrdinalPosition} " +
        "FROM sys.foreign_keys fk " +
        "INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id " +
        "INNER JOIN sys.tables pt ON pt.object_id = fk.parent_object_id " +
        "INNER JOIN sys.schemas ps ON ps.schema_id = pt.schema_id " +
        "INNER JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id " +
        "INNER JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id " +
        "INNER JOIN sys.schemas rs ON rs.schema_id = rt.schema_id " +
        "INNER JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id " +
        $"WHERE ps.name = @{SqlCatalogueParameters.SchemaName} AND pt.name = @{SqlCatalogueParameters.ObjectName} " +
        "ORDER BY fk.name, fkc.constraint_column_id";

    #endregion
}
