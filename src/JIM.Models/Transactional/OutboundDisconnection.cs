// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// One outbound disconnection recorded by an export evaluation run: a Metaverse Object left an export
/// Synchronisation Rule's scope under a Disconnect Deprovisioning Action, so the join to its Connected System Object
/// in the target system was broken and the object itself left in place. Immutable, and recorded on
/// <see cref="ExportEvaluationWorkingSet"/> only after the broken join has been persisted, so a failed write cannot
/// leave a disconnection on record that never happened.
/// </summary>
/// <param name="ConnectedSystemObjectId">The id of the Connected System Object that was disconnected.</param>
/// <param name="ConnectedSystemId">The Connected System the disconnected object belongs to.</param>
/// <param name="MetaverseObjectId">The Metaverse Object the object was disconnected from.</param>
public sealed record OutboundDisconnection(Guid ConnectedSystemObjectId, int ConnectedSystemId, Guid MetaverseObjectId);
