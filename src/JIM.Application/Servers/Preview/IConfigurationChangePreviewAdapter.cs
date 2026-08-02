// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// Answers "what would this configuration change do?" for one configuration surface. The framework owns everything
/// that is the same for every surface (staging, progress, grouping, capping, persistence, dispatch); an adapter owns
/// only the part that is genuinely surface-specific, which is the evaluation itself.
///
/// **Nothing an adapter does may persist a change.** Every method here is read-only with respect to the proposed
/// configuration and to the objects it would affect. A preview that wrote anything would be the single worst defect
/// this framework could have: an administrator asking what a change would do, and thereby doing it.
///
/// Not every surface implements every stage. <see cref="CountImpactAsync"/> is the minimum for a destructive
/// surface, because a count is what an administrator consents to; <see cref="EvaluateDeltasAsync"/> may yield
/// nothing for a count-only adapter, and the framework records that stage as not applicable rather than as failed.
/// </summary>
public interface IConfigurationChangePreviewAdapter
{
    /// <summary>
    /// The surface this adapter serves. One adapter per surface; the registry refuses two.
    /// </summary>
    ConfigurationChangePreviewSurface Surface { get; }

    /// <summary>
    /// Whether this adapter evaluates per-object deltas at all. False for a count-only adapter, and the framework
    /// then records the summary and delta stages as not applicable without calling
    /// <see cref="EvaluateDeltasAsync"/>.
    ///
    /// Declared rather than inferred from an empty stream, because the two are not the same thing: "this adapter
    /// does not evaluate objects" and "this change would affect no objects" are opposite answers, and an
    /// administrator reading an empty drill-down needs to know which one they are looking at.
    /// </summary>
    bool ProducesDeltas { get; }

    /// <summary>
    /// Stage 1. Structural findings about the proposal itself: what is invalid, contradictory, or blocked. Runs
    /// synchronously in the request path, so it must stay near-instant and must not evaluate any population.
    /// </summary>
    Task<List<PreviewValidationFinding>> ValidateAsync(PreviewContext context);

    /// <summary>
    /// A cheap, set-based estimate of the affected population, used to decide where the preview runs and whether to
    /// offer the administrator a capped result. Approximate is fine; expensive is not.
    /// </summary>
    Task<PreviewCostEstimate> EstimateCostAsync(PreviewContext context);

    /// <summary>
    /// Stage 2. Per-transition counts from set-based SQL only, never per-object evaluation. These are what a
    /// destructive change is confirmed against, so they arrive long before the detail does.
    /// </summary>
    Task<List<PreviewImpactCount>> CountImpactAsync(PreviewContext context);

    /// <summary>
    /// Stages 3 and 4. Streams one delta per affected object, read-only. Streaming rather than returning a list is
    /// load-bearing: previews run against populations of hundreds of thousands, and materialising them would put
    /// the whole population in memory in JIM.Web's process.
    /// </summary>
    /// <param name="cancellationToken">
    /// Honour this. An administrator who cancels a preview has said they no longer want the answer, and an
    /// evaluation that keeps running holds a database connection and a worker slot for nothing.
    /// </param>
    IAsyncEnumerable<PreviewDelta> EvaluateDeltasAsync(PreviewContext context, CancellationToken cancellationToken);
}
