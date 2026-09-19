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
    Generic,

    /// <summary>
    /// 389 Directory Server, including the Red Hat Directory Server builds of it.
    /// Detected via a vendorName containing "389" or a vendorVersion starting "389-Directory".
    /// <para>
    /// Deliberately appended after <see cref="Generic"/> even though it is a recognised server: this enum is
    /// persisted as an integer inside every Connected System's PersistedConnectorData, so inserting a member
    /// anywhere but the end would silently retype existing deployments. For everything except password policy
    /// discovery it behaves as <see cref="Generic"/> (entryUUID, changelog delta, paging), which is what these
    /// servers were treated as before they were recognised.
    /// </para>
    /// </summary>
    DirectoryServer389
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
