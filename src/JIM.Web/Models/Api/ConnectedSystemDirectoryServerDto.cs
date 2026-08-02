// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of a directory server (domain controller) discovered within a Connected System's
/// directory (issue #1167). Purely informational: JIM never writes this back to the Preferred Domain Controller
/// setting automatically; only an administrator's own selection does.
/// </summary>
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
