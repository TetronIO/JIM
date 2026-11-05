// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Staging.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// What a Configuration Change Preview predicted for a saved change, beside what the synchronisation that applied the
/// change did to the objects of the processed Object Type. Present only where both measure the same thing for the same
/// objects. The actual figures count objects of that type the run gave each outcome, including objects that changed for
/// other reasons since the preview was made, so they can exceed the prediction without the preview having been wrong.
/// </summary>
public class ActivityPredictionComparisonDto
{
    /// <summary>
    /// The unique identifier of the preview's Activity. The preview is removed under history retention, but this
    /// comparison is read from it, so a comparison is never present once it has been.
    /// </summary>
    public Guid PreviewActivityId { get; set; }

    /// <summary>
    /// One line per outcome either side counted, largest prediction first.
    /// </summary>
    public List<ActivityPredictionComparisonLineDto> Lines { get; set; } = new();

    /// <summary>
    /// Creates a DTO from the application-layer comparison.
    /// </summary>
    public static ActivityPredictionComparisonDto FromComparison(ConfigurationChangePredictionComparison comparison) => new()
    {
        PreviewActivityId = comparison.PreviewActivityId,
        Lines = comparison.Lines.Select(line => new ActivityPredictionComparisonLineDto
        {
            Outcome = line.Outcome,
            Predicted = line.Predicted,
            Actual = line.Actual
        }).ToList()
    };
}

/// <summary>
/// One outcome's predicted and actual count.
/// </summary>
public class ActivityPredictionComparisonLineDto
{
    /// <summary>
    /// What happened to the objects counted, such as Projected or Joined.
    /// </summary>
    public ActivityRunProfileExecutionItemSyncOutcomeType Outcome { get; set; }

    /// <summary>
    /// How many objects the preview said would have this outcome.
    /// </summary>
    public int Predicted { get; set; }

    /// <summary>
    /// How many objects of the processed Object Type the applying run gave this outcome.
    /// </summary>
    public int Actual { get; set; }
}
