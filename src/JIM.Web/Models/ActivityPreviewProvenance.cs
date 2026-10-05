// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Web.Models;

/// <summary>
/// Decides what an Activity's page says about the preview behind the change it records (#134).
/// </summary>
public static class ActivityPreviewProvenance
{
    /// <summary>
    /// Any change carrying a preview was informed by it. A Connected System deletion without one says it went ahead
    /// without a preview, because whether the administrator looked first is the question its audit trail is read to
    /// answer; every other change without one says nothing, since most have no preview surface at all.
    /// </summary>
    public static ActivityPreviewProvenanceKind For(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (activity.PreviewActivityId.HasValue)
            return ActivityPreviewProvenanceKind.InformedByPreview;

        return activity is { TargetType: ActivityTargetType.ConnectedSystem, TargetOperationType: ActivityTargetOperationType.Delete or ActivityTargetOperationType.Deprovision }
            ? ActivityPreviewProvenanceKind.WentAheadWithoutAPreview
            : ActivityPreviewProvenanceKind.NotApplicable;
    }
}
