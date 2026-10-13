// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// A Connector as a list shows it.
/// </summary>
public class ConnectorDefinitionHeader
{
    /// <summary>
    /// The ID of the Connector.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The name of the Connector.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// What the Connector connects to; null when none is given.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether the Connector ships with JIM.
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>
    /// When the Connector was added (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// When the Connector was last changed (UTC); null if it never has been.
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// How many files make up the Connector.
    /// </summary>
    public int Files { get; set; }

    /// <summary>
    /// The versions of the Connector's files, comma-separated.
    /// </summary>
    public string? Versions { get; set; }

    /// <summary>
    /// Whether any Connected System uses the Connector.
    /// </summary>
    public bool InUse { get; set; }
}