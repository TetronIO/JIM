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

    // LDAPS settings
    internal const int DEFAULT_LDAPS_PORT = 636;
    internal const int DEFAULT_LDAP_PORT = 389;

    // Certificate validation options
    internal const string CERT_VALIDATION_FULL = "Full Validation";
    internal const string CERT_VALIDATION_SKIP = "Skip Validation (Not Recommended)";

    // Retry settings
    internal const int DEFAULT_MAX_RETRIES = 3;
    internal const int DEFAULT_RETRY_DELAY_MS = 1000;

    // Export concurrency settings
    internal const int DEFAULT_EXPORT_CONCURRENCY = 4;
    internal const int MAX_EXPORT_CONCURRENCY = 8;

    /// <summary>
    /// LDAP_SERVER_SHOW_DELETED_OID - Server control that allows searching for deleted (tombstone) objects.
    /// When included in a search request, the directory returns objects from the Deleted Objects container.
    /// Required for delta import deletion detection in Active Directory.
    /// </summary>
    internal const string LDAP_SERVER_SHOW_DELETED_OID = "1.2.840.113556.1.4.417";

    /// <summary>
    /// Attribute names where Active Directory's SAM layer enforces single-valued behaviour despite the
    /// LDAP schema declaring them as multi-valued. This applies to all SAM-managed object classes
    /// (user, group, computer, samDomain, samServer, inetOrgPerson).
    /// <para>
    /// In both Microsoft AD and Samba AD, the Security Account Manager (SAM) rejects attempts to write
    /// more than one value to these attributes on security principals. The LDAP schema says multi-valued
    /// (no SINGLE-VALUE constraint per RFC 4519), but the SAM layer silently enforces single-valued
    /// semantics. Generic LDAP directories (OpenLDAP, 389DS) treat these as genuinely multi-valued.
    /// </para>
    /// <para>
    /// Currently only 'description' has this behaviour. All other SAM-managed attributes (sAMAccountName,
    /// userPrincipalName, cn, etc.) are already declared as single-valued in the LDAP schema itself.
    /// </para>
    /// </summary>
    internal static readonly HashSet<string> SAM_ENFORCED_SINGLE_VALUED_ATTRIBUTES = new(StringComparer.OrdinalIgnoreCase)
    {
        "description"
    };

    /// <summary>
    /// LDAP object class names managed by Active Directory's Security Account Manager (SAM).
    /// These classes have SAM-layer attribute plurality enforcement that differs from the LDAP schema.
    /// Includes both structural classes and their common subclasses.
    /// </summary>
    internal static readonly HashSet<string> SAM_MANAGED_OBJECT_CLASSES = new(StringComparer.OrdinalIgnoreCase)
    {
        "user",
        "computer",
        "inetOrgPerson",
        "group",
        "samDomain",
        "samServer"
    };
}