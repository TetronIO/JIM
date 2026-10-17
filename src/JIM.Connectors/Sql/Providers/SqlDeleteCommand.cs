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

    /// <summary>
    /// A further column the rows must fall within a half-open range of: at least the first parameter and less than the
    /// second. Used to remove a date at the column's resolution (#2153), where equality would miss a row the column holds
    /// finer than the value JIM sent. Null for none.
    /// </summary>
    internal SqlRangeColumnParameter? RangeColumn { get; init; }
}

/// <summary>
/// A column compared with a half-open range, <c>lower &lt;= column &lt; upper</c>, each bound carried by a parameter.
/// </summary>
/// <param name="ColumnName">The unquoted column name; the provider quotes it.</param>
/// <param name="LowerParameterName">The bare parameter name of the inclusive lower bound.</param>
/// <param name="UpperParameterName">The bare parameter name of the exclusive upper bound.</param>
internal sealed record SqlRangeColumnParameter(string ColumnName, string LowerParameterName, string UpperParameterName);
