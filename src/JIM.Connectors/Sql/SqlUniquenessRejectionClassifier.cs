// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
using System.Text.RegularExpressions;
using JIM.Connectors.Sql.Providers;
using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;

namespace JIM.Connectors.Sql;

/// <summary>
/// Tells a database's duplicate key refusal apart from every other export failure, and names the column where the
/// database named it (Unique Value Generation, #242, release 4, decision 9).
/// <para>
/// Classification is a table of (server family, error code, message pattern), read in order. Error numbers are
/// provider-specific (Oracle's 1 is not SQL Server's 1), so each row applies to one database type; PostgreSQL's row
/// keys on the SQLSTATE every driver for it reports, so it applies to whichever provider surfaces it. The column is
/// taken only from the database's own text and only when it names exactly one: SQL Server names the constraint or
/// index but never the column, so its refusals are classified without one, and a composite key names no single
/// column to revise.
/// </para>
/// </summary>
internal static class SqlUniquenessRejectionClassifier
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    // PostgreSQL's detail, present when the connection includes error detail: "Key (email)=(x@y.z) already exists."
    private static readonly Regex PostgreSqlKeyColumns = new(@"Key \((?<columns>[^)]*)\)=\(", RegexOptions.CultureInvariant, MatchTimeout);

    // Oracle Database 23ai's addition to ORA-00001: "... violated on table JIM.USERS columns (EMAIL)".
    private static readonly Regex OracleColumns = new(@"\bcolumns \((?<columns>[^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>
    /// The classification table, in the order it is read.
    /// </summary>
    internal static IReadOnlyList<SqlUniquenessRejectionRule> Rules { get; } =
    [
        new("SQL Server", SqlDatabaseType.SqlServer, ErrorNumber: 2627, SqlState: null, _ => null,
            "Violation of a PRIMARY KEY or UNIQUE KEY constraint; names the constraint, not the column"),
        new("SQL Server", SqlDatabaseType.SqlServer, ErrorNumber: 2601, SqlState: null, _ => null,
            "Duplicate key row in a unique index; names the index, not the column"),
        new("Oracle", SqlDatabaseType.Oracle, ErrorNumber: 1, SqlState: null, message => SingleColumn(OracleColumns, message),
            "ORA-00001 unique constraint violated; the column where Oracle 23ai names exactly one"),
        new("PostgreSQL", DatabaseType: null, ErrorNumber: null, SqlState: "23505", message => SingleColumn(PostgreSqlKeyColumns, message),
            "SQLSTATE 23505 unique_violation; the column where the detail names exactly one (it is redacted unless the connection includes error detail)")
    ];

    /// <summary>
    /// Classifies a database refusal.
    /// </summary>
    /// <param name="databaseType">The database the refusal came from, which says what its error number means.</param>
    /// <param name="errorNumber">The provider's error number, where it reports one.</param>
    /// <param name="sqlState">The SQLSTATE, where the provider reports one.</param>
    /// <param name="message">The database's message.</param>
    /// <param name="rejectedAttributeName">The column the database named, or null.</param>
    /// <returns>True when the refusal means a value is already in use.</returns>
    internal static bool TryClassify(SqlDatabaseType databaseType, int? errorNumber, string? sqlState, string? message, out string? rejectedAttributeName)
    {
        foreach (var rule in Rules.Where(r => Applies(r, databaseType, errorNumber, sqlState)))
        {
            rejectedAttributeName = rule.Column(message ?? string.Empty);
            return true;
        }

        rejectedAttributeName = null;
        return false;
    }

    /// <summary>
    /// Classifies the exception an export failed with, reading the error number from the provider's own exception
    /// type and the SQLSTATE from any other database exception.
    /// </summary>
    internal static bool TryClassify(Exception exception, out string? rejectedAttributeName)
    {
        switch (exception)
        {
            case SqlException sqlServer:
                return TryClassify(SqlDatabaseType.SqlServer, sqlServer.Number, null, sqlServer.Message, out rejectedAttributeName);
            case OracleException oracle:
                return TryClassify(SqlDatabaseType.Oracle, oracle.Number, null, oracle.Message, out rejectedAttributeName);
            case DbException database:
                return TryClassify(SqlDatabaseType.NotSet, null, database.SqlState, database.Message, out rejectedAttributeName);
            default:
                rejectedAttributeName = null;
                return false;
        }
    }

    private static bool Applies(SqlUniquenessRejectionRule rule, SqlDatabaseType databaseType, int? errorNumber, string? sqlState)
    {
        if (rule.SqlState != null)
            return string.Equals(rule.SqlState, sqlState, StringComparison.Ordinal);

        return rule.DatabaseType == databaseType && rule.ErrorNumber == errorNumber;
    }

    private static string? SingleColumn(Regex pattern, string message)
    {
        var match = pattern.Match(message);
        if (!match.Success)
            return null;

        var columns = match.Groups["columns"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return columns.Length == 1 ? columns[0].Trim('"') : null;
    }
}
