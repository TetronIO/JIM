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
/// The column a causality event belongs to in the Flow view: what came in (Source), what JIM did
/// (Identity), and what it caused (Downstream).
/// </summary>
public enum CausalityLane
{
    Source,
    Identity,
    Downstream
}

/// <summary>
/// The toggleable causality visualisation views. Timeline ships first; Flow and Graph arrive in
/// later phases by adding themselves to <c>CausalityPanel</c>'s available-view list.
/// </summary>
public enum CausalityView
{
    Flow,
    Timeline,
    Graph
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
