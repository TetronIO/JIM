---
title: Export Performance
description: "How many batches of changes JIM sends to a Connected System at once, and how to raise or limit it."
type: object
audience: administrator
---

# Export Performance

An export sends its changes to the target system in batches of 100. Where the Connector supports it, JIM sends several batches at once, which shortens a large export (provisioning thousands of new starters, say) without changing what is sent. **Max Export Parallelism** decides how many.

```jim-diagram
archetype: flow
title: How an export's changes reach the target system at once
desc: 'An export splits its Pending Exports into batches of 100. Max Export Parallelism decides how many batches JIM sends at once, each over a connection of its own, kept for the whole run. Within each batch, the LDAP Connector''s Export Concurrency sends several changes at once. The two multiply: two batches at once, each with an Export Concurrency of 16, is up to 32 operations in flight against the target system.'
in: {label: Pending Exports, note: the export's changes}
out: {label: Target system, note: 'the two multiply: 2 × 16 = up to 32'}
steps:
- {id: batch, label: Split into batches, sub: 100 changes each}
- {id: parallel, label: Batches sent at once, sub: up to Max Export Parallelism}
- {id: concurrency, label: Changes sent at once, sub: 'per batch: Export Concurrency'}
```

## :material-tune-variant: Setting it

Max Export Parallelism is under **Export Performance** on a Connected System's Settings tab, shown for Connectors that can export in parallel: the [LDAP Connector](../connectors/jim-ldap-connector.md) and the [SCIM 2.0 Client Connector](../connectors/jim-scim-connector.md). Script it with `Set-JIMConnectedSystem -MaxExportParallelism`, or `maxExportParallelism` over the REST API.

| Value | What JIM does |
|-------|---------------|
| Empty (default) | Uses the Connector's recommendation. When its Export Concurrency is 8 or more, the LDAP Connector recommends as many batches as keep 32 operations in flight, from 2 to 4: 4 at an Export Concurrency of 8, 2 at 16. Otherwise, and for the SCIM 2.0 Client Connector, JIM sends one batch at a time. |
| 1 | Sends one batch at a time. Choose this for a target system that must not be loaded any harder. |
| 2 or more | Sends up to that many batches at once. |

A directory with headroom can be given more than the recommendation. In a 500,000-user test against OpenLDAP, setting Max Export Parallelism to 4 and leaving Export Concurrency at 16 (64 operations in flight) exported a further 8% faster, at twice the load on the directory, so measure the directory's own load before choosing it.

JIM opens a connection to the target system for each batch it sends at once, and keeps it for the whole run.

## :material-swap-horizontal: How it combines with Export Concurrency

Within each batch, the LDAP Connector also sends several changes at once: its [Export Concurrency](../connectors/jim-ldap-connector.md#export-settings). Left at its default of 4, it is raised when JIM imports the directory's schema: to 16 for Active Directory and 389 Directory Server, and to 8 for OpenLDAP. Samba AD and other directories stay at 4. The two multiply. Two batches at once, each with an Export Concurrency of 16, is up to 32 operations in flight against the directory, and so is four batches of 8, so raise one at a time and watch the directory's load.

At the same load, more batches usually finish sooner, because JIM records one batch's results while others are being sent: against OpenLDAP, four batches of 8 exported a large initial load about a fifth faster than two batches of 16. JIM only raises Export Concurrency from the default, so a value already set is kept on a later schema import: an OpenLDAP Connected System at 16 sends two batches at once until you set its Export Concurrency to 8.

## :material-alert-octagon-outline: When a batch fails

A batch that fails as a whole (the target system stops answering, say) is recorded on the run's Activity as a failed attempt for each of its changes. Nothing is discarded: each change stays a [Pending Export](../concepts/synchronisation-pipeline.md#what-happens-during-export) for the next run. Batches already being sent alongside it finish and are recorded as normal, and JIM starts no new batch of ready-to-send changes in that run.
