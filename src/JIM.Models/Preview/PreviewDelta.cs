// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Preview;

/// <summary>
/// One object's outcome under a proposed configuration, as an adapter yields it. The framework consumes the stream,
/// counts groups exactly from it, and persists it as <see cref="ConfigurationChangePreviewDelta"/> rows in full or
/// capped per group.
///
/// This is the streamed form, not the stored form: it carries no identifiers the framework assigns (the preview,
/// the group) and no persistence concerns. Keeping them apart is what lets the framework decide what to keep
/// without the adapter knowing or caring, so an adapter cannot accidentally make capping its problem.
/// </summary>
/// <param name="TransitionType">What would happen to this object.</param>
/// <param name="ObjectDisplayName">
/// The object's name as it is now. Snapshotted rather than resolved later on purpose: previews frequently concern
/// objects that are about to be deleted or disconnected, which is exactly when a join would render nothing.
/// </param>
/// <param name="ObjectTypeName">The object's type name as it is now, snapshotted for the same reason.</param>
/// <param name="MetaverseObjectTypeId">
/// The Metaverse Object Type the object is of, where the transition concerns metaverse objects. Carried alongside
/// the name because the name is a snapshot for display and the id is what a summary group is filtered by.
/// </param>
/// <param name="MetaverseObjectId">The Metaverse Object concerned, where the transition names one.</param>
/// <param name="ConnectedSystemObjectId">The Connected System Object concerned, where the transition names one.</param>
/// <param name="ConnectedSystemId">The Connected System concerned, where the transition is per-system.</param>
/// <param name="AttributeName">The attribute concerned, for transitions that change a value.</param>
/// <param name="OldValue">
/// The value now. Personal data: never log this, and never put it in a diagnostic message.
/// </param>
/// <param name="NewValue">The value the proposed configuration would produce. Personal data, as above.</param>
public record PreviewDelta(
    ActivityRunProfileExecutionItemSyncOutcomeType TransitionType,
    string? ObjectDisplayName = null,
    string? ObjectTypeName = null,
    int? MetaverseObjectTypeId = null,
    Guid? MetaverseObjectId = null,
    Guid? ConnectedSystemObjectId = null,
    int? ConnectedSystemId = null,
    string? AttributeName = null,
    string? OldValue = null,
    string? NewValue = null);
