// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// One Connected System Object reduced to the facts a partition and container scope preview needs about it (#1251):
/// where it sits, and what it is joined to. Everything else about the object is irrelevant to the question, and a
/// preview runs over the whole connector space, so materialising the objects themselves would put hundreds of
/// thousands of entity graphs in memory to read four fields off each.
/// </summary>
/// <param name="Id">The Connected System Object.</param>
/// <param name="ObjectTypeName">Its type's name, snapshotted for display and for grouping the summary.</param>
/// <param name="PartitionId">
/// The partition it was imported from, or null for rows predating partition tracking. A null cannot be attributed
/// to any selection, so scope membership for it is undetermined rather than assumed.
/// </param>
/// <param name="ContainerIdentifier">
/// The identifier the Connector derives containment from: the secondary external ID where the Connected System has
/// one (the Distinguished Name, for a directory), and the primary external ID otherwise. Personal data, as a
/// Distinguished Name routinely carries a person's name: it belongs in a preview's drill-down rows, never in a log
/// line.
/// </param>
/// <param name="MetaverseObjectId">
/// The Metaverse Object it is joined to, or null when it is a disconnector. What decides whether this object
/// leaving scope costs JIM anything.
/// </param>
public record ConnectedSystemObjectScopeCandidate(
    Guid Id,
    string ObjectTypeName,
    int? PartitionId,
    string? ContainerIdentifier,
    Guid? MetaverseObjectId);
