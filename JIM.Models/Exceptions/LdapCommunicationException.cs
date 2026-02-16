namespace JIM.Models.Exceptions;

/// <summary>
/// Represents an LDAP communication or directory error that is user-actionable.
/// Wraps third-party LDAP exceptions at the connector boundary to prevent library-specific
/// types from leaking into the application layer.
///
/// Common causes: incorrect server configuration, network connectivity issues,
/// authentication failures, insufficient permissions.
/// </summary>
public class LdapCommunicationException : OperationalException
{
    public LdapCommunicationException(string message) : base(message) { }

    public LdapCommunicationException(string message, Exception innerException)
        : base(message, innerException) { }
}
