// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What a Connector's settings check found (<see cref="Interfaces.IConnectorSettingsCheck"/>).
/// </summary>
public class ConnectorSettingsCheckResult
{
    /// <summary>
    /// The findings: errors refuse the settings, warnings do not. Empty when all is well.
    /// </summary>
    public List<ConnectorSettingValueValidationResult> Results { get; set; } = [];
}
