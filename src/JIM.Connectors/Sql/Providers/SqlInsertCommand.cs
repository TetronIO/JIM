// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// Everything a provider needs to generate an INSERT that hands back the key the database generated
/// for the new row, which the Connector returns as the object's external ID.
/// </summary>
internal sealed record SqlInsertCommand
{
    internal string? SchemaName { get; init; }

    internal required string ObjectName { get; init; }

    /// <summary>
    /// The columns being written, each paired with the parameter carrying its value. Values are always
    /// bound; only the column names reach the statement text, quoted by the provider.
    /// </summary>
    internal required IReadOnlyList<SqlColumnParameter> Columns { get; init; }

    /// <summary>
    /// The identity, sequence or default-backed column whose generated value is wanted back.
    /// </summary>
    internal required string GeneratedKeyColumn { get; init; }

    /// <summary>
    /// The parameter the generated key is returned through. Named in the statement for dialects that
    /// bind an output parameter; retained for the others so the two paths read the same at the caller.
    /// </summary>
    internal required string GeneratedKeyParameterName { get; init; }
}
