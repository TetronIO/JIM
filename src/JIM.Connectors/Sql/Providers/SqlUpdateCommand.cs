// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// Everything a provider needs to generate an UPDATE of one row: what to set, and what identifies the
/// row to set it on.
/// </summary>
/// <remarks>
/// Both sides are bound parameters. Only the column names reach the statement text, quoted by the
/// provider, so a column name that is not a plausible identifier is refused rather than escaped.
/// </remarks>
internal sealed record SqlUpdateCommand
{
    internal string? SchemaName { get; init; }

    internal required string ObjectName { get; init; }

    /// <summary>
    /// The columns being written, each paired with the parameter carrying its new value.
    /// </summary>
    internal required IReadOnlyList<SqlColumnParameter> Columns { get; init; }

    /// <summary>
    /// The columns identifying the row, each paired with the parameter carrying its value: the anchor,
    /// in anchor order. Every one of them is compared, because comparing on part of a composite anchor
    /// would update somebody else's row without any error.
    /// </summary>
    internal required IReadOnlyList<SqlColumnParameter> KeyColumns { get; init; }
}
