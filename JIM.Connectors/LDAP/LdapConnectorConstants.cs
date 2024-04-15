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
}