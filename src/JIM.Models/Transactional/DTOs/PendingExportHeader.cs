namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// A lightweight representation of a Pending Export for list views.
/// </summary>
public class PendingExportHeader
{
    public Guid Id { get; set; }

    public int ConnectedSystemId { get; set; }

    public PendingExportChangeType ChangeType { get; set; }

    public PendingExportStatus Status { get; set; }

    /// <summary>
    /// When this pending export was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When was the last export attempt made?
    /// </summary>
    public DateTime? LastAttemptedAt { get; set; }

    /// <summary>
    /// When should the next retry be attempted?
    /// </summary>
    public DateTime? NextRetryAt { get; set; }

    /// <summary>
    /// How many times have we encountered an error whilst trying to export this change?
    /// </summary>
    public int ErrorCount { get; set; }

    /// <summary>
    /// Maximum number of retry attempts before marking as Failed.
    /// </summary>
    public int MaxRetries { get; set; }

    /// <summary>
    /// The error message from the last failed attempt.
    /// </summary>
    public string? LastErrorMessage { get; set; }

    /// <summary>
    /// Indicates whether this export has reference attributes that couldn't be resolved.
    /// </summary>
    public bool HasUnresolvedReferences { get; set; }

    /// <summary>
    /// The external identifier of the target CSO, if available.
    /// </summary>
    public string? TargetObjectIdentifier { get; set; }

    /// <summary>
    /// The ID of the source Metaverse Object that triggered this export.
    /// </summary>
    public Guid? SourceMetaverseObjectId { get; set; }

    /// <summary>
    /// The display name of the source Metaverse Object, if available.
    /// </summary>
    public string? SourceMetaverseObjectDisplayName { get; set; }

    /// <summary>
    /// The number of attribute value changes in this pending export.
    /// </summary>
    public int AttributeChangeCount { get; set; }

    /// <summary>
    /// The ID of the target Connected System Object, if available (for updates/deletes).
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// Creates a PendingExportHeader from a PendingExport entity.
    /// </summary>
    public static PendingExportHeader FromEntity(PendingExport entity, string? targetObjectIdentifier = null, string? sourceMvoDisplayName = null)
    {
        return new PendingExportHeader
        {
            Id = entity.Id,
            ConnectedSystemId = entity.ConnectedSystemId,
            ChangeType = entity.ChangeType,
            Status = entity.Status,
            CreatedAt = entity.CreatedAt,
            LastAttemptedAt = entity.LastAttemptedAt,
            NextRetryAt = entity.NextRetryAt,
            ErrorCount = entity.ErrorCount,
            MaxRetries = entity.MaxRetries,
            LastErrorMessage = entity.LastErrorMessage,
            HasUnresolvedReferences = entity.HasUnresolvedReferences,
            TargetObjectIdentifier = targetObjectIdentifier,
            SourceMetaverseObjectId = entity.SourceMetaverseObjectId,
            SourceMetaverseObjectDisplayName = sourceMvoDisplayName,
            AttributeChangeCount = entity.AttributeValueChanges?.Count ?? 0,
            ConnectedSystemObjectId = entity.ConnectedSystemObject?.Id
        };
    }
}
