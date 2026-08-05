// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// Thrown when a column's SQL type has no JIM attribute type. Failing here is deliberate: quietly
/// mapping an unrecognised type to Text would import values JIM cannot compare numerically or by date,
/// and the defect would only surface much later as a Synchronisation Rule that never matches.
/// </summary>
internal class SqlTypeMappingException : Exception
{
    /// <summary>
    /// The catalogue's type name, verbatim, so the administrator can find the column and either
    /// exclude it or expose it through a view with a supported type.
    /// </summary>
    internal string SqlTypeName { get; }

    /// <summary>
    /// The database server whose dialect was being mapped.
    /// </summary>
    internal SqlDatabaseType DatabaseType { get; }

    internal SqlTypeMappingException(string sqlTypeName, SqlDatabaseType databaseType)
        : base($"The {databaseType} column type '{sqlTypeName}' has no equivalent JIM attribute type. Exclude the column from the Connected System Object Type's configuration, or expose it through a view that casts it to a supported type.")
    {
        SqlTypeName = sqlTypeName;
        DatabaseType = databaseType;
    }
}
