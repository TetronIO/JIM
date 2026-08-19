// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Models.Preview;

/// <summary>
/// The Object Matching configuration an administrator is proposing for a Connected System (#1457): which Metaverse
/// Object each of its unjoined objects would join to.
///
/// The proposal covers both places matching rules live and the switch between them, because they are one decision.
/// In Simple mode the rules belong to a Connected System Object Type and serve every Synchronisation Rule of that
/// type; in Advanced mode each Synchronisation Rule carries its own. Flipping
/// <see cref="ConnectedSystem.ObjectMatchingRuleMode"/> changes which set applies without editing a single rule, so
/// a proposal that carried only rules could not describe it.
/// </summary>
/// <remarks>
/// A dedicated shape rather than the entity graph, for the reasons every proposal has one: a proposal may be
/// evaluated in JIM.Worker, so it has to survive a JSON round trip, and <see cref="ObjectMatchingRule"/> carries
/// whole attribute entities and a backlink to its parent. Whatever materialises a proposal back into rules must
/// resolve the ids into entities, because the matching query reads the attribute entities themselves.
/// </remarks>
/// <param name="Mode">Which set of rules applies: the object type's (Simple) or each rule's own (Advanced).</param>
/// <param name="Rules">
/// Every proposed rule across the Connected System, each naming its own parent. Held flat rather than grouped by
/// parent so that a rule moving between parents is one list, not a diff of two.
/// </param>
public record ObjectMatchingProposal(
    ObjectMatchingRuleMode Mode,
    IReadOnlyList<ObjectMatchingRuleProposal> Rules)
{
    /// <summary>
    /// The matching configuration currently in force on <paramref name="connectedSystem"/>, as a proposal. What
    /// "no change" looks like, and the baseline an adapter evaluates a proposal against.
    /// </summary>
    /// <param name="connectedSystem">The Connected System whose mode is read.</param>
    /// <param name="objectTypes">Its object types, carrying the Simple mode rules.</param>
    /// <param name="syncRules">Its Synchronisation Rules, carrying the Advanced mode rules.</param>
    public static ObjectMatchingProposal FromCurrentConfiguration(
        ConnectedSystem connectedSystem,
        IReadOnlyCollection<ConnectedSystemObjectType> objectTypes,
        IReadOnlyCollection<SyncRule> syncRules)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);
        ArgumentNullException.ThrowIfNull(objectTypes);
        ArgumentNullException.ThrowIfNull(syncRules);

        // Both sets are carried regardless of the mode in force. A preview of a mode switch has to state what the
        // rules being switched to would do, and they are only readable if they were loaded.
        var rules = objectTypes
            .SelectMany(objectType => objectType.ObjectMatchingRules ?? [])
            .Concat(syncRules.SelectMany(syncRule => syncRule.ObjectMatchingRules))
            .Select(ObjectMatchingRuleProposal.FromRule)
            .ToList();

        return new ObjectMatchingProposal(connectedSystem.ObjectMatchingRuleMode, rules);
    }

    /// <summary>
    /// The rules that would be evaluated for an object of <paramref name="connectedSystemObjectTypeId"/>, in
    /// evaluation order: the object type's rules in Simple mode, the named Synchronisation Rule's in Advanced.
    /// </summary>
    /// <remarks>
    /// Ordered rather than merely filtered because matching stops at the first rule that matches. A caller that
    /// evaluated them in list order would answer a question about a configuration nobody has.
    /// </remarks>
    public IEnumerable<ObjectMatchingRuleProposal> RulesFor(int? connectedSystemObjectTypeId, int? syncRuleId) =>
        (Mode == ObjectMatchingRuleMode.ConnectedSystem
            ? Rules.Where(rule => rule.ConnectedSystemObjectTypeId == connectedSystemObjectTypeId)
            : Rules.Where(rule => syncRuleId != null && rule.SyncRuleId == syncRuleId))
        .OrderBy(rule => rule.Order);

    /// <summary>
    /// Whether <paramref name="other"/> proposes the same matching as this one. What decides whether a preview an
    /// administrator is looking at still answers the question they are about to ask.
    /// </summary>
    /// <remarks>
    /// Not the record's own equality: the nested lists compare by reference, so an editor rebuilding its proposal
    /// on every render would report a change that never happened. Order-SENSITIVE at both levels, unlike the
    /// Scoping Criteria proposal: rules are evaluated in ascending order until one matches, and a rule's sources
    /// likewise, so moving one above another changes which Metaverse Object an account joins to.
    /// </remarks>
    public bool DescribesSameMatchingAs(ObjectMatchingProposal? other) =>
        other is not null && CanonicalKey() == other.CanonicalKey();

    private string CanonicalKey() =>
        $"{Mode}|{string.Join("|", Rules.OrderBy(rule => rule.Order).ThenBy(rule => rule.CanonicalKey(), StringComparer.Ordinal).Select(rule => rule.CanonicalKey()))}";
}

/// <summary>
/// One proposed Object Matching Rule: where it lives, what it searches, and what it compares.
/// </summary>
/// <param name="Order">Where this rule sits in the cascade; rules are evaluated in ascending order until one matches.</param>
/// <param name="ConnectedSystemObjectTypeId">The Connected System Object Type owning this rule in Simple mode.</param>
/// <param name="SyncRuleId">The Synchronisation Rule owning this rule in Advanced mode.</param>
/// <param name="MetaverseObjectTypeId">
/// The Metaverse Object Type searched. Required in Simple mode; in Advanced mode the owning Synchronisation Rule's
/// own type is used, and a rule with neither matches nothing at all.
/// </param>
/// <param name="TargetMetaverseAttributeId">The Metaverse Attribute the source values are compared against.</param>
/// <param name="CaseSensitive">Whether text comparison respects case.</param>
/// <param name="Sources">What supplies the value to match on, in evaluation order.</param>
public record ObjectMatchingRuleProposal(
    int Order,
    int? ConnectedSystemObjectTypeId,
    int? SyncRuleId,
    int? MetaverseObjectTypeId,
    int? TargetMetaverseAttributeId,
    bool CaseSensitive,
    IReadOnlyList<ObjectMatchingRuleSourceProposal> Sources)
{
    /// <summary>
    /// This rule as it currently stands, or as the editor has just built it.
    /// </summary>
    public static ObjectMatchingRuleProposal FromRule(ObjectMatchingRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        // Each id falls back to its navigation property's id: the editor builds an UNSAVED rule when an
        // administrator adds one, so the navigation is set and the foreign key stays unassigned until the
        // Connected System is saved. Reading the key alone made a rule the editor plainly shows read as naming no
        // attribute, which the preview reports as a blocking finding (#1450).
        return new ObjectMatchingRuleProposal(
            rule.Order,
            rule.ConnectedSystemObjectTypeId ?? rule.ConnectedSystemObjectType?.Id,
            rule.SyncRuleId ?? rule.SyncRule?.Id,
            rule.MetaverseObjectTypeId ?? rule.MetaverseObjectType?.Id,
            rule.TargetMetaverseAttributeId ?? rule.TargetMetaverseAttribute?.Id,
            rule.CaseSensitive,
            [.. rule.Sources.OrderBy(source => source.Order).Select(ObjectMatchingRuleSourceProposal.FromSource)]);
    }

    internal string CanonicalKey() =>
        string.Create(CultureInfo.InvariantCulture,
            $"o={Order};cst={ConnectedSystemObjectTypeId};sr={SyncRuleId};mvt={MetaverseObjectTypeId};" +
            $"tgt={TargetMetaverseAttributeId};cse={CaseSensitive};" +
            $"src=[{string.Join(",", Sources.Select(source => source.CanonicalKey()))}]");
}

/// <summary>
/// One proposed source of the value a rule matches on: a Connected System attribute, or an expression.
/// </summary>
/// <param name="Order">Where this source sits; sources are evaluated in ascending order.</param>
/// <param name="ConnectedSystemAttributeId">The Connected System attribute read.</param>
/// <param name="Expression">
/// An expression producing the value instead. Carried because the model allows it, and refused by the preview's
/// validation: the matching query reads attributes only, and an expression source fails at match time (#207).
/// </param>
public record ObjectMatchingRuleSourceProposal(
    int Order,
    int? ConnectedSystemAttributeId,
    string? Expression = null)
{
    /// <summary>
    /// This source as it currently stands, or as the editor has just built it.
    /// </summary>
    public static ObjectMatchingRuleSourceProposal FromSource(ObjectMatchingRuleSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new ObjectMatchingRuleSourceProposal(
            source.Order,
            source.ConnectedSystemAttributeId ?? source.ConnectedSystemAttribute?.Id,
            source.Expression);
    }

    internal string CanonicalKey() =>
        string.Create(CultureInfo.InvariantCulture, $"o={Order};cs={ConnectedSystemAttributeId};e={Expression}");
}
