// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using System.Data.Common;

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// The dialect seam for the JIM SQL Connector. Everything a database server does differently from
/// another one lives behind this interface, so the Connector itself never branches on which server it
/// is talking to and a new provider is additive rather than invasive.
/// <para>
/// It plays the role <c>ILdapOperationExecutor</c> plays for the LDAP Connector (the place unit tests
/// substitute), but it carries dialect knowledge rather than wrapping a sealed type: everything it
/// hands back is a <see cref="System.Data.Common"/> abstraction, so tests can mock the connection,
/// command and parameter directly and no provider-specific type crosses the seam.
/// </para>
/// <para>
/// <b>Security contract.</b> Values are always bound as parameters, never interpolated. Identifiers
/// cannot be parameterised, so the provider quotes and validates them (see <see cref="SqlIdentifier"/>).
/// Connector configuration is privileged administrator input, but the injection surface it defends is
/// still exactly these two: value parameterisation and identifier quoting.
/// </para>
/// </summary>
internal interface ISqlProvider
{
    /// <summary>
    /// The database server this provider addresses.
    /// </summary>
    SqlDatabaseType DatabaseType { get; }

    /// <summary>
    /// The server's name as an administrator would recognise it.
    /// </summary>
    string DisplayName { get; }

    #region Parameters

    /// <summary>
    /// The character the dialect prefixes bind variables with ("@" or ":").
    /// </summary>
    string ParameterPrefix { get; }

    /// <summary>
    /// Renders a parameter's placeholder for embedding in command text. Throws when the name is not
    /// identifier-shaped, because the result is interpolated into SQL.
    /// </summary>
    string GetParameterPlaceholder(string parameterName);

    /// <summary>
    /// Creates a bound parameter carrying a value. A null value becomes <see cref="DBNull"/>, which is
    /// how ADO.NET expresses SQL NULL.
    /// </summary>
    DbParameter CreateParameter(string parameterName, object? value);

    #endregion

    #region Connections

    /// <summary>
    /// Builds the provider's connection string from the discrete settings an administrator supplied.
    /// Never logged, and never assembled by string concatenation: each provider uses its own
    /// connection-string builder so values are escaped by the driver rather than by JIM.
    /// </summary>
    string BuildConnectionString(SqlConnectionSettings settings);

    /// <summary>
    /// Creates a closed connection. The caller owns opening, closing and disposing it.
    /// </summary>
    DbConnection CreateConnection(string connectionString);

    /// <summary>
    /// Creates a command on a connection, with any dialect-specific command configuration applied.
    /// </summary>
    DbCommand CreateCommand(DbConnection connection, string commandText);

    /// <summary>
    /// The cheapest statement that proves a connection works, used by save-time settings validation.
    /// </summary>
    string ConnectivityTestCommandText { get; }

    /// <summary>
    /// The listener port this dialect uses when an administrator leaves the port unset. Encrypted and
    /// unencrypted transports do not always share one, so the answer depends on which is in use.
    /// </summary>
    int GetDefaultPort(bool useTls);

    /// <summary>
    /// What this dialect calls its encrypted transport, for the wording an administrator is shown when
    /// a server's certificate is refused ("TLS", "TCPS").
    /// </summary>
    string SecureTransportName { get; }

    /// <summary>
    /// Whether this dialect's driver can be told to accept one specific server certificate, supplied as
    /// a file through <see cref="SqlConnectionSettings.PinnedServerCertificatePath"/>.
    /// <para>
    /// That mechanism is how a certificate an administrator added in Admin &gt; Certificates becomes an
    /// additional trust anchor: the operating system's own bundle is always tried first, and only a
    /// certificate JIM's certificate store vouches for is ever pinned. A driver that offers no such
    /// mechanism returns false, and its connections validate against the operating system bundle alone.
    /// </para>
    /// </summary>
    bool SupportsPinnedServerCertificate { get; }

    #endregion

    #region Identifiers

    /// <summary>
    /// Quotes a single identifier, doubling any embedded closing quote character so a hostile name
    /// cannot escape into the surrounding statement.
    /// </summary>
    string QuoteIdentifier(string identifier);

    /// <summary>
    /// Quotes and joins a schema-qualified object name. A null or empty schema yields the object name
    /// alone, letting the connection's default schema apply.
    /// </summary>
    string QualifyObjectName(string? schemaName, string objectName);

    #endregion

    #region Import

    /// <summary>
    /// Generates one page of a keyset-paginated read of a table or view.
    /// </summary>
    string BuildKeysetPageCommandText(SqlKeysetPageRequest request);

    #endregion

    #region Export

    /// <summary>
    /// How this dialect hands a generated key back to the client.
    /// </summary>
    SqlGeneratedKeyRetrieval GeneratedKeyRetrieval { get; }

    /// <summary>
    /// Generates an INSERT that returns the database-generated key for the new row.
    /// </summary>
    string BuildInsertReturningGeneratedKeyCommandText(SqlInsertCommand command);

    /// <summary>
    /// Creates the output parameter a generated key is returned through, or null where
    /// <see cref="GeneratedKeyRetrieval"/> is <see cref="SqlGeneratedKeyRetrieval.ResultSet"/> and
    /// there is no parameter to bind.
    /// </summary>
    DbParameter? CreateGeneratedKeyParameter(string parameterName, AttributeDataType keyType);

    #endregion

    #region Values

    /// <summary>
    /// Materialises a GUID from whatever the reader returned for a GUID-typed column. The byte order
    /// is dialect-specific and getting it wrong transposes the first three components silently, so it
    /// belongs behind the seam rather than at the call site.
    /// </summary>
    Guid ConvertToGuid(object value);

    /// <summary>
    /// Renders a GUID in the form this dialect's parameter binding expects, inverting
    /// <see cref="ConvertToGuid"/>.
    /// </summary>
    object ConvertFromGuid(Guid value);

    #endregion

    #region Schema catalogue

    /// <summary>
    /// Lists base tables as (<see cref="SqlCatalogueColumns.SchemaName"/>,
    /// <see cref="SqlCatalogueColumns.ObjectName"/>).
    /// </summary>
    string TablesCommandText { get; }

    /// <summary>
    /// Lists views in the same shape as <see cref="TablesCommandText"/>.
    /// </summary>
    string ViewsCommandText { get; }

    /// <summary>
    /// Lists a table or view's columns, filtered by the
    /// <see cref="SqlCatalogueParameters.SchemaName"/> and <see cref="SqlCatalogueParameters.ObjectName"/>
    /// parameters, in ordinal order.
    /// </summary>
    string ColumnsCommandText { get; }

    /// <summary>
    /// Lists a table's primary key columns in key order, which is what makes a composite anchor
    /// reproducible between runs.
    /// </summary>
    string PrimaryKeyColumnsCommandText { get; }

    /// <summary>
    /// Lists a table's foreign key columns with both sides of each constraint, which schema discovery
    /// turns into Reference suggestions for the administrator to confirm.
    /// </summary>
    string ForeignKeyColumnsCommandText { get; }

    #endregion

    #region Type mapping

    /// <summary>
    /// Maps a column's SQL type onto a JIM attribute type for this dialect. Throws
    /// <see cref="SqlTypeMappingException"/> rather than degrading an unrecognised type to Text.
    /// </summary>
    AttributeDataType MapColumnType(SqlColumnType columnType, SqlTypeMappingOptions options);

    #endregion
}
