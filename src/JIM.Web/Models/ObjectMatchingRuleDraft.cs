// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;

namespace JIM.Web.Models;

/// <summary>
/// An Object Matching Rule being composed in the portal (#2052), edited by <c>ObjectMatchingRuleEditor</c> and turned
/// into a rule by whichever page hosts it: the Connected System's Matching tab saves it at once (simple mode), a
/// Synchronisation Rule's adds it to the rule it saves later (advanced mode).
/// </summary>
/// <remarks>
/// Both kinds of source are held at once so switching between them in the editor loses neither; only the chosen kind
/// reaches the rule.
/// </remarks>
public sealed class ObjectMatchingRuleDraft
{
    public ObjectMatchingRuleSourceKind SourceKind { get; set; } = ObjectMatchingRuleSourceKind.Attribute;

    public ConnectedSystemObjectTypeAttribute? SourceAttribute { get; set; }

    public string? Expression { get; set; }

    public MetaverseAttribute? TargetAttribute { get; set; }

    /// <summary>The Metaverse Object Type searched; asked for in simple mode only, where no Synchronisation Rule supplies it.</summary>
    public MetaverseObjectType? MetaverseObjectType { get; set; }

    public bool CaseSensitive { get; set; } = ObjectMatchingDefaults.CaseSensitive;

    /// <summary>
    /// Why the rule as composed could never compare anything, as saving it would say (#2052); null when it can, and
    /// while it is still incomplete. Kept current by the editor as the draft changes.
    /// </summary>
    public string? Problem { get; set; }

    /// <summary>Whether a source of the chosen kind is given.</summary>
    public bool HasSource => SourceKind == ObjectMatchingRuleSourceKind.Expression
        ? !string.IsNullOrWhiteSpace(Expression)
        : SourceAttribute != null;

    /// <summary>Whether the draft names everything the rule compares: a source and a Metaverse attribute.</summary>
    public bool IsComplete => HasSource && TargetAttribute != null;

    /// <summary>
    /// Whether the draft describes a rule that could be saved: complete, workable, and in simple mode naming the
    /// Metaverse Object Type to search (a rule missing it is stored happily and never matches, #1458).
    /// </summary>
    public bool CanAdd(bool requireMetaverseObjectType) =>
        IsComplete && Problem == null && (!requireMetaverseObjectType || MetaverseObjectType != null);

    /// <summary>The source of the chosen kind, and only that kind.</summary>
    public ObjectMatchingRuleSource ToSource() => SourceKind == ObjectMatchingRuleSourceKind.Expression
        ? new ObjectMatchingRuleSource { Order = 0, Expression = Expression }
        : new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttributeId = SourceAttribute?.Id, ConnectedSystemAttribute = SourceAttribute };

    /// <summary>
    /// The rule the draft describes, at <paramref name="order"/>, with no owner: the host sets the Connected System
    /// Object Type (simple mode) or adds it to a Synchronisation Rule (advanced mode).
    /// </summary>
    public ObjectMatchingRule ToRule(int order) => new()
    {
        Order = order,
        MetaverseObjectTypeId = MetaverseObjectType?.Id,
        MetaverseObjectType = MetaverseObjectType,
        TargetMetaverseAttributeId = TargetAttribute?.Id,
        TargetMetaverseAttribute = TargetAttribute,
        CaseSensitive = CaseSensitive,
        Sources = [ToSource()]
    };

    /// <summary>The source as a Configuration Change Preview proposes it.</summary>
    public ObjectMatchingRuleSourceProposal ToSourceProposal() => SourceKind == ObjectMatchingRuleSourceKind.Expression
        ? new ObjectMatchingRuleSourceProposal(0, null, Expression)
        : new ObjectMatchingRuleSourceProposal(0, SourceAttribute?.Id);

    /// <summary>Clears the draft for the next rule.</summary>
    public void Reset()
    {
        SourceKind = ObjectMatchingRuleSourceKind.Attribute;
        SourceAttribute = null;
        Expression = null;
        TargetAttribute = null;
        MetaverseObjectType = null;
        CaseSensitive = ObjectMatchingDefaults.CaseSensitive;
        Problem = null;
    }
}
