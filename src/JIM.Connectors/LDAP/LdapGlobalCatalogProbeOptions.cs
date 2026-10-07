// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// Where the uniqueness probe may search an Active Directory forest's Global Catalog for a forest-wide value (#1940),
/// and how it opens a connection there.
/// </summary>
/// <param name="ConfiguredServer">
/// The Global Catalog Server setting, or null when it is blank. When set, forest-wide attributes are always searched
/// through it.
/// </param>
/// <param name="ConnectedServer">
/// The server the probe connection itself reached (the Preferred Domain Controller, the pinned domain controller, or
/// Host), searched as the Global Catalog when no server is configured, the forest has more than one domain, and it
/// says it is one.
/// </param>
/// <param name="Port">The Global Catalog port: 3268, or 3269 with LDAPS.</param>
/// <param name="Open">
/// Opens a bound connection to a Global Catalog server on <paramref name="Port"/> with the Connected System's
/// credentials and TLS settings, waiting no longer than the given time; throws when it cannot. Called at most once
/// per probe session.
/// </param>
internal sealed record LdapGlobalCatalogProbeOptions(
    string? ConfiguredServer,
    string ConnectedServer,
    int Port,
    Func<string, TimeSpan, ILdapOperationExecutor> Open);
