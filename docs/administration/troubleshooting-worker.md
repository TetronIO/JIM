---
title: Troubleshooting Worker Restarts and Memory
description: What it means when a run ends because the Worker restarted or ran short of memory, how to fix it, and how to check.
type: troubleshooting
audience: operator
---

# Troubleshooting Worker Restarts and Memory

The Worker runs every Run Profile, so a run ends early when the Worker itself stops, or when it runs short of memory. Open the run's Activity from **Activity** in the portal, or with `Get-JIMActivity`; its message is the title of one of the entries below. The Worker's card on the [Operations](../configuration/operations.md) page shows how much memory it holds and how full the memory it draws on is, and reports **Memory low** from 85% in use. For errors recorded against individual objects, see [Troubleshooting Synchronisation](troubleshooting-synchronisation.md).

## `Task was abandoned due to worker crash or restart.`

**What it means.** The worker stopped while it was running this Activity: it restarted, crashed, or its server went down. JIM marks the Activity **Failed with error** when the worker starts again, or when the worker has not reported progress for the [Stale task timeout](configuration.md#service-settings) Service Setting (five minutes by default). Whatever the run had already saved stays saved, and nothing is left half-written: the next run carries on from the state JIM's data is in.

When the Worker's last heartbeat showed memory low, the message adds `The Worker most likely ran out of memory`, with the figures it last reported: the operating system stops a process that runs out of memory without a word, so that last reading is the evidence.

**How to fix.** Find out why the worker stopped, from its log (`docker logs jim.worker`, or **Logs** in the portal), then start the Run Profile again. If it most likely ran out of memory, give JIM more memory, or run less at once, first; the Worker's card on the [Operations](../configuration/operations.md) page shows its memory while it runs.

**Check.** The new run's Activity ends **Complete**.

## `The Worker stopped this run because its memory was nearly exhausted`

**What it means.** While the run was going, 92% or more of the memory the Worker can draw on was in use, most of it by the Worker, so the Worker stopped the run rather than be stopped by the operating system part-way through. The Activity is **Failed with error**, and the message gives the figures. What the run had already saved stays saved, as for any failed run. If the run did not stop when asked, the message says the Worker `restarted itself`: it recorded why and restarted, which is what the operating system would otherwise have done without a word. A run that finished while memory was above 85% is **Complete with warning** instead, saying `Memory was low while this ran`.

**How to fix.** Give JIM more memory (the host's, or the Worker container's limit if you set one), or run less at once: a smaller page size on the Run Profile, or fewer Run Profiles in parallel. Then start the Run Profile again.

A run can also fail with `This run ran out of memory and stopped`. In a container with a memory limit, .NET stops its heap at about 75% of the limit, before the Worker's own threshold is reached, and the run fails with that message and the memory figures; the remedy is the same.

**Check.** The new run's Activity ends **Complete**, and the Worker's card on the [Operations](../configuration/operations.md) page no longer reports **Memory low**.

## Still stuck?

Collect these before asking for help:

- **The Activity's ID** and its status and message, from its page or `Get-JIMActivity -Id <id>`.
- **The Worker's memory** from its card on the [Operations](../configuration/operations.md) page, or `Get-JIMServiceHealth`, with the size of the run that stopped: how many objects, and how many members its largest groups have.
- **The Worker's log from the time of the run:** **Logs** in the portal, or `docker logs jim.worker` (`sudo podman logs jim-worker` on Podman).
- **The host's record of any out-of-memory kill:** `dmesg | grep -i "out of memory"` on the host, which names the process the operating system stopped and how much memory it held.
