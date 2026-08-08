// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;
using JIM.Models.Utility;

namespace JIM.Data.Repositories;

/// <summary>
/// Persistence for configuration change previews (#827). Preview rows hang off the preview's Activity and cascade
/// from it, so there is deliberately no delete-by-preview method here: preview data disappears when its Activity
/// does, under the retention the customer was told applies, and a second removal path would be a second thing to
/// keep correct.
/// </summary>
public interface IConfigurationChangePreviewRepository
{
    /// <summary>
    /// Persists a new preview row. The Activity it belongs to must already exist; set only
    /// <see cref="ConfigurationChangePreview.ActivityId"/> and never the navigation, or the insert walks the graph
    /// and tries to insert the Activity a second time.
    /// </summary>
    Task CreatePreviewAsync(ConfigurationChangePreview preview);

    /// <summary>
    /// Writes the preview row's own columns back: stage statuses, timings, estimates and cap decisions. Touches
    /// nothing else on the graph, so it is safe to call repeatedly during a run without disturbing the groups and
    /// deltas hanging off it.
    /// </summary>
    Task UpdatePreviewAsync(ConfigurationChangePreview preview);

    /// <summary>The preview row alone, without its groups or deltas.</summary>
    Task<ConfigurationChangePreview?> GetPreviewAsync(Guid activityId);

    /// <summary>The preview's summary groups, largest first: the panel's landing view.</summary>
    Task<List<ConfigurationChangePreviewGroup>> GetPreviewGroupsAsync(Guid activityId);

    /// <summary>
    /// Persists a completed preview's summary groups together with the delta rows kept for each. One call, one unit
    /// of work: a half-written result set that later reads as complete is the failure mode worth designing out,
    /// because its group counts would disagree with the rows beneath them and neither would announce itself as
    /// wrong.
    /// </summary>
    Task CreatePreviewResultsAsync(IReadOnlyCollection<ConfigurationChangePreviewGroup> groups);

    /// <summary>
    /// A page of drill-down rows, optionally restricted to one summary group. Ordered deterministically so paging
    /// is stable: the same page shows the same rows on every request.
    /// </summary>
    /// <param name="search">
    /// Optional case-insensitive text filter over the object's display name, the attribute, and the old and new
    /// values. Applied in the query rather than to the page, because a group holds far more rows than any page: a
    /// filter over an already-fetched page would search a fraction of the group and report the result as the whole
    /// answer.
    /// </param>
    Task<PagedResultSet<ConfigurationChangePreviewDelta>> GetPreviewDeltasAsync(Guid activityId, Guid? groupId, int page, int pageSize,
        string? search = null);
}
