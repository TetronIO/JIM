// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Tasking.DTOs;

namespace JIM.Web.Models;

/// <summary>
/// The filters above the Operations page's Activity tab (#2549), which apply to all three of its sections.
/// </summary>
/// <remarks>
/// Completed is filtered by the database; Queued and In progress are already in memory and are filtered by
/// <see cref="Matches"/>, which compares the same way the database does (names without regard to case, the initiator
/// by a partial match), so a filter means the same thing in every section. Result applies to Completed alone: queued
/// and running work has no result yet.
/// </remarks>
public sealed record OperationsActivityFilters
{
    /// <summary>No filter narrowed: everything shows.</summary>
    public static OperationsActivityFilters None { get; } = new();

    private static readonly ActivityStatus[] FinishedStatuses =
    [
        ActivityStatus.Complete,
        ActivityStatus.CompleteWithWarning,
        ActivityStatus.CompleteWithError,
        ActivityStatus.FailedWithError,
        ActivityStatus.Cancelled
    ];

    /// <summary>The Connected Systems to show, by name; empty for all.</summary>
    public IReadOnlyCollection<string> ConnectedSystems { get; init; } = [];

    /// <summary>The Run Profiles to show, by name; empty for all.</summary>
    public IReadOnlyCollection<string> RunProfiles { get; init; } = [];

    /// <summary>The Schedules whose work to show, by id; empty for all.</summary>
    public IReadOnlyCollection<Guid> Schedules { get; init; } = [];

    /// <summary>The results to show in Completed; empty for every finished result.</summary>
    public IReadOnlyCollection<ActivityStatus> Results { get; init; } = [];

    /// <summary>A partial, case-insensitive match on the initiator's name; blank for all.</summary>
    public string? InitiatedBy { get; init; }

    /// <summary>Only executions that have child Activities (Completed only).</summary>
    public bool HasChildActivities { get; init; }

    /// <summary>Only work a Schedule started.</summary>
    public bool ScheduledOnly { get; init; }

    /// <summary>Whether any filter is narrowed from its default.</summary>
    public bool IsActive => ConnectedSystems.Count > 0 || RunProfiles.Count > 0 || Schedules.Count > 0 || Results.Count > 0
        || !string.IsNullOrWhiteSpace(InitiatedBy) || HasChildActivities || ScheduledOnly;

    /// <summary>Whether a queued or running task passes the filters that apply to it.</summary>
    /// <param name="task">The task.</param>
    /// <param name="scheduleName">Looks a Schedule's name up by id; a queued task knows its Schedule by name only.</param>
    public bool Matches(WorkerTaskHeader task, Func<Guid, string?> scheduleName)
    {
        if (ConnectedSystems.Count > 0 && !ContainsIgnoringCase(ConnectedSystems, task.ConnectedSystemName))
            return false;

        if (RunProfiles.Count > 0 && !ContainsIgnoringCase(RunProfiles, task.RunProfileName))
            return false;

        if (ScheduledOnly && task.ScheduleExecutionId == null)
            return false;

        if (Schedules.Count > 0 && !ContainsIgnoringCase(Schedules.Select(scheduleName).OfType<string>(), task.ScheduleExecutionName))
            return false;

        if (!string.IsNullOrWhiteSpace(InitiatedBy)
            && task.InitiatedByName?.Contains(InitiatedBy.Trim(), StringComparison.OrdinalIgnoreCase) != true)
            return false;

        return true;
    }

    /// <summary>
    /// The statuses Completed asks the database for: the chosen results, or every finished one. Never In Progress,
    /// which In progress shows instead.
    /// </summary>
    public IReadOnlyCollection<ActivityStatus> CompletedStatuses() =>
        Results.Count > 0 ? Results.Where(FinishedStatuses.Contains).ToList() : FinishedStatuses;

    private static bool ContainsIgnoringCase(IEnumerable<string> values, string? value) =>
        value != null && values.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
}
