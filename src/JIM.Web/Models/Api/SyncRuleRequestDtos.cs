// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Core;
using JIM.Models.Logic;

namespace JIM.Web.Models.Api;

/// <summary>
/// Request DTO for creating a new Sync Rule.
/// </summary>
public class CreateSyncRuleRequest
{
    /// <summary>
    /// The name for the Sync Rule.
    /// </summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = null!;

    /// <summary>
    /// The ID of the Connected System this rule applies to.
    /// </summary>
    [Required]
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The ID of the Connected System Object Type.
    /// </summary>
    [Required]
    public int ConnectedSystemObjectTypeId { get; set; }

    /// <summary>
    /// The ID of the Metaverse Object Type.
    /// </summary>
    [Required]
    public int MetaverseObjectTypeId { get; set; }

    /// <summary>
    /// The direction of the sync rule (Import or Export).
    /// </summary>
    [Required]
    public SyncRuleDirection Direction { get; set; }

    /// <summary>
    /// Whether this rule should also cause objects to be projected to the Metaverse (for Import rules).
    /// </summary>
    public bool? ProjectToMetaverse { get; set; }

    /// <summary>
    /// Whether this rule should also cause objects to be provisioned to the Connected System (for Export rules).
    /// </summary>
    public bool? ProvisionToConnectedSystem { get; set; }

    /// <summary>
    /// Whether the sync rule is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// For Export rules: When true (default), inbound changes from the target system will trigger
    /// re-evaluation of this export rule to detect and remediate drift.
    /// Only applicable when Direction = Export.
    /// </summary>
    public bool EnforceState { get; set; } = true;
}

/// <summary>
/// Request DTO for updating an existing Sync Rule.
/// </summary>
public class UpdateSyncRuleRequest
{
    /// <summary>
    /// The updated name for the Sync Rule.
    /// </summary>
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; set; }

    /// <summary>
    /// Whether the sync rule is enabled.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Whether this rule should also cause objects to be projected to the Metaverse (for Import rules).
    /// </summary>
    public bool? ProjectToMetaverse { get; set; }

    /// <summary>
    /// Whether this rule should also cause objects to be provisioned to the Connected System (for Export rules).
    /// </summary>
    public bool? ProvisionToConnectedSystem { get; set; }

    /// <summary>
    /// For Export rules: When true (default), inbound changes from the target system will trigger
    /// re-evaluation of this export rule to detect and remediate drift.
    /// Only applicable when Direction = Export.
    /// </summary>
    public bool? EnforceState { get; set; }

    /// <summary>
    /// For Import rules: Action to take when a CSO falls out of this rule's scope.
    /// Disconnect breaks the CSO -> MVO join; whether the attributes contributed by
    /// this connected system are also recalled from the MVO depends on the CSO type's
    /// RemoveContributedAttributesOnObsoletion flag, the MVO type's deletion grace
    /// period, and whether the MVO is slated for immediate deletion. RemainJoined
    /// keeps the join intact and stops further attribute flow. Only applicable when
    /// Direction = Import.
    /// </summary>
    public InboundOutOfScopeAction? InboundOutOfScopeAction { get; set; }

    /// <summary>
    /// For Export rules: Action to take when an MVO falls out of this rule's scope
    /// (Disconnect breaks the join and leaves the CSO untouched in the target system;
    /// Delete queues a delete PendingExport). Only applicable when Direction = Export.
    /// </summary>
    public OutboundDeprovisionAction? OutboundDeprovisionAction { get; set; }
}
