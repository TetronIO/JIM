// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// A human-readable fact about a Connected System's target directory/system, detected by the Connector from
/// data it persisted about a previous connection (for example, an LDAP directory's type, vendor, and paging
/// support, discovered from its rootDSE). Purely a display concern for the "Directory Capabilities" card on
/// the Connected System details page; never consulted by the synchronisation engine.
/// </summary>
public class ConnectorCapability
{
    /// <summary>
    /// The display label for this detected fact, e.g. "Directory Type".
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The display value for this detected fact, e.g. "Active Directory".
    /// </summary>
    public string Value { get; set; } = null!;
}
