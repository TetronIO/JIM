// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// One Metaverse-Derived Attribute Flow mark (#1750, "Position 2"): the Connected System Object joined to
/// <see cref="MetaverseObjectId"/> in <see cref="ConnectedSystemId"/> is to be re-evaluated by that system's next
/// synchronisation, because a Metaverse attribute a derived flow on that system's rules reads has changed elsewhere.
/// Applied in bulk as <c>ConnectedSystemObjects.DerivedInputChangePending = true</c>.
/// </summary>
/// <param name="MetaverseObjectId">The Metaverse Object whose derived flow input changed.</param>
/// <param name="ConnectedSystemId">A Connected System whose import Synchronisation Rules host a derived flow reading it.</param>
public readonly record struct DerivedInputChangeMark(Guid MetaverseObjectId, int ConnectedSystemId);
