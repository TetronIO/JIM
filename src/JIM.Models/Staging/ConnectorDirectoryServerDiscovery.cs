// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What a Connector implementing <see cref="Interfaces.IConnectorDirectoryServers"/> says about its directory
/// server discovery, so the portal can offer the Discover action without knowing which Connector it is talking to.
/// </summary>
/// <param name="TargetSettingName">The name of the free-text Connectivity setting a selected server's host name fills
/// in; the portal shows the Discover action beside that field and nowhere else. Must match a String setting the
/// Connector declares in <c>GetSettings()</c>.</param>
/// <param name="ServerTerm">What the directory calls its servers, plural and in lower case as it reads mid-sentence,
/// e.g. "domain controllers". Used in the action's title, progress and empty-result text.</param>
public sealed record ConnectorDirectoryServerDiscovery(string TargetSettingName, string ServerTerm);
