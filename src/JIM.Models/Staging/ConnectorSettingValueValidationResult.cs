// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Represents the result of validating a setting value.
/// Can refer to a specific setting value, or generally to all setting values, i.e. in the case of dependent or aggregates setting value requirements.
/// </summary>
/// <remarks>
/// If not valid, do supply an error message so that the user can be informed of how to resolve the issue. A warning
/// (<see cref="Severity"/> of <see cref="ConnectorSettingValidationSeverity.Warning"/>) is valid: it never refuses a
/// save, and its message tells the administrator what will not work as the settings stand.
/// </remarks>
public class ConnectorSettingValueValidationResult
{
    public ConnectedSystemSettingValue? SettingValue { get; set; }

    public bool IsValid { get; set; }

    /// <summary>
    /// What is wrong, for an error; what will not work, for a warning.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Whether this refuses the settings (an error) or only tells the administrator something (a warning). An error
    /// unless set otherwise; use <see cref="Warning"/> to make a warning.
    /// </summary>
    public ConnectorSettingValidationSeverity Severity { get; set; } = ConnectorSettingValidationSeverity.Error;

    /// <summary>
    /// Whether this is a warning to show: valid, with something to say. Anything that is not valid is an error,
    /// whatever its <see cref="Severity"/> says.
    /// </summary>
    public bool IsWarning => IsValid && Severity == ConnectorSettingValidationSeverity.Warning && !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>
    /// If an unhandled exception was encountered whilst validating the setting values, it can be made available here.
    /// </summary>
    public Exception? Exception { get; set; }

    /// <summary>
    /// A finding that does not refuse the settings: they save, and the administrator is told.
    /// </summary>
    /// <param name="settingValue">The setting it is about, or null when it is about the settings as a whole.</param>
    /// <param name="message">What will not work as the settings stand, and what to do about it.</param>
    public static ConnectorSettingValueValidationResult Warning(ConnectedSystemSettingValue? settingValue, string message) => new()
    {
        SettingValue = settingValue,
        IsValid = true,
        ErrorMessage = message,
        Severity = ConnectorSettingValidationSeverity.Warning
    };
}
