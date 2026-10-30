---
title: Import Watermarks
description: How an import's Activity describes its watermark, the point a Delta Import reads changes after, for each Connector that keeps one.
type: reference
audience: operator
---

# Import Watermarks

How an import's Activity describes the Connected System's watermark: the point a Delta Import reads changes after.

```jim-diagram
archetype: flow
title: How a watermark carries from one import to the next
desc: 'A Full Import reads everything and sets the watermark: the point the next Delta Import reads changes after. A Delta Import reads the changes after that watermark and records a new one, which the next Delta Import reads after in turn. A Delta Import that fails, is cancelled or withholds detected deletions records no new watermark, so the next Delta Import reads from the same point.'
steps:
- {id: full, label: Full Import, sub: reads everything, next: sets the watermark}
- {id: delta, label: Delta Import, sub: reads changes after it, next: records a new one, note: 'failed, cancelled or deletions withheld: none recorded'}
- {id: next, label: Next Delta Import, sub: reads changes after that}
```

## :material-file-document-outline: On the Activity page

| Run | Shown as | Example | Meaning |
|-----|----------|---------|---------|
| Delta Import | **Read Changes** | After 8, up to 15 (change sequence) | The range of changes the run read. |
| Delta Import | **Read Changes** | No changes after 8 (change sequence) | The run found nothing newer than its watermark. |
| Delta Import | **Read Changes** | From the start, up to 15 (change sequence) | There was no watermark to start from, so the run read everything. |
| Delta Import | **Read Changes** | After 8 (change sequence), with a note | The run recorded no new watermark (it failed, was cancelled, or withheld detected deletions), so the next Delta Import reads from the same point. |
| Full Import | **Next Delta Import Reads Changes After** | 24 Oct 2026 21:21:13 (access log) | A Full Import reads everything; this is the point it set up for the next Delta Import. |

A Connector that keeps a watermark for each Object Type or source shows one line for each, such as "User: after 10, up to 25 (change log sequence)". A time is shown in local time.

**Show Connector state** reveals what was recorded before and after the run, exactly as the Connector keeps it. That is often more than the watermark: the LDAP Connector's also holds what it detected about the directory.

## :material-power-plug-outline: By Connector

| Connector | Watermark | Shown as |
|-----------|-----------|----------|
| LDAP Connector, Active Directory and Samba AD | The directory's highest Update Sequence Number | Update Sequence Number |
| LDAP Connector, OpenLDAP | The time of the latest access log entry | access log |
| LDAP Connector, 389 Directory Server and other directories | The latest change log entry number | change log entry |
| SQL Connector, change log table | The highest sequence read, per Object Type | change log sequence |
| SQL Connector, watermark column | The highest value read, per Object Type and per related table | watermark column |
| SCIM 2.0 Client Connector | The last modified time Delta Imports ask the service provider for changes after | last modified time |
| SCIM 2.0 Service Provider Connector | The last change sequence read from what JIM has kept from the SCIM endpoint | change sequence |

The File Connector keeps no watermark.

## :material-api: REST API and PowerShell

`GET /api/v1/activities/{id}` and `Get-JIMActivity -Id` return the recorded values as `importWatermarkFrom` and `importWatermarkTo`, and the Connector's reading of them as `importReadRange`: a `from` and a `to`, each with a `kind` and its `points`, each point a `number`, a UTC `time` or a `text`, with a `scope` where the Connector keeps several.
