// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;

namespace JIM.Connectors.LDAP;

/// <summary>
/// The production <see cref="ILdapDedicatedConnection"/>: a bound connection to the directory, owned by the one search
/// it serves and closed with it.
/// </summary>
internal sealed class LdapDedicatedConnection : LdapOperationExecutor, ILdapDedicatedConnection
{
    private readonly LdapConnection _connection;

    internal LdapDedicatedConnection(LdapConnection connection) : base(connection)
    {
        _connection = connection;
    }

    public void Dispose() => _connection.Dispose();
}
