// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// A directory server discovered within a Connected System's directory (for the LDAP Connector, a domain
/// controller). Purely informational: JIM never writes it to any setting automatically; only an administrator's
/// own selection does.
/// </summary>
/// <remarks>Issue #1167.</remarks>
public class ConnectedSystemDirectoryServerDto
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

    public static ConnectedSystemDirectoryServerDto FromModel(ConnectorDirectoryServer model)
    {
        return new ConnectedSystemDirectoryServerDto
        {
            HostName = model.HostName,
            Site = model.Site
        };
    }
}
