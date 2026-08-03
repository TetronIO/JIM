// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;
namespace JIM.Models.Interfaces;

/// <summary>
/// Enables a Connector to list the directory servers (domain controllers) available within a Connected System's
/// directory, so an administrator can be shown a choice rather than having to already know a hostname. Purely
/// informational: implementing this interface never causes JIM to write to any setting automatically. Introduced
/// for the LDAP Connector's Discover Domain Controllers action (issue #1167), which fills the free-text Preferred
/// Domain Controller setting only when the administrator selects a discovered server.
/// </summary>
public interface IConnectorDirectoryServers
{
    public Task<List<ConnectorDirectoryServer>> GetDirectoryServersAsync(List<ConnectedSystemSettingValue> settings, ILogger logger);
}
