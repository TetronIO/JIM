// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What one directory server discovery found: the servers, and the forest they were found in, so the portal can say
/// where it looked ("JIM did not find any Exchange servers in the panoply.local forest") rather than "in this
/// directory".
/// </summary>
/// <param name="ForestName">The DNS name of the forest the servers belong to, such as "panoply.local"; null when the
/// Connector cannot tell.</param>
/// <param name="Servers">The servers found, in the order the directory returned them; empty when there are none.</param>
public sealed record ConnectorDirectoryServerDiscoveryResult(string? ForestName, List<ConnectorDirectoryServer> Servers);
