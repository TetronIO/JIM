// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;

namespace JIM.Web.Models.Api;

// ValueOriginDto and its FromModel are defined in MetaverseObjectProvenanceDtos.cs and reused here so a value's
// origin serialises identically wherever it appears over the API.

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

    /// <summary>
    /// The reference changes (among <see cref="AttributeChanges"/>) that have not been written yet, each with
    /// the reason, computed against the target's current state when the detail is read. Empty when the
    /// Pending Export has no unresolved references.
    /// </summary>
    public List<PendingExportUnresolvedReferenceDto> UnresolvedReferences { get; set; } = new();

    /// <summary>
    /// Where the value queued for each Connected System attribute originated (#399's "Value from" column), one
    /// entry per attribute whose export mapping could be resolved. Empty when the Pending Export has no source
    /// Metaverse Object (a delete).
    /// </summary>
    public List<PendingExportValueSourceDto> ValueSources { get; set; } = new();

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
            ConnectedSystemObjectDisplayName = pe.ConnectedSystemObject?.NameOrId,
            ConnectedSystemObjectTypeName = pe.ConnectedSystemObject?.Type?.Name,
            SourceMetaverseObjectId = pe.SourceMetaverseObjectId,
            SourceMetaverseObjectDisplayName = pe.SourceMetaverseObject?.Name
                ?? pe.SourceMetaverseObjectId?.ToString(),
            SourceMetaverseObjectTypeName = pe.SourceMetaverseObject?.Type?.Name,
            AttributeChanges = pe.AttributeValueChanges
                .Select(PendingExportAttributeValueChangeDto.FromEntity)
                .ToList(),
            UnresolvedReferences = result.UnresolvedReferences
                .Select(PendingExportUnresolvedReferenceDto.FromModel)
                .ToList(),
            ValueSources = result.ValueSources
                .Select(PendingExportValueSourceDto.FromModel)
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
/// One reference change on a Pending Export that has not been written yet, and why.
/// </summary>
public class PendingExportUnresolvedReferenceDto
{
    /// <summary>
    /// The attribute value change (see <see cref="PendingExportDetailDto.AttributeChanges"/>) carrying the reference.
    /// </summary>
    public Guid AttributeChangeId { get; set; }

    /// <summary>
    /// The reference attribute's name in the Connected System.
    /// </summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>
    /// The Metaverse Object the change refers to.
    /// </summary>
    public Guid ReferencedMetaverseObjectId { get; set; }

    /// <summary>
    /// The referenced Metaverse Object's display name, when it has one.
    /// </summary>
    public string? ReferencedMetaverseObjectDisplayName { get; set; }

    /// <summary>
    /// Why the reference has not been written yet: <c>Resolvable</c> (the referenced object has an anchor in this
    /// Connected System and the reference is written on the next export run), <c>AwaitingAnchor</c> (the referenced
    /// object exists in this Connected System but has no anchor yet), or <c>NotInTargetSystem</c> (the referenced
    /// object has no Connected System Object in this Connected System at all).
    /// </summary>
    public UnresolvedReferenceReason Reason { get; set; }

    public static PendingExportUnresolvedReferenceDto FromModel(PendingExportUnresolvedReference model) => new()
    {
        AttributeChangeId = model.AttributeChangeId,
        AttributeName = model.AttributeName,
        ReferencedMetaverseObjectId = model.ReferencedMetaverseObjectId,
        ReferencedMetaverseObjectDisplayName = model.ReferencedMetaverseObjectDisplayName,
        Reason = model.Reason
    };
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
    public decimal? DecimalValue { get; set; }

    /// <summary>
    /// The value for Binary attributes. Serialised to JSON as a base64-encoded string
    /// (System.Text.Json's representation for byte arrays).
    /// </summary>
    public byte[]? ByteValue { get; set; }

    public Guid? GuidValue { get; set; }
    public bool? BoolValue { get; set; }
    public string? UnresolvedReferenceValue { get; set; }
    public int ExportAttemptCount { get; set; }

    /// <summary>
    /// The export Synchronisation Rule whose mapping produced this value. Null when the contributing rule
    /// has since been deleted; <see cref="SyncRuleName"/> is the denormalised record that survives it.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// Snapshot of the contributing Synchronisation Rule's name at staging time.
    /// </summary>
    public string? SyncRuleName { get; set; }

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
            DecimalValue = entity.DecimalValue,
            ByteValue = entity.ByteValue,
            GuidValue = entity.GuidValue,
            BoolValue = entity.BoolValue,
            UnresolvedReferenceValue = entity.UnresolvedReferenceValue,
            ExportAttemptCount = entity.ExportAttemptCount,
            SyncRuleId = entity.SyncRuleId,
            SyncRuleName = entity.SyncRuleName
        };
    }
}

/// <summary>
/// API representation of where the value queued for one Connected System attribute on a Pending Export
/// originated (#399's "Value from" column).
/// </summary>
public class PendingExportValueSourceDto
{
    public int ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// True when the mapping computes the value (an expression, an advanced/chained mapping, or generation).
    /// <see cref="Expression"/> describes it; the source-attribute and origin fields are null/absent.
    /// </summary>
    public bool IsComputed { get; set; }

    /// <summary>The text describing a computed source, when <see cref="IsComputed"/> is true.</summary>
    public string? Expression { get; set; }

    /// <summary>The single Metaverse attribute the value came from, when <see cref="IsComputed"/> is false.</summary>
    public int? SourceMetaverseAttributeId { get; set; }

    public string? SourceMetaverseAttributeName { get; set; }

    /// <summary>The origin of the source Metaverse Object's current value, when not computed.</summary>
    public ValueOriginDto? Origin { get; set; }

    /// <summary>
    /// True when the source Metaverse attribute's current values came from more than one distinct origin, in
    /// which case <see cref="Origin"/> is only the first.
    /// </summary>
    public bool HasSeveralOrigins { get; set; }

    public static PendingExportValueSourceDto FromModel(PendingExportValueSource model)
    {
        return new PendingExportValueSourceDto
        {
            ConnectedSystemAttributeId = model.ConnectedSystemAttributeId,
            IsComputed = model.IsComputed,
            Expression = model.Expression,
            SourceMetaverseAttributeId = model.SourceMetaverseAttributeId,
            SourceMetaverseAttributeName = model.SourceMetaverseAttributeName,
            Origin = model.IsComputed ? null : ValueOriginDto.FromModel(model.Origin),
            HasSeveralOrigins = model.HasSeveralOrigins
        };
    }
}
