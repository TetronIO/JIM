// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Application.Services;

/// <summary>
/// Per-run lookup of the import Synchronisation Rule mappings that contribute to each Metaverse Object attribute
/// (#91), keyed by (Metaverse Object Type, Metaverse Attribute). Built once at the start of a sync run from every
/// enabled import rule across all Connected Systems (mirroring the drift-detection import-mapping cache), so the
/// inline incumbent-comparison gate in the attribute-flow engine needs no per-object query.
///
/// The gate is an O(1) specialisation of <see cref="AttributePriorityService.Resolve"/>: rather than re-evaluating
/// every contributor, it compares one incoming contribution against the rule that already owns the value on the
/// Metaverse Object (the incumbent, identified by <c>ContributedBySyncRuleId</c>). The comparison uses the same
/// canonical order as the resolver, priority ascending then mapping id, so the fast path and the resolver agree.
/// </summary>
public sealed class AttributePriorityContext
{
    // (object type, attribute) -> contributing mappings, ordered canonically (priority asc, then mapping id).
    private readonly Dictionary<(int ObjectTypeId, int AttributeId), List<SyncRuleMapping>> _contributorsByAttribute = new();

    // (object type, attribute, Synchronisation Rule) -> the contributing mapping, for O(1) incumbent lookup.
    private readonly Dictionary<(int ObjectTypeId, int AttributeId, int SyncRuleId), SyncRuleMapping> _contributorBySyncRule = new();

    // (object type, attribute, Synchronisation Rule) with a DORMANT mapping: the row exists but is disabled,
    // or its whole rule is disabled. Dormant flows contribute nothing (they are absent from the contributor
    // structures above, so survivor takeover is unaffected), but their previously contributed values are
    // retained rather than recalled: the administrator has paused the flow and may re-enable it (#1537).
    // Only a mapping row that is genuinely GONE orphans its values.
    private readonly HashSet<(int ObjectTypeId, int AttributeId, int SyncRuleId)> _dormantContributors = new();

    /// <summary>
    /// Whether "Null is a value" assertions are honoured (asserted-null <c>NullValue</c> marker rows are written and
    /// block lower-priority fall-through). This is gated on the <c>NullValue</c> read-query filter being in place,
    /// because a marker must be invisible to export/drift/enumeration reads to avoid a silent downstream divergence.
    /// Until that filter lands the worker builds the context with this false: priority resolution among value
    /// contributors is live, but a no-value contribution falls through (abstains) regardless of "Null is a value",
    /// and no marker rows are written.
    /// </summary>
    public bool HonourNullAssertions { get; }

    /// <summary>
    /// Builds the contributor cache from all Synchronisation Rules across every Connected System. Only enabled
    /// import rules with a target Metaverse Attribute and a persisted Synchronisation Rule id contribute.
    /// </summary>
    /// <param name="allSyncRules">Every Synchronisation Rule across all Connected Systems.</param>
    /// <param name="honourNullAssertions">See <see cref="HonourNullAssertions"/>. Defaults to true for the resolution
    /// semantics; the worker passes false until the <c>NullValue</c> read-query filter is in place.</param>
    public AttributePriorityContext(IEnumerable<SyncRule> allSyncRules, bool honourNullAssertions = true)
    {
        ArgumentNullException.ThrowIfNull(allSyncRules);
        HonourNullAssertions = honourNullAssertions;

        foreach (var rule in allSyncRules.Where(r => r.Direction == SyncRuleDirection.Import))
        {
            // A disabled mapping (#1485), and every mapping of a disabled rule, contributes nothing: counted,
            // it would inflate the contributor count (forcing the slow multi-contributor path) and could be
            // elected as a surviving contributor whose values never actually flow. It is recorded as DORMANT
            // instead, so the recall pass can tell a paused flow (values retained, #1537) from a deleted
            // mapping (values orphaned, #1533).
            foreach (var mapping in rule.AttributeFlowRules.Where(m => m.TargetMetaverseAttribute != null && m.SyncRuleId.HasValue))
            {
                var attributeId = mapping.TargetMetaverseAttribute!.Id;

                if (!rule.Enabled || !mapping.Enabled)
                {
                    _dormantContributors.Add((rule.MetaverseObjectTypeId, attributeId, mapping.SyncRuleId!.Value));
                    continue;
                }

                var listKey = (rule.MetaverseObjectTypeId, attributeId);
                if (!_contributorsByAttribute.TryGetValue(listKey, out var list))
                {
                    list = [];
                    _contributorsByAttribute[listKey] = list;
                }

                list.Add(mapping);
                _contributorBySyncRule[(rule.MetaverseObjectTypeId, attributeId, mapping.SyncRuleId!.Value)] = mapping;
            }
        }

        // Order each contributor list canonically so callers that enumerate it resolve deterministically.
        foreach (var list in _contributorsByAttribute.Values)
            list.Sort(CompareByPriority);
    }

    /// <summary>
    /// The canonical resolution order for two contributing mappings: priority ascending (1 = highest), with mapping
    /// id as the deterministic tie-break. Matches <see cref="AttributePriorityService.Resolve"/>.
    /// </summary>
    internal static int CompareByPriority(SyncRuleMapping a, SyncRuleMapping b) =>
        a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.Id.CompareTo(b.Id);

    /// <summary>
    /// The number of import mappings that contribute to the given Metaverse Object attribute. A count of one (or
    /// zero) means the attribute has a single contributor and the engine can use its unchanged fast write path.
    /// </summary>
    public int GetContributorCount(int objectTypeId, int attributeId) =>
        _contributorsByAttribute.TryGetValue((objectTypeId, attributeId), out var list) ? list.Count : 0;

    /// <summary>
    /// The contributing mapping for a given Synchronisation Rule, or null if that rule no longer contributes to the attribute.
    /// </summary>
    public SyncRuleMapping? GetContributor(int objectTypeId, int attributeId, int syncRuleId) =>
        _contributorBySyncRule.TryGetValue((objectTypeId, attributeId, syncRuleId), out var mapping) ? mapping : null;

    /// <summary>
    /// Whether the given Synchronisation Rule holds a DORMANT mapping for the attribute: the mapping row exists
    /// but is disabled, or its whole rule is disabled (#1537). A dormant flow contributes nothing, but its
    /// previously contributed values are retained rather than recalled; only a mapping that is genuinely gone
    /// (no live contributor and no dormant one) orphans its values (#1533).
    /// </summary>
    public bool HasDormantContributor(int objectTypeId, int attributeId, int syncRuleId) =>
        _dormantContributors.Contains((objectTypeId, attributeId, syncRuleId));

    /// <summary>
    /// The import mappings contributing to a Metaverse Object attribute, ordered canonically (highest priority first),
    /// or an empty list when the attribute has no contributors. Used by the next-contributor recall fallback to find
    /// the surviving contributors to re-elect when the winning contributor's Connected System Object disconnects.
    /// </summary>
    public IReadOnlyList<SyncRuleMapping> GetContributors(int objectTypeId, int attributeId) =>
        _contributorsByAttribute.TryGetValue((objectTypeId, attributeId), out var list) ? list : [];

    /// <summary>
    /// Decides whether an incoming contribution should be applied to a Metaverse Object attribute, given the rule
    /// that currently owns the attribute's value (the incumbent, by <c>ContributedBySyncRuleId</c>). Returns true
    /// when the incoming mapping wins or is the same rule updating itself, or when there is no comparable incumbent
    /// (none owns it, the value is internally managed, or the incumbent rule no longer contributes); false only when
    /// a higher-priority incumbent must be left in place. Uses the canonical (priority, mapping id) order.
    /// </summary>
    public bool ShouldApply(int objectTypeId, int attributeId, SyncRuleMapping incoming, int? incumbentSyncRuleId)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        if (incumbentSyncRuleId == null)
            return true;

        if (incumbentSyncRuleId.Value == incoming.SyncRuleId)
            return true;

        var incumbent = GetContributor(objectTypeId, attributeId, incumbentSyncRuleId.Value);
        if (incumbent == null)
            return true;

        // Incoming wins iff it ranks at or before the incumbent in (priority asc, mapping id asc) order.
        return CompareByPriority(incoming, incumbent) <= 0;
    }
}
