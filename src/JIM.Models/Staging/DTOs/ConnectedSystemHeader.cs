// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// Lightweight representation of a ConnectedSystem for list views.
/// </summary>
public class ConnectedSystemHeader
{
    /// <summary>
    /// The ID of the Connected System.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The name of the Connected System.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// What the Connected System is for, for administrators; null when none is given.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// When the Connected System was created (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// Whether the Connected System takes part in synchronisation: Active, Disabled, or Deleting while a deletion
    /// is under way.
    /// </summary>
    public ConnectedSystemStatus Status { get; set; }

    /// <summary>
    /// How many Connected System Objects the Connected System holds.
    /// </summary>
    public int ObjectCount { get; set; }

    /// <summary>
    /// How many of those Connected System Objects are joined to a Metaverse Object.
    /// </summary>
    public int ConnectorsCount { get; set; }

    /// <summary>
    /// How many Pending Exports are waiting to be exported to the Connected System.
    /// </summary>
    public int PendingExportObjectsCount { get; set; }

    /// <summary>
    /// The name of the Connector the Connected System uses.
    /// </summary>
    public string ConnectorName { get; set; } = null!;

    /// <summary>
    /// The ID of that Connector.
    /// </summary>
    public int ConnectorId { get; set; }

    public override string ToString() => Name;
}