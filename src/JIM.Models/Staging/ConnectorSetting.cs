// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Defines a setting that a Connector will ask the administrator to supply a value for. 
/// JIM will discover these when inspecting a Connector and create internal copies of it's own, that it will then reference in ConnectedSystemSettingValue objects.
/// </summary>
public class ConnectorSetting
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public ConnectedSystemSettingCategory Category { get; set; }

    public ConnectedSystemSettingType Type { get; set; }

    public bool? DefaultCheckboxValue { get; set; }

    public string? DefaultStringValue { get; set; }

    public int? DefaultIntValue { get; set; }

    public List<string>? DropDownValues { get; set; }

    public bool Required { get; set; }

    /// <summary>
    /// When set, at least one setting in the same named group must have a value supplied by the administrator.
    /// Use for either/or requirements, where individual settings are optional but the group as a whole is required.
    /// Grouped settings should share the same Category and be declared consecutively so the UI can render them together.
    /// Enforced generically by JIM when validating Connected System settings; see ConnectorSettingGroupValidator.
    /// </summary>
    public string? RequiredGroup { get; set; }
}