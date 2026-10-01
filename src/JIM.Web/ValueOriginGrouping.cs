// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core.DTOs;

namespace JIM.Web;

/// <summary>
/// Groups a Metaverse Object's attributes by <see cref="ValueOrigin"/> (#399): the shared logic behind the
/// Inspect view's contribution bar, its legend/source filter, and "Group by: Source". One place decides what
/// counts as "the same source" and how a group is labelled, so the bar, the legend and the table's group headers
/// can never disagree about it.
/// </summary>
public static class ValueOriginGrouping
{
    /// <summary>One distinct source among an object's attributes, with the label an administrator reads for it.</summary>
    /// <param name="Key">Stable key identifying this source; the same key groups attributes and drives the filter.</param>
    /// <param name="Kind">Null for the synthetic "several origins" group.</param>
    /// <param name="Label">The text an administrator reads for this source (a contribution bar legend item, a Group by Source header).</param>
    /// <param name="ConnectedSystemId">The contributing Connected System, when the origin has one.</param>
    /// <param name="ConnectedSystemName">The contributing Connected System's name.</param>
    /// <param name="SyncRuleId">The contributing Synchronisation Rule, when one is recorded and not deleted.</param>
    /// <param name="SyncRuleName">The contributing Synchronisation Rule's name.</param>
    /// <param name="SyncRuleDeleted">True when a Connected System is recorded but its contributing rule no longer exists.</param>
    public sealed record Group(
        string Key,
        ValueOriginKind? Kind,
        string Label,
        int? ConnectedSystemId,
        string? ConnectedSystemName,
        int? SyncRuleId,
        string? SyncRuleName,
        bool SyncRuleDeleted);

    /// <summary>The key <see cref="BuildGroups"/> would assign this attribute's origin(s) to.</summary>
    public static string KeyFor(MetaverseAttributeOriginSummary attribute) =>
        attribute.HasSeveralOrigins ? "several" : KeyFor(attribute.Origins[0]);

    /// <summary>
    /// How a Generated Value is named wherever a source is: the Attribute Flow Source Type that produced it, after
    /// the Connected System (and Synchronisation Rule, where shown) it came through.
    /// </summary>
    public const string GeneratedValueLabel = "Generated Value";

    /// <summary>The colour every Generated Value takes, whichever Connected System it came through.</summary>
    public const string GeneratedValueColourToken = "--mud-palette-primary";

    private static string KeyFor(ValueOrigin origin) => origin.Kind switch
    {
        ValueOriginKind.SynchronisationRule => $"sr:{origin.ConnectedSystemId}:{origin.SyncRuleId}:{origin.SyncRuleDeleted}",
        ValueOriginKind.GeneratedValue => $"gen:{origin.ConnectedSystemId}:{origin.SyncRuleId}",
        ValueOriginKind.SetByPerson => $"person:{origin.PersonId}",
        _ => "unrecorded"
    };

    /// <summary>
    /// One group per distinct source among the given attributes (plus one for attributes with several origins),
    /// each with how many attributes belong to it. Ordered by count descending, largest source first; ties keep a
    /// Connected System's groups together.
    /// </summary>
    public static List<(Group Group, int Count)> BuildGroups(IReadOnlyList<MetaverseAttributeOriginSummary> attributes)
    {
        var byKey = attributes
            .GroupBy(KeyFor)
            .Select(g => (Key: g.Key, Count: g.Count(), Sample: g.First()))
            .ToList();

        // A Connected System's segments only need to name the Synchronisation Rule when the same system
        // contributes through more than one of them; otherwise the system name alone already disambiguates it.
        var multiRuleSystems = byKey
            .Where(g => !g.Sample.HasSeveralOrigins &&
                        g.Sample.Origins[0].Kind is ValueOriginKind.SynchronisationRule or ValueOriginKind.GeneratedValue)
            .GroupBy(g => g.Sample.Origins[0].ConnectedSystemId)
            .Where(g => g.Select(x => x.Sample.Origins[0].SyncRuleId).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        var groups = byKey
            .Select(g => (Group: BuildGroup(g.Key, g.Sample, multiRuleSystems), g.Count))
            .ToList();

        // Ties keep a Connected System's groups together (so its Generated Value sits beside it rather than behind
        // an unrelated source of the same size), ranked by the system's largest group, the rule's own values first.
        var largestBySystem = groups
            .Where(g => g.Group.ConnectedSystemId.HasValue)
            .GroupBy(g => g.Group.ConnectedSystemId!.Value)
            .ToDictionary(g => g.Key, g => g.Max(x => x.Count));

        return groups
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => g.Group.ConnectedSystemId is { } systemId ? largestBySystem[systemId] : g.Count)
            .ThenBy(g => g.Group.ConnectedSystemId ?? int.MaxValue)
            .ThenBy(g => g.Group.Kind == ValueOriginKind.GeneratedValue)
            .ToList();
    }

    private static Group BuildGroup(string key, MetaverseAttributeOriginSummary sample, HashSet<int?> multiRuleSystems)
    {
        if (sample.HasSeveralOrigins)
            return new Group(key, null, "Several sources", null, null, null, null, false);

        var origin = sample.Origins[0];
        var showRule = origin.ConnectedSystemId.HasValue && multiRuleSystems.Contains(origin.ConnectedSystemId);

        var label = origin.Kind switch
        {
            ValueOriginKind.SetByPerson => $"Set by {origin.PersonName}",
            ValueOriginKind.GeneratedValue => origin.ConnectedSystemName == null
                ? GeneratedValueLabel
                : showRule
                    ? $"{origin.ConnectedSystemName} · {origin.SyncRuleName} · {GeneratedValueLabel}"
                    : $"{origin.ConnectedSystemName} · {GeneratedValueLabel}",
            ValueOriginKind.SynchronisationRule => origin.SyncRuleDeleted
                ? $"{origin.ConnectedSystemName} · rule deleted"
                : showRule
                    ? $"{origin.ConnectedSystemName} · {origin.SyncRuleName}"
                    : origin.ConnectedSystemName ?? "Source not recorded",
            _ => "Source not recorded"
        };

        return new Group(key, origin.Kind, label, origin.ConnectedSystemId, origin.ConnectedSystemName,
            origin.SyncRuleId, origin.SyncRuleName, origin.SyncRuleDeleted);
    }

    /// <summary>
    /// The theme colour token for a Connected System. Each system keeps one colour on every object and across
    /// restarts, chosen from its id (never from a string hash: <c>string.GetHashCode()</c> is randomised per
    /// process). Ids start at 1, so the first system takes the info colour its Connected System chip glyph
    /// already wears.
    /// </summary>
    public static string ColourTokenForConnectedSystem(int connectedSystemId) =>
        SystemPaletteTokens[Math.Abs((connectedSystemId - 1) % SystemPaletteTokens.Length)];

    /// <summary>
    /// The theme colour token for a source group: its Connected System's colour, except that Generated Values
    /// take the primary colour and values a person set the secondary one, so those two never share a colour with a
    /// system. "Not recorded" and "several sources" take the same neutral token, since neither names a real
    /// source. The contribution bar, its legend and every source dot read their colour from here.
    /// </summary>
    public static string ColourTokenFor(Group group) => group.Kind switch
    {
        ValueOriginKind.GeneratedValue => GeneratedValueColourToken,
        ValueOriginKind.SetByPerson => "--mud-palette-secondary",
        ValueOriginKind.SynchronisationRule when group.ConnectedSystemId is { } systemId => ColourTokenForConnectedSystem(systemId),
        _ => NeutralToken
    };

    private static readonly string[] SystemPaletteTokens =
    [
        "--mud-palette-info",
        "--mud-palette-success",
        "--mud-palette-warning",
        "--mud-palette-tertiary"
    ];

    private const string NeutralToken = "--mud-palette-text-disabled";
}
