// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;
namespace JIM.Models.Interfaces;

/// <summary>
/// Enables a Connector to return a list of Partitions within a Connected System, i.e. LDAP partitions.
/// </summary>
public interface IConnectorPartitions
{
    public Task<List<ConnectorPartition>> GetPartitionsAsync(List<ConnectedSystemSettingValue> settings, ILogger logger);
}