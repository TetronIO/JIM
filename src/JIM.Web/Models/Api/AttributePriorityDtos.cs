// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Logic;

namespace JIM.Web.Models.Api;

/// <summary>
/// A Metaverse Attribute's priority order on one Metaverse Object Type: the Attribute Flows that contribute its
/// value, highest priority first.
/// </summary>
public class AttributePriorityOrderDto
{
    /// <summary>
    /// The unique identifier of the Object Type the order applies to.
    /// </summary>
    public int MetaverseObjectTypeId { get; set; }

    /// <summary>
    /// The unique identifier of the attribute.
    /// </summary>
    public int MetaverseAttributeId { get; set; }

    /// <summary>
    /// The attribute's name; null when nothing contributes to it.
    /// </summary>
    public string? MetaverseAttributeName { get; set; }

    /// <summary>
    /// The Attribute Flows that contribute a value to the attribute, highest priority first; empty when none does.
    /// </summary>
    public List<AttributePriorityContributorDto> Contributors { get; set; } = new();

    public static AttributePriorityOrderDto FromEntities(int metaverseObjectTypeId, int metaverseAttributeId, IEnumerable<SyncRuleMapping> orderedMappings)
    {
        var contributors = orderedMappings.Select(AttributePriorityContributorDto.FromEntity).ToList();
        return new AttributePriorityOrderDto
        {
            MetaverseObjectTypeId = metaverseObjectTypeId,
            MetaverseAttributeId = metaverseAttributeId,
            MetaverseAttributeName = contributors.Count > 0 ? orderedMappings.First().TargetMetaverseAttribute?.Name : null,
            Contributors = contributors
        };
    }
}

/// <summary>
/// One Attribute Flow in an attribute's priority order.
/// </summary>
public class AttributePriorityContributorDto
{
    /// <summary>
    /// The unique identifier of the Attribute Flow mapping.
    /// </summary>
    public int MappingId { get; set; }

    /// <summary>
    /// The Attribute Flow's position in the order, 1 being the highest priority.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Whether a source with no value asserts that the attribute has none, rather than deferring to the next
    /// source.
    /// </summary>
    public bool NullIsValue { get; set; }

    /// <summary>
    /// The unique identifier of the Synchronisation Rule the Attribute Flow belongs to.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// The Synchronisation Rule's name.
    /// </summary>
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// Whether the contributing Synchronisation Rule is enabled. Disabled rules remain in the list (they hold
    /// position) but never contribute during resolution.
    /// </summary>
    public bool SyncRuleEnabled { get; set; }

    /// <summary>
    /// The unique identifier of the Synchronisation Rule's Connected System.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// The Connected System's name.
    /// </summary>
    public string? ConnectedSystemName { get; set; }

    public static AttributePriorityContributorDto FromEntity(SyncRuleMapping entity)
    {
        return new AttributePriorityContributorDto
        {
            MappingId = entity.Id,
            Priority = entity.Priority,
            NullIsValue = entity.NullIsValue,
            SyncRuleId = entity.SyncRule?.Id,
            SyncRuleName = entity.SyncRule?.Name,
            SyncRuleEnabled = entity.SyncRule?.Enabled ?? false,
            ConnectedSystemId = entity.SyncRule?.ConnectedSystem?.Id,
            ConnectedSystemName = entity.SyncRule?.ConnectedSystem?.Name
        };
    }
}

/// <summary>
/// Request DTO for setting an attribute's priority order (#91). The contributors must list every current
/// contributing mapping for the attribute exactly once, in the desired priority order (highest first); the
/// server renumbers all of them transactionally.
/// </summary>
public class SetAttributePriorityOrderRequest
{
    [Required]
    public List<SetAttributePriorityContributorRequest> Contributors { get; set; } = new();
}

/// <summary>
/// A single entry in a <see cref="SetAttributePriorityOrderRequest"/>: the mapping and its "Null is a value" flag.
/// The mapping's priority is derived from its position in the list (first = priority 1).
/// </summary>
public class SetAttributePriorityContributorRequest
{
    public int MappingId { get; set; }
    public bool NullIsValue { get; set; }
}

/// <summary>
/// Request DTO for moving a single contributing mapping to a new priority position (#91). The server shuffles the
/// other contributors to keep the list contiguous and renumbers everything, so the caller need only state the new
/// position; it never has to renumber siblings itself.
/// </summary>
public class MoveAttributePriorityRequest
{
    /// <summary>
    /// The desired 1-based priority position for the mapping (1 = highest priority). Clamped to the valid range
    /// for the attribute's contributor list.
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// When supplied, also sets the moved mapping's "Null is a value" flag as part of the same transaction. When
    /// omitted, the flag is left unchanged.
    /// </summary>
    public bool? NullIsValue { get; set; }
}
