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
    /// Indicates if the connected directory is Active Directory (AD-DS or AD-LDS).
    /// Determines which delta import strategy to use.
    /// </summary>
    public bool IsActiveDirectory { get; set; }

    /// <summary>
    /// The vendor name of the directory server (e.g., "Samba Team", "Microsoft").
    /// Used for capability detection when directories claim AD compatibility but have behavioural differences.
    /// </summary>
    public string? VendorName { get; set; }

    /// <summary>
    /// Indicates if the directory supports paged search results.
    /// True AD supports paging; Samba AD claims support but returns duplicate results, so we disable it.
    /// </summary>
    public bool SupportsPaging { get; set; } = true;
}