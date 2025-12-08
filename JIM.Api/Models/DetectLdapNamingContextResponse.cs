namespace JIM.Api.Models;

/// <summary>
/// Response from the LDAP naming context detection endpoint.
/// </summary>
public class DetectLdapNamingContextResponse
{
    /// <summary>
    /// Indicates whether the detection was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// The detected default naming context (root DN) from the LDAP directory.
    /// Typical value: DC=contoso,DC=com
    /// Null if detection failed.
    /// </summary>
    public string? DefaultNamingContext { get; set; }
}
