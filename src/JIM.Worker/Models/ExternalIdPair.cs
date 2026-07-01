// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Worker.Models;

/// <summary>
/// Tracks an imported object's external ID for deletion detection: the Connected System Object Type it belongs
/// to (by ID) and the import attribute carrying the external ID value(s). Deletion detection only needs the type
/// ID to group by and the typed value lists to compare, so this deliberately does not hold the full
/// <see cref="ConnectedSystemObjectType"/> graph.
/// </summary>
public struct ExternalIdPair
{
    public int ConnectedSystemObjectTypeId { get; init; }
    public ConnectedSystemImportObjectAttribute ConnectedSystemImportObjectAttribute { get; init; }
}