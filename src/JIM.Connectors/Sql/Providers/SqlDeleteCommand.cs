// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// Everything a provider needs to generate a DELETE: which rows to remove, and from where.
/// </summary>
/// <remarks>
/// A delete with no key columns would empty the table, so the provider refuses one rather than
/// generating it. Removing every related row of one parent is expressed by keying on the join columns
/// alone, which is a narrower statement than it looks: the join columns are the parent's anchor.
/// </remarks>
internal sealed record SqlDeleteCommand
{
    internal string? SchemaName { get; init; }

    internal required string ObjectName { get; init; }

    /// <summary>
    /// The columns identifying the rows to remove, each paired with the parameter carrying its value.
    /// </summary>
    internal required IReadOnlyList<SqlColumnParameter> KeyColumns { get; init; }
}
