// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Logic;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of an attribute's priority list (#91): the ordered import contributions to a single
/// Metaverse attribute for a single Metaverse Object Type. Contributors are returned highest priority first.
/// </summary>
public class AttributePriorityOrderDto
{
    public int MetaverseObjectTypeId { get; set; }
    public int MetaverseAttributeId { get; set; }
    public string? MetaverseAttributeName { get; set; }

    /// <summary>
    /// The contributing import mappings, ordered by priority (highest first). May be empty when the attribute
    /// has no import contributors.
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
/// API representation of a single entry in an attribute's priority list (#91).
/// </summary>
public class AttributePriorityContributorDto
{
    public int MappingId { get; set; }
    public int Priority { get; set; }
    public bool NullIsValue { get; set; }
    public int? SyncRuleId { get; set; }
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// Whether the contributing Synchronisation Rule is enabled. Disabled rules remain in the list (they hold
    /// position) but never contribute during resolution.
    /// </summary>
    public bool SyncRuleEnabled { get; set; }

    public int? ConnectedSystemId { get; set; }
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
