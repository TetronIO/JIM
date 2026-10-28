// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Web;
using JIM.Models.Activities;
using JIM.Models.Tasking.DTOs;
using JIM.Utilities;

namespace JIM.Web.Models;

/// <summary>
/// The filters above the Operations page's Activity tab (#2549), which apply to all three of its sections.
/// </summary>
/// <remarks>
/// Completed is filtered by the database; Queued and In progress are already in memory and are filtered by
/// <see cref="Matches"/>, which compares the same way the database does (names without regard to case), so a filter
/// means the same thing in every section. Result applies to Completed alone: queued and running work has no result
/// yet. Who started the work is not a filter: Completed's search box searches the initiator, and the two sections
/// above it hold too few rows to need narrowing by name.
/// </remarks>
public sealed record OperationsActivityFilters
{
    /// <summary>No filter narrowed: everything shows.</summary>
    public static OperationsActivityFilters None { get; } = new();

    private const string ConnectedSystemParameter = "system";
    private const string RunProfileParameter = "profile";
    private const string ScheduleParameter = "schedule";
    private const string ResultParameter = "result";
    private const string HasChildActivitiesParameter = "children";
    private const string ScheduledOnlyParameter = "scheduled";
    private const string FlagValue = "1";

    /// <summary>The URL parameters the filters are written to, so a filtered view can be bookmarked and shared.</summary>
    public static IReadOnlyCollection<string> QueryParameterNames { get; } =
    [
        ConnectedSystemParameter, RunProfileParameter, ScheduleParameter, ResultParameter, HasChildActivitiesParameter,
        ScheduledOnlyParameter
    ];

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

    /// <summary>Only executions that have child Activities (Completed only).</summary>
    public bool HasChildActivities { get; init; }

    /// <summary>Only work a Schedule started.</summary>
    public bool ScheduledOnly { get; init; }

    /// <summary>Whether any filter is narrowed from its default.</summary>
    public bool IsActive => ConnectedSystems.Count > 0 || RunProfiles.Count > 0 || Schedules.Count > 0 || Results.Count > 0
        || HasChildActivities || ScheduledOnly;

    /// <summary>
    /// One chip per narrowed filter, in the filter row's order, each naming the filter and its values, so a view
    /// filtered with the row closed still says what narrows it.
    /// </summary>
    /// <param name="scheduleName">Looks a Schedule's name up by id, for the Schedule chip.</param>
    public IReadOnlyList<ActiveFilterChip> ActiveChips(Func<Guid, string?> scheduleName)
    {
        var chips = new List<ActiveFilterChip>();
        if (ConnectedSystems.Count > 0)
            chips.Add(new ActiveFilterChip(ActivityFilterKind.ConnectedSystems, $"Connected System: {string.Join(", ", ConnectedSystems)}"));
        if (RunProfiles.Count > 0)
            chips.Add(new ActiveFilterChip(ActivityFilterKind.RunProfiles, $"Run Profile: {string.Join(", ", RunProfiles)}"));
        if (Schedules.Count > 0)
            chips.Add(new ActiveFilterChip(ActivityFilterKind.Schedules, $"Schedule: {string.Join(", ", Schedules.Select(id => scheduleName(id) ?? "Unknown Schedule"))}"));
        if (Results.Count > 0)
            chips.Add(new ActiveFilterChip(ActivityFilterKind.Results, $"Result: {string.Join(", ", Results.Select(r => r.ToString().SplitOnCapitalLetters()))}"));
        if (HasChildActivities)
            chips.Add(new ActiveFilterChip(ActivityFilterKind.HasChildActivities, "Has child activities"));
        if (ScheduledOnly)
            chips.Add(new ActiveFilterChip(ActivityFilterKind.ScheduledOnly, "Scheduled only"));
        return chips;
    }

    /// <summary>These filters with one of them back at its default: what closing that filter's chip does.</summary>
    public OperationsActivityFilters Without(ActivityFilterKind kind)
    {
        var without = kind switch
        {
            ActivityFilterKind.ConnectedSystems => this with { ConnectedSystems = [] },
            ActivityFilterKind.RunProfiles => this with { RunProfiles = [] },
            ActivityFilterKind.Schedules => this with { Schedules = [] },
            ActivityFilterKind.Results => this with { Results = [] },
            ActivityFilterKind.HasChildActivities => this with { HasChildActivities = false },
            ActivityFilterKind.ScheduledOnly => this with { ScheduledOnly = false },
            _ => this
        };
        return without.IsActive ? without : None;
    }

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

        return true;
    }

    /// <summary>
    /// The statuses Completed asks the database for: the chosen results, or every finished one. Never In Progress,
    /// which In progress shows instead.
    /// </summary>
    public IReadOnlyCollection<ActivityStatus> CompletedStatuses() =>
        Results.Count > 0 ? Results.Where(FinishedStatuses.Contains).ToList() : FinishedStatuses;

    /// <summary>
    /// Reads the filters from a URL query. Lists are repeated parameters, so a name holding a comma survives. A value
    /// that does not parse is ignored rather than failing the page, since the URL is typed by hand as often as written
    /// by the page.
    /// </summary>
    public static OperationsActivityFilters FromQuery(string? query)
    {
        var parameters = HttpUtility.ParseQueryString(query ?? string.Empty);
        string[] Values(string name) => parameters.GetValues(name) ?? [];

        var filters = new OperationsActivityFilters
        {
            ConnectedSystems = Values(ConnectedSystemParameter).Where(v => v.Length > 0).ToList(),
            RunProfiles = Values(RunProfileParameter).Where(v => v.Length > 0).ToList(),
            Schedules = Values(ScheduleParameter).Select(v => Guid.TryParse(v, out var id) ? id : (Guid?)null).OfType<Guid>().ToList(),
            Results = Values(ResultParameter)
                .Select(v => Enum.TryParse<ActivityStatus>(v, ignoreCase: true, out var status) ? status : (ActivityStatus?)null)
                .OfType<ActivityStatus>()
                .Where(FinishedStatuses.Contains)
                .ToList(),
            HasChildActivities = parameters[HasChildActivitiesParameter] == FlagValue,
            ScheduledOnly = parameters[ScheduledOnlyParameter] == FlagValue
        };

        return filters.IsActive ? filters : None;
    }

    /// <summary>
    /// The filters as URL parameters: every filter parameter is stated, with null for one at its default, so applying
    /// them removes a filter the URL still carries and leaves every other parameter on the page alone.
    /// </summary>
    public IReadOnlyDictionary<string, string[]?> ToQueryParameters() => new Dictionary<string, string[]?>
    {
        [ConnectedSystemParameter] = ListOrNull(ConnectedSystems),
        [RunProfileParameter] = ListOrNull(RunProfiles),
        [ScheduleParameter] = ListOrNull(Schedules.Select(id => id.ToString())),
        [ResultParameter] = ListOrNull(Results.Select(r => r.ToString())),
        [HasChildActivitiesParameter] = HasChildActivities ? [FlagValue] : null,
        [ScheduledOnlyParameter] = ScheduledOnly ? [FlagValue] : null
    };

    private static string[]? ListOrNull(IEnumerable<string> values)
    {
        var list = values.ToArray();
        return list.Length > 0 ? list : null;
    }

    private static bool ContainsIgnoringCase(IEnumerable<string> values, string? value) =>
        value != null && values.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
}
