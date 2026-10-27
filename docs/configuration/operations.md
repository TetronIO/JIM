---
title: Operations
description: "The Operations page: queued, running and completed work, Schedules, Passwords, Generated Values, and the Service Health of the Worker and Scheduler."
type: object
audience: operator
---

# Operations

**Administration > Operations** is where an administrator finds out what JIM is doing, what it has done, what it will do next, and whether the services that do it are alive. It is one page with four tabs and a Service Health strip above them.

## :material-view-list-outline: The tabs

- **Activity**<br /> What the Worker has queued, what it is running and what it has finished, on one page. See [The Activity tab](#the-activity-tab).
- **Schedules**<br /> The [Schedules](schedules.md) that run work automatically, each with its last run and how that run ended.
- **Passwords**<br /> The Password Synchronisation queue: every password change on its way to a Connected System, with what the target said about it. A change shows as **Delivering** for the moment the Password Delivery Service is writing it to the target. An [initial password](../concepts/passwords.md#giving-new-connected-system-objects-their-first-password) on a newly provisioned Connected System Object travels the same queue, labelled with origin **Initial**. See [Watching the queue](../concepts/password-synchronisation.md#watching-the-queue).
- **Generated Values**<br /> Generated values held for a decision, and allowed renames waiting for their export. See [Generated values in target systems](generated-values-in-target-systems.md).

## :material-timeline-clock-outline: The Activity tab

The Activity tab reads in time order, with the latest at the top: **Queued**, then **In Progress**, then **Completed**. The three sections share their columns, so Connected System, Run Profile and Initiated By line up down the page. A section holding work opens with a heading that carries its count and a chevron, which folds the rows away under the heading and brings them back; a section holding nothing is a single line saying so, with no table beneath it.

- **Queued**<br /> Work waiting for the Worker: Run Profile executions and the other background tasks, and the steps of any Schedule Execution still to come, grouped under a header that names the Schedule, draws its steps as a rail, and carries **View execution** and the option to cancel it. The next run is the last row, directly above In Progress; **Runs** gives each row's place ("Next", "2nd", "3rd"), and the tasks of a parallel Schedule step share one. **Est. Start** says when each is expected to start, from the median of the last five successful runs of each Run Profile ahead of it. It is left blank where there is nothing to go on, such as a Run Profile with no successful run yet, and so is every estimate after it. The section shows as many rows as fit the window while In Progress stays in view, and never fewer than three; **Show more** reveals the rest.
- **In Progress**<br /> Everything the Worker is running now, each with its run's steps and the running step's progress (see [Live progress](activity-progress.md#the-runs-steps-on-the-activity-tab)). When a parallel Schedule step is running, its tasks appear together under the Schedule Execution's header. **Est. Finish** comes from the same median, and is never earlier than the running step needs.
- **Completed**<br /> The [Activities](activities.md) record of what the Worker has finished, newest first, with a side panel for each one. A run moves here from In Progress as soon as it ends.

**Filters** opens the filter row. **Connected System**, **Run Profile**, **Schedule** and **Scheduled only** apply to every section; **Result** and **Has child activities** apply to Completed alone, since queued and running work has no result yet. Each filter in use shows as a chip beside the button, whether or not the row is open, and closing a chip puts that one filter back; **Clear** puts them all back. To find work by who started it, use Completed's search box, which searches the target and the initiator. Which sections are folded, the filters and Completed's search are all kept in the page's address, so a filtered view can be bookmarked or shared and survives a refresh.

**Queue a Run Profile** adds a Run Profile execution to the end of the queue: choose the Connected System and the Run Profile, then **Queue**. If nothing else is queued or running, it starts straight away.

## :material-heart-pulse: Service Health

JIM does its work in two processes besides the web portal: the **Worker**, which runs Run Profiles and every other queued task and also hosts the [Password Delivery Service](../concepts/password-synchronisation.md#the-password-delivery-service), and the **Scheduler**, which starts Schedules when they fall due. Each service writes a heartbeat to the database every 5 seconds, and the Service Health strip at the top of Operations reads it, so a Worker that has stopped shows as stopped rather than looking like a Worker with nothing to do. The container health check alone is invisible from the portal.

The strip is a panel headed **Service Health**, with a one-line summary beside the heading ("All services healthy", or "1 service unhealthy, 1 degraded", worst first) and, on the right, whether the panel is **Updated live**: whether the portal is receiving real-time change notifications from the database. While it is reconnecting the portal falls back to polling; pages still update, more slowly. That indicator is about the portal's own connection, not a background service. The panel opens collapsed to that one line, keeping the summary and the live indicator in view; the chevron at the end of the header opens the cards, and the portal remembers the choice.

Below the header is one card per service, all built the same way so the same fact is in the same place on every card:

- **Worker · Sync**<br /> The synchronisation loop. When it is running something, the card names it ("Full Import: Corporate Directory") and says how long it has been at it.
- **Worker · Passwords**<br /> The Password Delivery Service, which delivers the Passwords tab's queue on its own clock. While it is writing to a directory the card says which ("Delivering to Corporate Directory"); its detail line reads what the queue holds ahead of it ("3 due, 1 retrying, next attempt 09:19 UTC"), and is blank when nothing is waiting. It shares a process with the synchronisation loop, so the two cards go **Unhealthy** together when the Worker is down; one alone means that half of the process has stopped while the other is still working, which is why they are reported separately.
- **Scheduler**<br /> The Schedule runner. The card also names the enabled Schedule due next, when it fires and how long until then ("Next: Weekly Full Reconciliation Sun 02:00 (in 3 d)"), or says **No Schedules due**. While the panel is collapsed, the header carries the same in brief ("next Schedule Sun 02:00"). A Scheduler that is Unhealthy shows neither, since it will not start anything until it is back.

Each card carries, top to bottom: the service's name with its status on a coloured pill (green, amber or red; the pill is the only coloured thing on the card); what it is doing now, or **Idle**; and its condition in plain words with the instance's uptime after it ("Heartbeat 3 seconds ago · up 54 s"). The host, version and instance id of the process reporting sit behind the card's details control (the chevron at the right of the heartbeat line); each value is in a fixed-width face with a copy button beside it, so a container id can go straight into a shell. The details also show **Memory**: how much memory the service's process holds, and how full the memory it can draw on is ("41 GB held · 66% of 62 GB in use"), where that memory is its container's limit when one is set, and otherwise the host's. A service that is Unhealthy leads with why ("No heartbeat for 4 minutes"), says what it was running when it went quiet, if anything, and keeps the uptime it had at its last heartbeat, which is how long it ran before it stopped.

### What Healthy, Degraded and Unhealthy mean

A service has a **status**, which is the word on its pill and the thing to alert on, and a **condition**, which is why it has that status. Every condition belongs to exactly one status.

| Status | Condition | Meaning | When |
|--------|-----------|---------|------|
| **Healthy** | Heartbeating | The service reported within its interval. Nothing to do. | Last heartbeat within 15 seconds |
| **Degraded** | Heartbeat overdue | A few heartbeats missed, but not enough to presume the process is gone. It may be paused under load, or the database may be slow. Worth a glance; not yet an alarm. | Last heartbeat more than 15 seconds ago |
| **Degraded** | Memory low | Most of the memory the service can draw on is in use. The Worker stops its running work at 92% rather than be stopped by the operating system part-way through, and a run stopped that way says so on its Activity. Give JIM more memory, or run less at once: smaller Run Profile page sizes, or fewer runs in parallel. | 85% or more of the memory in use at the last heartbeat |
| **Degraded** | Stalled | The service is alive and reports work in flight, but that work has not moved forward for a long time. The process is up; the task it is running may be wedged. Look at it in the Activity tab's In Progress section, and cancel it if it is genuinely stuck. | Current work has not progressed for 10 minutes |
| **Unhealthy** | No heartbeat | The service should be presumed down. Queued and scheduled work will not run until it is back. Check the container, then [the logs](../administration/troubleshooting.md). | No heartbeat for 60 seconds (Worker) or 120 seconds (Scheduler) |
| **Unhealthy** | Never started | The service has never reported at all. A deployment that never started its Worker says so here rather than leaving the card off the strip. | No heartbeat has ever been written |

The summary in the panel's header counts services by status, so "1 service degraded" is a glance's worth of information before any card is read.

### The administrator banner

When any service is **Unhealthy**, or is **Degraded** because its work has **stalled** or its **memory is low**, administrators see a banner above the page content wherever they are in the portal, naming the service and linking to Operations and to the logs. Both Worker services down is named once, as the Worker; one of them alone is named for what it is (the Worker's synchronisation service, or the Worker's password delivery service), because the remedy differs. A low-memory banner gives the figures and the remedy, and names the Worker once for both of its services, since they share one process. It appears only to administrators, only for those conditions, and disappears on its own when the service is seen again or its memory recovers. An overdue heartbeat never raises it: a few missed heartbeats are not worth interrupting anyone for.

The Operations tile on the Administration index carries the same signal as a red dot, so the problem is visible from the landing page.

### Version skew

Each card carries the JIM version the service is running behind its details control. When it differs from the version of the portal you are looking at, a **differs from portal** chip sits beside the status pill on the card's face, so the skew is seen without opening anything, and again beside the version number in the details, so the number and the warning read together. After an upgrade that means one container did not restart on the new image; restart it. See [Upgrading](../administration/upgrading.md).

### Timings

Every service writes its heartbeat every 5 seconds. Both Worker services are presumed down after 60 seconds without one; the Scheduler after 120 seconds, because its loop can legitimately block for a while while it advances a heavy Schedule. Both match the interval the container health checks already use. Work is judged stalled after 10 minutes without a progress report. These are fixed for now; if a deployment needs them tuned, say so.

The strip refreshes every 10 seconds, so a change of status appears within roughly one refresh of it happening.

## :material-monitor-dashboard: Watching from outside the portal

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

## :material-book-open-variant-outline: Related

- [Activities](activities.md): the record of every operation, and how to watch one run
- [Schedules](schedules.md): automated, ordered sequences of operations
- [Password Synchronisation](../concepts/password-synchronisation.md): what the Passwords tab is watching
- [Deployment: Health Monitoring](../administration/deployment.md#health-monitoring): the container health checks and web-tier probes
