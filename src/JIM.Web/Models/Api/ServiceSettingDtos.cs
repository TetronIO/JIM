using JIM.Models.Core;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of a service setting.
/// </summary>
public class ServiceSettingDto
{
    /// <summary>
    /// Unique setting key using dot notation (e.g., "ChangeTracking.CsoChanges.Enabled").
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Description of what this setting controls.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Category for grouping (e.g., "SSO", "Synchronisation", "Maintenance").
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Data type of the setting value (e.g., "String", "Boolean", "Integer").
    /// </summary>
    public string ValueType { get; set; } = string.Empty;

    /// <summary>
    /// The default value as a string.
    /// </summary>
    public string? DefaultValue { get; set; }

    /// <summary>
    /// The current overridden value, or null if using the default.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// The effective value (Value if set, otherwise DefaultValue).
    /// </summary>
    public string? EffectiveValue { get; set; }

    /// <summary>
    /// Whether this setting is read-only (e.g., mirrored from environment variables).
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// Whether the current value differs from the default.
    /// </summary>
    public bool IsOverridden { get; set; }

    /// <summary>
    /// Creates a DTO from a ServiceSetting entity.
    /// </summary>
    public static ServiceSettingDto FromEntity(ServiceSetting entity)
    {
        return new ServiceSettingDto
        {
            Key = entity.Key,
            DisplayName = entity.DisplayName,
            Description = entity.Description,
            Category = entity.Category.ToString(),
            ValueType = entity.ValueType.ToString(),
            DefaultValue = entity.DefaultValue,
            Value = entity.Value,
            EffectiveValue = entity.GetEffectiveValue(),
            IsReadOnly = entity.IsReadOnly,
            IsOverridden = entity.IsOverridden
        };
    }
}

/// <summary>
/// Request DTO for updating a service setting value.
/// </summary>
public class ServiceSettingUpdateRequestDto
{
    /// <summary>
    /// The new value for the setting as a string. Set to null to clear the override
    /// and revert to the default value.
    /// </summary>
    public string? Value { get; set; }
}
