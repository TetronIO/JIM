// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Preview;

namespace JIM.Models.Logic;

/// <summary>
/// The settings of a stored Synchronisation Rule that decide which Metaverse Objects an export rule provisions to, or
/// deprovisions from, its Connected System, and how: whether it is enabled, whether it provisions, its Scoping Criteria
/// (issue #1925), and its Deprovisioning Action (issue #1970).
/// </summary>
/// <remarks>
/// Export evaluation is driven by an object's own values changing, so a change to these settings reaches nothing until
/// the objects it affects are flagged for review. Comparing the rule as stored with the rule as saved decides whether
/// to flag them: a save that leaves these settings alone (a rename, an Attribute Flow edit) costs no review.
/// </remarks>
/// <param name="Enabled">Whether the rule is enabled.</param>
/// <param name="ProvisionToConnectedSystem">Whether the rule provisions to its Connected System.</param>
/// <param name="Scope">The rule's Scoping Criteria, compared order-insensitively at every depth.</param>
/// <param name="DeprovisionAction">What the rule does to an object that leaves its scope.</param>
public sealed record SyncRuleScopeState(
    bool Enabled,
    bool ProvisionToConnectedSystem,
    SyncRuleScopingProposal Scope,
    OutboundDeprovisionAction DeprovisionAction)
{
    /// <summary>
    /// Whether saving <paramref name="saved"/> over this stored state can move objects into or out of the rule's
    /// export scope, or into provisioning: the rule was re-enabled, provisioning was switched on, or the Scoping
    /// Criteria changed. Disabling a rule or switching provisioning off is not such a change; neither destroys what
    /// already exists.
    /// </summary>
    /// <param name="saved">The rule as it has just been saved.</param>
    public bool ExportScopeMovedBy(SyncRule saved)
    {
        ArgumentNullException.ThrowIfNull(saved);

        if (!saved.Enabled)
            return false;

        return !Enabled
               || (!ProvisionToConnectedSystem && saved.ProvisionToConnectedSystem == true)
               || ScopeChangedTo(SyncRuleScopingProposal.FromCurrentScope(saved));
    }

    /// <summary>
    /// Whether saving <paramref name="saved"/> over this stored state changes what the rule does to objects that leave
    /// its scope. Objects already out of scope are deprovisioned only when something reviews them, so without a review
    /// the new action reaches none of them: a Delete queued under the old action would still be exported after a switch
    /// to Disconnect, and an object still joined would never be deleted after a switch to Delete (#1970). A disabled
    /// rule deprovisions nothing, so the same save disabling it needs no review; re-enabling it is one of its own.
    /// </summary>
    /// <param name="saved">The rule as it has just been saved.</param>
    public bool DeprovisioningActionChangedBy(SyncRule saved)
    {
        ArgumentNullException.ThrowIfNull(saved);

        return saved.Enabled && DeprovisionAction != saved.OutboundDeprovisionAction;
    }

    /// <summary>
    /// Whether <paramref name="savedScope"/> matches different objects from the stored scope. Two scopes that each
    /// constrain nothing match every object alike, however they are written: the scripted way to scope a rule creates
    /// an empty group first and its criteria after, one save each, and that first save moves nobody.
    /// </summary>
    private bool ScopeChangedTo(SyncRuleScopingProposal savedScope) =>
        !(Scope.IsUnscoped && savedScope.IsUnscoped) && !Scope.DescribesSameScopeAs(savedScope);
}
