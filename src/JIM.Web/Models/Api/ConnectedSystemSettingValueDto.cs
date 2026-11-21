// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// One of a Connected System's Connector settings and the value it holds. An encrypted setting, such as a password or
/// a client secret, reports only whether a value is held, never the value.
/// </summary>
public class ConnectedSystemSettingValueDto
{
    /// <summary>The setting's ID, by which Set-JIMConnectedSystem and the update endpoint change its value.</summary>
    public int SettingId { get; set; }

    /// <summary>The setting's name, as the Connected System's settings show it.</summary>
    public string Name { get; set; } = null!;

    /// <summary>The kind of value the setting takes, which says which of the value properties holds it.</summary>
    public ConnectedSystemSettingType Type { get; set; }

    /// <summary>The value of a text, drop-down or file setting; null for every other kind, and always for an encrypted setting.</summary>
    public string? StringValue { get; set; }

    /// <summary>The value of a number setting, or null when it has none or the setting is of another kind.</summary>
    public int? IntValue { get; set; }

    /// <summary>Whether a tick-box setting is ticked; null for every other kind.</summary>
    public bool? CheckboxValue { get; set; }

    /// <summary>
    /// Whether the setting holds a value. For an encrypted setting this is the only thing reported. A tick-box setting
    /// always holds one.
    /// </summary>
    public bool HasValue { get; set; }

    /// <summary>The value one stored setting holds, or null for a setting that only lays out the form (a heading, a label or a divider).</summary>
    public static ConnectedSystemSettingValueDto? FromEntity(ConnectedSystemSettingValue value)
    {
        var type = value.Setting.Type;
        if (type is ConnectedSystemSettingType.Heading or ConnectedSystemSettingType.Label or ConnectedSystemSettingType.Divider)
            return null;

        var dto = new ConnectedSystemSettingValueDto { SettingId = value.Setting.Id, Name = value.Setting.Name ?? string.Empty, Type = type };
        switch (type)
        {
            case ConnectedSystemSettingType.StringEncrypted:
                dto.HasValue = !string.IsNullOrEmpty(value.StringEncryptedValue);
                break;
            case ConnectedSystemSettingType.Integer:
                dto.IntValue = value.IntValue;
                dto.HasValue = value.IntValue.HasValue;
                break;
            case ConnectedSystemSettingType.CheckBox:
                dto.CheckboxValue = value.CheckboxValue;
                dto.HasValue = true;
                break;
            default:
                dto.StringValue = value.StringValue;
                dto.HasValue = !string.IsNullOrEmpty(value.StringValue);
                break;
        }

        return dto;
    }
}
