using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of a Pending Export with capped multi-valued attribute changes.
/// </summary>
public class PendingExportDetailDto
{
    public Guid Id { get; set; }
    public int ConnectedSystemId { get; set; }
    public string ConnectedSystemName { get; set; } = null!;
    public PendingExportChangeType ChangeType { get; set; }
    public PendingExportStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public int ErrorCount { get; set; }
    public int MaxRetries { get; set; }
    public DateTime? LastAttemptedAt { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public string? LastErrorMessage { get; set; }
    public bool HasUnresolvedReferences { get; set; }

    /// <summary>
    /// The target Connected System Object, if one exists.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }
    public string? ConnectedSystemObjectDisplayName { get; set; }
    public string? ConnectedSystemObjectTypeName { get; set; }

    /// <summary>
    /// The source Metaverse Object that triggered this export.
    /// </summary>
    public Guid? SourceMetaverseObjectId { get; set; }
    public string? SourceMetaverseObjectDisplayName { get; set; }
    public string? SourceMetaverseObjectTypeName { get; set; }

    /// <summary>
    /// Attribute value changes (capped for multi-valued attributes).
    /// </summary>
    public List<PendingExportAttributeValueChangeDto> AttributeChanges { get; set; } = new();

    /// <summary>
    /// Per-attribute metadata showing total change counts. Present when values have been
    /// capped so consumers know when changes have been truncated.
    /// </summary>
    public List<AttributeChangeSummaryDto>? AttributeChangeSummaries { get; set; }

    public static PendingExportDetailDto FromDetailResult(PendingExportDetailResult result)
    {
        var pe = result.PendingExport;

        var dto = new PendingExportDetailDto
        {
            Id = pe.Id,
            ConnectedSystemId = pe.ConnectedSystemId,
            ConnectedSystemName = pe.ConnectedSystem?.Name ?? string.Empty,
            ChangeType = pe.ChangeType,
            Status = pe.Status,
            CreatedAt = pe.CreatedAt,
            ErrorCount = pe.ErrorCount,
            MaxRetries = pe.MaxRetries,
            LastAttemptedAt = pe.LastAttemptedAt,
            NextRetryAt = pe.NextRetryAt,
            LastErrorMessage = pe.LastErrorMessage,
            HasUnresolvedReferences = pe.HasUnresolvedReferences,
            ConnectedSystemObjectId = pe.ConnectedSystemObjectId,
            ConnectedSystemObjectDisplayName = pe.ConnectedSystemObject?.DisplayNameOrId,
            ConnectedSystemObjectTypeName = pe.ConnectedSystemObject?.Type?.Name,
            SourceMetaverseObjectId = pe.SourceMetaverseObjectId,
            SourceMetaverseObjectDisplayName = pe.SourceMetaverseObject?.DisplayName
                ?? pe.SourceMetaverseObjectId?.ToString(),
            SourceMetaverseObjectTypeName = pe.SourceMetaverseObject?.Type?.Name,
            AttributeChanges = pe.AttributeValueChanges
                .Select(PendingExportAttributeValueChangeDto.FromEntity)
                .ToList()
        };

        if (result.AttributeChangeTotalCounts.Count > 0)
        {
            var returnedCounts = pe.AttributeValueChanges
                .GroupBy(avc => avc.Attribute?.Name ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.Count());

            dto.AttributeChangeSummaries = result.AttributeChangeTotalCounts
                .Select(kvp => new AttributeChangeSummaryDto
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
/// Per-attribute metadata showing total vs. returned change counts for a Pending Export.
/// </summary>
public class AttributeChangeSummaryDto
{
    public string AttributeName { get; set; } = null!;
    public int TotalCount { get; set; }
    public int ReturnedCount { get; set; }
    public bool HasMore { get; set; }
}

/// <summary>
/// API representation of a single attribute value change within a Pending Export.
/// </summary>
public class PendingExportAttributeValueChangeDto
{
    public Guid Id { get; set; }
    public int AttributeId { get; set; }
    public string AttributeName { get; set; } = null!;
    public PendingExportAttributeChangeType ChangeType { get; set; }
    public PendingExportAttributeChangeStatus Status { get; set; }
    public string? StringValue { get; set; }
    public DateTime? DateTimeValue { get; set; }
    public int? IntValue { get; set; }
    public long? LongValue { get; set; }
    public Guid? GuidValue { get; set; }
    public bool? BoolValue { get; set; }
    public string? UnresolvedReferenceValue { get; set; }
    public int ExportAttemptCount { get; set; }

    public static PendingExportAttributeValueChangeDto FromEntity(PendingExportAttributeValueChange entity)
    {
        return new PendingExportAttributeValueChangeDto
        {
            Id = entity.Id,
            AttributeId = entity.AttributeId,
            AttributeName = entity.Attribute?.Name ?? string.Empty,
            ChangeType = entity.ChangeType,
            Status = entity.Status,
            StringValue = entity.StringValue,
            DateTimeValue = entity.DateTimeValue,
            IntValue = entity.IntValue,
            LongValue = entity.LongValue,
            GuidValue = entity.GuidValue,
            BoolValue = entity.BoolValue,
            UnresolvedReferenceValue = entity.UnresolvedReferenceValue,
            ExportAttemptCount = entity.ExportAttemptCount
        };
    }
}
