// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Where a Connected System stands on Password Synchronisation, for a list that must say so per row (#1119,
/// requirement 26).
/// <para>
/// Four states rather than a boolean, because the three ways of not receiving synchronised passwords are not the
/// same thing to an administrator: one is a Connector limitation nobody can configure away, one is work that has
/// never been done, and one is a deliberate pause that is still accumulating changes to deliver when it lifts.
/// </para>
/// <para>
/// Ordered by how much the system participates, so sorting a list on this value groups the systems that take
/// passwords together and puts the ones that never will at the other end.
/// </para>
/// </summary>
public enum PasswordSynchronisationState
{
    /// <summary>
    /// The Connected System's Connector cannot set passwords, so Password Synchronisation cannot be configured
    /// here whatever anyone does.
    /// </summary>
    NotSupported = 0,

    /// <summary>
    /// The Connector could deliver passwords, but nobody has configured Password Synchronisation on this system.
    /// </summary>
    NotConfigured = 1,

    /// <summary>
    /// Configured and deliberately switched off. Changes still accumulate for this system and are delivered when
    /// it is switched back on; they are not discarded in the meantime.
    /// </summary>
    Disabled = 2,

    /// <summary>
    /// Configured and delivering.
    /// </summary>
    Enabled = 3
}
