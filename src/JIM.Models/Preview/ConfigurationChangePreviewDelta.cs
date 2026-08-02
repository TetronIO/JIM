// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Preview;

/// <summary>
/// What a proposed configuration would do to one object, for one attribute where the transition concerns one. These
/// are the drill-down rows behind a summary group.
///
/// **Old and new values are attribute values, and therefore personal data.** They carry the same posture as the
/// change data on a Run Profile Execution Item: never logged, never emitted in diagnostics, and removed by the same
/// retention housekeeping (rows hang off the preview's Activity, so the existing cascade covers them).
/// </summary>
public class ConfigurationChangePreviewDelta
{
    public Guid Id { get; set; }

    /// <summary>The preview this delta belongs to; also the Activity that owns both.</summary>
    public Guid ActivityId { get; set; }

    public ConfigurationChangePreview Preview { get; set; } = null!;

    /// <summary>The summary group this delta was counted in. Drill-down queries lead with this.</summary>
    public Guid GroupId { get; set; }

    public ConfigurationChangePreviewGroup Group { get; set; } = null!;

    /// <summary>What would happen to this object.</summary>
    public ActivityRunProfileExecutionItemSyncOutcomeType TransitionType { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // The object concerned. Which identifiers are populated depends on the transition: a scope change names a
    // Connected System Object, a deletion-eligibility change names a Metaverse Object, an Attribute Flow change
    // names both.
    // -----------------------------------------------------------------------------------------------------------------

    public Guid? MetaverseObjectId { get; set; }

    public Guid? ConnectedSystemObjectId { get; set; }

    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// The object's display name and type name as they were when the preview ran. Snapshotted deliberately: the
    /// point of a preview is often that these objects are about to be deleted or disconnected, and a drill-down
    /// that joined to them would render blank rows for exactly the objects the administrator most needs to see.
    /// </summary>
    public string? ObjectDisplayName { get; set; }

    public string? ObjectTypeName { get; set; }

    /// <summary>The attribute this delta concerns, for transitions that change a value.</summary>
    public string? AttributeName { get; set; }

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    /// <summary>
    /// The pattern a detector recognised in this delta, or null. Present from the first migration so the detector
    /// registry can be added later without a schema change; nothing populates it yet.
    /// </summary>
    public string? PatternKey { get; set; }
}
