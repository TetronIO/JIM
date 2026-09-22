// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// One row of a Metaverse Object's Connections tab (#1519): one joined Connected System Object, its
/// owning Connected System, its role in synchronisation, and its derived state. Built by
/// <c>MetaverseServer.GetMetaverseObjectConnectionsAsync</c>, reused by the Connector Space list per
/// D-S7.
/// </summary>
public class MetaverseObjectConnection
{
    /// <summary>
    /// The Connected System Object's id.
    /// </summary>
    public Guid ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// The object's external id, or its display name where the external id could not be rendered.
    /// Never both; see <see cref="ConnectedSystemObject.ExternalIdAttributeValue"/>.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// The Connected System's id.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The Connected System's name.
    /// </summary>
    public string ConnectedSystemName { get; set; } = string.Empty;

    /// <summary>
    /// The Connected System Object Type's name (e.g. "person").
    /// </summary>
    public string ObjectTypeName { get; set; } = string.Empty;

    /// <summary>
    /// How the object was joined to the Metaverse Object.
    /// </summary>
    public ConnectedSystemObjectJoinType JoinType { get; set; }

    /// <summary>
    /// True when at least one enabled Import Synchronisation Rule exists for this object's Connected
    /// System and Connected System Object Type: values can flow inbound from it.
    /// </summary>
    public bool IsSource { get; set; }

    /// <summary>
    /// True when at least one enabled Export Synchronisation Rule exists for this object's Connected
    /// System and Connected System Object Type: values can flow outbound to it.
    /// </summary>
    public bool IsTarget { get; set; }

    /// <summary>
    /// The object's derived connection state.
    /// </summary>
    public ConnectedSystemObjectConnectionState State { get; set; }

    /// <summary>
    /// The number of attribute changes on the object's Pending Export, when one exists and carries
    /// attribute changes (an Update in progress). Null otherwise, so a caller does not render "0
    /// attributes" for a state that is not about attribute changes at all (a Create or a Delete).
    /// </summary>
    public int? PendingAttributeChangeCount { get; set; }

    /// <summary>
    /// When the object was last synchronised (its <c>LastUpdated</c> timestamp).
    /// </summary>
    public DateTime? LastSynchronised { get; set; }
}
