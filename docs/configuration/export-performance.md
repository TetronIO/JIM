---
title: Export Performance
description: "How many batches of changes JIM sends to a Connected System at once, and how to raise or limit it."
type: object
audience: administrator
---

# Export Performance

An export sends its changes to the target system in batches of 100. Where the Connector supports it, JIM sends several batches at once, which shortens a large export (provisioning thousands of new starters, say) without changing what is sent. **Max Export Parallelism** decides how many.

## Setting it

Max Export Parallelism is under **Export Performance** on a Connected System's Settings tab, shown for Connectors that can export in parallel: the [LDAP Connector](../connectors/jim-ldap-connector.md) and the [SCIM 2.0 Client Connector](../connectors/jim-scim-connector.md). Script it with `Set-JIMConnectedSystem -MaxExportParallelism`, or `maxExportParallelism` over the REST API.

| Value | What JIM does |
|-------|---------------|
| Empty (default) | Uses the Connector's recommendation. The LDAP Connector recommends 2 when its Export Concurrency is 8 or more; otherwise, and for the SCIM 2.0 Client Connector, JIM sends one batch at a time. |
| 1 | Sends one batch at a time. Choose this for a target system that must not be loaded any harder. |
| 2 or more | Sends up to that many batches at once. |

JIM opens a connection to the target system for each batch it sends at once, and keeps it for the whole run.

## How it combines with Export Concurrency

Within each batch, the LDAP Connector also sends several changes at once: its [Export Concurrency](../connectors/jim-ldap-connector.md#export-settings). The two multiply. Two batches at once, each with an Export Concurrency of 16, is up to 32 operations in flight against the directory, so raise one at a time and watch the directory's load.

Past two or three batches, JIM's own work of recording each batch's results is usually what limits an export, rather than the target system, so a higher value gains less.

## When a batch fails

A batch that fails as a whole (the target system stops answering, say) is recorded on the run's Activity as a failed attempt for each of its changes. Nothing is discarded: each change stays a [Pending Export](../concepts/synchronisation-pipeline.md#what-happens-during-export) for the next run. Batches already being sent alongside it finish and are recorded as normal, and JIM starts no new batch of ready-to-send changes in that run.
