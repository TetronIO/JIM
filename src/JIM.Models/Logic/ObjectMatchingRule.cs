// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
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
    public bool CaseSensitive { get; set; } = ObjectMatchingDefaults.CaseSensitive;

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

        return DescribeComparisonInvalidity();
    }

    /// <summary>
    /// What this rule compares, for example <c>Employee ID equals employeeId</c>, or the expression in place of the
    /// attribute name when the rule computes its value.
    /// </summary>
    /// <remarks>
    /// Used where a rule has to be named after the fact: the candidates of an ambiguous match, and the Joined outcome
    /// a matching join records (#2052). An attribute that has not been loaded is named by its id, so the description
    /// never leaves a side blank.
    /// </remarks>
    public string DescribeComparison()
    {
        var source = Sources.OrderBy(s => s.Order).FirstOrDefault();
        var sourceText = string.IsNullOrWhiteSpace(source?.Expression)
            ? source?.ConnectedSystemAttribute?.Name ?? source?.ConnectedSystemAttributeId?.ToString(CultureInfo.InvariantCulture)
            : source.Expression;
        var target = TargetMetaverseAttribute?.Name ?? TargetMetaverseAttributeId?.ToString(CultureInfo.InvariantCulture);
        return $"{target} equals {sourceText}";
    }

    /// <summary>
    /// Why what this rule compares could never be worked out, in the terms an administrator would use, or null when
    /// its shape is sound: exactly one source, holding exactly one of a Connected System attribute or an expression,
    /// and a Metaverse attribute to compare it with.
    /// </summary>
    /// <remarks>
    /// The part of <see cref="DescribeInvalidity"/> that does not depend on who owns the rule, so a rule can be
    /// checked before it has an owner: one added to a Synchronisation Rule in the portal has none until the
    /// Synchronisation Rule is saved. The matching engine fails a rule of any other shape for every object it meets
    /// (#2052), so the shape is refused when the rule is saved. Checking the types involved needs the Connected
    /// System Object Type and the Metaverse attribute, which this model may not have loaded; the application layer's
    /// validator does that.
    /// </remarks>
    public string? DescribeComparisonInvalidity()
    {
        if (Sources.Count == 0)
            return "An Object Matching Rule must have a source, a Connected System attribute or an expression, or it has nothing to match on.";

        if (Sources.Count > 1)
            return $"An Object Matching Rule takes exactly one source, a Connected System attribute or an expression; this one has {Sources.Count}. " +
                   "To match on a combination of attributes, write an expression that combines them.";

        var source = Sources[0];
        var hasAttribute = source.ConnectedSystemAttributeId.HasValue || source.ConnectedSystemAttribute != null;
        var hasExpression = !string.IsNullOrWhiteSpace(source.Expression);

        if (hasAttribute && hasExpression)
            return "An Object Matching Rule's source is either a Connected System attribute or an expression, not both.";

        if (!hasAttribute && !hasExpression)
            return "An Object Matching Rule's source names neither a Connected System attribute nor an expression, so the rule has nothing to match on.";

        if (TargetMetaverseAttributeId == null && TargetMetaverseAttribute == null)
            return "An Object Matching Rule must name the Metaverse Attribute its source values are compared against.";

        return null;
    }

    /// <summary>
    /// The data types an Object Matching Rule can compare: those an equality lookup over the Metaverse attribute value
    /// indexes covers. Matching on anything else (a date, a flag, a reference, raw bytes) cannot find an object.
    /// </summary>
    public static IReadOnlySet<AttributeDataType> MatchableTypes { get; } = new HashSet<AttributeDataType>
    {
        AttributeDataType.Text,
        AttributeDataType.Number,
        AttributeDataType.LongNumber,
        AttributeDataType.Decimal,
        AttributeDataType.Guid
    };

    /// <summary>
    /// Whether an Object Matching Rule can compare values of <paramref name="type"/>; see <see cref="MatchableTypes"/>.
    /// </summary>
    public static bool IsMatchableType(AttributeDataType type) => MatchableTypes.Contains(type);

    /// <summary>
    /// The matchable types as an administrator reads them, for messages that say what to choose instead.
    /// </summary>
    public const string MatchableTypesDescription = "Text, Number, LongNumber, Decimal or Guid";

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
