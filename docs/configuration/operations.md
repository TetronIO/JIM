---
title: Operations
---

# Operations

**Administration > Operations** is where an administrator finds out what JIM is doing, what it has done, what it will do next, and whether the services that do it are alive. It is one page with four tabs and a Service Health strip above them.

## The tabs

- **Queue**<br /> The work in flight and the work waiting behind it: Run Profile executions, schema imports, deletions and the other background tasks, with live progress per row. Running Schedules are grouped under a header drawn as a rail of their steps. See [The Operations queue](../administration/portal-lists.md#the-operations-queue) and [Live progress](activities.md#live-progress).
- **History**<br /> The [Activities](activities.md) record of everything JIM has done, filterable by outcome, type and Schedule, with a side panel for each one.
- **Schedules**<br /> The [Schedules](schedules.md) that run work automatically, each with its last run and how that run ended.
- **Passwords**<br /> The Password Synchronisation queue: every password change on its way to a Connected System, with what the target said about it. See [Watching the queue](../concepts/passwords.md#-watching-the-queue).

## Service Health

JIM does its work in two processes besides the web portal: the **Worker**, which runs Run Profiles and executes every other queued task, and the **Scheduler**, which starts Schedules when they fall due. Until now the only sign either was alive was the container health check, which a person at the portal never sees; a Worker that had stopped looked exactly like a Worker with nothing to do, right up until a Schedule failed to run. Each service now writes a heartbeat to the database every 5 seconds, and the Service Health strip at the top of Operations reads it.

The strip shows three cards:

- **Worker**<br /> The synchronisation loop. When it is running something, the card names it ("Full Import: Corporate Directory") and says how long it has been at it.
- **Scheduler**<br /> The Schedule runner.
- **Live updates**<br /> Whether the portal is receiving real-time change notifications from the database. When it is not, the portal falls back to polling; pages still update, more slowly. This card is about the portal's own connection, not a background service.

Each service card carries a state, a one-sentence reason, the host and version it reports from, and when it was last seen.

### What the states mean

| State | Meaning | When |
|-------|---------|------|
| **Running** | The service reported within its interval. Nothing to do. | Last heartbeat within 15 seconds |
| **Stale** | A few heartbeats missed, but not enough to presume the process is gone. It may be paused under load, or the database may be slow. Worth a glance; not yet an alarm. | Last heartbeat more than 15 seconds ago |
| **No progress** | The service is alive and reports work in flight, but that work has not moved forward for a long time. The process is up; the task it is running may be wedged. Look at it on the Queue tab, and cancel it if it is genuinely stuck. | Current work has not progressed for 10 minutes |
| **Not seen** | The service should be presumed down, or it has never reported at all. Queued and scheduled work will not run until it is back. Check the container, then [the logs](../administration/troubleshooting.md). | No heartbeat for 60 seconds (Worker) or 120 seconds (Scheduler), or never reported |

A service that has never written a heartbeat is shown as **Not seen** with the reason "Never reported" rather than left off the strip, so a deployment that never started its Worker says so.

### The administrator banner

When any service is **Not seen** or has made **No progress**, administrators see a banner above the page content wherever they are in the portal, naming the service and linking to Operations and to the logs. It appears only to administrators, only for those two states, and disappears on its own when the service is seen again. Stale never raises it: a few missed heartbeats are not worth interrupting anyone for.

The Operations tile on the Administration index carries the same signal as a red dot, so the problem is visible from the landing page.

### Version skew

Each card shows the JIM version the service is running, and warns when it differs from the version of the portal you are looking at. After an upgrade that means one container did not restart on the new image; restart it. See [Upgrading](../administration/upgrading.md).

### Timings

Every service writes its heartbeat every 5 seconds. The Worker is presumed down after 60 seconds without one; the Scheduler after 120 seconds, because its loop can legitimately block for a while while it advances a heavy Schedule. Both match the interval the container health checks already use. Work is judged to have made no progress after 10 minutes without a progress report. These are fixed for now; if a deployment needs them tuned, say so.

The strip refreshes every 10 seconds, so a state change appears within roughly one refresh of it happening.

## Watching from outside the portal

The same report is available to monitoring and scripts, and comes from the same rules, so a script and an administrator at the portal always agree.

- **REST API**<br /> `GET /api/v1/system/health` (Administrator role) returns `overall`, `webVersion`, `generatedAt` and one entry per service under `services`, each with `state`, `reason`, `currentWork`, `lastSeenAt`, `hostName`, `version` and the other fields the cards show. The response is marked `Cache-Control: no-store`. See the [interactive API reference](../../api/reference/).
- **PowerShell**<br /> [`Get-JIMServiceHealth`](../powershell/system.md#get-jimservicehealth) returns one object per service, or one summary object with `-Summary` whose `Overall` is the worst state present.

```powershell title="Fail a monitoring check when any service is unhealthy"
$health = Get-JIMServiceHealth -Summary
if ($health.Overall -ne 'Running') {
    $health.Services | Where-Object State -ne 'Running' | Format-List Service, State, Reason
    exit 1
}
```

The unauthenticated `/api/v1/health` endpoints ([Health Monitoring](../administration/deployment.md#health-monitoring)) answer for the web tier only and say nothing about the Worker or the Scheduler. Use them for load balancers and orchestrators; use `system/health` for anything that needs to know whether JIM's work is actually being done.

## See also

- [Activities](activities.md) -- the record of every operation, and how to watch one run
- [Schedules](schedules.md) -- automated, ordered sequences of operations
- [Password Synchronisation](../concepts/passwords.md#-password-synchronisation) -- what the Passwords tab is watching
- [Deployment: Health Monitoring](../administration/deployment.md#health-monitoring) -- the container health checks and web-tier probes
