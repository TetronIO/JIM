// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Preview;

namespace JIM.Web.Models;

/// <summary>
/// What can be said about a Connected System's latest preview of one kind, and which preview, if any, the change it
/// previews records as having informed it if made now: a deletion (#134), or a Full Synchronisation (#1530).
/// </summary>
/// <param name="Status">Where the preview stands.</param>
/// <param name="ActivityId">The preview's Activity, or null when there is none.</param>
/// <param name="Started">When the preview started, which is what its age and its staleness are measured from.</param>
/// <param name="PercentComplete">How far a running preview has got, or null where it has no total to measure against.</param>
/// <param name="Lines">A finished preview's verdict, one sentence per transition, worst first.</param>
/// <param name="Staleness">What has happened since the preview started, where that was judged.</param>
public sealed record ConnectedSystemPreviewState(
    ConnectedSystemPreviewStatus Status,
    Guid? ActivityId = null,
    DateTime? Started = null,
    int? PercentComplete = null,
    IReadOnlyList<PreviewVerdictLine>? Lines = null,
    ConfigurationChangePreviewStaleness? Staleness = null)
{
    /// <summary>
    /// The preview the change records as having informed it if made now: a finished preview nothing has overtaken,
    /// and no other. An audit trail claiming the administrator was shown the consequences, when what they were shown
    /// no longer held, is worse than one saying they went ahead without looking.
    /// </summary>
    public Guid? InformingPreviewActivityId => Status == ConnectedSystemPreviewStatus.Current ? ActivityId : null;

    /// <summary>
    /// What overtook a stale preview, as the subject of a sentence ("Configuration has changed since this preview
    /// ran"). Said plainly because the remedy differs: configuration the administrator may have just fixed, or data an
    /// import has since moved.
    /// </summary>
    public string StaleReason => Staleness switch
    {
        { DataChangedAt: not null, ConfigurationChangedAt: not null } => "Data and configuration have changed",
        { ConfigurationChangedAt: not null } => "Configuration has changed",
        _ => "Data has changed"
    };

    /// <summary>
    /// Reads a preview and its staleness. <paramref name="preview"/> must carry its Activity, which says when it
    /// started, how it ended and how far a running one has got.
    /// </summary>
    public static ConnectedSystemPreviewState From(ConfigurationChangePreview? preview, ConfigurationChangePreviewStaleness? staleness)
    {
        if (preview?.Activity is not { } activity)
            return new ConnectedSystemPreviewState(ConnectedSystemPreviewStatus.NotPreviewed);

        var ended = activity.Status is ActivityStatus.FailedWithError or ActivityStatus.Cancelled;
        var blocked = preview.ReadValidationFindings().Exists(f => f.Severity == PreviewValidationSeverity.Blocking);
        if (ended || preview.HasFailed || blocked)
            return new ConnectedSystemPreviewState(ConnectedSystemPreviewStatus.DidNotFinish, preview.ActivityId, activity.Created);

        if (!preview.IsComplete)
        {
            int? percent = activity.ObjectsToProcess > 0
                ? Math.Clamp(activity.ObjectsProcessed * 100 / activity.ObjectsToProcess, 0, 100)
                : null;
            return new ConnectedSystemPreviewState(ConnectedSystemPreviewStatus.Running, preview.ActivityId, activity.Created, percent);
        }

        var status = staleness is { IsStale: true } ? ConnectedSystemPreviewStatus.Stale : ConnectedSystemPreviewStatus.Current;
        return new ConnectedSystemPreviewState(status, preview.ActivityId, activity.Created,
            Lines: ConfigurationChangePreviewVerdict.Lines(preview.ReadImpactCounts()), Staleness: staleness);
    }
}
