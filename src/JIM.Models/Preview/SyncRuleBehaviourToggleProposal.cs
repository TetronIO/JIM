// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Models.Preview;

/// <summary>
/// The five Synchronisation Rule behaviour toggles an administrator is proposing, as the behaviour-toggle preview
/// adapter receives them (#1462): whether the rule runs at all, which way it runs, and what it is allowed to
/// create or correct.
/// </summary>
/// <remarks>
/// A flat, JSON-round-trippable record rather than a <see cref="SyncRule"/>, for the reason every proposal has
/// one: a preview may be evaluated in JIM.Worker, so it has to survive a queue, and handing over an entity graph
/// invites an adapter to read the saved configuration instead of the proposed one.
///
/// Every toggle is carried resolved, never optional. Two of them are nullable on the rule itself, where null means
/// off at synchronisation time; the surface that builds the proposal merges any omitted field with the stored rule
/// first, exactly as the update surfaces do, so the adapter never has to decide whether an absence meant
/// "unchanged" or "off". Those are opposite answers about whether accounts get created.
/// </remarks>
/// <param name="Enabled">Whether the rule is evaluated at all. A disabled rule contributes nothing, scopes nothing and creates nothing.</param>
/// <param name="Direction">Whether the rule flows data into the Metaverse or out of it.</param>
/// <param name="ProjectToMetaverse">Whether an in-scope object that matches no Metaverse Object creates a new one.</param>
/// <param name="ProvisionToConnectedSystem">Whether an in-scope Metaverse Object with no object in the target system has one created for it.</param>
/// <param name="EnforceState">Whether inbound changes re-evaluate an export rule, so drift in the target system is corrected.</param>
public record SyncRuleBehaviourToggleProposal(
    bool Enabled,
    SyncRuleDirection Direction,
    bool ProjectToMetaverse,
    bool ProvisionToConnectedSystem,
    bool EnforceState)
{
    /// <summary>
    /// The settings currently in force on <paramref name="syncRule"/>, as a proposal. What "no change" looks like,
    /// and the baseline an adapter evaluates a proposal against.
    /// </summary>
    public static SyncRuleBehaviourToggleProposal FromCurrentSettings(SyncRule syncRule)
    {
        ArgumentNullException.ThrowIfNull(syncRule);

        return new SyncRuleBehaviourToggleProposal(
            syncRule.Enabled,
            syncRule.Direction,
            syncRule.ProjectToMetaverse ?? false,
            syncRule.ProvisionToConnectedSystem ?? false,
            syncRule.EnforceState);
    }

    /// <summary>
    /// Whether <paramref name="other"/> proposes the same settings as this one. What decides whether a preview an
    /// administrator is looking at still answers the question they are about to ask.
    /// </summary>
    public bool DescribesSameSettingsAs(SyncRuleBehaviourToggleProposal? other) => other is not null && other == this;
}
