// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;
namespace JIM.Models.Interfaces;

/// <summary>
/// Connectors that can read the password policy their Connected System enforces implement this interface.
/// <para>
/// The point is that an administrator configuring an initial password should not have to know, or retype, rules
/// the target already publishes. JIM reads them alongside the schema and pre-fills the generator.
/// </para>
/// </summary>
public interface IConnectorPasswordPolicyDiscovery
{
    /// <summary>
    /// Reads the password policy the Connected System applies.
    /// <para>
    /// This runs as part of schema import, which must not fail because a policy could not be read. Return null
    /// when the system publishes nothing usable, and record partial results rather than throwing when only some
    /// of the policy is readable: a service account is frequently allowed to see less than all of it, and half a
    /// policy is considerably more useful to an administrator than none.
    /// </para>
    /// </summary>
    /// <returns>The discovered policy, or null when the Connected System exposes none.</returns>
    public Task<ConnectedSystemPasswordPolicy?> GetPasswordPolicyAsync(List<ConnectedSystemSettingValue> settings, ILogger logger);
}
