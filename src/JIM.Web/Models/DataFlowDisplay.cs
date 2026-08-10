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
    /// A mapping that has never been ordered carries the safe-addition sentinel (<see cref="int.MaxValue"/>), which
    /// is a "put me last, harmlessly" marker rather than a position an administrator chose. Rendering the raw number
    /// would be accurate and unreadable.
    /// </remarks>
    public static string? PriorityLabel(DataFlowHeader flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        if (flow.Direction != SyncRuleDirection.Import || !flow.Priority.HasValue)
            return null;

        var priority = flow.Priority.Value;
        return priority == int.MaxValue ? "Unranked" : priority.ToString();
    }

    /// <summary>
    /// The direction in the vocabulary the rest of the portal uses for Synchronisation Rules.
    /// </summary>
    public static string DirectionLabel(SyncRuleDirection direction) =>
        direction == SyncRuleDirection.Import ? "Inbound" : "Outbound";

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
