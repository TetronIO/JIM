using System.DirectoryServices.Protocols;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Abstraction over LDAP connection operations to enable unit testing.
/// LdapConnection is a sealed class and cannot be mocked directly with Moq.
/// Production code uses <see cref="LdapOperationExecutor"/> which delegates to a real LdapConnection.
/// </summary>
internal interface ILdapOperationExecutor
{
    /// <summary>
    /// Sends an LDAP request synchronously.
    /// </summary>
    DirectoryResponse SendRequest(DirectoryRequest request);

    /// <summary>
    /// Sends an LDAP request asynchronously using the APM pattern wrapper.
    /// Enables concurrent LDAP operations on the same connection via message-ID multiplexing.
    /// </summary>
    Task<DirectoryResponse> SendRequestAsync(DirectoryRequest request);
}
