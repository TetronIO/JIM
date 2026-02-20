using System.DirectoryServices.Protocols;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Production implementation of <see cref="ILdapOperationExecutor"/> that delegates to a real LdapConnection.
/// Supports both synchronous and asynchronous LDAP operations.
/// </summary>
internal class LdapOperationExecutor : ILdapOperationExecutor
{
    private readonly LdapConnection _connection;

    internal LdapOperationExecutor(LdapConnection connection)
    {
        _connection = connection;
    }

    public DirectoryResponse SendRequest(DirectoryRequest request)
        => _connection.SendRequest(request);

    public Task<DirectoryResponse> SendRequestAsync(DirectoryRequest request)
        => _connection.SendRequestAsync(request);
}
