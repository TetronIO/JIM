// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP.Security;

/// <summary>
/// One entry from a discretionary access control list, reduced to the fields an access check needs.
/// <para>
/// Deliberately not a faithful model of every ACE type in [MS-DTYP] 2.4.4. JIM asks one question of these
/// structures ("may this account reset a password here"), and modelling audit ACEs, callback ACEs, conditional
/// expressions and resource attributes would be a large surface with no caller.
/// </para>
/// </summary>
internal sealed class AccessControlEntry
{
    /// <summary>Whether this entry grants rather than denies. Audit and other entry types never reach here.</summary>
    internal required bool IsAllow { get; init; }

    /// <summary>
    /// Whether the entry applies only to children of this object rather than the object itself. An inherit-only
    /// entry says nothing about access to the object carrying it and must be skipped.
    /// </summary>
    internal required bool IsInheritOnly { get; init; }

    /// <summary>The rights this entry grants or denies.</summary>
    internal required uint AccessMask { get; init; }

    /// <summary>
    /// What the entry is scoped to: a control access right, a property, a property set, or a child object type.
    /// Null when the entry carries no ObjectType, which means it applies to everything the mask covers.
    /// </summary>
    internal Guid? ObjectType { get; init; }

    /// <summary>The account or group the entry applies to. Null only where the entry was unparseable.</summary>
    internal SecurityIdentifier? Sid { get; init; }
}
