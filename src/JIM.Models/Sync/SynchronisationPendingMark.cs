// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// One synchronisation-pending mark (#2092) addressed by join rather than by object id: the Connected System Object
/// joined to <see cref="MetaverseObjectId"/> in <see cref="ConnectedSystemId"/> is to be processed by that system's
/// next synchronisation although its own data has not changed. Applied in bulk as
/// <c>ConnectedSystemObjects.SynchronisationPending = true</c>.
/// </summary>
/// <param name="MetaverseObjectId">The Metaverse Object whose joined object must be processed again.</param>
/// <param name="ConnectedSystemId">The Connected System whose joined object is marked.</param>
public readonly record struct SynchronisationPendingMark(Guid MetaverseObjectId, int ConnectedSystemId);
