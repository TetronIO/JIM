// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// The phases the LDAP Connector performs inside the JIM phase that calls it, and the labels an
/// administrator sees for them (#454). Declared through <see cref="JIM.Models.Interfaces.IConnectorPhases"/>
/// so the steps show up before they run, and entered by key as the work progresses.
/// </summary>
internal static class LdapConnectorPhases
{
    /// <summary>Reading the directory's root DSE: what it supports, and where its change watermark stands.</summary>
    internal const string RootDse = "root-dse";

    internal const string RootDseName = "Querying the directory";

    /// <summary>Asking the directory what has changed since the last import. Delta Imports only.</summary>
    internal const string QueryChanges = "query-changes";

    internal const string QueryChangesName = "Querying changes";

    /// <summary>Fetching objects, a page at a time, from each selected container.</summary>
    internal const string Fetch = "fetch";

    internal const string FetchName = "Fetching objects";

    /// <summary>Asking the directory which objects have been deleted. Delta Imports against Active Directory.</summary>
    internal const string QueryDeletions = "query-deletions";

    internal const string QueryDeletionsName = "Querying deleted objects";
}
