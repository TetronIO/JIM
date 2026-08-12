// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;

namespace JIM.Web.Models;

/// <summary>
/// How an attribute data flow reads on the system-wide Data Flow page (#1199).
/// <para>
/// Every judgement here is direction-dependent: the target sits on the Metaverse side inbound and on the Connected
/// System side outbound, priority orders inbound contributions and means nothing outbound, and the destination a row
/// should link to differs accordingly. Keeping these as plain functions rather than inline markup makes them
/// testable, which matters because a mistake produces a page that looks right and misinforms the reader.
/// </para>
/// </summary>
public static class DataFlowDisplay
{
    /// <summary>
    /// What the Priority column shows, or null where the column does not apply (Export flows).
    /// </summary>
    /// <remarks>
    /// A position is only meaningful against the set it is a position in, and a table row cannot rely on that set
    /// being visible: sorting by another column, filtering, or a page boundary all separate a contribution from its
    /// fellow contributors. So a flow sharing its target carries its own denominator ("2 of 3") rather than leaving the
    /// reader to reconstruct the set from neighbouring rows. A sole contributor shows the bare number, because
    /// "1 of 1" states a set that does not exist.
    /// <para>
    /// A mapping that has never been ordered carries the safe-addition sentinel (<see cref="int.MaxValue"/>), which
    /// is a "put me last, harmlessly" marker rather than a position an administrator chose. Rendering the raw number
    /// would be accurate and unreadable.
    /// </para>
    /// </remarks>
    public static string? PriorityLabel(DataFlowHeader flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        if (flow.Direction != SyncRuleDirection.Import || !flow.Priority.HasValue)
            return null;

        var priority = flow.Priority.Value;
        if (priority == int.MaxValue)
            return "Unranked";

        return flow.HasMultipleContributors ? $"{priority} of {flow.ContributorCount}" : priority.ToString();
    }

    /// <summary>
    /// Whether this flow holds the highest position among the contributions to its target Metaverse Attribute, and
    /// is therefore the row to emphasise in the Priority column.
    /// </summary>
    /// <remarks>
    /// Emphasis encodes rank, not "has more than one contributor". Encoding the latter paints an identical chip on
    /// positions 1 and 2, which tells the reader something matters here and then gives them no way to see which
    /// one it is. Several systems feeding one attribute is a normal arrangement, not a problem to flag.
    /// <para>
    /// "Highest priority" is not the same as "wins": resolution is decided per object, and a priority-1 contribution
    /// with no value for a given object loses to the next one that has one. The emphasis marks configuration, and
    /// the column's tooltip carries that qualification.
    /// </para>
    /// </remarks>
    public static bool IsHighestPriorityContributor(DataFlowHeader flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        return flow.Direction == SyncRuleDirection.Import && flow.HasMultipleContributors && flow.Priority == 1;
    }

    /// <summary>
    /// The direction in the vocabulary the rest of the portal uses for Synchronisation Rules.
    /// </summary>
    public static string DirectionLabel(SyncRuleDirection direction) =>
        direction == SyncRuleDirection.Import ? "Inbound" : "Outbound";

    /// <summary>
    /// The attribute on the Connected System side of the flow: its source inbound, its target outbound. Where
    /// several sources feed the flow, the first one evaluated.
    /// </summary>
    /// <remarks>
    /// The table anchors its columns to sides rather than to source and target, because those two swap sides
    /// between directions: a column whose meaning changes from row to row cannot be learned, and the reader is left
    /// checking each cell's marker to work out what they are looking at. Anchoring means only the arrow between the
    /// two blocks changes with direction.
    /// </remarks>
    public static string ConnectedSystemSideName(DataFlowHeader flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        return flow.Direction == SyncRuleDirection.Import
            ? FirstSourceName(flow)
            : flow.TargetConnectedSystemAttributeName ?? string.Empty;
    }

    /// <summary>
    /// The attribute on the Metaverse side of the flow: its target inbound, its source outbound. The mirror of
    /// <see cref="ConnectedSystemSideName"/>.
    /// </summary>
    public static string MetaverseSideName(DataFlowHeader flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        return flow.Direction == SyncRuleDirection.Import
            ? flow.TargetMetaverseAttributeName ?? string.Empty
            : FirstSourceName(flow);
    }

    /// <summary>
    /// Which way the value moves between the two side columns. The Connected System side is always on the left, so
    /// an Inbound flow runs left to right and an Outbound flow runs right to left.
    /// </summary>
    public static string DirectionArrow(SyncRuleDirection direction) =>
        direction == SyncRuleDirection.Import ? "→" : "←";

    private static string FirstSourceName(DataFlowHeader flow) =>
        flow.Sources.OrderBy(s => s.Order).Select(s => s.DisplayName).FirstOrDefault() ?? string.Empty;

    /// <summary>
    /// The attribute the flow writes, whichever side of the Metaverse that lands on.
    /// </summary>
    public static string TargetName(DataFlowHeader flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        return (flow.Direction == SyncRuleDirection.Import
            ? flow.TargetMetaverseAttributeName
            : flow.TargetConnectedSystemAttributeName) ?? string.Empty;
    }

    /// <summary>
    /// The attributes feeding the flow, in the order the engine evaluates them, with computed sources named as
    /// expressions because they have no single attribute to name.
    /// </summary>
    public static IReadOnlyList<string> SourceNames(DataFlowHeader flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        return flow.Sources
            .OrderBy(s => s.Order)
            .Select(s => s.DisplayName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
    }

    /// <summary>
    /// Where the target attribute takes the administrator: inbound, the priority order that decides which
    /// contribution wins; outbound, the Connected System schema the written attribute belongs to.
    /// </summary>
    public static string TargetHref(DataFlowHeader flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        return flow.Direction == SyncRuleDirection.Import
            ? $"/admin/schema/object-types/{flow.MetaverseObjectTypeId}?t=attributes"
            : $"/admin/connected-systems/{flow.ConnectedSystemId}?t=schema";
    }

    /// <summary>
    /// The owning Synchronisation Rule's Attribute Flow tab, which is where the mapping behind this row is edited.
    /// </summary>
    public static string RuleHref(DataFlowHeader flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        return $"/admin/sync-rules/{flow.SyncRuleId}?t=attribute-flow";
    }

    /// <summary>
    /// Explains what the Priority cell means for this particular flow, which varies more than the number suggests:
    /// a position only decides anything when something else is competing for the same Metaverse Attribute.
    /// </summary>
    public static string PriorityTooltip(DataFlowHeader flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        if (flow.Direction != SyncRuleDirection.Import)
            return "Priority orders competing contributions into the Metaverse, so it applies to Inbound flows only.";

        var unranked = flow.Priority == int.MaxValue;
        var contributors = flow.ContributorCount ?? 0;

        if (contributors <= 1)
        {
            return unranked
                ? "This is the only contributor to this Metaverse Attribute, so it is not competing with anything and has never needed a position."
                : "This is the only contributor to this Metaverse Attribute, so its position decides nothing until another Synchronisation Rule contributes to it.";
        }

        return unranked
            ? $"This contribution has never been given a position, so it ranks lowest of the {contributors} contributors to this Metaverse Attribute. Give it a position to change that."
            : $"{contributors} Synchronisation Rules contribute to this Metaverse Attribute; the lowest-numbered contribution with a value wins.";
    }
}
