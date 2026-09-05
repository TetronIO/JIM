// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Operations;
using JIM.Utilities;

namespace JIM.Application.Servers;

/// <summary>
/// Answers "are JIM's background services alive, and what are they doing?" from the heartbeats each one writes
/// (<see cref="Services.ServiceHeartbeatWriter"/>). The verdicts are pure functions of the newest heartbeat per
/// service and the moment of asking, so the same rows give the same answer to the portal, the REST API and
/// PowerShell.
/// </summary>
public class SystemHealthServer
{
    /// <summary>
    /// How often every service writes its heartbeat. The writer throttles to this; the thresholds below are
    /// expressed in multiples of it.
    /// </summary>
    public const int HeartbeatIntervalSeconds = 5;

    /// <summary>
    /// A heartbeat older than this many intervals is <see cref="ServiceHealthState.Stale"/>: enough missed writes
    /// to notice, not enough to conclude the process is gone.
    /// </summary>
    public const int StaleAfterIntervals = 3;

    /// <summary>
    /// After this long without a heartbeat a Worker service is <see cref="ServiceHealthState.NotSeen"/>. A minute is
    /// twelve missed writes: long past a slow database or a garbage-collection pause.
    /// </summary>
    public const int WorkerNotSeenAfterSeconds = 60;

    /// <summary>
    /// The Scheduler's equivalent of <see cref="WorkerNotSeenAfterSeconds"/>. It is given longer because its loop
    /// blocks on schedule advancement, which can legitimately hold it for a while under a heavy schedule.
    /// </summary>
    public const int SchedulerNotSeenAfterSeconds = 120;

    /// <summary>
    /// Work that has reported no progress for this long is <see cref="ServiceHealthState.NoProgress"/>. Only judged
    /// when the service supplies a progress timestamp; a service that cannot is never accused of it.
    /// </summary>
    public const int NoProgressAfterMinutes = 10;

    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(HeartbeatIntervalSeconds);
    public static readonly TimeSpan StaleAfter = HeartbeatInterval * StaleAfterIntervals;
    public static readonly TimeSpan NoProgressAfter = TimeSpan.FromMinutes(NoProgressAfterMinutes);

    /// <summary>
    /// The order services appear in every report, so a display can rely on the position.
    /// </summary>
    private static readonly JimService[] ReportOrder =
    [
        JimService.WorkerSync,
        JimService.WorkerPasswordDelivery,
        JimService.Scheduler
    ];

    private JimApplication Application { get; }

    internal SystemHealthServer(JimApplication application)
    {
        Application = application;
    }

    /// <summary>
    /// How long a service may go unheard before it is presumed down.
    /// </summary>
    public static TimeSpan NotSeenAfter(JimService service) => service switch
    {
        JimService.Scheduler => TimeSpan.FromSeconds(SchedulerNotSeenAfterSeconds),
        _ => TimeSpan.FromSeconds(WorkerNotSeenAfterSeconds)
    };

    /// <summary>
    /// Builds the health report as of <paramref name="asOf"/> (UTC). Every service is present, in a fixed order; one
    /// that has never written a heartbeat is reported as <see cref="ServiceHealthState.NotSeen"/> with the reason
    /// "Never reported".
    /// </summary>
    public async Task<ServiceHealthReport> GetServiceHealthAsync(DateTime asOf)
    {
        var heartbeats = await Application.Repository.System.GetLatestServiceHeartbeatsAsync();

        // The repository already returns the newest row per service, but the verdict must not depend on that: a
        // second row for a service (a restarted instance beside its predecessor) is resolved here as well, so the
        // newest instance always wins whatever the source.
        var newestByService = heartbeats
            .GroupBy(h => h.Service)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.LastSeenAt).First());

        var services = ReportOrder
            .Select(service => Derive(service, newestByService.GetValueOrDefault(service), asOf))
            .ToList();

        return new ServiceHealthReport
        {
            Services = services,
            // ServiceHealthState is ordered by severity, so the worst state present is the largest value.
            Overall = services.Max(s => s.State),
            WebVersion = JimVersion.Current,
            GeneratedAt = asOf
        };
    }

    /// <summary>
    /// Derives one service's verdict from its newest heartbeat (or none) at <paramref name="asOf"/>. Public and static
    /// so a display can be tested against the same rules without a repository behind it.
    /// </summary>
    public static ServiceHealth Derive(JimService service, ServiceHeartbeat? heartbeat, DateTime asOf)
    {
        if (heartbeat == null)
        {
            return new ServiceHealth
            {
                Service = service,
                State = ServiceHealthState.NotSeen,
                Reason = "Never reported"
            };
        }

        var age = asOf - heartbeat.LastSeenAt;
        var notSeenAfter = NotSeenAfter(service);
        ServiceHealthState state;
        string reason;

        // Precedence, most to least serious. A dead process's last words about its work are not a wedged task, so
        // NotSeen is judged before NoProgress; and a wedged task matters more than a few late heartbeats, so
        // NoProgress is judged before Stale.
        if (age >= notSeenAfter)
        {
            state = ServiceHealthState.NotSeen;
            reason = $"Last seen {Describe(age)} ago; expected within {(int)notSeenAfter.TotalSeconds} seconds";
        }
        else if (heartbeat.CurrentWork != null
                 && heartbeat.LastProgressAt is { } lastProgressAt
                 && asOf - lastProgressAt > NoProgressAfter)
        {
            state = ServiceHealthState.NoProgress;
            reason = $"{heartbeat.CurrentWork} has made no progress for {Describe(asOf - lastProgressAt)}";
        }
        else if (age > StaleAfter)
        {
            state = ServiceHealthState.Stale;
            reason = $"Last seen {Describe(age)} ago; expected every {HeartbeatIntervalSeconds} seconds";
        }
        else
        {
            state = ServiceHealthState.Running;
            reason = $"Last seen {Describe(age)} ago";
        }

        return new ServiceHealth
        {
            Service = service,
            State = state,
            Reason = reason,
            InstanceId = heartbeat.InstanceId,
            HostName = heartbeat.HostName,
            Version = heartbeat.Version,
            StartedAt = heartbeat.StartedAt,
            LastSeenAt = heartbeat.LastSeenAt,
            CurrentWork = heartbeat.CurrentWork,
            CurrentWorkStartedAt = heartbeat.CurrentWorkStartedAt,
            LastProgressAt = heartbeat.LastProgressAt,
            Detail = heartbeat.Detail
        };
    }

    /// <summary>
    /// An age in its single most useful unit ("16 seconds", "4 minutes", "3 hours", "2 days"), rounded down. One
    /// unit is right for a reason sentence: "1 minute 4 seconds ago" is precision nobody acts on.
    /// </summary>
    private static string Describe(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        return age switch
        {
            { TotalMinutes: < 1 } => Unit((int)age.TotalSeconds, "second"),
            { TotalHours: < 1 } => Unit((int)age.TotalMinutes, "minute"),
            { TotalDays: < 1 } => Unit((int)age.TotalHours, "hour"),
            _ => Unit((int)age.TotalDays, "day")
        };

        static string Unit(int value, string singular) => value == 1 ? $"1 {singular}" : $"{value} {singular}s";
    }
}
