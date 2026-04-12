// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Result of clearing all objects from a connected system, including counts of removed items.
/// </summary>
public class ClearConnectedSystemResult
{
    /// <summary>
    /// Count of pending exports removed during the clear operation.
    /// </summary>
    public int PendingExportsRemoved { get; set; }

    /// <summary>
    /// Count of connected system objects removed during the clear operation.
    /// </summary>
    public int ConnectedSystemObjectsRemoved { get; set; }
}
