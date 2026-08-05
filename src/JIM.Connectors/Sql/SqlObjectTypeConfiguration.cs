// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql;

/// <summary>
/// One Connected System Object Type as an administrator configured it: where its objects come from,
/// what identifies them, and which of its columns mean more than their SQL type says.
/// </summary>
/// <remarks>
/// This shape is richer than the flat settings framework can express (N object types, each with its own
/// source, anchor column set and N related tables), which is why it arrives as a JSON document in one
/// Text setting rather than as a settings form. Everything here has been validated by
/// <see cref="SqlSchemaConfiguration.Parse"/> before it exists: identifiers are plausible object names,
/// exactly one source is named, and every referenced object type is one the document declares.
/// </remarks>
internal sealed record SqlObjectTypeConfiguration
{
    /// <summary>
    /// What JIM calls this object type. Standard names ("User", "Group", "Person") let JIM auto-map it
    /// to a Metaverse Object Type.
    /// </summary>
    internal required string Name { get; init; }

    /// <summary>
    /// The schema qualifying <see cref="TableName"/>. Null lets discovery resolve it from the catalogue,
    /// which is unambiguous whenever the database account can see exactly one object of that name.
    /// </summary>
    internal string? SchemaName { get; init; }

    /// <summary>
    /// The table or view objects of this type are read from. Null when <see cref="SelectStatement"/> is
    /// configured instead; exactly one of the two is always set.
    /// </summary>
    internal string? TableName { get; init; }

    /// <summary>
    /// An administrator-supplied SELECT statement standing in for a table or view, for the cases a view
    /// cannot be created for. Privileged administrator input per the PRD trust model, but still never a
    /// carrier for values: everything JIM binds around it is a parameter.
    /// </summary>
    internal string? SelectStatement { get; init; }

    /// <summary>
    /// The column or columns whose values identify a row, in key order. The order is what makes a
    /// keyset page boundary reproducible between runs, so it is never sorted.
    /// </summary>
    internal IReadOnlyList<string> AnchorColumns { get; init; } = [];

    /// <summary>
    /// The columns that mean more than their SQL type says. A column absent from here is mapped by its
    /// type alone.
    /// </summary>
    internal IReadOnlyList<SqlColumnConfiguration> Columns { get; init; } = [];

    /// <summary>
    /// The tables holding this object type's multi-valued attributes, one attribute each.
    /// </summary>
    internal IReadOnlyList<SqlRelatedTableConfiguration> RelatedTables { get; init; } = [];

    /// <summary>
    /// Whether objects of this type come from a statement rather than a table or view, which is what
    /// decides how discovery learns the shape and whether constraint metadata exists at all.
    /// </summary>
    internal bool IsCustomSelect => SelectStatement != null;
}

/// <summary>
/// A column whose meaning the catalogue cannot state. A column carrying another object type's anchor is
/// an ordinary integer or string as far as the database is concerned, so it is declared here rather
/// than inferred: views carry no constraint metadata at all, and the common identity case (a manager
/// column with no foreign key behind it) carries none either.
/// </summary>
internal sealed record SqlColumnConfiguration
{
    internal required string Name { get; init; }

    /// <summary>
    /// The object type whose anchor this column holds.
    /// </summary>
    internal required string ReferencesObjectType { get; init; }
}

/// <summary>
/// A table holding one multi-valued attribute of a parent object type: one row per value, joined back
/// to the parent by its anchor.
/// </summary>
internal sealed record SqlRelatedTableConfiguration
{
    /// <summary>
    /// What the attribute is called on the parent object type. Named by the administrator because a
    /// value column's own name ("PHONE_NUMBER") rarely reads as the plural attribute it becomes.
    /// </summary>
    internal required string AttributeName { get; init; }

    internal string? SchemaName { get; init; }

    internal required string TableName { get; init; }

    /// <summary>
    /// The column holding the value itself. Its SQL type decides the attribute's type.
    /// </summary>
    internal required string ValueColumn { get; init; }

    /// <summary>
    /// The columns joining a row back to its parent, one per anchor column and in the same order.
    /// Joining on fewer would gather another object's values onto this one, without any error.
    /// </summary>
    internal IReadOnlyList<string> JoinColumns { get; init; } = [];

    /// <summary>
    /// The object type whose anchor <see cref="ValueColumn"/> holds, where the values are references
    /// rather than data. Group membership is exactly this shape.
    /// </summary>
    internal string? ReferencesObjectType { get; init; }
}
