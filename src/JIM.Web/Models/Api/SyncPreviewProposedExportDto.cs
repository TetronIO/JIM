// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Web.Models.Api;

/// <summary>
/// One Pending Export a Sync Preview's outbound chain would stage, were the real synchronisation to run.
/// Nothing here is persisted; a preview claims no match and stages nothing.
/// </summary>
public class SyncPreviewProposedExportDto
{
    /// <summary>
    /// The target Connected System the export would be staged against.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The kind of change the export would represent.
    /// </summary>
    public PendingExportChangeType ChangeType { get; set; }

    /// <summary>
    /// The existing target Connected System Object the export would apply to, when the change is an update
    /// or delete. Null for a create, which has no target object yet.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// The Metaverse Object that would trigger this export.
    /// </summary>
    public Guid? SourceMetaverseObjectId { get; set; }

    /// <summary>
    /// The attribute value changes the export would carry.
    /// </summary>
    public List<PendingExportAttributeValueChangeDto> AttributeChanges { get; set; } = [];

    public static SyncPreviewProposedExportDto FromModel(PendingExport model) => new()
    {
        ConnectedSystemId = model.ConnectedSystemId,
        ChangeType = model.ChangeType,
        ConnectedSystemObjectId = model.ConnectedSystemObjectId,
        SourceMetaverseObjectId = model.SourceMetaverseObjectId,
        AttributeChanges = [.. model.AttributeValueChanges.Select(PendingExportAttributeValueChangeDto.FromEntity)]
    };
}
