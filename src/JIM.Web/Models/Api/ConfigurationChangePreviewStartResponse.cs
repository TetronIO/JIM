// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;

namespace JIM.Web.Models.Api;

/// <summary>
/// What a caller learns the moment a preview is started: the Activity to poll, and stage 1's verdict on the
/// proposal itself.
///
/// Stage 1 runs in the request path precisely so this can be answered immediately. A proposal carrying a blocking
/// finding is never evaluated: counting the objects a change would affect is a statement about a change that will
/// happen, and this one cannot.
/// </summary>
public class ConfigurationChangePreviewStartResponse
{
    /// <summary>
    /// The preview's Activity, which is also the preview's identifier. Read the preview back from
    /// <c>GET /previews/{activityId}</c> and its drill-down rows from <c>GET /previews/{activityId}/deltas</c>.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>What stage 1 found about the proposal itself, in the order the adapter reported it.</summary>
    public List<PreviewValidationFinding> ValidationFindings { get; set; } = [];

    /// <summary>
    /// True when the proposal cannot be applied. The remaining stages are recorded as not applicable rather than
    /// run; read the findings instead of waiting for results that will never arrive.
    /// </summary>
    public bool IsBlocked { get; set; }

    /// <summary>
    /// True when stage 1 itself errored, as opposed to finding something wrong with the proposal. The Activity
    /// carries the error; nothing further ran.
    /// </summary>
    public bool Failed { get; set; }

    /// <summary>
    /// How many objects the adapter expects the change to touch, and how many drill-down rows that implies. Null
    /// when the proposal was blocked or stage 1 failed, in which case no estimate was taken.
    /// </summary>
    public int? EstimatedAffectedObjects { get; set; }

    public long? EstimatedDeltaRows { get; set; }

    public static ConfigurationChangePreviewStartResponse FromResult(ConfigurationChangePreviewStartResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new ConfigurationChangePreviewStartResponse
        {
            ActivityId = result.ActivityId,
            ValidationFindings = [.. result.Findings],
            IsBlocked = result.IsBlocked,
            Failed = result.Failed,
            EstimatedAffectedObjects = result.Estimate?.AffectedObjects,
            EstimatedDeltaRows = result.Estimate?.EstimatedDeltaRows
        };
    }
}
