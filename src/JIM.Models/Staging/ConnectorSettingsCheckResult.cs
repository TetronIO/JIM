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

    /// <summary>
    /// What the check learnt, to show with the Connector's detected facts once the settings are saved: for example the
    /// version of a system the settings name. Empty when it learnt nothing, which clears what an earlier check learnt.
    /// </summary>
    public List<ConnectorCapability> Facts { get; set; } = [];
}
