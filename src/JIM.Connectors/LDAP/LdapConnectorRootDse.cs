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
    /// For changelog-based directories (e.g., Oracle Directory): The last change number processed.
    /// Used for delta imports — we query cn=changelog for entries with changeNumber > this value.
    /// </summary>
    public int? LastChangeNumber { get; set; }

    /// <summary>
    /// For OpenLDAP with accesslog overlay: The reqStart timestamp of the last processed entry.
    /// Used for delta imports — we query cn=accesslog for entries with reqStart > this value.
    /// Format: Generalised time (e.g., "20260326183000.000000Z").
    /// </summary>
    public string? LastAccesslogTimestamp { get; set; }

    /// <summary>
    /// The detected directory server type, determined from rootDSE capabilities during connection.
    /// Drives all directory-specific behaviour: schema discovery, external ID, delta strategy, etc.
    /// </summary>
    public LdapDirectoryType DirectoryType { get; set; } = LdapDirectoryType.Generic;

    /// <summary>
    /// The vendor name of the directory server (e.g., "Samba Team", "Microsoft", "OpenLDAP").
    /// Retained for logging and diagnostics.
    /// </summary>
    public string? VendorName { get; set; }

    // -----------------------------------------------------------------------
    // Computed properties — centralised directory-type-specific behaviour
    // -----------------------------------------------------------------------

    /// <summary>
    /// The attribute name used as the unique, immutable external identifier for directory objects.
    /// AD/Samba AD use objectGUID (binary GUID in Microsoft byte order); OpenLDAP uses entryUUID (RFC 4530, string format).
    /// </summary>
    public string ExternalIdAttributeName => DirectoryType switch
    {
        LdapDirectoryType.ActiveDirectory => "objectGUID",
        LdapDirectoryType.SambaAD => "objectGUID",
        LdapDirectoryType.OpenLDAP => "entryUUID",
        LdapDirectoryType.Generic => "entryUUID",
        _ => "entryUUID"
    };

    /// <summary>
    /// The data type of the external ID attribute in JIM's attribute model.
    /// AD/Samba AD objectGUID is a binary GUID; OpenLDAP entryUUID is a string representation of a UUID.
    /// </summary>
    public AttributeDataType ExternalIdDataType => DirectoryType switch
    {
        LdapDirectoryType.ActiveDirectory => AttributeDataType.Guid,
        LdapDirectoryType.SambaAD => AttributeDataType.Guid,
        LdapDirectoryType.OpenLDAP => AttributeDataType.Text,
        LdapDirectoryType.Generic => AttributeDataType.Text,
        _ => AttributeDataType.Text
    };

    /// <summary>
    /// Whether delta imports should use USN-based change tracking (AD/Samba AD).
    /// </summary>
    public bool UseUsnDeltaImport => DirectoryType is LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD;

    /// <summary>
    /// Whether delta imports should use the OpenLDAP accesslog overlay (cn=accesslog with reqStart timestamps).
    /// Falls back to standard changelog (cn=changelog with changeNumber) for Generic directories.
    /// </summary>
    public bool UseAccesslogDeltaImport => DirectoryType is LdapDirectoryType.OpenLDAP;

    /// <summary>
    /// Whether the directory's SAM layer enforces single-valued semantics on certain multi-valued schema attributes
    /// (e.g., 'description' on user/group objects). Applies to both Microsoft AD and Samba AD.
    /// </summary>
    public bool EnforcesSamSingleValuedRules => DirectoryType is LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD;

    /// <summary>
    /// The recommended export concurrency for this directory type.
    /// AD DS and OpenLDAP handle concurrent connections well; Samba AD and unknown servers
    /// are kept conservative due to known quirks (e.g. paged search duplicates).
    /// </summary>
    public int RecommendedExportConcurrency => DirectoryType switch
    {
        LdapDirectoryType.ActiveDirectory => 16,
        LdapDirectoryType.OpenLDAP => 16,
        LdapDirectoryType.SambaAD => LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY,
        LdapDirectoryType.Generic => LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY,
        _ => LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY
    };

    /// <summary>
    /// Whether the directory supports paged search results.
    /// Microsoft AD supports paging; Samba AD claims support but returns duplicate results.
    /// OpenLDAP supports paging via Simple Paged Results control.
    /// </summary>
    public bool SupportsPaging => DirectoryType switch
    {
        LdapDirectoryType.ActiveDirectory => true,
        LdapDirectoryType.SambaAD => false,
        LdapDirectoryType.OpenLDAP => true,
        LdapDirectoryType.Generic => true,
        _ => true
    };
}
