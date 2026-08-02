// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Preview;

/// <summary>
/// What the caller learns the moment a preview is requested: the Activity to watch, and stage 1's verdict on the
/// proposal itself.
///
/// Stage 1 runs in the request path precisely so this can be answered immediately. A proposal with a blocking
/// finding never proceeds to evaluation: counting the objects a change would affect is meaningless when the change
/// cannot be applied at all, and running it anyway would make an invalid proposal look like a considered one.
/// </summary>
/// <param name="ActivityId">The preview's Activity; the handle for progress, results and cancellation.</param>
/// <param name="Findings">Stage 1's findings, in the order the adapter returned them.</param>
/// <param name="Estimate">
/// The adapter's cost estimate, or null when stage 1 blocked or failed and no estimate was taken.
/// </param>
/// <param name="Failed">
/// True when stage 1 itself errored, as opposed to finding something wrong with the proposal. The preview's Activity
/// carries the error; nothing further ran.
/// </param>
public record ConfigurationChangePreviewStartResult(
    Guid ActivityId,
    IReadOnlyList<PreviewValidationFinding> Findings,
    PreviewCostEstimate? Estimate = null,
    bool Failed = false)
{
    /// <summary>
    /// True when the proposal cannot be applied. The remaining stages are recorded as not applicable rather than
    /// run, and the surface should show the findings instead of a preview.
    /// </summary>
    public bool IsBlocked => Findings.Any(f => f.Severity == PreviewValidationSeverity.Blocking);
}
