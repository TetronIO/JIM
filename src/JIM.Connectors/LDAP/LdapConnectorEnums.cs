// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// The type of LDAP directory server detected via rootDSE capabilities.
/// Determines directory-specific behaviour: schema discovery, external ID attribute,
/// delta import strategy, and attribute semantics.
/// </summary>
internal enum LdapDirectoryType
{
    /// <summary>
    /// Microsoft Active Directory (AD-DS) or Active Directory Lightweight Directory Services (AD-LDS).
    /// Detected via supportedCapabilities OIDs on rootDSE.
    /// </summary>
    ActiveDirectory,

    /// <summary>
    /// Samba Active Directory Domain Controller.
    /// Advertises AD capability OIDs but has behavioural differences: paged search returns duplicates,
    /// different error codes for missing objects, and different backend tooling (ldbadd vs ldapmodify).
    /// Detected via AD capability OIDs combined with vendorName containing "Samba".
    /// </summary>
    SambaAD,

    /// <summary>
    /// OpenLDAP directory server.
    /// Detected via vendorName or vendorVersion on rootDSE.
    /// </summary>
    OpenLDAP,

    /// <summary>
    /// Unrecognised directory server. Uses RFC-standard LDAP behaviour.
    /// Falls back to OpenLDAP-compatible defaults (entryUUID, changelog delta, RFC 4512 schema).
    /// </summary>
    Generic
}

/// <summary>
/// Where the domain controller/directory server used for a connection came from, per
/// <see cref="LdapConnectorUtilities.ResolveEffectiveServer"/> (issue #230 Phase 2). Drives whether a
/// failed connection invalidates a pin: only a connection resolved via <see cref="Pinned"/> can have its
/// pin invalidated, since the other two sources are administrator-supplied or unpinned by definition.
/// </summary>
internal enum LdapServerResolutionSource
{
    /// <summary>
    /// The administrator supplied a non-blank "Preferred Domain Controller" setting; that value always wins.
    /// </summary>
    PreferredSetting,

    /// <summary>
    /// No Preferred Domain Controller is configured; the domain controller pinned in persisted connector
    /// data (from a previous connection) was used.
    /// </summary>
    Pinned,

    /// <summary>
    /// Neither a Preferred Domain Controller setting nor a usable pin was available; the configured Host
    /// setting was used, as it always was before pinning existed.
    /// </summary>
    Host
}
