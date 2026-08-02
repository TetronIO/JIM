// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Represents a directory server (domain controller) discovered within a Connected System's directory, i.e. an
/// Active Directory or Samba AD forest. Used by the Discover Domain Controllers action (issue #1167) to help an
/// administrator choose a value for the LDAP Connector's Preferred Domain Controller setting; discovery only ever
/// informs that choice, it never writes to the setting itself.
/// </summary>
public class ConnectorDirectoryServer
{
    /// <summary>
    /// The fully qualified domain name of the directory server, e.g. "dc01.corp.local".
    /// </summary>
    public string HostName { get; set; } = null!;

    /// <summary>
    /// The Active Directory Site the server belongs to, e.g. "Default-First-Site-Name". Null for directories that
    /// have no concept of Sites.
    /// </summary>
    public string? Site { get; set; }
}
