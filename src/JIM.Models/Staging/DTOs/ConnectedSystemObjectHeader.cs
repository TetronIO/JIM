// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// A Connected System Object as a list shows it: what identifies it, whether it is joined, and what is queued to
/// change it.
/// </summary>
public class ConnectedSystemObjectHeader
{
    #region accessors

    /// <summary>
    /// The ID of the Connected System Object.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The ID of the Connected System the object belongs to.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The object's display name; null when it has none.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// The value of the object's external identifier attribute; null when it has none.
    /// </summary>
    /// <remarks>
    /// A scalar projection, so the list query does not materialise an attribute-value entity per row just to read
    /// its string value.
    /// </remarks>
    public string? ExternalIdValue { get; set; }

    /// <summary>
    /// The name of the attribute that identifies objects of this type in the Connected System.
    /// </summary>
    public string? ExternalIdAttributeName { get; set; }

    /// <summary>
    /// The value of the object's secondary external identifier attribute; null when it has none.
    /// </summary>
    /// <remarks>
    /// A scalar projection, for the same reason as ExternalIdValue.
    /// </remarks>
    public string? SecondaryExternalIdValue { get; set; }

    /// <summary>
    /// The name of the attribute the Connected System also identifies objects of this type by, such as a
    /// distinguished name; null when it has none.
    /// </summary>
    public string? SecondaryExternalIdAttributeName { get; set; }

    /// <summary>
    /// When the object first arrived in the Connector Space (UTC).
    /// </summary>
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the object was last changed (UTC); null if it never has been.
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The ID of the object's Object Type.
    /// </summary>
    public int TypeId { get; set; }

    /// <summary>
    /// The name of the object's Object Type.
    /// </summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>
    /// Normal for an object taking part in synchronisation; Obsolete for one the last Full Import did not return,
    /// so no longer in the Connected System; PendingProvisioning for one JIM has decided to create and not yet
    /// confirmed.
    /// </summary>
    public ConnectedSystemObjectStatus Status { get; set; } = ConnectedSystemObjectStatus.Normal;

    /// <summary>
    /// How the object came to be joined to a Metaverse Object: Projected (it created one), Provisioned (one created
    /// it), Joined (it was matched to one), or NotJoined.
    /// </summary>
    public ConnectedSystemObjectJoinType JoinType { get; set; } = ConnectedSystemObjectJoinType.NotJoined;

    /// <summary>
    /// When the object was joined to its Metaverse Object (UTC); null when it is not joined.
    /// </summary>
    public DateTime? DateJoined { get; set; }

    #region Pending Export Fields

    /// <summary>
    /// The display name a queued Pending Export will give the object; null when none is queued.
    /// </summary>
    public string? PendingDisplayName { get; set; }

    /// <summary>
    /// The external identifier a queued Pending Export will give the object; null when none is queued.
    /// </summary>
    public string? PendingExternalId { get; set; }

    /// <summary>
    /// The secondary external identifier a queued Pending Export will give the object, where it has no confirmed
    /// one yet; null otherwise.
    /// </summary>
    public string? PendingSecondaryExternalId { get; set; }

    /// <summary>
    /// Whether a Pending Export is queued for the object.
    /// </summary>
    public bool HasPendingExport { get; set; }

    /// <summary>
    /// The ID of the queued Pending Export; null when there is none.
    /// </summary>
    public Guid? PendingExportId { get; set; }

    /// <summary>
    /// The status of the queued Pending Export: Pending while it is waiting to be exported, Exported once it is
    /// awaiting a confirming import, and so on. Null when none is queued.
    /// </summary>
    /// <remarks>
    /// Tells a pending value that has merely been staged apart from one that has been exported and awaits
    /// confirmation, which the pending values alone cannot say.
    /// </remarks>
    public PendingExportStatus? PendingExportStatus { get; set; }

    /// <summary>
    /// Whether the queued Pending Export creates, updates or deletes the object; null when there is none.
    /// </summary>
    public PendingExportChangeType? PendingExportChangeType { get; set; }

    /// <summary>
    /// The object's state, combining its status with any queued Pending Export: InSync, UpdatePending,
    /// ProvisioningExportPending, ProvisioningAwaitingConfirmation, ExportNotConfirmed, ExportFailed, DeletePending
    /// or Obsolete.
    /// </summary>
    /// <remarks>
    /// Resolved in JIM.Application, because the resolver cannot be translated into SQL. It is the same derivation
    /// the Metaverse Object's Connections tab renders, so the two lists cannot describe the same object differently
    /// (D-S7 on #1519's plan).
    /// </remarks>
    public ConnectedSystemObjectConnectionState State { get; set; }

    #endregion

    /// <summary>
    /// How many of the object's reference values JIM has not resolved to a Connected System Object. Zero when there
    /// are none.
    /// </summary>
    /// <remarks>
    /// Issue #959.
    /// </remarks>
    public int UnresolvedReferenceCount { get; set; }
    #endregion
}
