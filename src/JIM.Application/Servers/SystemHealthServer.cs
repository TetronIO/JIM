// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Localisation;
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
    /// Worker that never came up. Password delivery is hosted in the Worker process (plan #1635, D3) and reports
    /// separately, because a wedged synchronisation loop and a wedged password loop need different responses; Exchange
    /// delivery (#2021) is hosted and reported the same way, for the same reason.
    /// </summary>
    public static readonly JimService[] ExpectedServices =
    [
        JimService.WorkerSync,
        JimService.WorkerDelivery,
        JimService.WorkerExchange,
        JimService.Scheduler
    ];

    /// <summary>
    /// The order services appear in a report: the Worker's services together, then the Scheduler. A service not
    /// named here (one this build predates) follows, in enum order.
    /// </summary>
    private static readonly JimService[] ReportOrder =
    [
        JimService.WorkerSync,
        JimService.WorkerDelivery,
        JimService.WorkerExchange,
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
        // information an administrator wants. The order is fixed (ReportOrder) so the display can rely on it.
        var services = ExpectedServices
            .Concat(newestByService.Keys)
            .Distinct()
            .OrderBy(service => Array.IndexOf(ReportOrder, service) is var position and >= 0 ? position : ReportOrder.Length)
            .ThenBy(service => (int)service)
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
                ReasonDetail = Health(nameof(HealthMessages.NeverStarted))
            };
        }

        var age = asOf - heartbeat.LastSeenAt;
        var noHeartbeatAfter = NoHeartbeatAfter(service);
        ServiceHealthCondition condition;
        JimMessage reason;

        // Precedence, most to least serious. A dead process's last words about its work are not a wedged task, so
        // no heartbeat is judged before stalled; and a wedged task matters more than a few late heartbeats, so
        // stalled is judged before overdue. Memory low comes before overdue too: a process short of memory pauses, which
        // is the likelier reason its heartbeats are late, and the one an administrator can act on. The status follows
        // from the condition, never the other way round.
        var memory = MemoryOf(heartbeat);
        if (age >= noHeartbeatAfter)
        {
            condition = ServiceHealthCondition.NoHeartbeat;
            reason = Health(nameof(HealthMessages.NoHeartbeat), Describe(age));
        }
        else if (heartbeat.CurrentWork != null
                 && heartbeat.LastProgressAt is { } lastProgressAt
                 && asOf - lastProgressAt > StalledAfter)
        {
            condition = ServiceHealthCondition.Stalled;
            reason = Health(nameof(HealthMessages.Stalled), heartbeat.CurrentWork, Describe(asOf - lastProgressAt));
        }
        else if (memory != null && MemoryPressure.IsLow(memory))
        {
            condition = ServiceHealthCondition.MemoryLow;
            // Short enough for a status card three across; the figures are on the heartbeat for whoever wants them.
            reason = Health(nameof(HealthMessages.MemoryLow), MemoryPressure.Percent(memory.UsedFraction!.Value), MemoryPressure.Bytes(memory.TotalBytes!.Value));
        }
        else if (age > HeartbeatOverdueAfter)
        {
            condition = ServiceHealthCondition.HeartbeatOverdue;
            reason = Health(nameof(HealthMessages.HeartbeatOverdue), Describe(age), HeartbeatIntervalSeconds.ToString());
        }
        else
        {
            condition = ServiceHealthCondition.Heartbeating;
            reason = Health(nameof(HealthMessages.Heartbeating), Describe(age));
        }

        return new ServiceHealth
        {
            Service = service,
            Status = StatusOf(condition),
            Condition = condition,
            ReasonDetail = reason,
            InstanceId = heartbeat.InstanceId,
            HostName = heartbeat.HostName,
            Version = heartbeat.Version,
            StartedAt = heartbeat.StartedAt,
            LastSeenAt = heartbeat.LastSeenAt,
            CurrentWork = heartbeat.CurrentWork,
            CurrentWorkStartedAt = heartbeat.CurrentWorkStartedAt,
            LastProgressAt = heartbeat.LastProgressAt,
            Detail = heartbeat.Detail,
            ProcessMemoryBytes = heartbeat.ProcessMemoryBytes,
            MemoryTotalBytes = heartbeat.MemoryTotalBytes,
            MemoryAvailableBytes = heartbeat.MemoryAvailableBytes
        };
    }

    /// <summary>The memory a heartbeat reported, or null when it did not report all three figures.</summary>
    private static MemoryReading? MemoryOf(ServiceHeartbeat heartbeat) =>
        heartbeat is { ProcessMemoryBytes: { } process, MemoryTotalBytes: { } total, MemoryAvailableBytes: { } available }
            ? new MemoryReading { ProcessBytes = process, TotalBytes = total, AvailableBytes = available }
            : null;

    /// <summary>
    /// The status each condition earns. Kept as the single mapping so the portal, the API and PowerShell cannot
    /// colour one condition two ways.
    /// </summary>
    public static ServiceHealthStatus StatusOf(ServiceHealthCondition condition) => condition switch
    {
        ServiceHealthCondition.Heartbeating => ServiceHealthStatus.Healthy,
        ServiceHealthCondition.HeartbeatOverdue => ServiceHealthStatus.Degraded,
        ServiceHealthCondition.Stalled => ServiceHealthStatus.Degraded,
        ServiceHealthCondition.MemoryLow => ServiceHealthStatus.Degraded,
        ServiceHealthCondition.NoHeartbeat => ServiceHealthStatus.Unhealthy,
        ServiceHealthCondition.NeverStarted => ServiceHealthStatus.Unhealthy,
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "Unknown service health condition")
    };

    /// <summary>
    /// An age in its single most useful unit ("16 seconds", "4 minutes", "3 hours", "2 days"), rounded down. One
    /// unit is right for a reason sentence: "1 minute 4 seconds ago" is precision nobody acts on.
    /// </summary>
    private static JimMessage Describe(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        return age switch
        {
            { TotalMinutes: < 1 } => Unit((int)age.TotalSeconds, nameof(HealthMessages.DurationSecondsOne)),
            { TotalHours: < 1 } => Unit((int)age.TotalMinutes, nameof(HealthMessages.DurationMinutesOne)),
            { TotalDays: < 1 } => Unit((int)age.TotalHours, nameof(HealthMessages.DurationHoursOne)),
            _ => Unit((int)age.TotalDays, nameof(HealthMessages.DurationDaysOne))
        };

        // The figure goes in as text, not a number, so a large count of days has no digit grouping, as before.
        static JimMessage Unit(int value, string oneKey) =>
            JimMessage.Counted(ApplicationMessageCatalogues.Health, oneKey, value, value.ToString());
    }

    private static JimMessage Health(string key, params MessageArgument[] arguments) =>
        JimMessage.Create(ApplicationMessageCatalogues.Health, key, arguments);
}
