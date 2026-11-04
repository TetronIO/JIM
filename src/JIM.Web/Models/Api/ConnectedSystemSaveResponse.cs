// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// A Connected System as saved, with what checking its settings raised that did not stop the save.
/// </summary>
public class ConnectedSystemSaveResponse : ConnectedSystemDetailDto
{
    /// <summary>
    /// Warnings the Connector raised about the settings the request wrote: something that will not work as the
    /// settings stand, such as an account that may not create one kind of Exchange recipient. The save went ahead
    /// regardless. Always present; empty when there are none, and when the request wrote no settings.
    /// </summary>
    public List<ConnectedSystemSettingWarningDto> SettingWarnings { get; set; } = [];

    /// <summary>
    /// Builds the response from the Connected System as saved and the findings from checking its settings.
    /// </summary>
    /// <param name="saved">The Connected System as stored after the save.</param>
    /// <param name="settingFindings">Every finding from checking the settings; only the warnings are kept.</param>
    public static ConnectedSystemSaveResponse FromSave(ConnectedSystem saved, IEnumerable<ConnectorSettingValueValidationResult> settingFindings)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(settingFindings);

        var detail = FromEntity(saved);
        var response = new ConnectedSystemSaveResponse
        {
            SettingWarnings = settingFindings
                .Where(finding => finding.IsWarning)
                .Select(finding => new ConnectedSystemSettingWarningDto { Setting = finding.SettingValue?.Setting?.Name, Message = finding.ErrorMessage! })
                .ToList()
        };

        // Copy every detail property, so a field added to the detail later reaches this response without anyone having
        // to remember it here.
        foreach (var property in typeof(ConnectedSystemDetailDto).GetProperties().Where(property => property.CanRead && property.CanWrite))
            property.SetValue(response, property.GetValue(detail));

        return response;
    }
}
