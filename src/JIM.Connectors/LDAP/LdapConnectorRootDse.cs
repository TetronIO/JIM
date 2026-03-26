using JIM.Models.Core;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Holder of key synchronisation-related information about an LDAP directory from its rootDSE object.
/// This data is persisted between synchronisation runs to support delta imports.
/// </summary>
internal class LdapConnectorRootDse
{
    /// <summary>
    /// The DNS hostname of the connected directory server.
    /// </summary>
    public string? DnsHostName { get; set; }

    /// <summary>
    /// For Active Directory: The highest committed Update Sequence Number (USN) at the time of the last sync.
    /// Used for delta imports - we query for objects where uSNChanged > this value.
    /// </summary>
    public long? HighestCommittedUsn { get; set; }

    /// <summary>
    /// For changelog-based directories (e.g., OpenLDAP, Oracle Directory): The last change number processed.
    /// Used for delta imports - we query the changelog for entries > this value.
    /// </summary>
    public int? LastChangeNumber { get; set; }

    /// <summary>
    /// The detected directory server type, determined from rootDSE capabilities during connection.
    /// Drives all directory-specific behaviour: schema discovery, external ID, delta strategy, etc.
    /// </summary>
    public LdapDirectoryType DirectoryType { get; set; } = LdapDirectoryType.Generic;

    /// <summary>
    /// The vendor name of the directory server (e.g., "Samba Team", "Microsoft", "OpenLDAP").
    /// Used for vendor-specific workarounds (e.g., Samba paging bug).
    /// </summary>
    public string? VendorName { get; set; }

    /// <summary>
    /// Indicates if the directory supports paged search results.
    /// True AD supports paging; Samba AD claims support but returns duplicate results, so we disable it.
    /// </summary>
    public bool SupportsPaging { get; set; } = true;

    // -----------------------------------------------------------------------
    // Computed properties — centralised directory-type-specific behaviour
    // -----------------------------------------------------------------------

    /// <summary>
    /// The attribute name used as the unique, immutable external identifier for directory objects.
    /// AD uses objectGUID (binary GUID in Microsoft byte order); OpenLDAP uses entryUUID (RFC 4530, string format).
    /// </summary>
    public string ExternalIdAttributeName => DirectoryType switch
    {
        LdapDirectoryType.ActiveDirectory => "objectGUID",
        LdapDirectoryType.OpenLDAP => "entryUUID",
        LdapDirectoryType.Generic => "entryUUID",
        _ => "entryUUID"
    };

    /// <summary>
    /// The data type of the external ID attribute in JIM's attribute model.
    /// AD's objectGUID is a binary GUID; OpenLDAP's entryUUID is a string representation of a UUID.
    /// </summary>
    public AttributeDataType ExternalIdDataType => DirectoryType switch
    {
        LdapDirectoryType.ActiveDirectory => AttributeDataType.Guid,
        LdapDirectoryType.OpenLDAP => AttributeDataType.Text,
        LdapDirectoryType.Generic => AttributeDataType.Text,
        _ => AttributeDataType.Text
    };

    /// <summary>
    /// Whether delta imports should use USN-based change tracking (AD) or changelog-based (OpenLDAP/generic).
    /// </summary>
    public bool UseUsnDeltaImport => DirectoryType == LdapDirectoryType.ActiveDirectory;

    /// <summary>
    /// Whether the directory's SAM layer enforces single-valued semantics on certain multi-valued schema attributes
    /// (e.g., 'description' on user/group objects). Only applies to Active Directory and Samba AD.
    /// </summary>
    public bool EnforcesSamSingleValuedRules => DirectoryType == LdapDirectoryType.ActiveDirectory;
}
