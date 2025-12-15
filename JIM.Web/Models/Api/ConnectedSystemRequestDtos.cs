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

/// <summary>
/// Request DTO for updating a Connected System Object Type.
/// </summary>
public class UpdateConnectedSystemObjectTypeRequest
{
    /// <summary>
    /// Whether this object type is selected for management by JIM.
    /// </summary>
    public bool? Selected { get; set; }

    /// <summary>
    /// Controls whether Metaverse Object attribute values contributed by a Connected System Object of this type
    /// should be removed when the CSO is obsoleted.
    /// </summary>
    public bool? RemoveContributedAttributesOnObsoletion { get; set; }
}

/// <summary>
/// Request DTO for updating a Connected System Attribute.
/// </summary>
public class UpdateConnectedSystemAttributeRequest
{
    /// <summary>
    /// Whether this attribute is selected for management by JIM.
    /// </summary>
    public bool? Selected { get; set; }

    /// <summary>
    /// Indicates if this attribute is a unique identifier for the object type in the connected system.
    /// </summary>
    public bool? IsExternalId { get; set; }

    /// <summary>
    /// Indicates if this attribute is used as a secondary identifier by the connected system (e.g., DN in LDAP).
    /// </summary>
    public bool? IsSecondaryExternalId { get; set; }
}
