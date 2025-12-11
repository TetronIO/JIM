using System.ComponentModel.DataAnnotations;

namespace JIM.Web.Models.Api;

/// <summary>
/// Request DTO for creating a new Connected System.
/// </summary>
public class CreateConnectedSystemRequest
{
    /// <summary>
    /// The name for the Connected System.
    /// </summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Optional description for the Connected System.
    /// </summary>
    [StringLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// The ID of the ConnectorDefinition to use.
    /// </summary>
    [Required]
    public int ConnectorDefinitionId { get; set; }
}

/// <summary>
/// Request DTO for updating an existing Connected System.
/// </summary>
public class UpdateConnectedSystemRequest
{
    /// <summary>
    /// The updated name for the Connected System.
    /// </summary>
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; set; }

    /// <summary>
    /// The updated description for the Connected System.
    /// </summary>
    [StringLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Updated setting values as key-value pairs where key is the setting ID.
    /// </summary>
    public Dictionary<int, ConnectedSystemSettingValueUpdate>? SettingValues { get; set; }
}

/// <summary>
/// DTO for updating a single setting value.
/// </summary>
public class ConnectedSystemSettingValueUpdate
{
    /// <summary>
    /// String value for String or StringEncrypted settings.
    /// </summary>
    public string? StringValue { get; set; }

    /// <summary>
    /// Integer value for Integer settings.
    /// </summary>
    public int? IntValue { get; set; }

    /// <summary>
    /// Checkbox/boolean value for CheckBox settings.
    /// </summary>
    public bool? CheckboxValue { get; set; }
}
