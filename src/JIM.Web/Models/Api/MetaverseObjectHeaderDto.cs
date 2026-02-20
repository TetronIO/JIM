using JIM.Models.Core;
using JIM.Models.Core.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// Lightweight DTO for metaverse objects in list views.
/// </summary>
public class MetaverseObjectHeaderDto
{
    /// <summary>
    /// The unique identifier (GUID) of the metaverse object.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// When the object was created.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// The display name of the object (always included).
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// The current status of the object.
    /// </summary>
    public MetaverseObjectStatus Status { get; set; }

    /// <summary>
    /// The object type ID.
    /// </summary>
    public int TypeId { get; set; }

    /// <summary>
    /// The object type name.
    /// </summary>
    public string TypeName { get; set; } = null!;

    /// <summary>
    /// Additional attribute values requested via the 'attributes' query parameter.
    /// Key is the attribute name, value is the string representation of the attribute value.
    /// DisplayName is not included here as it has its own property.
    /// </summary>
    public Dictionary<string, string?> Attributes { get; set; } = new();

    /// <summary>
    /// Creates a DTO from a MetaverseObjectHeader.
    /// </summary>
    public static MetaverseObjectHeaderDto FromHeader(MetaverseObjectHeader header)
    {
        var dto = new MetaverseObjectHeaderDto
        {
            Id = header.Id,
            Created = header.Created,
            DisplayName = header.DisplayName,
            Status = header.Status,
            TypeId = header.TypeId,
            TypeName = header.TypeName
        };

        // Add any additional attributes (excluding DisplayName which has its own property)
        foreach (var av in header.AttributeValues)
        {
            if (av.Attribute.Name != Constants.BuiltInAttributes.DisplayName)
            {
                dto.Attributes[av.Attribute.Name] = av.StringValue;
            }
        }

        return dto;
    }
}
