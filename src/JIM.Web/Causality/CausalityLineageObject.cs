// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// One object in the lineage's story (#1495): a record in a Connected System, or the Identity. The
/// object is never its system; a record names its Connected System beneath its head rather than
/// being headed by it. Objects on the same side of the Identity share a column
/// (<see cref="CausalityLineageColumn"/>), each enclosing its own events.
/// </summary>
public sealed class CausalityLineageObject
{
    /// <summary>
    /// The object's head: its name where the story has a single object here, or its role where it
    /// speaks for several (see <see cref="IsRoleHead"/>).
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// True when <see cref="Title"/> is a role ("Users") rather than a single object's name,
    /// because the object's story involves several objects and no one name would be honest.
    /// </summary>
    public bool IsRoleHead { get; init; }

    /// <summary>
    /// Id of the Connected System a record lives on; null for the Identity.
    /// </summary>
    public int? SystemId { get; init; }

    /// <summary>
    /// Name of the Connected System, shown beneath a record's head ("record in Yellowstone APAC");
    /// null for the Identity. Snapshot-sourced for chain-derived objects, so a renamed or deleted
    /// system still reads as it was at the time.
    /// </summary>
    public string? SystemName { get; init; }

    /// <summary>
    /// The object's type name, where known: the record's own type ("person") on the page's record,
    /// or the Metaverse Object Type ("User") on a single-object Identity. Null for chain-derived
    /// records (their snapshots do not carry it) and for role heads, whose title is already a type
    /// noun.
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
    public IReadOnlyList<CausalityLineageCard> Cards { get; init; } = [];

    /// <summary>
    /// The chain endings that close under this object, one per distinct resolution: the walk's
    /// terminal states rendered as quiet footers, never warnings.
    /// </summary>
    public IReadOnlyList<CausalityLineageEnding> Endings { get; init; } = [];

    /// <summary>
    /// Whether any of this run's own events landed on this object: what the item did, against the
    /// subdued history around it.
    /// </summary>
    public bool IsLit => Cards.Any(c => c.IsThisRun);

    /// <summary>
    /// The object's durable deletion record, where the panel can prove the object is gone; null where it
    /// cannot. Non-null is what <see cref="IsDeleted"/> reads, so the two can never disagree.
    /// </summary>
    /// <remarks>
    /// "Gone" is only ever claimed from evidence: this run recorded the deletion, or the page looked the
    /// object up and it was not there. An object JIM simply cannot build a route to is a different fact
    /// (see <see cref="Href"/> being null while this is too), and saying it no longer exists would be
    /// false: its type may just be unresolvable.
    /// </remarks>
    public string? DeletionRecordHref { get; init; }

    /// <summary>
    /// Whether the object is known to be gone. A deleted object is not a dead end: JIM retains a deletion
    /// record for it, which is what <see cref="DeletionRecordHref"/> points at.
    /// </summary>
    public bool IsDeleted => DeletionRecordHref != null;

    /// <summary>
    /// Whether one of this object's own cards already offers the deletion record, in which case the head
    /// states the fact but does not repeat the link: every entity is named once on this panel.
    /// </summary>
    public bool DeletionRecordShownOnACard =>
        Cards.Any(c => c.Event?.Links.Any(l => l.Kind == CausalityEntityKind.DeletionRecord) == true);
}
