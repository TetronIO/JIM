// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Operations;

namespace JIM.Web.Pages.Admin.Components;

/// <summary>
/// The service health the Operations page's Service Health strip shows in demo mode (<c>?d=true</c>, Development only):
/// a service in each state, busy and idle, so the strip can be judged collapsed and expanded. Held to that by
/// <c>OperationsDemoDataTests</c>.
/// </summary>
public static class OperationsHealthDemoData
{
    private const long GiB = 1024L * 1024 * 1024;

    public static ServiceHealthReport Build(DateTime now)
    {
        List<ServiceHealth> services =
        [
            Service(JimService.WorkerSync, ServiceHealthStatus.Healthy, ServiceHealthCondition.Heartbeating, now,
                "Heartbeat received 4 seconds ago.", work: "Full Sync: Yellowstone APAC", workStarted: now.AddMinutes(-12), memory: 9.6),
            Service(JimService.WorkerDelivery, ServiceHealthStatus.Degraded, ServiceHealthCondition.MemoryLow, now,
                "Available memory is below 10% of the host's.", memory: 14.1),
            Service(JimService.Scheduler, ServiceHealthStatus.Healthy, ServiceHealthCondition.Heartbeating, now,
                "Heartbeat received 9 seconds ago."),
            Service(JimService.WorkerExchange, ServiceHealthStatus.Unhealthy, ServiceHealthCondition.NoHeartbeat, now,
                "No heartbeat for 6 minutes.", lastSeen: now.AddMinutes(-6)),
        ];

        return new ServiceHealthReport
        {
            Services = services,
            Overall = services.Max(s => s.Status),
            WebVersion = "0.0.0-demo",
            GeneratedAt = now
        };
    }

    private static ServiceHealth Service(JimService service, ServiceHealthStatus status, ServiceHealthCondition condition, DateTime now,
        string reason, string? work = null, DateTime? workStarted = null, double? memory = null, DateTime? lastSeen = null) => new()
        {
            Service = service,
            Status = status,
            Condition = condition,
            Reason = reason,
            InstanceId = Guid.NewGuid().ToString("N")[..12],
            HostName = "jim-demo-host",
            Version = "0.0.0-demo",
            StartedAt = now.AddHours(-26),
            LastSeenAt = lastSeen ?? now.AddSeconds(-4),
            CurrentWork = work,
            CurrentWorkStartedAt = workStarted,
            LastProgressAt = work is null ? null : now.AddSeconds(-2),
            ProcessMemoryBytes = memory is { } gib ? (long)(gib * GiB) : null,
            MemoryTotalBytes = memory is null ? null : 16 * GiB,
            MemoryAvailableBytes = memory is { } used ? (long)((16 - used) * GiB) : null
        };
}
