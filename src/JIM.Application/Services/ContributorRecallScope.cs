// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Application.Services;

/// <summary>
/// Describes whose contribution a recall is withdrawing, so the surviving-contributor re-election core
/// (<see cref="ContributorReElectionService"/>) knows which contributing Synchronisation Rules may be
/// re-elected and which joined Connected System Objects count as survivors. The shipped scopes:
/// <list type="bullet">
/// <item>an obsoleting or withdrawing Connected System Object (#91): every contributor from the leaver's own
/// Connected System is ineligible (its other enabled rules were already evaluated in the run's ordinary flow),
/// and the leaver itself is never a survivor;</item>
/// <item>a Synchronisation Rule being deleted (#1537): only the deleted rule's own contribution is ineligible;
/// other rules of the same Connected System are legitimate survivors, because no ordinary flow accompanies the
/// deletion recall;</item>
/// <item>a Connected System being deleted (#809): the whole system is leaving, so every one of its rules is
/// ineligible and none of its objects counts as a survivor.</item>
/// </list>
/// Future recall scopes (#1549 stranded values) add factories here rather than forking the core.
/// </summary>
public sealed class ContributorRecallScope
{
    private readonly Func<SyncRule, bool> _isEligibleContributorRule;
    private readonly Func<ConnectedSystemObject, bool> _isEligibleSurvivor;

    private ContributorRecallScope(
        Func<SyncRule, bool> isEligibleContributorRule,
        Func<ConnectedSystemObject, bool> isEligibleSurvivor)
    {
        _isEligibleContributorRule = isEligibleContributorRule;
        _isEligibleSurvivor = isEligibleSurvivor;
    }

    /// <summary>
    /// The obsoletion/withdrawal scope (#91): the leaver's whole Connected System is excluded from
    /// re-election, and the leaver itself can never be a survivor.
    /// </summary>
    /// <param name="leaver">The obsoleting or withdrawing Connected System Object whose contribution is being recalled.</param>
    public static ContributorRecallScope ForObsoletingConnectedSystemObject(ConnectedSystemObject leaver)
    {
        ArgumentNullException.ThrowIfNull(leaver);
        return new ContributorRecallScope(
            rule => rule.ConnectedSystemId != leaver.ConnectedSystemId,
            cso => cso.Id != leaver.Id);
    }

    /// <summary>
    /// The rule-deletion scope (#1537): only the deleted Synchronisation Rule itself is excluded from
    /// re-election (defensively; the rule is disabled at queue time, so the contributor cache omits it
    /// anyway), and any joined Connected System Object, the deleted rule's own system's included, is a
    /// legitimate survivor.
    /// </summary>
    /// <param name="syncRuleId">The Synchronisation Rule being deleted.</param>
    public static ContributorRecallScope ForDeletedSyncRule(int syncRuleId)
    {
        return new ContributorRecallScope(
            rule => rule.Id != syncRuleId,
            _ => true);
    }

    /// <summary>
    /// The system-deletion scope (#809): the whole Connected System is leaving, so every contributing
    /// Synchronisation Rule belonging to it is excluded from re-election, and none of its joined
    /// Connected System Objects counts as a survivor; only objects joined via other Connected Systems
    /// may take over.
    /// </summary>
    /// <param name="connectedSystemId">The Connected System being deleted.</param>
    public static ContributorRecallScope ForDeletedConnectedSystem(int connectedSystemId)
    {
        return new ContributorRecallScope(
            rule => rule.ConnectedSystemId != connectedSystemId,
            cso => cso.ConnectedSystemId != connectedSystemId);
    }

    /// <summary>
    /// Whether a contributing Synchronisation Rule may be re-elected under this recall scope.
    /// </summary>
    public bool IsEligibleContributorRule(SyncRule rule) => _isEligibleContributorRule(rule);

    /// <summary>
    /// Whether a joined Connected System Object counts as a survivor under this recall scope.
    /// </summary>
    public bool IsEligibleSurvivor(ConnectedSystemObject cso) => _isEligibleSurvivor(cso);
}
