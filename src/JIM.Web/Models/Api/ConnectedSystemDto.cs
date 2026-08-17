// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional.DTOs;

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
    public DateTime? LastUpdated { get; set; }
    public ConnectedSystemStatus Status { get; set; }
    public bool SettingValuesValid { get; set; }
    public ConnectorReferenceDto Connector { get; set; } = null!;
    public List<ConnectedSystemObjectTypeDto> ObjectTypes { get; set; } = new();
    public int ObjectCount { get; set; }
    public int PendingExportCount { get; set; }
    public int? MaxExportParallelism { get; set; }

    /// <summary>
    /// How long an account provisioned into this Connected System stays owed an initial password before JIM
    /// records an expiry and stops trying. Null means JIM's default of seven days, and is reported as null rather
    /// than as the default so a caller can tell a system configured to seven days from one never configured.
    /// </summary>
    public TimeSpan? InitialPasswordTimeToLive { get; set; }

    /// <summary>
    /// Controls how an import-time reference attribute value that cannot be resolved to a Connected System Object
    /// is treated. Default is Error (current behaviour); Warn downgrades to an Activity warning; Ignore suppresses
    /// both the per-object error and the Activity warning while still logging the occurrence.
    /// </summary>
    public UnresolvedReferenceHandling UnresolvedReferenceHandling { get; set; }

    /// <summary>
    /// Whether the configuration has changed in a way that needs a Full Synchronisation to take effect. Null on the
    /// create and update responses, which describe the write that just happened rather than the system's readiness.
    /// </summary>
    public ConfigurationDriftDto? ConfigurationDrift { get; set; }

    /// <summary>
    /// How many accounts in this Connected System are waiting on a person over their initial password: refused by
    /// the target and parked, or never given one before its time to live passed. Null on the create and update
    /// responses, which describe the write that just happened rather than the system's readiness.
    /// <para>
    /// The two counts are never summed. Parked work is fixed on the Synchronisation Rules that provisioned those
    /// accounts, by correcting their initial password settings; expired work cannot be fixed there at all.
    /// </para>
    /// </summary>
    public int? ParkedInitialPasswordCount { get; set; }

    /// <inheritdoc cref="ParkedInitialPasswordCount"/>
    public int? ExpiredInitialPasswordCount { get; set; }

    /// <summary>
    /// Creates a detailed DTO from a ConnectedSystem entity.
    /// </summary>
    /// <param name="entity">The Connected System entity.</param>
    /// <param name="pendingExportCount">
    /// Pre-computed Pending Export count. Required because GetConnectedSystemAsync
    /// does not load the PendingExports navigation property (too expensive).
    /// </param>
    /// <param name="objectCount">
    /// Pre-computed Connected System Object count. Required because GetConnectedSystemAsync
    /// does not load the Objects navigation property (it can be very large).
    /// </param>
    /// <param name="configurationDrift">
    /// Pre-computed configuration drift status, or null to omit it (create and update responses do not carry it).
    /// </param>
    /// <param name="initialPasswordAttention">
    /// Pre-computed initial-password counts, or null to omit them (create and update responses do not carry them).
    /// </param>
    public static ConnectedSystemDetailDto FromEntity(ConnectedSystem entity, int pendingExportCount = 0, int objectCount = 0,
        ConfigurationDriftStatus? configurationDrift = null, InitialPasswordAttention? initialPasswordAttention = null)
    {
        return new ConnectedSystemDetailDto
        {
            ConfigurationDrift = configurationDrift == null ? null : ConfigurationDriftDto.FromStatus(configurationDrift),
            ParkedInitialPasswordCount = initialPasswordAttention?.ParkedCount,
            ExpiredInitialPasswordCount = initialPasswordAttention?.ExpiredCount,
            Id = entity.Id,
            Name = entity.Name,
            Description = entity.Description,
            Created = entity.Created,
            LastUpdated = entity.LastUpdated,
            Status = entity.Status,
            SettingValuesValid = entity.SettingValuesValid,
            MaxExportParallelism = entity.MaxExportParallelism,
            InitialPasswordTimeToLive = entity.InitialPasswordTimeToLive,
            UnresolvedReferenceHandling = entity.UnresolvedReferenceHandling,
            Connector = new ConnectorReferenceDto
            {
                Id = entity.ConnectorDefinition?.Id ?? 0,
                Name = entity.ConnectorDefinition?.Name ?? string.Empty
            },
            ObjectTypes = entity.ObjectTypes?
                .Select(ConnectedSystemObjectTypeDto.FromEntity)
                .ToList() ?? new(),
            ObjectCount = objectCount,
            PendingExportCount = pendingExportCount
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

    /// <summary>
    /// How the Connected System classified this object type, as open key/value tags. A directory connector reports
    /// the class kind (structural, auxiliary, abstract) and, for classes the directory keeps for its own
    /// configuration or operation, a visibility of "internal". An object type carrying no tags is unclassified,
    /// which means "show it".
    /// </summary>
    public List<ConnectedSystemObjectTypeTagDto> Tags { get; set; } = [];

    /// <summary>
    /// Whether the Connected System reported this object type as one it uses internally. Derived from
    /// <see cref="Tags"/>, and offered here so callers can filter on it without matching tag strings themselves.
    /// The portal hides these object types by default.
    /// </summary>
    public bool IsInternal { get; set; }

    public List<ConnectedSystemAttributeDto>? Attributes { get; set; }

    public static ConnectedSystemObjectTypeDto FromEntity(ConnectedSystemObjectType entity)
    {
        return new ConnectedSystemObjectTypeDto
        {
            Id = entity.Id,
            Name = entity.Name,
            Created = entity.Created,
            Selected = entity.Selected,
            RemoveContributedAttributesOnObsoletion = entity.RemoveContributedAttributesOnObsoletion,
            AttributeCount = entity.Attributes?.Count ?? 0,
            Tags = entity.Tags
                .Select(tag => new ConnectedSystemObjectTypeTagDto { Key = tag.Key, Value = tag.Value })
                .ToList(),
            IsInternal = entity.IsInternal(),
            Attributes = entity.Attributes?
                .Select(ConnectedSystemAttributeDto.FromEntity)
                .ToList()
        };
    }
}

/// <summary>
/// API representation of a classification tag on a ConnectedSystemObjectType.
/// </summary>
public class ConnectedSystemObjectTypeTagDto
{
    public string Key { get; set; } = null!;
    public string Value { get; set; } = null!;
}

/// <summary>
/// API representation of a ConnectedSystemObjectTypeAttribute.
/// </summary>
public class ConnectedSystemAttributeDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string? ClassName { get; set; }
    public DateTime Created { get; set; }
    public string Type { get; set; } = null!;

    /// <summary>
    /// Whether <see cref="Type"/> was chosen by an administrator rather than inferred by schema discovery.
    /// A chosen type is left alone by a schema refresh; an inferred one is restated from the Connector.
    /// </summary>
    public bool TypeSetByAdministrator { get; set; }

    public string AttributePlurality { get; set; } = null!;
    public bool Selected { get; set; }
    public bool IsExternalId { get; set; }
    public bool IsSecondaryExternalId { get; set; }

    /// <summary>
    /// Indicates if this attribute's selection state is locked and cannot be changed.
    /// This is true for External ID and Secondary External ID attributes.
    /// </summary>
    public bool SelectionLocked { get; set; }

    /// <summary>
    /// Indicates whether this attribute can be written to in the Connected System. One of
    /// <c>Writable</c>, <c>ReadOnly</c> or <c>WritableOnCreate</c>.
    /// <c>ReadOnly</c> attributes can be imported but cannot be targeted by export Attribute Flows.
    /// <c>WritableOnCreate</c> attributes can be targeted, but only ever flow on a Create Pending Export.
    /// </summary>
    /// <remarks>
    /// Read-only: discovered from the Connected System's schema, never set through this API. The value is
    /// the enum name so that a client can switch on it; the portal renders its own wording.
    /// </remarks>
    public string Writability { get; set; } = null!;

    public static ConnectedSystemAttributeDto FromEntity(ConnectedSystemObjectTypeAttribute entity)
    {
        return new ConnectedSystemAttributeDto
        {
            Id = entity.Id,
            Name = entity.Name,
            Description = entity.Description,
            ClassName = entity.ClassName,
            Created = entity.Created,
            Type = entity.Type.ToString(),
            TypeSetByAdministrator = entity.TypeSetByAdministrator,
            AttributePlurality = entity.AttributePlurality.ToString(),
            Selected = entity.Selected,
            IsExternalId = entity.IsExternalId,
            IsSecondaryExternalId = entity.IsSecondaryExternalId,
            SelectionLocked = entity.SelectionLocked,
            Writability = entity.Writability.ToString()
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

    /// <summary>
    /// Per-attribute metadata showing total value counts. Present when the detail was loaded
    /// with a capped strategy so consumers know when values have been truncated.
    /// </summary>
    public List<AttributeValueSummaryDto>? AttributeValueSummaries { get; set; }

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
            DisplayName = entity.NameOrId,
            ConnectedSystemId = entity.ConnectedSystemId,
            ConnectedSystemName = entity.ConnectedSystem?.Name ?? string.Empty,
            TypeId = entity.TypeId,
            TypeName = entity.Type?.Name ?? string.Empty,
            MetaverseObjectId = entity.MetaverseObjectId,
            MetaverseObjectDisplayName = entity.MetaverseObject?.Name,
            AttributeValues = entity.AttributeValues
                .Select(ConnectedSystemObjectAttributeValueDto.FromEntity)
                .ToList()
        };
    }

    public static ConnectedSystemObjectDetailDto FromDetailResult(CsoDetailResult result)
    {
        var dto = FromEntity(result.ConnectedSystemObject);

        if (result.AttributeValueTotalCounts.Count > 0)
        {
            var returnedCounts = result.ConnectedSystemObject.AttributeValues
                .GroupBy(av => av.Attribute?.Name ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.Count());

            dto.AttributeValueSummaries = result.AttributeValueTotalCounts
                .Select(kvp => new AttributeValueSummaryDto
                {
                    AttributeName = kvp.Key,
                    TotalCount = kvp.Value,
                    ReturnedCount = returnedCounts.GetValueOrDefault(kvp.Key, 0),
                    HasMore = kvp.Value > returnedCounts.GetValueOrDefault(kvp.Key, 0)
                })
                .OrderBy(s => s.AttributeName)
                .ToList();
        }

        return dto;
    }
}

/// <summary>
/// Per-attribute metadata showing total vs. returned value counts.
/// </summary>
public class AttributeValueSummaryDto
{
    public string AttributeName { get; set; } = null!;
    public int TotalCount { get; set; }
    public int ReturnedCount { get; set; }
    public bool HasMore { get; set; }
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
    public long? LongValue { get; set; }
    public decimal? DecimalValue { get; set; }

    /// <summary>
    /// The value for Binary attributes. Serialised to JSON as a base64-encoded string
    /// (System.Text.Json's representation for byte arrays).
    /// </summary>
    public byte[]? ByteValue { get; set; }

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
            LongValue = entity.LongValue,
            DecimalValue = entity.DecimalValue,
            ByteValue = entity.ByteValue,
            GuidValue = entity.GuidValue,
            BoolValue = entity.BoolValue,
            ReferenceValueId = entity.ReferenceValue?.Id
        };
    }
}

/// <summary>
/// API representation of a ConnectedSystemPartition.
/// </summary>
public class ConnectedSystemPartitionDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string ExternalId { get; set; } = null!;
    public bool Selected { get; set; }
    public int ConnectedSystemId { get; set; }
    public List<ConnectedSystemContainerDto> Containers { get; set; } = new();

    public static ConnectedSystemPartitionDto FromEntity(ConnectedSystemPartition entity)
    {
        return new ConnectedSystemPartitionDto
        {
            Id = entity.Id,
            Name = entity.Name,
            ExternalId = entity.ExternalId,
            Selected = entity.Selected,
            ConnectedSystemId = entity.ConnectedSystem?.Id ?? 0,
            Containers = entity.Containers?
                .Select(ConnectedSystemContainerDto.FromEntity)
                .ToList() ?? new()
        };
    }
}

/// <summary>
/// API representation of a ConnectedSystemContainer.
/// </summary>
public class ConnectedSystemContainerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string ExternalId { get; set; } = null!;
    public string? Description { get; set; }
    public bool Hidden { get; set; }
    public bool Selected { get; set; }

    /// <summary>
    /// Whether this Container is carved out of a selection an ancestor made, leaving the objects within it
    /// deliberately unimported.
    /// </summary>
    public bool Excluded { get; set; }

    /// <summary>
    /// How far beneath this Container objects are imported from, when it is selected.
    /// </summary>
    public ConnectedSystemContainerScope Scope { get; set; }

    /// <summary>
    /// How many objects sit directly in this Container in the Connected System, as at the last hierarchy
    /// retrieval. Null where the Connector cannot report counts, or the hierarchy has not been retrieved since
    /// counting was introduced.
    /// </summary>
    /// <remarks>
    /// Zero and null mean different things: zero is a Container the Connector searched and found nothing in, null
    /// is one nobody has counted. Counts what a Full Import would return for the selected Object Types, and is
    /// deliberately blind to Container selections and exclusions.
    /// </remarks>
    public int? ObjectCount { get; set; }

    /// <summary>
    /// <see cref="ObjectCount"/> plus every descendant Container's, which is what a Subtree statement over this
    /// Container reaches. Null where this Container has not been counted.
    /// </summary>
    public int? SubtreeObjectCount { get; set; }

    public int? PartitionId { get; set; }
    public int? ConnectedSystemId { get; set; }
    public List<ConnectedSystemContainerDto> ChildContainers { get; set; } = new();

    public static ConnectedSystemContainerDto FromEntity(ConnectedSystemContainer entity)
    {
        return new ConnectedSystemContainerDto
        {
            Id = entity.Id,
            Name = entity.Name,
            ExternalId = entity.ExternalId,
            Description = entity.Description,
            Hidden = entity.Hidden,
            Selected = entity.Selected,
            Excluded = entity.Excluded,
            Scope = entity.Scope,
            ObjectCount = entity.ObjectCount,
            SubtreeObjectCount = entity.SubtreeObjectCount,
            PartitionId = entity.Partition?.Id,
            ConnectedSystemId = entity.ConnectedSystem?.Id,
            ChildContainers = entity.ChildContainers
                .Select(FromEntity)
                .ToList()
        };
    }
}

/// <summary>
/// API representation of a ConnectorCapability: a human-readable fact the Connector detected about the
/// target system (e.g. an LDAP directory's type, vendor, or paging support), for the "Directory Capabilities"
/// card on the Connected System details page.
/// </summary>
public class ConnectorCapabilityDto
{
    public string Name { get; set; } = null!;
    public string Value { get; set; } = null!;

    public static ConnectorCapabilityDto FromEntity(ConnectorCapability entity)
    {
        return new ConnectorCapabilityDto
        {
            Name = entity.Name,
            Value = entity.Value
        };
    }
}
