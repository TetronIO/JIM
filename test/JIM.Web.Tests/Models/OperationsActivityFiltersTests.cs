// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests.Models;

/// <summary>
/// The Activity tab's filters (#2549) apply to queued and running work as well as to history. History is filtered by
/// the database; queued and running work is already in memory, so it is filtered here, and must match the same way
/// the database does (names compared without regard to case) or the three sections would disagree about what a filter means.
/// </summary>
[TestFixture]
public class OperationsActivityFiltersTests
{
    private static WorkerTaskHeader Task(string? connectedSystem = "Glitterband EMEA", string? runProfile = "Full Sync",
        string? schedule = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"{connectedSystem} - {runProfile}",
        Type = "Synchronisation",
        Status = WorkerTaskStatus.Queued,
        ConnectedSystemName = connectedSystem,
        RunProfileName = runProfile,
        InitiatedByName = "Infrastructure Key",
        ScheduleExecutionId = schedule != null ? Guid.NewGuid() : null,
        ScheduleExecutionName = schedule
    };

    [Test]
    public void Matches_NoFilters_MatchesEverything()
    {
        Assert.That(OperationsActivityFilters.None.Matches(Task(), _ => null), Is.True);
    }

    [Test]
    public void Matches_ConnectedSystemAndRunProfile_ComparedWithoutRegardToCase()
    {
        var filters = OperationsActivityFilters.None with { ConnectedSystems = ["glitterband emea"], RunProfiles = ["FULL SYNC"] };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(filters.Matches(Task(), _ => null), Is.True);
            Assert.That(filters.Matches(Task(connectedSystem: "Yellowstone APAC"), _ => null), Is.False);
            Assert.That(filters.Matches(Task(runProfile: "Export"), _ => null), Is.False);
        }
    }

    [Test]
    public void Matches_Schedule_MatchesTasksOfTheChosenSchedulesByName()
    {
        var nightly = Guid.NewGuid();
        var filters = OperationsActivityFilters.None with { Schedules = [nightly] };
        string? NameOf(Guid id) => id == nightly ? "Nightly" : null;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(filters.Matches(Task(schedule: "Nightly"), NameOf), Is.True);
            Assert.That(filters.Matches(Task(schedule: "Weekly"), NameOf), Is.False);
            Assert.That(filters.Matches(Task(), NameOf), Is.False);
        }
    }

    [Test]
    public void Matches_ScheduledOnly_ExcludesTasksOutsideSchedules()
    {
        var filters = OperationsActivityFilters.None with { ScheduledOnly = true };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(filters.Matches(Task(schedule: "Nightly"), _ => null), Is.True);
            Assert.That(filters.Matches(Task(), _ => null), Is.False);
        }
    }

    [Test]
    public void Matches_TaskThatIsNotARunProfile_FailsAConnectedSystemFilterItCannotSatisfy()
    {
        var filters = OperationsActivityFilters.None with { ConnectedSystems = ["Glitterband EMEA"] };

        Assert.That(filters.Matches(Task(connectedSystem: null, runProfile: null), _ => null), Is.False);
    }

    [Test]
    public void CompletedStatuses_NoResultChosen_IsEveryFinishedStatusButNeverInProgress()
    {
        var statuses = OperationsActivityFilters.None.CompletedStatuses();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statuses, Does.Not.Contain(ActivityStatus.InProgress));
            Assert.That(statuses, Does.Not.Contain(ActivityStatus.NotSet));
            Assert.That(statuses, Does.Contain(ActivityStatus.Complete));
            Assert.That(statuses, Does.Contain(ActivityStatus.Cancelled));
        }
    }

    [Test]
    public void CompletedStatuses_ResultChosen_IsJustThat()
    {
        var filters = OperationsActivityFilters.None with { Results = [ActivityStatus.FailedWithError] };

        Assert.That(filters.CompletedStatuses(), Is.EqualTo(new[] { ActivityStatus.FailedWithError }));
    }

    [Test]
    public void IsActive_AnyFilterNarrowed_IsTrue()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(OperationsActivityFilters.None.IsActive, Is.False);
            Assert.That((OperationsActivityFilters.None with { HasChildActivities = true }).IsActive, Is.True);
        }
    }

    [Test]
    public void ToQueryParameters_NoFilters_RemovesEveryFilterParameter()
    {
        var parameters = OperationsActivityFilters.None.ToQueryParameters();

        // Every key is stated, so applying them clears a filter the URL still carries from before.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parameters.Keys, Is.EquivalentTo(OperationsActivityFilters.QueryParameterNames));
            Assert.That(parameters.Values, Is.All.Null);
        }
    }

    [Test]
    public void FromQuery_WhatToQueryParametersWrote_RoundTripsEveryFilter()
    {
        var scheduleId = Guid.NewGuid();
        var filters = new OperationsActivityFilters
        {
            ConnectedSystems = ["Glitterband EMEA", "Yellowstone, APAC"],
            RunProfiles = ["Full Import"],
            Schedules = [scheduleId],
            Results = [ActivityStatus.FailedWithError, ActivityStatus.Cancelled],
            HasChildActivities = true,
            ScheduledOnly = true
        };

        var read = OperationsActivityFilters.FromQuery(ToQuery(filters.ToQueryParameters()));

        using (Assert.EnterMultipleScope())
        {
            // A comma inside a name must survive: names are repeated parameters, not a joined list.
            Assert.That(read.ConnectedSystems, Is.EqualTo(filters.ConnectedSystems));
            Assert.That(read.RunProfiles, Is.EqualTo(filters.RunProfiles));
            Assert.That(read.Schedules, Is.EqualTo(filters.Schedules));
            Assert.That(read.Results, Is.EqualTo(filters.Results));
            Assert.That(read.HasChildActivities, Is.True);
            Assert.That(read.ScheduledOnly, Is.True);
        }
    }

    [Test]
    public void FromQuery_InitiatedByParameter_IsNotAFilter()
    {
        // The Completed section's search box searches the initiator already; a second box for it was the filter row's
        // one redundancy. A bookmarked URL that still carries it reads as unfiltered.
        Assert.That(OperationsActivityFilters.FromQuery("?by=Infrastructure"), Is.EqualTo(OperationsActivityFilters.None));
    }

    [Test]
    public void ActiveChips_NoFilters_IsEmpty()
    {
        Assert.That(OperationsActivityFilters.None.ActiveChips(_ => null), Is.Empty);
    }

    [Test]
    public void ActiveChips_EachNarrowedFilter_IsOneChipNamingItsValuesInFilterRowOrder()
    {
        var nightly = Guid.NewGuid();
        var filters = new OperationsActivityFilters
        {
            ConnectedSystems = ["Glitterband EMEA", "Yellowstone APAC"],
            RunProfiles = ["Full Import"],
            Schedules = [nightly],
            Results = [ActivityStatus.CompleteWithWarning, ActivityStatus.FailedWithError],
            HasChildActivities = true,
            ScheduledOnly = true
        };

        var chips = filters.ActiveChips(id => id == nightly ? "Nightly Delta Cycle" : null);

        Assert.That(chips, Is.EqualTo(new[]
        {
            new ActiveFilterChip(ActivityFilterKind.ConnectedSystems, "Connected System: Glitterband EMEA, Yellowstone APAC"),
            new ActiveFilterChip(ActivityFilterKind.RunProfiles, "Run Profile: Full Import"),
            new ActiveFilterChip(ActivityFilterKind.Schedules, "Schedule: Nightly Delta Cycle"),
            new ActiveFilterChip(ActivityFilterKind.Results, "Result: Complete With Warning, Failed With Error"),
            new ActiveFilterChip(ActivityFilterKind.HasChildActivities, "Has child activities"),
            new ActiveFilterChip(ActivityFilterKind.ScheduledOnly, "Scheduled only")
        }));
    }

    [Test]
    public void Without_ClearsOnlyTheNamedFilter()
    {
        var filters = new OperationsActivityFilters { ConnectedSystems = ["Glitterband EMEA"], ScheduledOnly = true };

        var without = filters.Without(ActivityFilterKind.ConnectedSystems);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(without.ConnectedSystems, Is.Empty);
            Assert.That(without.ScheduledOnly, Is.True);
            Assert.That(filters.Without(ActivityFilterKind.ScheduledOnly).Without(ActivityFilterKind.ConnectedSystems),
                Is.EqualTo(OperationsActivityFilters.None), "clearing every narrowed filter is no filter at all");
        }
    }

    [Test]
    public void FromQuery_NoFilterParameters_ReturnsNoFilters()
    {
        Assert.That(OperationsActivityFilters.FromQuery("?t=activity&s=queued"), Is.EqualTo(OperationsActivityFilters.None));
    }

    [Test]
    public void FromQuery_ValuesTypedWrongly_IgnoresThemRatherThanFailing()
    {
        var read = OperationsActivityFilters.FromQuery("?schedule=not-a-guid&result=InProgress&result=cancelled&result=nonsense&children=yes");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Schedules, Is.Empty);
            // In Progress is never a finished result; case is tolerated for the rest.
            Assert.That(read.Results, Is.EqualTo(new[] { ActivityStatus.Cancelled }));
            Assert.That(read.HasChildActivities, Is.False);
        }
    }

    /// <summary>Applies parameters the way the browser does: each value of a key becomes one repeated parameter.</summary>
    private static string ToQuery(IReadOnlyDictionary<string, string[]?> parameters) =>
        "?" + string.Join("&", parameters
            .Where(p => p.Value != null)
            .SelectMany(p => p.Value!.Select(v => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(v)}")));
}
