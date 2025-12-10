using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// Detailed API representation of a ConnectedSystem.
/// </summary>
public class ConnectedSystemDetailDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public DateTime Created { get; set; }
    public DateTime LastUpdated { get; set; }
    public ConnectedSystemStatus Status { get; set; }
    public bool SettingValuesValid { get; set; }
    public ConnectorReferenceDto Connector { get; set; } = null!;
    public List<ConnectedSystemObjectTypeDto> ObjectTypes { get; set; } = new();
    public int ObjectCount { get; set; }
    public int PendingExportCount { get; set; }

    /// <summary>
    /// Creates a detailed DTO from a ConnectedSystem entity.
    /// </summary>
    public static ConnectedSystemDetailDto FromEntity(ConnectedSystem entity)
    {
        return new ConnectedSystemDetailDto
        {
            Id = entity.Id,
            Name = entity.Name,
            Description = entity.Description,
            Created = entity.Created,
            LastUpdated = entity.LastUpdated,
            Status = entity.Status,
            SettingValuesValid = entity.SettingValuesValid,
            Connector = new ConnectorReferenceDto
            {
                Id = entity.ConnectorDefinition?.Id ?? 0,
                Name = entity.ConnectorDefinition?.Name ?? string.Empty
            },
            ObjectTypes = entity.ObjectTypes?
                .Select(ConnectedSystemObjectTypeDto.FromEntity)
                .ToList() ?? new(),
            ObjectCount = entity.Objects?.Count ?? 0,
            PendingExportCount = entity.PendingExports?.Count ?? 0
        };
    }
}

/// <summary>
/// Lightweight reference to a ConnectorDefinition.
/// </summary>
public class ConnectorReferenceDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
}

/// <summary>
/// API representation of a ConnectedSystemObjectType.
/// </summary>
public class ConnectedSystemObjectTypeDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public DateTime Created { get; set; }
    public bool Selected { get; set; }
    public bool RemoveContributedAttributesOnObsoletion { get; set; }
    public int AttributeCount { get; set; }

    public static ConnectedSystemObjectTypeDto FromEntity(ConnectedSystemObjectType entity)
    {
        return new ConnectedSystemObjectTypeDto
        {
            Id = entity.Id,
            Name = entity.Name,
            Created = entity.Created,
            Selected = entity.Selected,
            RemoveContributedAttributesOnObsoletion = entity.RemoveContributedAttributesOnObsoletion,
            AttributeCount = entity.Attributes?.Count ?? 0
        };
    }
}

/// <summary>
/// Detailed API representation of a ConnectedSystemObject.
/// </summary>
public class ConnectedSystemObjectDetailDto
{
    public Guid Id { get; set; }
    public DateTime Created { get; set; }
    public DateTime? LastUpdated { get; set; }
    public ConnectedSystemObjectStatus Status { get; set; }
    public ConnectedSystemObjectJoinType JoinType { get; set; }
    public DateTime? DateJoined { get; set; }
    public string? DisplayName { get; set; }
    public int ConnectedSystemId { get; set; }
    public string ConnectedSystemName { get; set; } = null!;
    public int TypeId { get; set; }
    public string TypeName { get; set; } = null!;
    public Guid? MetaverseObjectId { get; set; }
    public string? MetaverseObjectDisplayName { get; set; }
    public List<ConnectedSystemObjectAttributeValueDto> AttributeValues { get; set; } = new();

    public static ConnectedSystemObjectDetailDto FromEntity(ConnectedSystemObject entity)
    {
        return new ConnectedSystemObjectDetailDto
        {
            Id = entity.Id,
            Created = entity.Created,
            LastUpdated = entity.LastUpdated,
            Status = entity.Status,
            JoinType = entity.JoinType,
            DateJoined = entity.DateJoined,
            DisplayName = entity.DisplayNameOrId,
            ConnectedSystemId = entity.ConnectedSystemId,
            ConnectedSystemName = entity.ConnectedSystem?.Name ?? string.Empty,
            TypeId = entity.TypeId,
            TypeName = entity.Type?.Name ?? string.Empty,
            MetaverseObjectId = entity.MetaverseObjectId,
            MetaverseObjectDisplayName = entity.MetaverseObject?.DisplayName,
            AttributeValues = entity.AttributeValues
                .Select(ConnectedSystemObjectAttributeValueDto.FromEntity)
                .ToList()
        };
    }
}

/// <summary>
/// API representation of a ConnectedSystemObjectAttributeValue.
/// </summary>
public class ConnectedSystemObjectAttributeValueDto
{
    public Guid Id { get; set; }
    public int AttributeId { get; set; }
    public string AttributeName { get; set; } = null!;
    public string? StringValue { get; set; }
    public DateTime? DateTimeValue { get; set; }
    public int? IntValue { get; set; }
    public Guid? GuidValue { get; set; }
    public bool? BoolValue { get; set; }
    public Guid? ReferenceValueId { get; set; }

    public static ConnectedSystemObjectAttributeValueDto FromEntity(ConnectedSystemObjectAttributeValue entity)
    {
        return new ConnectedSystemObjectAttributeValueDto
        {
            Id = entity.Id,
            AttributeId = entity.Attribute?.Id ?? 0,
            AttributeName = entity.Attribute?.Name ?? string.Empty,
            StringValue = entity.StringValue,
            DateTimeValue = entity.DateTimeValue,
            IntValue = entity.IntValue,
            GuidValue = entity.GuidValue,
            BoolValue = entity.BoolValue,
            ReferenceValueId = entity.ReferenceValue?.Id
        };
    }
}
