// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// One Connected System's discovered password policy, carrying the system's name alongside it (issue #1172).
/// <para>
/// The name travels separately rather than being read off <see cref="ConnectedSystemPasswordPolicy.ConnectedSystem"/>,
/// because a caller that read policies by Connected System id holds rows whose navigation is not loaded, and an
/// unloaded navigation is indistinguishable from an absent one. Reconciliation reports its findings by naming
/// systems, so it cannot be handed a graph it cannot name.
/// </para>
/// </summary>
public class PasswordPolicyForSystem
{
    /// <summary>
    /// The Connected System's name, as an administrator would recognise it.
    /// </summary>
    public required string ConnectedSystemName { get; init; }

    /// <summary>
    /// What JIM last discovered on that system, or null where nothing was ever read. Null is reported rather
    /// than treated as "no constraints": a system whose rules are unknown is a caveat on the whole exercise,
    /// not a system that will accept anything.
    /// </summary>
    public ConnectedSystemPasswordPolicy? Policy { get; init; }
}
