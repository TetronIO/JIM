// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Staging.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// One saved configuration change that a Connected System's synchronisation applies by processing every object of a
/// Connected System Object Type, such as an edit to an import Synchronisation Rule.
/// </summary>
public class AwaitingSynchronisationChangeDto
{
    /// <summary>
    /// What was saved: the rule and what was done to it, as recorded when it was saved. It still reads after the rule has
    /// been deleted.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// When the change was saved (UTC).
    /// </summary>
    public DateTime SavedAt { get; set; }

    /// <summary>
    /// The Activity that recorded the change, which holds what changed. Null once that Activity has been removed.
    /// </summary>
    public Guid? ConfigurationChangeActivityId { get; set; }

    /// <summary>
    /// The kind of principal that saved the change (User, ApiKey or System). Null once the Activity that recorded the change
    /// has been removed.
    /// </summary>
    public ActivityInitiatorType? InitiatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that saved the change; null when it has none or the Activity that recorded
    /// the change has been removed.
    /// </summary>
    public Guid? InitiatedById { get; set; }

    /// <summary>
    /// The name of the principal that saved the change, as it was when the change was saved; null once the Activity that
    /// recorded the change has been removed.
    /// </summary>
    public string? InitiatedByName { get; set; }

    /// <summary>
    /// When a synchronisation applied the change (UTC). Null while the change is awaiting synchronisation, as every change
    /// in a Connected System's awaiting list is.
    /// </summary>
    public DateTime? AppliedAt { get; set; }

    /// <summary>
    /// The synchronisation run that claimed the change. Null while no run has. A run that failed or was cancelled leaves
    /// its claim in place, and the next run takes it over, so this can name a run that did not apply the change: check
    /// <c>AppliedAt</c> for that.
    /// </summary>
    public Guid? SynchronisationActivityId { get; set; }

    /// <summary>
    /// Creates a DTO from a change read for a page or response.
    /// </summary>
    public static AwaitingSynchronisationChangeDto FromSummary(ConfigurationChangeSynchronisationSummary summary)
    {
        return new AwaitingSynchronisationChangeDto
        {
            Reason = summary.Reason,
            SavedAt = summary.RequestedAt,
            ConfigurationChangeActivityId = summary.ConfigurationChangeActivityId,
            InitiatedByType = summary.InitiatedByType,
            InitiatedById = summary.InitiatedById,
            InitiatedByName = summary.InitiatedByName,
            AppliedAt = summary.AppliedAt,
            SynchronisationActivityId = summary.ClaimedByActivityId
        };
    }
}
