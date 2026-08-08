// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// Resolves the dialect a Connected System is configured for. The single place a database type becomes
/// a provider, so adding a Priority 2 provider is one arm here plus the class itself.
/// </summary>
internal static class SqlProviderFactory
{
    /// <summary>
    /// Creates the provider for a database type. Providers hold no connection state, so a fresh one per
    /// call costs nothing and keeps them free of shared mutable state between Connected Systems.
    /// </summary>
    /// <exception cref="NotSupportedException">No provider ships for this database type.</exception>
    internal static ISqlProvider Create(SqlDatabaseType databaseType)
    {
        return databaseType switch
        {
            SqlDatabaseType.SqlServer => new SqlServerProvider(),
            SqlDatabaseType.Oracle => new OracleProvider(),
            _ => throw new NotSupportedException($"The JIM SQL Connector has no provider for database type '{databaseType}'.")
        };
    }
}
