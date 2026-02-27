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
    public MetaverseObjectOrigin Origin { get; set; }
    public DateTime? LastConnectorDisconnectedDate { get; set; }
    public bool IsPendingDeletion { get; set; }
    public DateTime? DeletionEligibleDate { get; set; }
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
            Origin = entity.Origin,
            LastConnectorDisconnectedDate = entity.LastConnectorDisconnectedDate,
            IsPendingDeletion = entity.IsPendingDeletion,
            DeletionEligibleDate = entity.DeletionEligibleDate,
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
            ContributedBySystemId = entity.ContributedBySystemId,
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

/// <summary>
/// API representation of a MetaverseObject pending deletion.
/// </summary>
public class PendingDeletionDto
{
    /// <summary>The unique identifier of the metaverse object.</summary>
    public Guid Id { get; set; }

    /// <summary>The display name of the metaverse object.</summary>
    public string? DisplayName { get; set; }

    /// <summary>The type of the metaverse object.</summary>
    public string TypeName { get; set; } = null!;

    /// <summary>The type ID of the metaverse object.</summary>
    public int TypeId { get; set; }

    /// <summary>When the last connector was disconnected from this MVO.</summary>
    public DateTime LastConnectorDisconnectedDate { get; set; }

    /// <summary>The date when this MVO becomes eligible for deletion (after grace period expires).</summary>
    public DateTime? DeletionEligibleDate { get; set; }

    /// <summary>Number of days remaining until deletion (negative if overdue).</summary>
    public int? DaysUntilDeletion { get; set; }

    /// <summary>The grace period configured for this object type.</summary>
    public TimeSpan? GracePeriod { get; set; }

    /// <summary>Number of connected system objects still linked to this MVO.</summary>
    public int ConnectedSystemObjectCount { get; set; }

    /// <summary>
    /// The deletion status: Deprovisioning (has remaining connectors), AwaitingGracePeriod (fully disconnected, waiting),
    /// or ReadyForDeletion (grace period expired, no connectors).
    /// </summary>
    public string Status { get; set; } = null!;

    /// <summary>
    /// Creates a DTO from a MetaverseObject entity.
    /// </summary>
    public static PendingDeletionDto FromEntity(MetaverseObject entity)
    {
        var connectorCount = entity.ConnectedSystemObjects?.Count ?? 0;
        var daysUntilDeletion = entity.DeletionEligibleDate.HasValue
            ? (int)Math.Ceiling((entity.DeletionEligibleDate.Value - DateTime.UtcNow).TotalDays)
            : (int?)null;

        // Determine status
        string status;
        if (connectorCount > 0)
        {
            status = "Deprovisioning";
        }
        else if (daysUntilDeletion.HasValue && daysUntilDeletion.Value > 0)
        {
            status = "AwaitingGracePeriod";
        }
        else
        {
            status = "ReadyForDeletion";
        }

        return new PendingDeletionDto
        {
            Id = entity.Id,
            DisplayName = entity.DisplayName,
            TypeName = entity.Type?.Name ?? "Unknown",
            TypeId = entity.Type?.Id ?? 0,
            LastConnectorDisconnectedDate = entity.LastConnectorDisconnectedDate!.Value,
            DeletionEligibleDate = entity.DeletionEligibleDate,
            DaysUntilDeletion = daysUntilDeletion,
            GracePeriod = entity.Type?.DeletionGracePeriod,
            ConnectedSystemObjectCount = connectorCount,
            Status = status
        };
    }
}

/// <summary>
/// Summary statistics for pending deletions.
/// </summary>
public class PendingDeletionSummary
{
    /// <summary>Total count of MVOs pending deletion.</summary>
    public int TotalCount { get; set; }

    /// <summary>Count of MVOs still connected to other systems, awaiting cascade deletion.</summary>
    public int DeprovisioningCount { get; set; }

    /// <summary>Count of MVOs fully disconnected but waiting for grace period to expire.</summary>
    public int AwaitingGracePeriodCount { get; set; }

    /// <summary>Count of MVOs eligible for deletion (grace period expired, no connectors).</summary>
    public int ReadyForDeletionCount { get; set; }
}
