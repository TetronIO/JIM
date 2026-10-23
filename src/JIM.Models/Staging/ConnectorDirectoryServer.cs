// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Represents a directory server discovered within a Connected System's directory (for the LDAP Connector, a
/// domain controller in an Active Directory or Samba AD forest). Used by the Discover action (issue #1167) to help
/// an administrator choose a value for the setting the Connector names in
/// <see cref="ConnectorDirectoryServerDiscovery.TargetSettingName"/>; discovery only ever informs that choice, it
/// never writes to the setting itself.
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
