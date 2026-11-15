// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// A connection opened for one paged search alone, and closed when that search is done. A directory whose paging
/// cookies are scoped to the connection (OpenLDAP, 389 Directory Server, a generic directory) forgets every outstanding
/// cookie on a connection the moment another search is sent on it, so each search a Full Import pages through needs a
/// connection of its own. Disposing it closes the connection.
/// </summary>
/// <remarks>
/// An interface rather than the platform's <c>LdapConnection</c> so a test can script the directory each search
/// reaches, and see when each connection is closed.
/// </remarks>
internal interface ILdapDedicatedConnection : ILdapOperationExecutor, IDisposable;
