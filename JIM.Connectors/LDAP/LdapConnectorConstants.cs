namespace JIM.Connectors.LDAP;

internal static class LdapConnectorConstants
{
    internal static string SETTING_AUTH_TYPE_SIMPLE => "Simple";

    internal static string SETTING_AUTH_TYPE_NTLM => "NTLM";

    /// <summary>
    /// Indicates the directory is AD-DS.
    /// </summary>
    internal static string LDAP_CAP_ACTIVE_DIRECTORY_OID => "1.2.840.113556.1.4.800";

    /// <summary>
    /// Indicates the directory is AD-LDS.
    /// </summary>
    internal static string LDAP_CAP_ACTIVE_DIRECTORY_ADAM_OID => "1.2.840.113556.1.4.1851";

    /// <summary>
    /// Active Directory systemFlags value indicating a domain partition (NC).
    /// Domain partitions have systemFlags=3 which combines:
    /// - FLAG_ATTR_NOT_REPLICATED (1) - Not replicated to other DCs
    /// - FLAG_ATTR_REQ_PARTIAL_SET_MEMBER (2) - Required for partial attribute set
    /// Non-domain partitions (Configuration, Schema) have different values and should be hidden by default.
    /// </summary>
    internal const string SYSTEM_FLAGS_DOMAIN_PARTITION = "3";

    // Export delete behaviour options
    internal const string DELETE_BEHAVIOUR_DELETE = "Delete";
    internal const string DELETE_BEHAVIOUR_DISABLE = "Disable";

    /// <summary>
    /// Active Directory userAccountControl flag for disabled accounts.
    /// When this bit is set (0x2), the account is disabled.
    /// </summary>
    internal const int UAC_ACCOUNTDISABLE = 0x2;
}