// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// A Connected System that projects into a Metaverse Object Type deleted When Authoritative Source Disconnected,
/// without being one of its selected authoritative sources (#1256). It can create Metaverse Objects that no selected
/// source governs.
/// </summary>
/// <param name="ConnectedSystemId">The projecting Connected System.</param>
/// <param name="ConnectedSystemName">Its name, for display.</param>
public sealed record DeletionSourceGap(int ConnectedSystemId, string ConnectedSystemName);
