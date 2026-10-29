// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Staging.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// An Object Matching Rule's source: the Connected System attribute or expression whose value is matched.
/// </summary>
public class ObjectMatchingRuleSourceDto
{
    /// <summary>
    /// The ID of the source.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The source's position in the rule. A rule has exactly one source.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// The ID of the Connected System attribute whose values the rule compares, or null when it compares an
    /// expression.
    /// </summary>
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// The name of that Connected System attribute, or null when the rule compares an expression.
    /// </summary>
    public string? ConnectedSystemAttributeName { get; set; }

    /// <summary>
    /// The expression whose result the rule compares, or null when it compares a Connected System attribute.
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// Creates a DTO from an ObjectMatchingRuleSource entity.
    /// </summary>
    public static ObjectMatchingRuleSourceDto FromEntity(ObjectMatchingRuleSource source)
    {
        return new ObjectMatchingRuleSourceDto
        {
            Id = source.Id,
            Order = source.Order,
            ConnectedSystemAttributeId = source.ConnectedSystemAttributeId,
            ConnectedSystemAttributeName = source.ConnectedSystemAttribute?.Name,
            Expression = source.Expression
        };
    }
}

/// <summary>
/// An Object Matching Rule: a source on the Connected System Object matched against a Metaverse attribute.
/// </summary>
public class ObjectMatchingRuleDto
{
    /// <summary>
    /// The ID of the rule.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Where the rule comes in the order JIM tries the rules, lowest first.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// The ID of the Connected System Object Type the rule is on, for a simple mode rule; 0 for an advanced mode
    /// rule, which is on a Synchronisation Rule instead.
    /// </summary>
    public int ConnectedSystemObjectTypeId { get; set; }

    /// <summary>
    /// The name of the Connected System Object Type the rule is on; null for an advanced mode rule.
    /// </summary>
    public string? ConnectedSystemObjectTypeName { get; set; }

    /// <summary>
    /// The ID of the Metaverse Object Type whose objects a simple mode rule searches; null for an advanced mode
    /// rule, which searches its Synchronisation Rule's Metaverse Object Type.
    /// </summary>
    public int? MetaverseObjectTypeId { get; set; }

    /// <summary>
    /// The name of that Metaverse Object Type; null for an advanced mode rule.
    /// </summary>
    public string? MetaverseObjectTypeName { get; set; }

    /// <summary>
    /// The ID of the Synchronisation Rule an advanced mode rule is on; null for a simple mode rule.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// The ID of the Metaverse attribute the source is matched against.
    /// </summary>
    public int? TargetMetaverseAttributeId { get; set; }

    /// <summary>
    /// The name of the Metaverse attribute the source is matched against.
    /// </summary>
    public string? TargetMetaverseAttributeName { get; set; }

    /// <summary>
    /// The rule's source: one entry, naming a Connected System attribute or an expression.
    /// </summary>
    public List<ObjectMatchingRuleSourceDto> Sources { get; set; } = new();

    /// <summary>
    /// Whether the comparison respects case; false ignores it.
    /// </summary>
    public bool CaseSensitive { get; set; }

    /// <summary>
    /// When the rule was created.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// On the response to a create or update of the rule: the Connected System Object Types the save recorded as awaiting
    /// synchronisation. For each, the Connected System's next synchronisation, Delta or Full, processes every object of the
    /// type, not only the changed ones; the entry gives how many objects that is and about how long it takes at the speed
    /// of the Connected System's last Full Synchronisation (empty when it has never completed one). Empty on a read, and
    /// when the save recorded nothing.
    /// </summary>
    public List<AwaitingSynchronisationEstimate> AwaitingSynchronisation { get; set; } = new();

    /// <summary>
    /// Creates a DTO from an ObjectMatchingRule entity.
    /// </summary>
    public static ObjectMatchingRuleDto FromEntity(ObjectMatchingRule rule)
    {
        return new ObjectMatchingRuleDto
        {
            Id = rule.Id,
            Order = rule.Order,
            ConnectedSystemObjectTypeId = rule.ConnectedSystemObjectTypeId ?? 0,
            ConnectedSystemObjectTypeName = rule.ConnectedSystemObjectType?.Name,
            MetaverseObjectTypeId = rule.MetaverseObjectTypeId,
            MetaverseObjectTypeName = rule.MetaverseObjectType?.Name,
            SyncRuleId = rule.SyncRuleId,
            TargetMetaverseAttributeId = rule.TargetMetaverseAttributeId,
            TargetMetaverseAttributeName = rule.TargetMetaverseAttribute?.Name,
            Sources = rule.Sources.Select(ObjectMatchingRuleSourceDto.FromEntity).ToList(),
            CaseSensitive = rule.CaseSensitive,
            Created = rule.Created
        };
    }
}
