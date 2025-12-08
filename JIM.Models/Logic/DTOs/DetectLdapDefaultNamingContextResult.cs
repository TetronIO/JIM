namespace JIM.Models.Logic.DTOs;

/// <summary>
/// Result of attempting to detect the default naming context from an LDAP directory.
/// </summary>
public class DetectLdapDefaultNamingContextResult
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

    /// <summary>
    /// Error message if detection failed.
    /// Null if detection was successful.
    /// </summary>
    public string? ErrorMessage { get; set; }
}
