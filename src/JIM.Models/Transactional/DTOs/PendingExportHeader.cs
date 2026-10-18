// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// A lightweight representation of a Pending Export for list views.
/// </summary>
public class PendingExportHeader
{
    /// <summary>
    /// The ID of the Pending Export.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The ID of the Connected System the change is for.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// Whether the Pending Export creates, updates or deletes the object.
    /// </summary>
    public PendingExportChangeType ChangeType { get; set; }

    /// <summary>
    /// Where the Pending Export has got to: Pending, Executing, Exported (awaiting a confirming import),
    /// ExportNotConfirmed (to be retried), or Parked (waiting on a decision about a generated value). A Pending
    /// Export whose last attempt failed is Pending, with <see cref="FailingSince"/> set.
    /// </summary>
    public PendingExportStatus Status { get; set; }

    /// <summary>
    /// When this Pending Export was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the last export attempt was made (UTC); null before the first.
    /// </summary>
    public DateTime? LastAttemptedAt { get; set; }

    /// <summary>
    /// How many export attempts in a row have failed; zero once an attempt succeeds. A failing Pending Export is
    /// attempted again at every export run, however many attempts have failed.
    /// </summary>
    public int ErrorCount { get; set; }

    /// <summary>
    /// When the first of the current run of failed attempts was made (UTC); null when the last attempt did not fail.
    /// </summary>
    public DateTime? FailingSince { get; set; }

    /// <summary>
    /// The error from the last failed attempt; null when none has failed.
    /// </summary>
    public string? LastErrorMessage { get; set; }

    /// <summary>
    /// Whether any reference change cannot be written yet, because its target has no object in the Connected
    /// System.
    /// </summary>
    public bool HasUnresolvedReferences { get; set; }

    /// <summary>
    /// How many of the attribute changes are references still owed to the Connected System: their targets have no
    /// object there yet, so they were not written with the rest of the export.
    /// </summary>
    /// <remarks>
    /// Issue #1398. The detail view explains each owed reference; this count surfaces the wait in a list.
    /// </remarks>
    public int UnresolvedReferenceCount { get; set; }

    /// <summary>
    /// The target Connected System Object's external identifier, when it has one.
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
    /// The plural name of the source Metaverse Object's Metaverse Object Type, which its page's address is built from;
    /// null when there is no source Metaverse Object.
    /// </summary>
    public string? SourceMetaverseObjectTypePluralName { get; set; }

    /// <summary>
    /// The number of attribute value changes in this Pending Export.
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
            ErrorCount = entity.ErrorCount,
            FailingSince = entity.FailingSince,
            LastErrorMessage = entity.LastErrorMessage,
            HasUnresolvedReferences = entity.HasUnresolvedReferences,
            UnresolvedReferenceCount = entity.AttributeValueChanges?
                .Count(ac => !string.IsNullOrEmpty(ac.UnresolvedReferenceValue)) ?? 0,
            TargetObjectIdentifier = targetObjectIdentifier,
            SourceMetaverseObjectId = entity.SourceMetaverseObjectId,
            SourceMetaverseObjectDisplayName = sourceMvoDisplayName,
            SourceMetaverseObjectTypePluralName = entity.SourceMetaverseObject?.Type?.PluralName,
            AttributeChangeCount = entity.AttributeValueChanges?.Count ?? 0,
            ConnectedSystemObjectId = entity.ConnectedSystemObject?.Id
        };
    }
}
