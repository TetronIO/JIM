namespace JIM.Connectors.LDAP;

/// <summary>
/// Holder of key synchronisation-related information about an LDAP directory from it's rootDSE object.
/// </summary>
internal struct LdapConnectorRootDse
{
    public string? DnsHostName { get; set; }
    public int? HighestCommittedUsn { get; set; }
}