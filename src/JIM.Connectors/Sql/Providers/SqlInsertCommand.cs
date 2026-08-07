// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// Everything a provider needs to generate an INSERT: the row's columns, and where the key comes from.
/// </summary>
/// <remarks>
/// The same record serves both inserts a Connector performs. Where the database generates the row's key
/// (an identity or a sequence), <see cref="GeneratedKeyColumn"/> names it and the statement hands the
/// value back, which the Connector returns as the object's external ID. Where JIM supplies the key
/// itself, or the row has no key of its own (a related table's row), both generated-key members are
/// left null and the statement is a plain INSERT.
/// </remarks>
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
    /// The identity, sequence or default-backed column whose generated value is wanted back, or null
    /// where the row's key is supplied rather than generated.
    /// </summary>
    internal string? GeneratedKeyColumn { get; init; }

    /// <summary>
    /// The parameter the generated key is returned through. Named in the statement for dialects that
    /// bind an output parameter; retained for the others so the two paths read the same at the caller.
    /// </summary>
    internal string? GeneratedKeyParameterName { get; init; }
}
