// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;

namespace JIM.Models.Interfaces;

/// <summary>
/// A Connector some of whose settings are only judged rightly with the schema selection in hand: they say
/// something about Object Types, and only the Object Types selected for synchronisation matter, because an
/// unselected one takes no part in any Run Profile.
/// </summary>
/// <remarks>
/// JIM asks this wherever the selection or the settings can change (the Settings tab, the schema save the
/// portal makes, and the per-Object Type update the REST API and PowerShell make), and refuses a change
/// the Connector reports as invalid. It is asked of the values and the schema alone; unlike
/// <see cref="IConnectorSettings.ValidateSettingValues"/> it must never reach out to the target system,
/// because it runs on the schema tab's save path, where an unreachable target must not block the
/// administrator (see <c>ConnectedSystemServer.AreSettingValuesComplete</c> for that principle).
/// </remarks>
public interface IConnectorObjectTypeSelectionValidation
{
    /// <summary>
    /// Judges the setting values against the Object Types as they will stand: <paramref name="objectTypes"/>
    /// is the whole schema, and each one's <see cref="ConnectedSystemObjectType.Selected"/> is what counts.
    /// An empty schema (nothing imported yet) is a valid answer with nothing selected, not a reason to fail.
    /// </summary>
    /// <returns>One result per problem found; empty when the selection is one the settings can serve.</returns>
    public List<ConnectorSettingValueValidationResult> ValidateObjectTypeSelection(List<ConnectedSystemSettingValue> settingValues, IReadOnlyCollection<ConnectedSystemObjectType> objectTypes, ILogger logger);
}
