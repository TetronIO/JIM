// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Web.Models;

/// <summary>
/// The advisory Standard Mapping hints the Attribute Flow editor shows while an administrator is choosing
/// attributes (#1122): the counterpart name a Metaverse Attribute carries in the applicable standard's
/// vocabulary, and which Metaverse Attributes a Connected System attribute name corresponds to.
///
/// Hints are guidance only. They never filter, disable or pre-select anything, an attribute with no mapping
/// is ordinary rather than deficient, and nothing outside the portal reads them; the synchronisation engine
/// never consults Standard Mappings, and Attribute Flow configuration remains the single source of mapping truth.
/// </summary>
public sealed class StandardMappingHints
{
    private static readonly IReadOnlyList<StandardMappingHint> NoHints = [];

    private readonly Dictionary<int, IReadOnlyList<StandardMappingHint>> _hintsByAttributeId;
    private readonly Dictionary<string, IReadOnlyList<StandardMappingMatch>> _matchesByCounterpartName;

    private StandardMappingHints(
        Dictionary<int, IReadOnlyList<StandardMappingHint>> hintsByAttributeId,
        Dictionary<string, IReadOnlyList<StandardMappingMatch>> matchesByCounterpartName)
    {
        _hintsByAttributeId = hintsByAttributeId;
        _matchesByCounterpartName = matchesByCounterpartName;
    }

    /// <summary>
    /// Hints for a system that has no Standard Mappings to draw on; every lookup comes back empty.
    /// </summary>
    public static StandardMappingHints Empty { get; } = new(new Dictionary<int, IReadOnlyList<StandardMappingHint>>(), new Dictionary<string, IReadOnlyList<StandardMappingMatch>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Whether any hint is available at all; the editor renders its hint affordances only when this is true.
    /// </summary>
    public bool HasHints => _hintsByAttributeId.Count > 0;

    /// <summary>
    /// Whether the hints span more than one standard, which is what makes naming the standard against each
    /// counterpart worth the space. When a Connected System's Connector declares its vocabulary, every hint
    /// carries the same label and repeating it down a long picker is noise, so the editor omits it there.
    /// </summary>
    public bool HasMixedStandards { get; private init; }

    /// <summary>
    /// Builds the hints for one Connected System's editor session.
    /// </summary>
    /// <param name="mappings">Every Standard Mapping held by the Metaverse Attributes in scope.</param>
    /// <param name="connectedSystemStandard">
    /// The vocabulary the Connected System's schema follows, as declared by its Connector. When a Connector
    /// declares nothing (<see cref="AttributeStandard.NotSet"/>), every standard is offered instead, each
    /// labelled, so systems with standards-shaped schemas but no declaration still get guidance.
    /// </param>
    public static StandardMappingHints Build(IEnumerable<MetaverseAttributeStandardMapping> mappings, AttributeStandard connectedSystemStandard)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        var applicable = connectedSystemStandard == AttributeStandard.NotSet
            ? mappings.Where(m => m.Standard != AttributeStandard.NotSet && !string.IsNullOrWhiteSpace(m.CounterpartName))
            : mappings.Where(m => m.Standard == connectedSystemStandard && !string.IsNullOrWhiteSpace(m.CounterpartName));

        var hintsByAttributeId = new Dictionary<int, IReadOnlyList<StandardMappingHint>>();
        var matchesByCounterpartName = new Dictionary<string, List<StandardMappingMatch>>(StringComparer.OrdinalIgnoreCase);

        foreach (var attributeMappings in applicable.GroupBy(m => m.MetaverseAttributeId))
        {
            // One Metaverse Attribute can carry the same counterpart name in more than one vocabulary
            // (Display Name is "displayName" in both SCIM and LDAP); collapse those into a single hint
            // labelled with both standards rather than repeating the name.
            var hints = attributeMappings
                .GroupBy(m => m.CounterpartName.Trim(), StringComparer.Ordinal)
                .Select(counterpart => BuildHint(counterpart.Key, counterpart.OrderBy(m => m.Standard).ToList()))
                .OrderBy(h => h.CounterpartName, StringComparer.Ordinal)
                .ToList();

            hintsByAttributeId[attributeMappings.Key] = hints;

            foreach (var hint in hints)
            {
                if (!matchesByCounterpartName.TryGetValue(hint.CounterpartName, out var matches))
                {
                    matches = [];
                    matchesByCounterpartName[hint.CounterpartName] = matches;
                }

                matches.Add(new StandardMappingMatch(attributeMappings.Key, hint.StandardLabel, hint.CounterpartName, hint.Notes));
            }
        }

        var matchIndex = matchesByCounterpartName.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<StandardMappingMatch>)pair.Value.OrderBy(m => m.MetaverseAttributeId).ToList(),
            StringComparer.OrdinalIgnoreCase);

        var distinctLabels = hintsByAttributeId.Values
            .SelectMany(hints => hints.Select(h => h.StandardLabel))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count();

        return new StandardMappingHints(hintsByAttributeId, matchIndex) { HasMixedStandards = distinctLabels > 1 };
    }

    /// <summary>
    /// The counterpart names to show alongside a Metaverse Attribute, ordered by name. Empty when the
    /// attribute has no mapping for the applicable standard, which is not an error state.
    /// </summary>
    public IReadOnlyList<StandardMappingHint> ForAttribute(int metaverseAttributeId) =>
        _hintsByAttributeId.TryGetValue(metaverseAttributeId, out var hints) ? hints : NoHints;

    /// <summary>
    /// The Metaverse Attributes a Connected System attribute name corresponds to, matched case-insensitively
    /// against counterpart names. More than one can match (SCIM "emails" suits both Email and Emails), and
    /// none matching is the common case for schemas that do not follow a standard.
    /// </summary>
    public IReadOnlyList<StandardMappingMatch> MatchesForAttributeName(string? connectedSystemAttributeName)
    {
        if (string.IsNullOrWhiteSpace(connectedSystemAttributeName))
            return [];

        return _matchesByCounterpartName.TryGetValue(connectedSystemAttributeName.Trim(), out var matches) ? matches : [];
    }

    /// <summary>
    /// The portal's wording for a standard's vocabulary, matching the Metaverse Attribute editor dialog.
    /// </summary>
    public static string StandardLabel(AttributeStandard standard) => standard switch
    {
        AttributeStandard.Scim => "SCIM 2.0",
        AttributeStandard.Ldap => "LDAP/AD",
        AttributeStandard.Jim => "JIM",
        _ => standard.ToString()
    };

    private static StandardMappingHint BuildHint(string counterpartName, List<MetaverseAttributeStandardMapping> mappings)
    {
        var label = string.Join(" · ", mappings.Select(m => StandardLabel(m.Standard)).Distinct(StringComparer.Ordinal));

        // A single standard's note reads on its own; when two standards share a counterpart name and both
        // carry a note, each note needs its standard named or the combined text is ambiguous.
        var noted = mappings.Where(m => !string.IsNullOrWhiteSpace(m.Notes)).ToList();
        var notes = noted.Count switch
        {
            0 => null,
            1 => noted[0].Notes!.Trim(),
            _ => string.Join(" ", noted.Select(m => $"{StandardLabel(m.Standard)}: {m.Notes!.Trim()}"))
        };

        return new StandardMappingHint(label, counterpartName, notes);
    }
}
