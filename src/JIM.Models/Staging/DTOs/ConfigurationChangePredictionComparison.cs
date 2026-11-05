// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// What a Configuration Change Preview predicted for a saved change, beside what the synchronisation that applied the
/// change did (#2618). Present only where both sides measure the same thing for the same objects; see
/// <c>ConfigurationChangePredictionPolicy</c> for the conditions.
/// </summary>
public class ConfigurationChangePredictionComparison
{
    /// <summary>
    /// The preview that informed the save.
    /// </summary>
    public Guid PreviewActivityId { get; init; }

    /// <summary>
    /// One line per outcome both sides count, largest prediction first.
    /// </summary>
    public IReadOnlyList<ConfigurationChangePredictionLine> Lines { get; init; } = [];
}

/// <summary>
/// One outcome's predicted and actual count.
/// </summary>
/// <param name="Outcome">What happened to the objects counted.</param>
/// <param name="Predicted">How many objects the preview said would have this outcome.</param>
/// <param name="Actual">How many objects of the processed Object Type the applying run gave this outcome.</param>
public record ConfigurationChangePredictionLine(ActivityRunProfileExecutionItemSyncOutcomeType Outcome, int Predicted, int Actual);
