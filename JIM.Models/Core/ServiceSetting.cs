using System.ComponentModel.DataAnnotations;

namespace JIM.Models.Core;

/// <summary>
/// Represents a single configurable service setting with metadata.
/// Settings can be read-only (mirrored from environment variables) or editable via admin UI.
/// </summary>
public class ServiceSetting
{
    /// <summary>
    /// Primary key - the unique setting key (e.g., "SSO.Authority", "SSO.ClientId").
    /// Uses dot notation for categorisation.
    /// </summary>
    [Key]
    [Required]
    [MaxLength(100)]
    public string Key { get; set; } = null!;

    /// <summary>
    /// Human-readable display name (e.g., "SSO authority").
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string DisplayName { get; set; } = null!;

    /// <summary>
    /// Detailed description of what this setting controls.
    /// </summary>
    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Category for grouping in the admin UI (e.g., SSO, Synchronisation, Maintenance).
    /// </summary>
    public ServiceSettingCategory Category { get; set; }

    /// <summary>
    /// The data type of this setting's value.
    /// </summary>
    public ServiceSettingValueType ValueType { get; set; }

    /// <summary>
    /// The default value as a string (converted based on ValueType).
    /// </summary>
    [MaxLength(2000)]
    public string? DefaultValue { get; set; }

    /// <summary>
    /// The current value as a string (null means use DefaultValue).
    /// </summary>
    [MaxLength(2000)]
    public string? Value { get; set; }

    /// <summary>
    /// If true, this setting cannot be modified via the admin UI.
    /// Typically used for settings mirrored from environment variables.
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// For enum-type settings, the fully qualified enum type name.
    /// </summary>
    [MaxLength(500)]
    public string? EnumTypeName { get; set; }

    /// <summary>
    /// When this setting was last modified.
    /// </summary>
    public DateTime? LastModified { get; set; }

    /// <summary>
    /// Who last modified this setting (display name or "System").
    /// </summary>
    [MaxLength(200)]
    public string? LastModifiedBy { get; set; }

    /// <summary>
    /// Returns the effective value (Value if set, otherwise DefaultValue).
    /// </summary>
    public string? GetEffectiveValue() => Value ?? DefaultValue;

    /// <summary>
    /// Returns true if the current value differs from the default.
    /// </summary>
    public bool IsOverridden => Value != null && Value != DefaultValue;

    public override string ToString() => $"{Key}: {GetEffectiveValue()}";
}
