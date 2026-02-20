using System.DirectoryServices.Protocols;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Extension methods for LdapConnection to provide Task-based async operations.
/// Wraps the legacy APM pattern (BeginSendRequest/EndSendRequest) using Task.Factory.FromAsync.
/// The underlying native LDAP library uses polling rather than true async callbacks,
/// but this still enables multiple concurrent in-flight requests on the same connection
/// via LDAP message-ID multiplexing.
/// </summary>
internal static class LdapConnectionExtensions
{
    /// <summary>
    /// Sends an LDAP request asynchronously using the APM pattern wrapper.
    /// Enables multiple concurrent requests on the same LdapConnection via message-ID multiplexing.
    /// </summary>
    internal static Task<DirectoryResponse> SendRequestAsync(
        this LdapConnection connection,
        DirectoryRequest request)
    {
        return Task.Factory.FromAsync(
            (callback, state) => connection.BeginSendRequest(
                request,
                PartialResultProcessing.NoPartialResultSupport,
                callback,
                state),
            connection.EndSendRequest,
            null);
    }
}
