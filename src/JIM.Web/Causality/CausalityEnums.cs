// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The visual tone of a causality event or outcome pill. Maps onto the MudBlazor palette via
/// <see cref="OutcomeDisplayMap.ToMudBlazorColor"/> so all themes derive colours from theme tokens.
/// </summary>
public enum CausalityTone
{
    Primary,
    Success,
    Info,
    Warning,
    Error,
    Secondary
}

/// <summary>
/// Which side of the story a causality event belongs to: what happened (Source), what JIM did
/// (Identity), and what it caused (Downstream). The lineage builder projects lanes onto object
/// columns: Identity-lane events land on the Identity, the rest on the record of their owning
/// system.
/// </summary>
public enum CausalityLane
{
    Source,
    Identity,
    Downstream
}

/// <summary>
/// The toggleable causality visualisation views: the Lineage (the object graph with every causally
/// relevant event on the object it happened to; the default), the Timeline (strict chronological
/// reading), and the Table (#1519 Phase 3: a flat, per-object change list, filterable by change kind).
/// The Flow and Graph views the panel launched with were retired when the Lineage replaced them
/// (#1495); their stored preference keys fall back to the default.
/// </summary>
public enum CausalityView
{
    Timeline,
    Lineage,
    Table
}

/// <summary>
/// The kind of object a lineage column stands for (#1495): a record in a Connected System, the
/// Identity (the Metaverse side of the story), or the neutral trailing column that holds any chain
/// hop the builder cannot place, so nothing in the chain is ever silently dropped.
/// </summary>
public enum CausalityLineageColumnKind
{
    Record,
    Identity,
    Unassigned
}

/// <summary>
/// The kind of entity a causality link or sentence segment refers to, so the renderer can choose
/// the matching glyph chip (Connected System, Record, Identity, Synchronisation Rule, etc.).
/// </summary>
public enum CausalityEntityKind
{
    ConnectedSystem,
    Record,
    Identity,
    SynchronisationRule,
    PendingExport,
    DeletionRecord,
    RunProfile
}

/// <summary>
/// The operation an attribute change row represents. Single-valued Add and Remove pairs collapse
/// into a Set with a previous value; multi-valued changes keep their individual Add/Remove rows.
/// </summary>
public enum CausalityAttributeOperation
{
    Set,
    Add,
    Remove
}

/// <summary>
/// What kind of change a Table view row (#1519 Phase 3) states, derived from its event's outcome
/// type. Every value but <see cref="AttributeChange"/> is an object-level fact; see
/// <see cref="CausalityTableModelBuilder"/> for the outcome-type mapping.
/// </summary>
public enum CausalityTableChangeKind
{
    /// <summary>The object's import scope changed (DisconnectedOutOfScope, OutOfScopeRetainJoin).</summary>
    Scope,

    /// <summary>A new Identity was created for the object (Projected).</summary>
    Projection,

    /// <summary>The object was joined to an existing Identity, or a scheduled deletion was cancelled by a rejoin (Joined, MvoDeletionCancelled).</summary>
    Join,

    /// <summary>A join broke without an out-of-scope determination, or a downstream target lost its join with no matching export rule.</summary>
    Disconnect,

    /// <summary>The Identity was deleted or its deletion was scheduled.</summary>
    Delete,

    /// <summary>A downstream account was queued for removal or removed.</summary>
    Deprovision,

    /// <summary>A downstream account was provisioned.</summary>
    Provision,

    /// <summary>An export was queued for a downstream target.</summary>
    ExportQueued,

    /// <summary>A downstream account's provisioning was withdrawn before it was ever exported (ProvisioningCancelled): nothing was created, updated or removed in the target system.</summary>
    ProvisioningCancelled,

    /// <summary>An attribute value was cleared because no import source contributes it any more.</summary>
    NoContributor,

    /// <summary>An attribute value was preserved because no import source remains to assert the object.</summary>
    ValuesPreserved,

    /// <summary>An individual attribute value change, from an event's own <see cref="CausalityAttributeRow"/>s.</summary>
    AttributeChange
}

/// <summary>
/// Which group a <see cref="CausalityTableObject"/> renders under in the Table view's left navigation.
/// </summary>
public enum CausalityTableObjectRole
{
    /// <summary>The flattened "everything" entry, shown first and outside every group.</summary>
    Everything,

    /// <summary>The object being synchronised: the item's own Connected System Object.</summary>
    Source,

    /// <summary>The Identity: the Metaverse Object.</summary>
    Identity,

    /// <summary>A downstream object: a target Connected System (or Connected System Object) the events reached.</summary>
    Downstream
}

/// <summary>
/// A Table view row filter (#1519 Phase 3). <see cref="CausalityTableFilters.Matches"/> is the pure
/// predicate over a <see cref="CausalityTableRow"/>.
/// </summary>
public enum CausalityTableFilter
{
    All,
    ScopeAndJoin,
    AttributeChanges,
    ObjectChanges,
    Destructive
}
