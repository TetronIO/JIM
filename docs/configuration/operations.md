---
title: Operations
---

# Operations

**Administration > Operations** is where an administrator finds out what JIM is doing, what it has done, what it will do next, and whether the services that do it are alive. It is one page with four tabs and a Service Health strip above them.

## The tabs

- **Queue**<br /> The work in flight and the work waiting behind it: Run Profile executions, schema imports, deletions and the other background tasks, with live progress per row. Running Schedules are grouped under a header drawn as a rail of their steps. See [The Operations queue](../administration/portal-lists.md#the-operations-queue) and [Live progress](activities.md#live-progress).
- **History**<br /> The [Activities](activities.md) record of everything JIM has done, filterable by outcome, type and Schedule, with a side panel for each one.
- **Schedules**<br /> The [Schedules](schedules.md) that run work automatically, each with its last run and how that run ended.
- **Passwords**<br /> The Password Synchronisation queue: every password change on its way to a Connected System, with what the target said about it. A change shows as **Delivering** for the moment the Password Delivery Service is writing it to the target. See [Watching the queue](../concepts/passwords.md#-watching-the-queue).

## Service Health

JIM does its work in two processes besides the web portal: the **Worker**, which runs Run Profiles and every other queued task and also hosts the [Password Delivery Service](../concepts/passwords.md#-the-password-delivery-service), and the **Scheduler**, which starts Schedules when they fall due. Until now the only sign either was alive was the container health check, which a person at the portal never sees; a Worker that had stopped looked exactly like a Worker with nothing to do, right up until a Schedule failed to run. Each service now writes a heartbeat to the database every 5 seconds, and the Service Health strip at the top of Operations reads it.

The strip is a panel headed **Service Health**, with a one-line summary beside the heading ("All services healthy", or "1 service unhealthy, 1 degraded", worst first) and, on the right, whether **Live updates** are connected: whether the portal is receiving real-time change notifications from the database. When they are reconnecting the portal falls back to polling; pages still update, more slowly. That indicator is about the portal's own connection, not a background service.

Below the header is one card per service, all built the same way so the same fact is in the same place on every card:

- **Worker · Sync**<br /> The synchronisation loop. When it is running something, the card names it ("Full Import: Corporate Directory") and says how long it has been at it.
- **Worker · Passwords**<br /> The Password Delivery Service, which delivers the Passwords tab's queue on its own clock. While it is writing to a directory the card says which ("Delivering to Corporate Directory"); its detail line reads what the queue holds ahead of it ("3 due, 1 retrying, next attempt 09:19 UTC"), and is blank when nothing is waiting. It shares a process with the synchronisation loop, so the two cards go **Unhealthy** together when the Worker is down; one alone means that half of the process has stopped while the other is still working, which is why they are reported separately.
- **Scheduler**<br /> The Schedule runner.

Each card carries, top to bottom: the service's name with its status on a coloured pill (green, amber or red; the pill is the only coloured thing on the card); what it is doing now, or **Idle**; and its condition in plain words with the instance's uptime after it ("Heartbeat 3 seconds ago · up 54 s"). The host, version and instance id of the process reporting sit behind the card's details control (the chevron at the right of the heartbeat line); each value is in a fixed-width face with a copy button beside it, so a container id can go straight into a shell. A service that is Unhealthy leads with why ("No heartbeat for 4 minutes"), says what it was running when it went quiet, if anything, and keeps the uptime it had at its last heartbeat, which is how long it ran before it stopped.

### What Healthy, Degraded and Unhealthy mean

A service has a **status**, which is the word on its pill and the thing to alert on, and a **condition**, which is why it has that status. Every condition belongs to exactly one status.

| Status | Condition | Meaning | When |
|--------|-----------|---------|------|
| **Healthy** | Heartbeating | The service reported within its interval. Nothing to do. | Last heartbeat within 15 seconds |
| **Degraded** | Heartbeat overdue | A few heartbeats missed, but not enough to presume the process is gone. It may be paused under load, or the database may be slow. Worth a glance; not yet an alarm. | Last heartbeat more than 15 seconds ago |
| **Degraded** | Stalled | The service is alive and reports work in flight, but that work has not moved forward for a long time. The process is up; the task it is running may be wedged. Look at it on the Queue tab, and cancel it if it is genuinely stuck. | Current work has not progressed for 10 minutes |
| **Unhealthy** | No heartbeat | The service should be presumed down. Queued and scheduled work will not run until it is back. Check the container, then [the logs](../administration/troubleshooting.md). | No heartbeat for 60 seconds (Worker) or 120 seconds (Scheduler) |
| **Unhealthy** | Never started | The service has never reported at all. A deployment that never started its Worker says so here rather than leaving the card off the strip. | No heartbeat has ever been written |

The summary in the panel's header counts services by status, so "1 service degraded" is a glance's worth of information before any card is read.

### The administrator banner

When any service is **Unhealthy**, or is **Degraded** because its work has **stalled**, administrators see a banner above the page content wherever they are in the portal, naming the service and linking to Operations and to the logs. Both Worker services down is named once, as the Worker; one of them alone is named for what it is (the Worker's synchronisation service, or the Worker's password delivery service), because the remedy differs. It appears only to administrators, only for those conditions, and disappears on its own when the service is seen again. An overdue heartbeat never raises it: a few missed heartbeats are not worth interrupting anyone for.

The Operations tile on the Administration index carries the same signal as a red dot, so the problem is visible from the landing page.

### Version skew

Each card carries the JIM version the service is running behind its details control. When it differs from the version of the portal you are looking at, a **differs from portal** chip sits beside the status pill on the card's face, so the skew is seen without opening anything, and again beside the version number in the details, so the number and the warning read together. After an upgrade that means one container did not restart on the new image; restart it. See [Upgrading](../administration/upgrading.md).

### Timings

Every service writes its heartbeat every 5 seconds. Both Worker services are presumed down after 60 seconds without one; the Scheduler after 120 seconds, because its loop can legitimately block for a while while it advances a heavy Schedule. Both match the interval the container health checks already use. Work is judged stalled after 10 minutes without a progress report. These are fixed for now; if a deployment needs them tuned, say so.

The strip refreshes every 10 seconds, so a change of status appears within roughly one refresh of it happening.

## Watching from outside the portal

The same report is available to monitoring and scripts, and comes from the same rules, so a script and an administrator at the portal always agree.

- **REST API**<br /> `GET /api/v1/system/health` (Administrator role) returns `overall`, `webVersion`, `generatedAt` and one entry per service under `services`, each with `status`, `condition`, `reason`, `currentWork`, `lastSeenAt`, `hostName`, `version` and the other fields the cards show. The response is marked `Cache-Control: no-store`. See the [interactive API reference](../../api/reference/).
- **PowerShell**<br /> [`Get-JIMServiceHealth`](../powershell/system.md#get-jimservicehealth) returns one object per service, or one summary object with `-Summary` whose `Overall` is the worst status present.

```powershell title="Fail a monitoring check when any service is unhealthy"
$health = Get-JIMServiceHealth -Summary
if ($health.Overall -ne 'Healthy') {
    $health.Services | Where-Object Status -ne 'Healthy' | Format-List Service, Status, Condition, Reason
    exit 1
}
```

The unauthenticated `/api/v1/health` endpoints ([Health Monitoring](../administration/deployment.md#health-monitoring)) answer for the web tier only and say nothing about the Worker, the Password Delivery Service or the Scheduler. Use them for load balancers and orchestrators; use `system/health` for anything that needs to know whether JIM's work is actually being done.

## See also

- [Activities](activities.md) -- the record of every operation, and how to watch one run
- [Schedules](schedules.md) -- automated, ordered sequences of operations
- [Password Synchronisation](../concepts/passwords.md#-password-synchronisation) -- what the Passwords tab is watching
- [Deployment: Health Monitoring](../administration/deployment.md#health-monitoring) -- the container health checks and web-tier probes
