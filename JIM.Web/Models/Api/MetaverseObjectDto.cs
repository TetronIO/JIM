using JIM.Models.Core;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of a MetaverseObject for detail views.
/// </summary>
public class MetaverseObjectDto
{
    public Guid Id { get; set; }
    public DateTime Created { get; set; }
    public DateTime? LastUpdated { get; set; }
    public string? DisplayName { get; set; }
    public MetaverseObjectStatus Status { get; set; }
    public DateTime? ScheduledDeletionDate { get; set; }
    public MetaverseObjectTypeDto Type { get; set; } = null!;
    public List<MetaverseObjectAttributeValueDto> AttributeValues { get; set; } = new();
    public List<ConnectedSystemObjectReferenceDto> ConnectedSystemObjects { get; set; } = new();

    /// <summary>
    /// Creates a DTO from a MetaverseObject entity.
    /// </summary>
    public static MetaverseObjectDto FromEntity(MetaverseObject entity)
    {
        return new MetaverseObjectDto
        {
            Id = entity.Id,
            Created = entity.Created,
            LastUpdated = entity.LastUpdated,
            DisplayName = entity.DisplayName,
            Status = entity.Status,
            ScheduledDeletionDate = entity.ScheduledDeletionDate,
            Type = MetaverseObjectTypeDto.FromEntity(entity.Type),
            AttributeValues = entity.AttributeValues
                .Select(MetaverseObjectAttributeValueDto.FromEntity)
                .ToList(),
            ConnectedSystemObjects = entity.ConnectedSystemObjects
                .Select(ConnectedSystemObjectReferenceDto.FromEntity)
                .ToList()
        };
    }
}

/// <summary>
/// Lightweight API representation of a MetaverseObjectType.
/// </summary>
public class MetaverseObjectTypeDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;

    public static MetaverseObjectTypeDto FromEntity(MetaverseObjectType entity)
    {
        return new MetaverseObjectTypeDto
        {
            Id = entity.Id,
            Name = entity.Name
        };
    }
}

/// <summary>
/// API representation of a MetaverseObjectAttributeValue.
/// </summary>
public class MetaverseObjectAttributeValueDto
{
    public Guid Id { get; set; }
    public int AttributeId { get; set; }
    public string AttributeName { get; set; } = null!;
    public AttributeDataType AttributeType { get; set; }
    public AttributePlurality AttributePlurality { get; set; }
    public string? StringValue { get; set; }
    public DateTime? DateTimeValue { get; set; }
    public int? IntValue { get; set; }
    public Guid? GuidValue { get; set; }
    public bool? BoolValue { get; set; }
    public Guid? ReferenceValueId { get; set; }
    public string? ReferenceValueDisplayName { get; set; }
    public int? ContributedBySystemId { get; set; }
    public string? ContributedBySystemName { get; set; }

    public static MetaverseObjectAttributeValueDto FromEntity(MetaverseObjectAttributeValue entity)
    {
        return new MetaverseObjectAttributeValueDto
        {
            Id = entity.Id,
            AttributeId = entity.AttributeId,
            AttributeName = entity.Attribute?.Name ?? string.Empty,
            AttributeType = entity.Attribute?.Type ?? AttributeDataType.NotSet,
            AttributePlurality = entity.Attribute?.AttributePlurality ?? AttributePlurality.SingleValued,
            StringValue = entity.StringValue,
            DateTimeValue = entity.DateTimeValue,
            IntValue = entity.IntValue,
            GuidValue = entity.GuidValue,
            BoolValue = entity.BoolValue,
            ReferenceValueId = entity.ReferenceValueId,
            ReferenceValueDisplayName = entity.ReferenceValue?.DisplayName,
            ContributedBySystemId = entity.ContributedBySystem?.Id,
            ContributedBySystemName = entity.ContributedBySystem?.Name
        };
    }
}

/// <summary>
/// Lightweight reference to a ConnectedSystemObject from a MetaverseObject.
/// </summary>
public class ConnectedSystemObjectReferenceDto
{
    public Guid Id { get; set; }
    public int ConnectedSystemId { get; set; }
    public string ConnectedSystemName { get; set; } = null!;
    public string? DisplayName { get; set; }

    public static ConnectedSystemObjectReferenceDto FromEntity(JIM.Models.Staging.ConnectedSystemObject entity)
    {
        return new ConnectedSystemObjectReferenceDto
        {
            Id = entity.Id,
            ConnectedSystemId = entity.ConnectedSystem?.Id ?? 0,
            ConnectedSystemName = entity.ConnectedSystem?.Name ?? string.Empty,
            DisplayName = entity.DisplayNameOrId
        };
    }
}
