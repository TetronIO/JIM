// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// One object in the spine's story (#1495): a record in a Connected System, or the Identity. The
/// column is the object, never the system; a record column names its Connected System beneath its
/// head rather than being headed by it.
/// </summary>
public sealed class CausalitySpineColumn
{
    /// <summary>
    /// What kind of object this column stands for.
    /// </summary>
    public CausalitySpineColumnKind Kind { get; init; }

    /// <summary>
    /// The column's head: the object's name where the story has a single object here, or its role
    /// where it speaks for several (see <see cref="IsRoleHead"/>).
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// True when <see cref="Title"/> is a role ("Users") rather than a single object's name,
    /// because the column's story involves several objects and no one name would be honest.
    /// </summary>
    public bool IsRoleHead { get; init; }

    /// <summary>
    /// Id of the Connected System a record column's object lives on; null for the Identity.
    /// </summary>
    public int? SystemId { get; init; }

    /// <summary>
    /// Name of the Connected System, shown beneath a record column's head ("record in Yellowstone
    /// APAC"); null for the Identity. Snapshot-sourced for chain-derived columns, so a renamed or
    /// deleted system still reads as it was at the time.
    /// </summary>
    public string? SystemName { get; init; }

    /// <summary>
    /// The record's object type name ("person"), where known; null for the Identity and for
    /// chain-derived record columns, whose snapshots do not carry it.
    /// </summary>
    public string? ObjectTypeName { get; init; }

    /// <summary>
    /// Link to the object's own page, or null where it no longer exists or was never resolvable.
    /// </summary>
    public string? Href { get; init; }

    /// <summary>
    /// The events that happened to this object, oldest first: chain cards in time order, then this
    /// run's cards in outcome order (this run is always the newest thing in the story).
    /// </summary>
    public IReadOnlyList<CausalitySpineCard> Cards { get; init; } = [];

    /// <summary>
    /// The chain endings that close under this column, one per distinct resolution: the walk's
    /// terminal states rendered as quiet footers, never warnings.
    /// </summary>
    public IReadOnlyList<CausalitySpineEnding> Endings { get; init; } = [];

    /// <summary>
    /// Whether any of this run's own events landed on this column: the lit column is what the item
    /// did, against the subdued history around it.
    /// </summary>
    public bool IsLit => Cards.Any(c => c.IsThisRun);
}
