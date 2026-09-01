// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
namespace JIM.Models.Logic;

/// <summary>
/// Defines how objects should be matched/correlated between a Connected System and the Metaverse.
/// Object Matching Rules are used during import (to join CSOs to MVOs) and during export evaluation
/// (to find existing CSOs for provisioning).
///
/// Rules can belong to EITHER:
/// - A ConnectedSystemObjectType (Mode A - default): Used for all Synchronisation Rules of that object type
/// - A SyncRule (Mode B - advanced): Used only for that specific Synchronisation Rule
///
/// Multiple rules can be defined with different Order values for cascading/fallback matching.
/// </summary>
public class ObjectMatchingRule : IAuditable
{
    public int Id { get; set; }

    /// <summary>
    /// The order in which this rule should be evaluated relative to other rules.
    /// Rules are evaluated in ascending order (0, 1, 2, etc.) until a match is found.
    /// </summary>
    public int Order { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The type of security principal that created this entity.
    /// </summary>
    public ActivityInitiatorType CreatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that created this entity.
    /// Null for system-created (seeded) entities.
    /// </summary>
    public Guid? CreatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of creation.
    /// Retained even if the principal is later deleted.
    /// </summary>
    public string? CreatedByName { get; set; }

    /// <summary>
    /// When the entity was last modified (UTC). Null if never modified after creation.
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The type of security principal that last modified this entity.
    /// </summary>
    public ActivityInitiatorType LastUpdatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that last modified this entity.
    /// </summary>
    public Guid? LastUpdatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of the last modification.
    /// </summary>
    public string? LastUpdatedByName { get; set; }

    /// <summary>
    /// Optional backlink to a SyncRule when this rule is defined at the Synchronisation Rule level (Mode B).
    /// Mutually exclusive with ConnectedSystemObjectType.
    /// </summary>
    public int? SyncRuleId { get; set; }
    public SyncRule? SyncRule { get; set; }

    /// <summary>
    /// Optional backlink to a ConnectedSystemObjectType when this rule is defined at the object type level (Mode A).
    /// Mutually exclusive with SyncRule.
    /// </summary>
    public int? ConnectedSystemObjectTypeId { get; set; }
    public ConnectedSystemObjectType? ConnectedSystemObjectType { get; set; }

    /// <summary>
    /// The Metaverse Object Type to search when evaluating this rule.
    /// Required for simple mode rules (<see cref="Staging.ObjectMatchingRuleMode.ConnectedSystem"/>)
    /// where no Synchronisation Rule provides the MVO type. Null for advanced mode rules where the
    /// Synchronisation Rule's <see cref="SyncRule.MetaverseObjectTypeId"/> is used instead.
    /// </summary>
    public int? MetaverseObjectTypeId { get; set; }
    public MetaverseObjectType? MetaverseObjectType { get; set; }

    /// <summary>
    /// The sources that provide the value(s) to match against. Typically a Connected System attribute
    /// or an expression that transforms attribute values.
    /// </summary>
    public List<ObjectMatchingRuleSource> Sources { get; set; } = new();

    /// <summary>
    /// The Metaverse attribute to match against. The value from Sources will be compared
    /// to this attribute's value on Metaverse Objects to find a match.
    /// </summary>
    public int? TargetMetaverseAttributeId { get; set; }
    public MetaverseAttribute? TargetMetaverseAttribute { get; set; }

    /// <summary>
    /// When true, attribute value comparisons are case-sensitive.
    /// When false (default), comparisons ignore case differences.
    /// </summary>
    public bool CaseSensitive { get; set; } = false;

    /// <summary>
    /// Validates that the rule is correctly configured.
    /// </summary>
    public bool IsValid() => DescribeInvalidity() == null;

    /// <summary>
    /// Why this rule could never match anything, in the terms an administrator would use, or null when it is
    /// workable.
    /// </summary>
    /// <remarks>
    /// The reason rather than a bare bool because the failures here are silent at synchronisation time: the
    /// matching engine skips a rule it cannot evaluate and moves to the next one, so a Connected System whose only
    /// rule is malformed matches nothing and projects a duplicate identity for every account that should have
    /// joined (#1458). Whatever refuses such a rule has to be able to say which field is at fault.
    /// </remarks>
    public string? DescribeInvalidity()
    {
        // Must belong to exactly one parent (SyncRule XOR ConnectedSystemObjectType)
        var hasSyncRule = SyncRuleId.HasValue || SyncRule != null;
        var hasObjectType = ConnectedSystemObjectTypeId.HasValue || ConnectedSystemObjectType != null;

        if (hasSyncRule && hasObjectType)
            return "An Object Matching Rule belongs either to a Connected System Object Type (Simple mode) or to a Synchronisation Rule (Advanced mode), never to both.";

        if (!hasSyncRule && !hasObjectType)
            return "An Object Matching Rule must belong to a Connected System Object Type (Simple mode) or to a Synchronisation Rule (Advanced mode).";

        // Simple mode rules must have MetaverseObjectTypeId set (no Synchronisation Rule to provide MVO type)
        if (hasObjectType && MetaverseObjectTypeId == null && MetaverseObjectType == null)
            return "A Simple mode Object Matching Rule must name the Metaverse Object Type it searches; without one it has nowhere to look and matches nothing.";

        // Advanced mode rules must NOT have MetaverseObjectTypeId (Synchronisation Rule provides MVO type)
        if (hasSyncRule && (MetaverseObjectTypeId != null || MetaverseObjectType != null))
            return "An Advanced mode Object Matching Rule takes the Metaverse Object Type from the Synchronisation Rule that owns it, so it must not name one of its own.";

        // Must have at least one source
        if (Sources.Count == 0)
            return "An Object Matching Rule must have a source attribute, or it has nothing to match on.";

        // Must have a target attribute
        if (TargetMetaverseAttributeId == null && TargetMetaverseAttribute == null)
            return "An Object Matching Rule must name the Metaverse Attribute its source values are compared against.";

        return null;
    }

    /// <summary>
    /// Why a rule of the given scope would never be consulted under the Connected System's active matching mode,
    /// in the terms an administrator would use, or null when scope and mode agree.
    /// </summary>
    /// <remarks>
    /// <see cref="DescribeInvalidity"/> is deliberately mode-blind: it validates the rule's own shape, which this
    /// method cannot know because the active mode lives on the Connected System. The synchronisation engine only
    /// consults the scope the mode names, so a rule of the other scope is silently inert: synchronisation joins
    /// nothing, no error, no warning (#1569). Creation refuses such a rule with this message; rules of the other
    /// scope that already exist (retained by a mode switch for a later switch back) remain editable and deletable.
    /// </remarks>
    /// <param name="activeMode">The owning Connected System's current matching mode.</param>
    /// <param name="ruleIsSyncRuleScoped">True when the rule belongs to a Synchronisation Rule; false when it
    /// belongs to a Connected System Object Type.</param>
    /// <param name="connectedSystemName">The owning Connected System's name, for the message.</param>
    public static string? DescribeScopeMismatch(ObjectMatchingRuleMode activeMode, bool ruleIsSyncRuleScoped, string connectedSystemName)
    {
        if (activeMode == ObjectMatchingRuleMode.ConnectedSystem && ruleIsSyncRuleScoped)
            return $"Connected System '{connectedSystemName}' is in simple matching mode: Object Matching Rules are defined per Connected System Object Type, and a rule scoped to a Synchronisation Rule would never be consulted. Define the rule on the Connected System Object Type instead, or switch the system to advanced matching mode first.";

        if (activeMode == ObjectMatchingRuleMode.SyncRule && !ruleIsSyncRuleScoped)
            return $"Connected System '{connectedSystemName}' is in advanced matching mode: Object Matching Rules are defined per Synchronisation Rule, and a rule scoped to a Connected System Object Type would never be consulted. Define the rule on the Synchronisation Rule instead, or switch the system to simple matching mode first.";

        return null;
    }

    /// <summary>
    /// Helper method to provide a description for the user on what type of source configuration this is.
    /// </summary>
    public SyncRuleMappingSourcesType GetSourceType()
    {
        if (Sources.Count == 0)
            return SyncRuleMappingSourcesType.NotSet;

        if (Sources.All(s => s.ConnectedSystemAttribute != null))
            return SyncRuleMappingSourcesType.AttributeMapping;

        if (Sources.All(s => !string.IsNullOrWhiteSpace(s.Expression)))
            return SyncRuleMappingSourcesType.ExpressionMapping;

        return SyncRuleMappingSourcesType.AdvancedMapping;
    }
}
