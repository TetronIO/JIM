// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// A Metaverse Object Type in full.
/// </summary>
public class MetaverseObjectTypeDetailDto
{
    /// <summary>
    /// The unique identifier of the Object Type.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The Object Type's singular name, such as User or Group.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The Object Type's plural name, such as Users or Groups.
    /// </summary>
    public string PluralName { get; set; } = null!;

    /// <summary>
    /// When the Object Type was created (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// Whether JIM provides the Object Type. A built-in Object Type cannot be renamed or deleted, but its deletion
    /// settings can be changed.
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>
    /// The name of the MudBlazor icon the portal shows for the Object Type; null when none is set.
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// When JIM deletes this type's Metaverse Objects on its own.
    /// </summary>
    public MetaverseObjectDeletionRule DeletionRule { get; set; }

    /// <summary>
    /// How long JIM waits, once the deletion rule is met, before it deletes an object; null or zero deletes at
    /// once.
    /// </summary>
    public TimeSpan? DeletionGracePeriod { get; set; }

    /// <summary>
    /// For the WhenAuthoritativeSourceDisconnected deletion rule, whether every selected source must disconnect
    /// before an object is deleted (AllSourcesDisconnect) or any one is enough (SpecificSourcesDisconnect). It has
    /// no effect under the other rules.
    /// </summary>
    public AuthoritativeSourceTriggerMode DeletionTriggerMode { get; set; }

    /// <summary>
    /// The IDs of the Connected Systems whose disconnection triggers deletion under the
    /// WhenAuthoritativeSourceDisconnected rule.
    /// </summary>
    public List<int> DeletionTriggerConnectedSystemIds { get; set; } = new();

    /// <summary>
    /// The attributes bound to the Object Type. Listed only when the request includes child objects, so empty in
    /// the result of creating or changing an Object Type.
    /// </summary>
    public List<MetaverseAttributeSummaryDto> Attributes { get; set; } = new();

    /// <summary>
    /// A warning about the deletion rule, or null when there is none. It is set when the rule is
    /// WhenLastConnectorDisconnected and enabled export Synchronisation Rules provision objects of this type: a
    /// provisioned account counts as a connector, so an object outlives its last source and keeps that source's
    /// values. Use WhenAuthoritativeSourceDisconnected if objects should be deprovisioned when their source of
    /// record leaves.
    /// </summary>
    public string? DeletionRuleAdvisory { get; set; }

    /// <summary>
    /// The Connected Systems that project into this type without being one of its selected authoritative sources,
    /// filled in only under the WhenAuthoritativeSourceDisconnected rule. Objects such a system creates that no
    /// selected source also holds are never deleted automatically, and objects it shares with a selected source are
    /// deleted when that source disconnects. Systems that only join are never listed. Always present, and empty
    /// when there is nothing to warn about.
    /// </summary>
    public List<DeletionSourceGap> DeletionSourceWarnings { get; set; } = new();

    /// <summary>
    /// Creates a detailed DTO from a MetaverseObjectType entity.
    /// </summary>
    public static MetaverseObjectTypeDetailDto FromEntity(MetaverseObjectType entity)
    {
        return new MetaverseObjectTypeDetailDto
        {
            Id = entity.Id,
            Name = entity.Name,
            PluralName = entity.PluralName,
            Created = entity.Created,
            BuiltIn = entity.BuiltIn,
            Icon = entity.Icon,
            DeletionRule = entity.DeletionRule,
            DeletionGracePeriod = entity.DeletionGracePeriod,
            DeletionTriggerMode = entity.DeletionTriggerMode,
            DeletionTriggerConnectedSystemIds = entity.DeletionTriggerConnectedSystemIds ?? new(),
            Attributes = entity.Attributes?
                .Select(MetaverseAttributeSummaryDto.FromEntity)
                .ToList() ?? new()
        };
    }
}

/// <summary>
/// Request DTO for creating a new MetaverseObjectType.
/// </summary>
public class CreateMetaverseObjectTypeRequest
{
    /// <summary>
    /// The singular name of the object type (e.g., "User"). Must be unique.
    /// </summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = null!;

    /// <summary>
    /// The plural name of the object type (e.g., "Users"). Must be unique.
    /// </summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string PluralName { get; set; } = null!;

    /// <summary>
    /// Optional MudBlazor icon name to associate with the object type in the UI.
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Optional list of existing Metaverse Attribute IDs to associate with this object type.
    /// Attributes can also be associated later via PUT /metaverse/attributes/{id}.
    /// </summary>
    public List<int>? AttributeIds { get; set; }

    /// <summary>
    /// Determines when Metaverse Objects of this type should be automatically deleted.
    /// Defaults to Manual when omitted.
    /// </summary>
    public MetaverseObjectDeletionRule? DeletionRule { get; set; }

    /// <summary>
    /// Optional grace period before deletion is executed.
    /// </summary>
    public TimeSpan? DeletionGracePeriod { get; set; }

    /// <summary>
    /// For the WhenAuthoritativeSourceDisconnected deletion rule: whether any one selected authoritative
    /// source disconnecting triggers deletion (SpecificSourcesDisconnect), or every selected source must
    /// disconnect first (AllSourcesDisconnect). Omitted or null defaults to AllSourcesDisconnect, the safe
    /// mode for new configurations. Supply the enum member name as a string.
    /// </summary>
    public AuthoritativeSourceTriggerMode? DeletionTriggerMode { get; set; }

    /// <summary>
    /// List of Connected System IDs that are authoritative sources for deletion.
    /// Required when DeletionRule is WhenAuthoritativeSourceDisconnected.
    /// </summary>
    public List<int>? DeletionTriggerConnectedSystemIds { get; set; }

    /// <summary>
    /// Optional reason for the change, recorded on the audit Activity and configuration change history.
    /// </summary>
    public string? ChangeReason { get; set; }
}

/// <summary>
/// Request DTO for updating a MetaverseObjectType's identity (name, plural name, icon) and deletion rules. Every field
/// is optional: a field that is omitted (or JSON null) leaves the stored value unchanged. Built-in types accept
/// deletion-rule changes but reject Name, PluralName and Icon changes.
/// </summary>
public class UpdateMetaverseObjectTypeRequest
{
    /// <summary>
    /// The singular name of the object type (e.g. "Device"). Omitted or null leaves it unchanged. Must be unique
    /// (compared case-insensitively). Cannot be changed on a built-in type.
    /// </summary>
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; set; }

    /// <summary>
    /// The plural name of the object type (e.g. "Devices"). Omitted or null leaves it unchanged. Must be unique
    /// (compared case-insensitively). Cannot be changed on a built-in type.
    /// </summary>
    [StringLength(200, MinimumLength = 1)]
    public string? PluralName { get; set; }

    /// <summary>
    /// The MudBlazor icon name shown for the type in the UI. Omitted or null leaves it unchanged; an empty or
    /// whitespace-only string clears it. Cannot be changed on a built-in type.
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Determines when Metaverse Objects of this type should be automatically deleted.
    /// </summary>
    public MetaverseObjectDeletionRule? DeletionRule { get; set; }

    /// <summary>
    /// Optional grace period before deletion is executed, as an ISO 8601 duration string (e.g., "00:01:00" for 1 minute, "1.00:00:00" for 1 day).
    /// Set to "00:00:00", TimeSpan.Zero, or null for immediate deletion when conditions are met.
    /// </summary>
    public TimeSpan? DeletionGracePeriod { get; set; }

    /// <summary>
    /// For the WhenAuthoritativeSourceDisconnected deletion rule: whether any one selected authoritative
    /// source disconnecting triggers deletion (SpecificSourcesDisconnect), or every selected source must
    /// disconnect first (AllSourcesDisconnect). Omitted or null leaves the stored mode unchanged. Supply
    /// the enum member name as a string.
    /// </summary>
    public AuthoritativeSourceTriggerMode? DeletionTriggerMode { get; set; }

    /// <summary>
    /// List of Connected System IDs that are authoritative sources for deletion.
    /// Required when DeletionRule is WhenAuthoritativeSourceDisconnected.
    /// How they trigger deletion is governed by DeletionTriggerMode.
    /// Ignored when DeletionRule is Manual or WhenLastConnectorDisconnected.
    /// </summary>
    public List<int>? DeletionTriggerConnectedSystemIds { get; set; }

    /// <summary>
    /// Optional reason for the change, recorded on the audit Activity and configuration change history.
    /// </summary>
    public string? ChangeReason { get; set; }

    /// <summary>
    /// The Configuration Change Preview read before making this change, if any (#827). Supply the Activity id
    /// returned by <c>POST object-types/{id}/deletion-settings/preview</c> and the update's own Activity records
    /// the link, so the audit answers not only what changed but what the caller was told it would do.
    /// Optional: a preview is an affordance, not a precondition.
    /// </summary>
    public Guid? PreviewActivityId { get; set; }
}

/// <summary>
/// Response DTO reporting whether a candidate Metaverse Object Type name and/or plural name are available (compared
/// case-insensitively). Backs the real-time create/edit dialog validator. A field is null when the caller did not ask
/// about it.
/// </summary>
public class MetaverseObjectTypeNameAvailabilityDto
{
    public string? Name { get; set; }
    public bool? NameAvailable { get; set; }
    public string? PluralName { get; set; }
    public bool? PluralNameAvailable { get; set; }
}

/// <summary>
/// A Metaverse Attribute bound to an Object Type.
/// </summary>
public class MetaverseAttributeSummaryDto
{
    /// <summary>
    /// The unique identifier of the attribute.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The attribute's name.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The kind of value the attribute holds.
    /// </summary>
    public AttributeDataType Type { get; set; }

    /// <summary>
    /// Whether the attribute holds one value or many.
    /// </summary>
    public AttributePlurality AttributePlurality { get; set; }

    /// <summary>
    /// Whether JIM provides the attribute.
    /// </summary>
    public bool BuiltIn { get; set; }

    public static MetaverseAttributeSummaryDto FromEntity(MetaverseAttribute entity)
    {
        return new MetaverseAttributeSummaryDto
        {
            Id = entity.Id,
            Name = entity.Name,
            Type = entity.Type,
            AttributePlurality = entity.AttributePlurality,
            BuiltIn = entity.BuiltIn
        };
    }
}
