// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
using JIM.Connectors.Sql.Providers;
using JIM.Models.Staging;
using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;

namespace JIM.Connectors.Sql;

/// <summary>
/// Tells an export the database refused apart from one that failed for any other reason (#2118).
/// <para>
/// A database that answers a write with a permission, constraint, data or schema error has considered the change and
/// said no: the account JIM connects as lacks the rights, a foreign key or check constraint does not hold, a required
/// column is empty, a value does not fit its column, a table or column is not there, or a trigger raised an error.
/// None of that is a fault in JIM, and reported unclassified it lands as an Unhandled Error, which reads as one and
/// fails the Activity. Classified, a permission failure becomes <see cref="ConnectedSystemExportErrorType.AccessDenied"/>,
/// naming the rights to grant, and every other refusal becomes
/// <see cref="ConnectedSystemExportErrorType.RejectedByConnectedSystem"/>, carrying the database's own words.
/// </para>
/// <para>
/// Classification is a table of (server family, error code), read in order, in the same shape as
/// <see cref="SqlUniquenessRejectionClassifier"/>, which is consulted first. Error numbers are provider-specific, so
/// each SQL Server and Oracle row applies to one database type; PostgreSQL's rows key on the SQLSTATE, so they apply
/// to whichever provider surfaces it. Anything the table does not name stays unclassified, which is deliberate for a
/// database that never considered the change: a lost connection, a timeout, a deadlock, a cancelled statement or a
/// server out of resources.
/// </para>
/// </summary>
internal static class SqlExportRefusalClassifier
{
    private const ConnectedSystemExportErrorType AccessDenied = ConnectedSystemExportErrorType.AccessDenied;
    private const ConnectedSystemExportErrorType Rejected = ConnectedSystemExportErrorType.RejectedByConnectedSystem;

    /// <summary>
    /// The classification table, in the order it is read.
    /// </summary>
    internal static IReadOnlyList<SqlExportRefusalRule> Rules { get; } =
    [
        // PostgreSQL, by SQLSTATE. 42501 comes before the rest of class 42 is considered; class 42 is listed by
        // condition, because a syntax error (42601) is JIM's own statement at fault, not the database refusing data.
        PostgreSql("42501", AccessDenied, "insufficient_privilege"),
        PostgreSql("23", Rejected, "Class 23, integrity constraint violation: not null, foreign key, check, exclusion"),
        PostgreSql("22", Rejected, "Class 22, data exception: a value too long, out of range or not of the column's type"),
        PostgreSql("42703", Rejected, "undefined_column"),
        PostgreSql("42P01", Rejected, "undefined_table"),
        PostgreSql("42804", Rejected, "datatype_mismatch"),
        PostgreSql("P0001", Rejected, "raise_exception, an error raised by a trigger or function"),

        // SQL Server, by error number.
        SqlServer(229, AccessDenied, "The permission was denied on the object"),
        SqlServer(230, AccessDenied, "The permission was denied on the column"),
        SqlServer(262, AccessDenied, "The permission was denied in the database"),
        SqlServer(297, AccessDenied, "The user does not have permission to perform this action"),
        SqlServer(515, Rejected, "Cannot insert the value NULL into a column that does not allow nulls"),
        SqlServer(547, Rejected, "The statement conflicted with a FOREIGN KEY or CHECK constraint"),
        SqlServer(2628, Rejected, "String or binary data would be truncated, naming the column"),
        SqlServer(8152, Rejected, "String or binary data would be truncated"),
        SqlServer(245, Rejected, "Conversion failed when converting a value to the column's type"),
        SqlServer(241, Rejected, "Conversion failed when converting date and/or time from a character string"),
        SqlServer(8114, Rejected, "Error converting data type"),
        SqlServer(8115, Rejected, "Arithmetic overflow error converting to the column's type"),
        SqlServer(220, Rejected, "Arithmetic overflow error for the column's type"),
        SqlServer(207, Rejected, "Invalid column name"),
        SqlServer(208, Rejected, "Invalid object name"),
        SqlServer(3609, Rejected, "The transaction ended in a trigger"),
        new("SQL Server", SqlDatabaseType.SqlServer, 50000, int.MaxValue, null, Rejected,
            "User-defined errors (50000 and above), raised by a trigger with RAISERROR or THROW"),

        // Oracle, by ORA- number.
        Oracle(1031, AccessDenied, "ORA-01031 insufficient privileges"),
        Oracle(1400, Rejected, "ORA-01400 cannot insert NULL"),
        Oracle(1407, Rejected, "ORA-01407 cannot update to NULL"),
        Oracle(2290, Rejected, "ORA-02290 check constraint violated"),
        Oracle(2291, Rejected, "ORA-02291 integrity constraint violated, parent key not found"),
        Oracle(2292, Rejected, "ORA-02292 integrity constraint violated, child record found"),
        Oracle(12899, Rejected, "ORA-12899 value too large for column"),
        Oracle(1401, Rejected, "ORA-01401 inserted value too large for column"),
        Oracle(1438, Rejected, "ORA-01438 value larger than specified precision allowed for this column"),
        Oracle(1722, Rejected, "ORA-01722 invalid number"),
        Oracle(1840, Rejected, "ORA-01840 input value not long enough for date format"),
        Oracle(1841, Rejected, "ORA-01841 year must be between -4713 and +9999"),
        Oracle(1861, Rejected, "ORA-01861 literal does not match format string"),
        Oracle(904, Rejected, "ORA-00904 invalid identifier"),
        Oracle(942, Rejected, "ORA-00942 table or view does not exist (also what a table the account cannot see reports)"),
        new("Oracle", SqlDatabaseType.Oracle, 20000, 20999, null, Rejected,
            "ORA-20000 to ORA-20999, raised by a trigger with RAISE_APPLICATION_ERROR")
    ];

    /// <summary>
    /// Classifies a database error.
    /// </summary>
    /// <param name="databaseType">The database the error came from, which says what its error number means.</param>
    /// <param name="errorNumber">The provider's error number, where it reports one.</param>
    /// <param name="sqlState">The SQLSTATE, where the provider reports one.</param>
    /// <param name="errorType">What the refusal is reported as, when it is one.</param>
    /// <returns>True when the database refused the change.</returns>
    internal static bool TryClassify(SqlDatabaseType databaseType, int? errorNumber, string? sqlState, out ConnectedSystemExportErrorType errorType)
    {
        var rule = Rules.FirstOrDefault(r => Applies(r, databaseType, errorNumber, sqlState));
        errorType = rule?.ErrorType ?? ConnectedSystemExportErrorType.General;
        return rule != null;
    }

    /// <summary>
    /// The result for an export the database refused, or null when the failure is not a refusal. The error number is
    /// read from the provider's own exception type and the SQLSTATE from any other database exception.
    /// </summary>
    internal static ConnectedSystemExportResult? FailureFor(Exception exception)
    {
        if (exception is not DbException database)
            return null;

        var (databaseType, errorNumber, sqlState, code) = database switch
        {
            SqlException sqlServer => (SqlDatabaseType.SqlServer, (int?)sqlServer.Number, (string?)null, $"SQL Server error {sqlServer.Number}"),
            OracleException oracle => (SqlDatabaseType.Oracle, oracle.Number, null, $"ORA-{oracle.Number:00000}"),
            _ => (SqlDatabaseType.NotSet, null, database.SqlState, $"SQLSTATE {database.SqlState}")
        };

        if (!TryClassify(databaseType, errorNumber, sqlState, out var errorType))
            return null;

        var message = exception.Message.Trim();
        return errorType == ConnectedSystemExportErrorType.AccessDenied
            ? ConnectedSystemExportResult.Failed(
                $"The database refused the change because the account JIM connects as does not have permission to make it ({code}). " +
                "Grant the Connected System's account the rights to insert, update or delete rows in the tables this object type " +
                $"writes, or narrow the Attribute Flows to what it may write. The database said: {message}",
                errorType)
            : ConnectedSystemExportResult.Failed($"The database refused the change ({code}): {message}", errorType);
    }

    private static bool Applies(SqlExportRefusalRule rule, SqlDatabaseType databaseType, int? errorNumber, string? sqlState)
    {
        if (rule.SqlState != null)
            return sqlState is { Length: 5 } && (rule.SqlState.Length == 2
                ? sqlState.StartsWith(rule.SqlState, StringComparison.Ordinal)
                : string.Equals(rule.SqlState, sqlState, StringComparison.Ordinal));

        return rule.DatabaseType == databaseType && errorNumber >= rule.FirstErrorNumber && errorNumber <= rule.LastErrorNumber;
    }

    private static SqlExportRefusalRule PostgreSql(string sqlState, ConnectedSystemExportErrorType errorType, string description) =>
        new("PostgreSQL", DatabaseType: null, FirstErrorNumber: null, LastErrorNumber: null, sqlState, errorType, $"SQLSTATE {sqlState}: {description}");

    private static SqlExportRefusalRule SqlServer(int errorNumber, ConnectedSystemExportErrorType errorType, string description) =>
        new("SQL Server", SqlDatabaseType.SqlServer, errorNumber, errorNumber, SqlState: null, errorType, $"{errorNumber}: {description}");

    private static SqlExportRefusalRule Oracle(int errorNumber, ConnectedSystemExportErrorType errorType, string description) =>
        new("Oracle", SqlDatabaseType.Oracle, errorNumber, errorNumber, SqlState: null, errorType, description);
}
