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

    /// <summary>
    /// The id of the Connected System setting this server's host name is a value for (for the LDAP Connector,
    /// Preferred Domain Controller): the key to use when setting it with <c>Set-JIMConnectedSystem -SettingValues</c>
    /// or the update endpoint. Null only if the Connected System does not carry the setting its Connector names.
    /// </summary>
    public int? TargetSettingId { get; set; }

    /// <summary>
    /// The name of the Connected System setting this server's host name is a value for, e.g. "Preferred Domain
    /// Controller".
    /// </summary>
    public string TargetSettingName { get; set; } = null!;

    /// <param name="model">The discovered server.</param>
    /// <param name="targetSettingName">The setting the Connector says discovery fills.</param>
    /// <param name="targetSettingId">That setting's id on the Connected System, if it carries it.</param>
    public static ConnectedSystemDirectoryServerDto FromModel(ConnectorDirectoryServer model, string targetSettingName, int? targetSettingId)
    {
        return new ConnectedSystemDirectoryServerDto
        {
            HostName = model.HostName,
            Site = model.Site,
            TargetSettingId = targetSettingId,
            TargetSettingName = targetSettingName
        };
    }
}
