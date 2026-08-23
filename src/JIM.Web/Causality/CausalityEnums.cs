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
/// (Identity), and what it caused (Downstream). The spine builder projects lanes onto object
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
/// The toggleable causality visualisation views: the Spine (the object graph with every causally
/// relevant event on the object it happened to; the default) and the Timeline (strict chronological
/// reading). The Flow and Graph views the panel launched with were retired when the Spine replaced
/// them (#1495); their stored preference keys fall back to the default.
/// </summary>
public enum CausalityView
{
    Timeline,
    Spine
}

/// <summary>
/// The kind of object a spine column stands for (#1495): a record in a Connected System, the
/// Identity (the Metaverse side of the story), or the neutral trailing column that holds any chain
/// hop the builder cannot place, so nothing in the chain is ever silently dropped.
/// </summary>
public enum CausalitySpineColumnKind
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
