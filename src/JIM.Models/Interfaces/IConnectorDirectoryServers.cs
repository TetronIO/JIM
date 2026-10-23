// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;
namespace JIM.Models.Interfaces;

/// <summary>
/// Enables a Connector to list the directory servers available within a Connected System's directory, so an
/// administrator can be shown a choice rather than having to already know a hostname. Purely informational:
/// implementing this interface never causes JIM to write to any setting automatically; the setting named by
/// <see cref="DirectoryServerDiscovery"/> is filled only when the administrator selects a discovered server.
/// </summary>
public interface IConnectorDirectoryServers
{
    /// <summary>
    /// Which setting discovery fills in, and what the directory calls its servers. A property of the Connector,
    /// not of any Connected System's settings, so it can be read before anything has been configured.
    /// </summary>
    public ConnectorDirectoryServerDiscovery DirectoryServerDiscovery { get; }

    public Task<List<ConnectorDirectoryServer>> GetDirectoryServersAsync(List<ConnectedSystemSettingValue> settings, ILogger logger);
}
