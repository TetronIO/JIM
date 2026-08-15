// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
    /// Sends an LDAP request synchronously, giving up if the directory has not answered within the timeout.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="SendRequest(DirectoryRequest)"/>, which waits as long as the connection's own
    /// timeout allows. Used where a slow directory must not be allowed to hold up something an administrator is
    /// waiting on, such as the Container object counts folded into a hierarchy retrieval.
    /// </remarks>
    DirectoryResponse SendRequest(DirectoryRequest request, TimeSpan timeout);

    /// <summary>
    /// Sends an LDAP request asynchronously using the APM pattern wrapper.
    /// Enables concurrent LDAP operations on the same connection via message-ID multiplexing.
    /// </summary>
    Task<DirectoryResponse> SendRequestAsync(DirectoryRequest request);
}
