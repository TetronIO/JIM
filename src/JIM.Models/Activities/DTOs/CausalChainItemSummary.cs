// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// What the causal walk needs to know about a Run Profile Execution Item it is about to resolve (#1223):
/// enough to decide whether the item is retained (its presence in the result says so) and whether the record's
/// own timeline continues behind it to the import that fed it.
/// </summary>
public class CausalChainItemSummary
{
    /// <summary>
    /// The Run Profile Execution Item this summarises.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// What the run did to the object, which is what decides whether a source-import hop applies: a
    /// synchronisation-side item (a projection, a join, an attribute flow, a disconnect) was fed by whatever
    /// import last changed its Connected System Object; an import-side item is itself the far end.
    /// </summary>
    public ObjectChangeType ObjectChangeType { get; init; }

    /// <summary>
    /// The Connected System Object the item processed, where it processed one: the key the record's timeline
    /// is walked on.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; init; }

    /// <summary>
    /// When the item's Activity ran, bounding the timeline walk: the import that fed a synchronisation is the
    /// latest one at or before it.
    /// </summary>
    public DateTime ActivityExecuted { get; init; }
}
