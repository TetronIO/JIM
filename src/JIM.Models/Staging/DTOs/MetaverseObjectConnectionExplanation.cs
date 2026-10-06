// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic.Scoping;

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// One joined connection with the reasons it exists (#348): the row the Connections tab shows, how the join came
/// about, the scoping of every relevant Synchronisation Rule evaluated now, and any export rule that cannot connect
/// because this connection holds the slot.
/// </summary>
public sealed class MetaverseObjectConnectionExplanation : MetaverseObjectConnection
{
    public JoinRecord Join { get; set; } = new();

    /// <summary>
    /// One explanation per enabled Synchronisation Rule for this connection's Connected System and object type, in
    /// rule name order: import rules evaluated against the Connected System Object's values, export rules against the
    /// Metaverse Object's. Evaluated now, against current values; not the reason at the time of joining.
    /// </summary>
    public List<ScopingExplanation> Scoping { get; set; } = [];

    public List<ConnectionObjectTypeConflict> Conflicts { get; set; } = [];
}
