// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Models.Staging.DTOs;

public class ConnectedSystemObjectHeader
{
    #region accessors
    public Guid Id { get; set; }

    public int ConnectedSystemId { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>
    /// Scalar string value of the external id attribute on this CSO. Replaces the previous
    /// full-entity projection so the list query does not materialise an attribute-value entity
    /// per row just to read its string value.
    /// </summary>
    public string? ExternalIdValue { get; set; }

    public string? ExternalIdAttributeName { get; set; }

    /// <summary>
    /// Scalar string value of the secondary external id attribute on this CSO, if defined and present.
    /// </summary>
    public string? SecondaryExternalIdValue { get; set; }

    public string? SecondaryExternalIdAttributeName { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public DateTime? LastUpdated { get; set; }

    public int TypeId { get; set; }

    public string TypeName { get; set; } = string.Empty;

    public ConnectedSystemObjectStatus Status { get; set; } = ConnectedSystemObjectStatus.Normal;

    /// <summary>
    /// How was this CSO joined to an MVO, if at all?
    /// </summary>
    public ConnectedSystemObjectJoinType JoinType { get; set; } = ConnectedSystemObjectJoinType.NotJoined;

    /// <summary>
    /// When this Connected System Object was joined to the Metaverse.
    /// </summary>
    public DateTime? DateJoined { get; set; }

    #region Pending Export Fields

    /// <summary>
    /// The pending Display Name value from a PendingExport, if one exists.
    /// </summary>
    public string? PendingDisplayName { get; set; }

    /// <summary>
    /// The pending External ID value from a PendingExport, if one exists.
    /// </summary>
    public string? PendingExternalId { get; set; }

    /// <summary>
    /// The secondary external ID (e.g., DN) from a Pending Export, if one exists and the CSO doesn't have a confirmed value.
    /// </summary>
    public string? PendingSecondaryExternalId { get; set; }

    /// <summary>
    /// Whether there is a Pending Export for this CSO.
    /// </summary>
    public bool HasPendingExport { get; set; }

    /// <summary>
    /// The ID of the PendingExport associated with this CSO, if one exists.
    /// </summary>
    public Guid? PendingExportId { get; set; }

    /// <summary>
    /// The status of the Pending Export associated with this CSO, if one exists. Tells a pending value that has
    /// merely been staged (Pending) apart from one that has been exported and awaits a confirming import
    /// (Exported), which the pending values above cannot say on their own.
    /// </summary>
    public PendingExportStatus? PendingExportStatus { get; set; }

    /// <summary>
    /// The change type of the object's Pending Export, when one is queued. Projected as a scalar beside
    /// <see cref="PendingExportStatus"/> so <see cref="State"/> can tell a pending Create from an Update
    /// or a Delete without loading the Pending Export itself.
    /// </summary>
    public PendingExportChangeType? PendingExportChangeType { get; set; }

    /// <summary>
    /// The row's derived connection state: its status combined with any queued Pending Export, resolved in
    /// JIM.Application (a resolver call cannot be translated into SQL). This is the same derivation the
    /// Metaverse Object's Connections tab renders, so the two lists cannot describe the same object
    /// differently (D-S7 on #1519's plan).
    /// </summary>
    public ConnectedSystemObjectConnectionState State { get; set; }

    #endregion
    #endregion
}
