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
