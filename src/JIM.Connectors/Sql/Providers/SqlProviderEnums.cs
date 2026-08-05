// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// The database servers the JIM SQL Connector can address. Priority 1 members ship first; Priority 2
/// members (PostgreSQL, MySQL/MariaDB) are additive behind <see cref="ISqlProvider"/>.
/// </summary>
internal enum SqlDatabaseType
{
    NotSet = 0,
    SqlServer = 1,
    Oracle = 2
}

/// <summary>
/// How a database hands back the key it generated for an inserted row.
/// </summary>
internal enum SqlGeneratedKeyRetrieval
{
    /// <summary>
    /// The insert statement returns the key as a single-row result set (SQL Server's OUTPUT clause).
    /// </summary>
    ResultSet = 0,

    /// <summary>
    /// The insert statement writes the key into a bound output parameter (Oracle's RETURNING ... INTO).
    /// </summary>
    OutputParameter = 1
}
