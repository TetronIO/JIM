// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;

namespace JIM.Models.Preview;

/// <summary>
/// The two destructive Synchronisation Rule settings an administrator is proposing, as the G3 preview adapter
/// receives them (#1115). Deliberately a flat, JSON-round-trippable record rather than a
/// <see cref="JIM.Models.Logic.SyncRule"/>: a preview may be evaluated in JIM.Worker, so the proposal has to
/// survive being serialised onto a queue, and handing over an entity graph would invite an adapter to read the
/// saved configuration instead of the proposed one.
///
/// Both values are carried resolved, never optional: the surface that builds the proposal (portal editor, REST
/// controller, PowerShell) merges any omitted field with the stored rule first, exactly as the update surfaces
/// do, so the adapter never has to guess what an absence means.
/// </summary>
/// <param name="OutboundDeprovisionAction">
/// What an export Synchronisation Rule would do to a joined target object whose Metaverse Object leaves the
/// rule's scope: disconnect it, or stage a Delete export that removes it from the target Connected System.
/// </param>
/// <param name="InboundOutOfScopeAction">
/// What an import Synchronisation Rule would do to a joined Connected System Object that leaves import scope or
/// is obsoleted: keep the join ("once managed, always managed") or disconnect it, recalling what it contributed
/// and potentially triggering the Metaverse Object's deletion rules.
/// </param>
public record SyncRuleDestructiveToggleProposal(
    OutboundDeprovisionAction OutboundDeprovisionAction,
    InboundOutOfScopeAction InboundOutOfScopeAction)
{
    /// <summary>
    /// The two toggles as a Synchronisation Rule currently holds them. One reading shared by every surface that
    /// builds a proposal off a rule, so none of them names the two settings for itself.
    /// </summary>
    public static SyncRuleDestructiveToggleProposal FromCurrentSettings(SyncRule syncRule)
    {
        ArgumentNullException.ThrowIfNull(syncRule);
        return new SyncRuleDestructiveToggleProposal(syncRule.OutboundDeprovisionAction, syncRule.InboundOutOfScopeAction);
    }

    /// <summary>
    /// Whether <paramref name="other"/> proposes the same settings as this one. What decides whether a preview an
    /// administrator is looking at still answers the question they are about to ask; an editor compares what is
    /// on the form against what the visible preview was run for, and labels the preview stale when they diverge.
    /// The generated record equality already answers this for two enum values; the method exists so editors read
    /// the same way against every proposal type.
    /// </summary>
    public bool DescribesSameSettingsAs(SyncRuleDestructiveToggleProposal? other) =>
        other is not null &&
        OutboundDeprovisionAction == other.OutboundDeprovisionAction &&
        InboundOutOfScopeAction == other.InboundOutOfScopeAction;
}
