// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;
namespace JIM.Models.Interfaces;

/// <summary>
/// Enables a Connector to check its settings against the target system in a way too slow, or too far-reaching, for
/// <see cref="IConnectorSettings.ValidateSettingValues"/>: signing in to a second service the settings name, for
/// example. JIM asks only when the quick validation has found nothing that refuses the settings, each time they are
/// saved from the portal or the REST API.
/// </summary>
public interface IConnectorSettingsCheck
{
    /// <summary>
    /// Checks the settings, returning what the check found and the facts it learnt on the way.
    /// </summary>
    /// <param name="settings">The setting values about to be saved.</param>
    /// <param name="logger">Logger for diagnostics. Never log a secret.</param>
    /// <param name="cancellationToken">Cancels the check, for example when the administrator leaves the page.</param>
    /// <returns>
    /// The findings: errors refuse the save, warnings (<see cref="ConnectorSettingValueValidationResult.Warning"/>) do
    /// not. A failure the Connector can explain is a finding, never an exception.
    /// </returns>
    public Task<ConnectorSettingsCheckResult> CheckSettingsAsync(List<ConnectedSystemSettingValue> settings, ILogger logger, CancellationToken cancellationToken);
}
