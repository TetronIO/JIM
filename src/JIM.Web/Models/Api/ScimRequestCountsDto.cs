// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Web.Models.Api;

/// <summary>
/// What one SCIM client key's requests did to a Connected System's SCIM endpoint within one 15-minute window.
/// </summary>
public class ScimRequestCountsDto
{
    /// <summary>
    /// Resources created.
    /// </summary>
    public int Created { get; set; }

    /// <summary>
    /// Replace and patch requests accepted.
    /// </summary>
    public int Updated { get; set; }

    /// <summary>
    /// Resources deleted.
    /// </summary>
    public int Deleted { get; set; }

    /// <summary>
    /// Requests refused for a reason other than authentication. The Activity's warning names the latest.
    /// </summary>
    public int Rejected { get; set; }

    /// <summary>
    /// The counts of a SCIM request summary Activity; null for any other Activity.
    /// </summary>
    /// <remarks>The summary keeps its counts in the Activity's existing counter columns (#124, D17).</remarks>
    public static ScimRequestCountsDto? FromActivity(Activity activity) => activity.TargetType != ActivityTargetType.ScimRequests
        ? null
        : new ScimRequestCountsDto
        {
            Created = activity.TotalAdded,
            Updated = activity.TotalUpdated,
            Deleted = activity.TotalDeleted,
            Rejected = activity.TotalErrors
        };
}
