// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// Everything a provider needs to render the predicate that selects the rows a Delta Import in Watermark
/// Column mode considers changed: the source's own watermark column, and each related table that carries
/// a watermark of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>A related table is a source of changes to the parent object, not to itself.</b> A group membership
/// added or revoked, or a phone number replaced, changes the object without touching the object's own
/// row, so the parent's watermark does not move and a predicate reading the primary source alone would
/// never see it. The parent is therefore selected on either its own watermark or any of its related
/// tables', which is why this exists as one filter rather than as a column comparison.
/// </para>
/// <para>
/// <b>Correlated subqueries, never a join.</b> A join to a related table returns one parent row per
/// matching related row, which turns one changed object into several import objects and inflates the
/// page against the Run Profile's page size. EXISTS answers "is there such a row" once per parent and
/// stops at the first match.
/// </para>
/// </remarks>
internal sealed record SqlChangeFilter
{
    /// <summary>
    /// The column on the source itself whose value moves whenever a row changes.
    /// </summary>
    internal required string ChangeColumn { get; init; }

    /// <summary>
    /// The parameter carrying the watermark <see cref="ChangeColumn"/> is compared against.
    /// </summary>
    internal required string ChangeParameterName { get; init; }

    /// <summary>
    /// The source's anchor columns, in key order, which each related table is correlated on.
    /// </summary>
    internal required IReadOnlyList<string> AnchorColumns { get; init; }

    /// <summary>
    /// The related tables whose own changes count as changes to the parent object. Empty where the
    /// object type has none, which is the case the primary source's watermark alone answers.
    /// </summary>
    internal IReadOnlyList<SqlRelatedChangeSource> RelatedSources { get; init; } = [];

    /// <summary>
    /// True when a related table can select a parent whose own watermark has not moved, which is what
    /// makes the source have to be aliased so a subquery can refer back to it.
    /// </summary>
    internal bool HasRelatedSources => RelatedSources.Count > 0;
}

/// <summary>
/// One related table a Delta Import watches for changes to its parent object.
/// </summary>
internal sealed record SqlRelatedChangeSource
{
    /// <summary>
    /// The prefix each related table's subquery alias is built from, suffixed by its position in the
    /// filter. Chosen so no real object collides with it.
    /// </summary>
    internal const string AliasPrefix = "JIM_RELATED";

    internal string? SchemaName { get; init; }

    internal required string TableName { get; init; }

    /// <summary>
    /// The columns joining a related row back to its parent, one per anchor column and in the same
    /// order. Correlating on fewer would attribute another object's changes to this one.
    /// </summary>
    internal required IReadOnlyList<string> JoinColumns { get; init; }

    /// <summary>
    /// The column on this related table whose value moves whenever one of its rows changes.
    /// </summary>
    internal required string WatermarkColumn { get; init; }

    /// <summary>
    /// The parameter carrying the watermark <see cref="WatermarkColumn"/> is compared against, or null
    /// where JIM holds none for this related table yet. Null means every row of it counts as changed,
    /// which is the only answer that cannot miss one.
    /// </summary>
    internal string? WatermarkParameterName { get; init; }
}
