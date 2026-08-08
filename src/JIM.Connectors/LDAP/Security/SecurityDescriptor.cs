// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP.Security;

/// <summary>
/// A parsed security descriptor, reduced to the discretionary access control list an access check reads.
/// </summary>
internal sealed class SecurityDescriptor
{
    /// <summary>
    /// Whether the descriptor carries a discretionary access control list at all.
    /// <para>
    /// Not the same question as whether <see cref="Aces"/> is empty, and the difference is the opposite of
    /// intuitive: no list at all means unrestricted access, whereas a list with no entries means no access to
    /// anyone. Conflating them inverts the answer in one of the two cases.
    /// </para>
    /// </summary>
    internal required bool DaclPresent { get; init; }

    /// <summary>
    /// The entries, in the order the directory holds them. Order is part of the meaning: an access check walks
    /// them in sequence and the first entry that matches decides the outcome.
    /// </summary>
    internal required IReadOnlyList<AccessControlEntry> Aces { get; init; }
}
