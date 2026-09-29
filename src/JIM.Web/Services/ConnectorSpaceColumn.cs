// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Services;

/// <summary>
/// The Connector Space list's optional columns: the ones an administrator can hide, per Connected System, through
/// the list's Columns menu. The names are part of the stored preference key, so renaming one forgets every saved
/// choice for it.
/// </summary>
public enum ConnectorSpaceColumn
{
    /// <summary>The External ID column (the Connected System's anchor attribute, e.g. entryUUID).</summary>
    ExternalId,

    /// <summary>The Secondary External ID column (e.g. an LDAP directory's distinguishedName).</summary>
    SecondaryExternalId
}
