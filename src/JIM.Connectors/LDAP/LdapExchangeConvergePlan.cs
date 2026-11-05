// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// What one convergence step does (#2021): run these commands, or refuse with this reason, or nothing at all when the
/// recipient is already in its declared state.
/// </summary>
/// <param name="Commands">The commands to run, in order; empty when there is nothing to do or the change is refused.</param>
/// <param name="ReadAgainAfter">
/// True after an enable: the new recipient is read again, so whatever the enable could not set is set by a second step.
/// </param>
/// <param name="Refusal">Why the change is refused, in words an administrator can act on; null when it is not.</param>
internal sealed record LdapExchangeConvergePlan(IReadOnlyList<LdapExchangeCommand> Commands, bool ReadAgainAfter, string? Refusal)
{
    internal static LdapExchangeConvergePlan Nothing { get; } = new([], false, null);

    internal static LdapExchangeConvergePlan Refuse(string reason) => new([], false, reason);
}
