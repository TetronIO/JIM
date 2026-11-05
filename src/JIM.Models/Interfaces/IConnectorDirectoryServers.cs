// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;
namespace JIM.Models.Interfaces;

/// <summary>
/// Enables a Connector to list the servers a Connected System's directory knows of, so an administrator can be shown
/// a choice rather than having to already know a hostname: one list per setting that names a server (the LDAP
/// Connector's domain controllers and Exchange servers). Purely informational: implementing this interface never causes
/// JIM to write to any setting automatically; a setting named in <see cref="DirectoryServerDiscoveries"/> is filled only
/// when the administrator selects a discovered server.
/// </summary>
public interface IConnectorDirectoryServers
{
    /// <summary>
    /// What the Connector can discover: for each setting it fills, what the directory calls those servers. In the order
    /// the Connector offers them; the first is what a caller naming no setting gets. A property of the Connector, not
    /// of any Connected System's settings, so it can be read before anything has been configured.
    /// </summary>
    public IReadOnlyList<ConnectorDirectoryServerDiscovery> DirectoryServerDiscoveries { get; }

    /// <summary>
    /// Discovers the servers for one of <see cref="DirectoryServerDiscoveries"/>.
    /// </summary>
    /// <param name="settings">The Connected System's settings, to connect with.</param>
    /// <param name="targetSettingName">The setting the servers are for: the discovery's <see cref="ConnectorDirectoryServerDiscovery.TargetSettingName"/>.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <exception cref="NotSupportedException">No discovery fills that setting, or the directory cannot answer it.</exception>
    public Task<List<ConnectorDirectoryServer>> GetDirectoryServersAsync(List<ConnectedSystemSettingValue> settings, string targetSettingName, ILogger logger);
}
