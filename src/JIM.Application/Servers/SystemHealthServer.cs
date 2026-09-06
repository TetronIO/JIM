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
    /// A heartbeat older than this many intervals is <see cref="ServiceHealthCondition.HeartbeatOverdue"/>: enough
    /// missed writes to notice, not enough to conclude the process is gone.
    /// </summary>
    public const int HeartbeatOverdueAfterIntervals = 3;

    /// <summary>
    /// After this long without a heartbeat a Worker service is <see cref="ServiceHealthCondition.NoHeartbeat"/>. A
    /// minute is twelve missed writes: long past a slow database or a garbage-collection pause.
    /// </summary>
    public const int WorkerNoHeartbeatAfterSeconds = 60;

    /// <summary>
    /// The Scheduler's equivalent of <see cref="WorkerNoHeartbeatAfterSeconds"/>. It is given longer because its
    /// loop blocks on schedule advancement, which can legitimately hold it for a while under a heavy schedule.
    /// </summary>
    public const int SchedulerNoHeartbeatAfterSeconds = 120;

    /// <summary>
    /// Work that has reported no progress for this long is <see cref="ServiceHealthCondition.Stalled"/>. Only judged
    /// when the service supplies a progress timestamp; a service that cannot is never accused of it.
    /// </summary>
    public const int StalledAfterMinutes = 10;

    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(HeartbeatIntervalSeconds);
    public static readonly TimeSpan HeartbeatOverdueAfter = HeartbeatInterval * HeartbeatOverdueAfterIntervals;
    public static readonly TimeSpan StalledAfter = TimeSpan.FromMinutes(StalledAfterMinutes);

    /// <summary>
    /// The services every deployment is expected to run, in the order they appear in a report. A service on this
    /// list with no heartbeat at all is reported as unhealthy ("never started"), which is the honest reading of a
    /// Worker that never came up. Password delivery is deliberately absent until the Password Delivery Service
    /// exists to write its heartbeat (plan #1635, layer 2); listing it earlier would put a permanent red card and
    /// a permanent banner on every deployment for a service that cannot yet report.
    /// </summary>
    public static readonly JimService[] ExpectedServices =
    [
        JimService.WorkerSync,
        JimService.Scheduler
    ];

    private JimApplication Application { get; }

    internal SystemHealthServer(JimApplication application)
    {
        Application = application;
    }

    /// <summary>
    /// How long a service may go without a heartbeat before it is presumed down.
    /// </summary>
    public static TimeSpan NoHeartbeatAfter(JimService service) => service switch
    {
        JimService.Scheduler => TimeSpan.FromSeconds(SchedulerNoHeartbeatAfterSeconds),
        _ => TimeSpan.FromSeconds(WorkerNoHeartbeatAfterSeconds)
    };

    /// <summary>
    /// Builds the health report as of <paramref name="asOf"/> (UTC). Every expected service is present, in a fixed
    /// order; one that has never written a heartbeat is reported as <see cref="ServiceHealthStatus.Unhealthy"/>
    /// with the condition <see cref="ServiceHealthCondition.NeverStarted"/>.
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

        // Every expected service, plus any other service that has actually reported: a heartbeat from a service
        // this build did not expect (an upgraded Worker reporting a loop this portal predates, say) is still
        // information an administrator wants, and the order is the enum's so the display can rely on it.
        var services = ExpectedServices
            .Concat(newestByService.Keys)
            .Distinct()
            .OrderBy(service => (int)service)
            .Select(service => Derive(service, newestByService.GetValueOrDefault(service), asOf))
            .ToList();

        return new ServiceHealthReport
        {
            Services = services,
            // ServiceHealthStatus is ordered by severity, so the worst status present is the largest value.
            Overall = services.Max(s => s.Status),
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
                Status = ServiceHealthStatus.Unhealthy,
                Condition = ServiceHealthCondition.NeverStarted,
                Reason = "Never started"
            };
        }

        var age = asOf - heartbeat.LastSeenAt;
        var noHeartbeatAfter = NoHeartbeatAfter(service);
        ServiceHealthCondition condition;
        string reason;

        // Precedence, most to least serious. A dead process's last words about its work are not a wedged task, so
        // no heartbeat is judged before stalled; and a wedged task matters more than a few late heartbeats, so
        // stalled is judged before overdue. The status follows from the condition, never the other way round.
        if (age >= noHeartbeatAfter)
        {
            condition = ServiceHealthCondition.NoHeartbeat;
            reason = $"No heartbeat for {Describe(age)}";
        }
        else if (heartbeat.CurrentWork != null
                 && heartbeat.LastProgressAt is { } lastProgressAt
                 && asOf - lastProgressAt > StalledAfter)
        {
            condition = ServiceHealthCondition.Stalled;
            reason = $"Stalled: no progress on {heartbeat.CurrentWork} for {Describe(asOf - lastProgressAt)}";
        }
        else if (age > HeartbeatOverdueAfter)
        {
            condition = ServiceHealthCondition.HeartbeatOverdue;
            reason = $"Heartbeat overdue: last seen {Describe(age)} ago, expected every {HeartbeatIntervalSeconds} seconds";
        }
        else
        {
            condition = ServiceHealthCondition.Heartbeating;
            reason = $"Heartbeat {Describe(age)} ago";
        }

        return new ServiceHealth
        {
            Service = service,
            Status = StatusOf(condition),
            Condition = condition,
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
    /// The status each condition earns. Kept as the single mapping so the portal, the API and PowerShell cannot
    /// colour one condition two ways.
    /// </summary>
    public static ServiceHealthStatus StatusOf(ServiceHealthCondition condition) => condition switch
    {
        ServiceHealthCondition.Heartbeating => ServiceHealthStatus.Healthy,
        ServiceHealthCondition.HeartbeatOverdue => ServiceHealthStatus.Degraded,
        ServiceHealthCondition.Stalled => ServiceHealthStatus.Degraded,
        ServiceHealthCondition.NoHeartbeat => ServiceHealthStatus.Unhealthy,
        ServiceHealthCondition.NeverStarted => ServiceHealthStatus.Unhealthy,
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "Unknown service health condition")
    };

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
