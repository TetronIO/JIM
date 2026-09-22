// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The pure filter predicate for the Table view (#1519 Phase 3).
/// </summary>
public static class CausalityTableFilters
{
    /// <summary>
    /// Whether <paramref name="row"/> matches <paramref name="filter"/>. Destructive covers Delete,
    /// Deprovision, Disconnect and No Contributor: the change kinds that remove or lose a value, a
    /// join or an object outright.
    /// </summary>
    public static bool Matches(CausalityTableRow row, CausalityTableFilter filter)
    {
        ArgumentNullException.ThrowIfNull(row);

        return filter switch
        {
            CausalityTableFilter.ScopeAndJoin => row.ChangeKind is CausalityTableChangeKind.Scope
                or CausalityTableChangeKind.Projection
                or CausalityTableChangeKind.Join,
            CausalityTableFilter.AttributeChanges => row.ChangeKind == CausalityTableChangeKind.AttributeChange,
            CausalityTableFilter.ObjectChanges => row.ChangeKind != CausalityTableChangeKind.AttributeChange,
            // ProvisioningCancelled is deliberately excluded: nothing was ever created in the target system,
            // so nothing is lost by the cancellation, unlike a genuine Deprovision.
            CausalityTableFilter.Destructive => row.ChangeKind is CausalityTableChangeKind.Delete
                or CausalityTableChangeKind.Deprovision
                or CausalityTableChangeKind.Disconnect
                or CausalityTableChangeKind.NoContributor,
            _ => true
        };
    }
}
