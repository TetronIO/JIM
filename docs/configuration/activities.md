---
title: Activities
description: What an Activity records about each operation JIM performs, from a Run Profile execution to a configuration change, and how to find one.
type: object
audience: administrator
validated: v0.16.0
---

# Activities

An **activity** is a tracked operation in JIM. Every significant action creates an activity record with status, timing, and summary statistics: Run Profile executions, schema imports, data generation, certificate management, and configuration changes all produce activities. Example data generation is now its own distinct **Data Generation** activity type, separate from configuration changes to an Example Data Template.

Activities are the primary mechanism for monitoring synchronisation progress and troubleshooting issues. Run Profile activities additionally include detailed per-object execution items, so you can drill from a high-level "5 errors" counter down to the specific objects that failed.

## Lifecycle

Activities move through a small set of statuses:

- **In progress**<br /> Currently executing.
- **Complete**<br /> Finished successfully.
- **Complete with warning**<br /> Finished, but some objects hit a problem JIM recognises (a rejected import object, an export refusal its Connector classified), or a safeguard stepped in. Each object's error is on its execution item.
- **Complete with error**<br /> For a Run Profile execution, at least one object hit an error JIM did not anticipate: a fault in JIM, or an export refusal its Connector could not classify. For a password delivery, the target system refused the password, and the message says why.
- **Failed with error**<br /> Failed due to a critical error before or during execution, or every object the run recorded had an error. An import records nothing for an object that has not changed, so an import fails this way only when every object it read had an error; one bad object among unchanged ones completes with a warning.
- **Cancelled**<br /> Cancelled before completion.

[Troubleshooting Synchronisation](../administration/troubleshooting-synchronisation.md) explains each warning and error.

## Initiated by

Every activity records who or what triggered it: a user (with their Metaverse Object reference), an API key, the system itself, or an unidentified, unauthenticated caller (**Anonymous**) for a failed sign-in, API key or SSO access token attempt. This is the audit trail. An activity a Schedule produced names the Schedule, with **(Schedule)** beside it, and links the Schedule Execution while it is kept; **System** is JIM's own work, such as seeding and housekeeping. Each kind has one icon, the same on every page that shows it.

## Summary statistics

Activities for Run Profile executions carry counters relevant to the operation type, named here as the REST API and `Get-JIMActivity` return them:

- **Imports**<br /> `totalAdded`, `totalUpdated`, `totalDeleted`.
- **Synchronisation**<br /> `totalProjected`, `totalJoined`, `totalAttributeFlows`, `totalDisconnected`, `totalDisconnectedOutOfScope`, `totalOutOfScopeRetainJoin` (out of scope, but kept joined), `totalDriftCorrections` (drift found, and a correcting Pending Export staged), `totalProvisioned`.
- **Exports**<br /> `totalExported`, `totalDeprovisioned`, `totalPendingExports`.
- **Other operations**<br /> `totalCreated`: Metaverse Objects created directly, such as by example data generation, rather than by projection.
- **All operations**<br /> `totalErrors`, `totalActivityTime`, `executionTime`.

The [interactive API reference](../../api/reference/) documents the full schema.

A Delta Import from a directory first checks that it can read where the directory records its changes; [Delta Import change source checks](../connectors/jim-ldap-connector-delta-import.md#delta-import-change-source-checks) says how each outcome ends the run.

### Run Profile Safeguards (export limits)

Every Export activity carries three withheld counters: `exportCreatesWithheld`, `exportUpdatesWithheld` and `exportDeletesWithheld`. Each is `0` when its Run Profile has no limit for that change type, or the pending count did not exceed it; otherwise it is every Pending Export of that type left untouched (still Pending), because a run that would exceed a limit attempts none of that change type at all. See [Run Profiles > Safeguards](run-profiles.md#safeguards) for setting the limits.

When an Export activity withholds anything, it completes as **Complete with warning**, and its warning message carries one sentence per withheld change type, for example:

> Max deletes is 100, but 342 deletes were pending, so none were attempted and all 342 remain pending. Check what staged them, then raise or clear the limit on this Run Profile, or run an Export Run Profile without the limit.

### Run Profile Safeguards (import deletion limits)

Every Full Import activity that ran deletion detection, and every Delta Import activity that completed, carries a `detectedDeletionsWithheld` counter. It is `0` when the Run Profile has no deletion limit, or the run's deletions did not exceed either limit. Otherwise it is how many Connected System Objects the run left exactly as they were instead of marking them as deleted: for a Full Import, the objects deletion detection no longer found; for a Delta Import, the objects the Connected System reported as deleted. It is `null` on every other activity, including a Full Import that imported zero objects and so skipped deletion detection entirely. See [Run Profiles > Safeguards](run-profiles.md#safeguards) for setting the limits.

When a run withholds its deletions, it completes as **Complete with warning**, and its warning message names the limit, how many objects were involved and what share of the Connected System that is. A Delta Import's warning reads, for example:

> The Connected System reported 342 objects (7% of 4,880) as deleted, above this Run Profile's limit of 100; none were marked as deleted. Each Delta Import will report them again until this is resolved. Check what deleted them in the Connected System. If the deletions are intended, raise the limit, or run a Delta Import Run Profile without these limits to let them through.

When a Full Import activity's deletion detection is refused, it completes as **Complete with warning**, and its warning message names the limit that tripped, for example:

> Deletion detection found 4,120 objects (41% of 10,000) no longer in the Connected System, above this Run Profile's limit of 10%; none were marked as deleted. Check the Connected System's scope and the connector's filters, or raise the limit, then run the Full Import again.

A refused detection also means the run does not count as a successful Full Import for the post-clear reconciliation gate; see [Run Profiles > Safeguards > Full Import](run-profiles.md#full-import).

## Execution items

A Run Profile execution records an **execution item** for each object it touched, with what happened to it and any error, so you can go from a run's error counts to the objects behind them. [Execution Items](activity-execution-items.md) covers what each one records, and how its detail page traces what caused a change and what it caused in turn.

## Scheduled Metaverse Object Deletion

When a Metaverse Object's [deletion grace period](metaverse.md) expires, the worker's housekeeping deletes it, queues deletes for any accounts covered by an export Synchronisation Rule whose [Deprovisioning Action](deprovisioning.md) is Delete, and stages membership-removal Pending Exports for any objects (such as groups) that referenced it. Each housekeeping batch that does work is recorded as a **Scheduled Metaverse Object Deletion** activity, with an execution item per deleted Metaverse Object, per staged membership-removal Pending Export, and per failure. Deprovisioning deletes are reported on the deleted object's own item, nested beneath its **Metaverse Object deleted** outcome, as on a synchronisation run. Each deletion's item names the run that scheduled it, so [why it happened](causality.md#why-it-happened) reaches back across the grace period to the change that started it. A pass with nothing to delete records no activity.

Housekeeping runs while the Worker has no other work, at most once a minute, and deletes up to 50 Metaverse Objects a pass, so a large number of expired deletions clears over several passes. A deletion that fails is recorded on the Activity and tried again on the next pass. A Metaverse Object whose deletion fails during a synchronisation run, where no grace period applies, is left for housekeeping to delete instead.

The activity's detail page shows the batch like a Run Profile execution: summary cards (Metaverse Objects Deleted, Recall Pending Exports, Object Types, Errors) above a searchable, filterable table listing each deleted object by name and type, with any per-object errors alongside.

## Parent and child activities

Some Activities group others beneath them. The **System Initialisation** Activity groups the built-in objects JIM creates for itself as it starts, such as Roles and Schedules, one child Activity each; and a password change's Activity groups the Activities recording its delivery to each Connected System. The REST API returns each Activity's `parentActivityId`, lists an Activity's children, and filters the list with `hasChildActivities`.

A Schedule's steps are not grouped this way: each step's Activity carries its [Schedule context](#schedule-context) instead, and the [Schedule Execution view](schedules.md#the-schedule-execution-view) links each step to its Activity.

## Schedule context

An Activity that a [Schedule](schedules.md) produced says so, naming the Schedule and which of its steps this was ("step 3 of 6") and linking back to the Schedule Execution it belonged to. It appears on the Activity's detail page and in the Operations History side panel, and is absent for work nobody scheduled.

The attribution is recorded on the Activity itself rather than looked up through the execution, so it survives the Schedule later being renamed or deleted. Activities are a permanent audit record; deleting a Schedule does not rewrite the history of what it did.

The same attribution reaches automation: `Get-JIMActivity` and the Activities REST endpoints carry `ScheduledByScheduleName`, `ScheduledByScheduleId`, `ScheduleExecutionId` and `ScheduleStepIndex` on each Activity, and leave them empty for work nobody scheduled. See the [Activity cmdlets](../powershell/activities.md#get-jimactivity).

## Target links

On an activity's detail page, the Target links to where that object is managed: a Synchronisation Rule change opens the rule's detail page, a schema import opens the Connected System's Schema tab, and so on. Service Settings have no page of their own, so their Target link opens the Service Settings page with a matching search already applied, taking you straight to that setting instead of the full list.

## Filtering the Activity list

The Activity page in the admin portal filters a busy list down to what you are reviewing:

- **Category quick-filter**<br /> One click isolates a whole class of activity: **Configuration** (Connected Systems, Synchronisation Rules, Schedules, schema, settings), **Identity** (Metaverse Objects), **Synchronisation** (Run Profile executions), **System** (housekeeping, resets, data generation), **Security** ([interactive sign-in, API key and SSO access token authentication events](../administration/security-audit-events.md)), or **Password Synchronisation** ([a password change fanning out to the systems an identity has accounts in](../concepts/passwords.md), and what each of them did with it). Selecting a category sets the Type filter to the matching target types; you can then fine-tune individual types.
- **Detail filters**<br /> Operation, outcome, type, status, initiator (user, API key, or system), a created date range, and a target/initiator search.
- **Schedule filters**<br /> On the Operations > History tab, **Scheduled only** narrows the list to work a [Schedule](schedules.md) produced, and the Schedule filter narrows it to particular ones. Combined with the outcome filter, this answers whether a step has been failing repeatedly or only once: filter to the Schedule, set the outcome to the failure you saw, and read down the dates.
- **Shareable URLs**<br /> The filter state is reflected in the page URL, so a filtered view can be bookmarked or shared; opening the link reproduces the same view. For example, reviewing user-made configuration changes over the last week is one URL an auditor can return to each review cycle.

Automation gets the same filters, not a subset of them. `GET /api/v1/activities` accepts `search`, `targetType`, `initiatorType`, `operation`, `outcome`, `status`, `initiatedById`, `initiatedBy`, `hasChildActivities`, `createdFrom`, `createdTo`, `connectedSystem`, `runProfile`, `scheduledOnly` and `scheduleId`; repeat a query parameter to pass several values, which combine with OR within that filter, while separate filters narrow each other. `Get-JIMActivity` exposes the same set as parameters (see the [Activity cmdlets](../powershell/activities.md#get-jimactivity)). The portal, the REST API and PowerShell all run the same query, so the same filters return the same Activities on every surface.

## Configuration Change History {#configuration-change-history}

Changes to configuration objects are recorded on the Activity itself. When you create, update, or delete a Synchronisation Rule, Connected System, Schedule, Metaverse Object Type, Metaverse Attribute, Trusted Certificate, API Key, Role, Predefined Search (including its criteria groups and criteria), Connector Definition (its capabilities, setting definitions, and files), Example Data Set, or Example Data Template, or update or revert a Service Setting, JIM captures a complete, versioned snapshot of the object's post-change state and carries it on the originating Activity, alongside who made the change, when, and an optional reason. This is how JIM answers "what did this rule look like last week, and who changed it" without a separate audit store.

A few properties of this model:

- **Versioned snapshots, not diffs**<br /> Each change stores the full post-change state and a per-object version number, so any two versions can be compared and the change rendered as a structured diff.
- **Secrets are redacted**<br /> Sensitive values (for example encrypted Connected System settings, encrypted Service Setting values, or a Schedule step's SQL connection string) are never stored. A changed secret is recorded as changed, using a keyed hash that proves it differs without revealing it; its value is never written to, or shown from, the history. Trusted Certificate history likewise stores only metadata, never the certificate material itself. API Key history stores metadata and Role assignments only; the key secret never appears in the history in any form, not even as a hash.
- **Carried with the Activity**<br /> Because the snapshot lives on the Activity, retrieving the full Activity record also retrieves its change payload; no separate call is needed.
- **Retained on its own schedule**<br /> Configuration Change History is kept for the `History.ConfigurationChangeRetentionPeriod` [Service Setting](../administration/configuration.md#service-settings) (default ~10 years), independently of, and typically much longer than, the general history retention period. The routine history cleanup never touches it; only its own retention period removes it.

!!! note "Coverage"
    Configuration Change History covers **every** administrator-mutable configuration type, the ones listed above. It is enabled by default (set the `ChangeTracking.ConfigurationChanges.Enabled` [Service Setting](../administration/configuration.md#service-settings) to disable it; disabling does not delete existing history). Connected System Object and Metaverse Object change history is a separate, related capability.

JIM's own seeding of built-in configuration (built-in Roles, Schedules, and similar) is recorded under a single System Initialisation Activity per startup that applies changes, with each seeded object appearing as a child Activity carrying its version-1 snapshot. A normal restart that changes nothing records nothing.

Retrieve Configuration Change History with the `Get-JIMConfigurationChangeHistory` [cmdlet](../powershell/history.md) (paged summary, single-version diff, or compare two versions) or the equivalent `change-history` endpoints in the [interactive API reference](../../api/reference/). To record a reason with a change, enter it in the optional "Reason for change" prompt that appears when saving from the admin portal, pass `-ChangeReason` to the write cmdlets, or use the optional reason field on the REST write requests. The reason is optional in all three; cancelling the admin portal prompt abandons the save.

When an object is **deleted**, its final captured state is shown on the delete Activity itself, rendered as a removal, together with who deleted it and any reason given. This is where to look for the history of something that no longer exists: the object's own Changes tab and its by-id change-history lookup are gone with it, but opening the delete Activity from the Activities list shows exactly what the object looked like at the moment it was removed. As with every snapshot, secrets are recorded as changed but never stored.

## Live progress

While a Run Profile runs, its Activity reports progress live: in the portal, from `GET /api/v1/activities/{id}/progress`, and with `Get-JIMActivity -Follow`. [Live Progress](activity-progress.md) describes what each shows, and the steps a run reports.

## Common workflows

**Monitoring a Run Profile execution:**

1. Trigger the Run Profile; capture the returned activity ID
2. Follow its live progress (the portal's Activity page, `Get-JIMActivity -Follow`, or the progress endpoint) until it reaches a terminal status
3. If it finished with errors, retrieve the execution items to inspect the per-object failures

**Reviewing recent operations:**

1. List activities, filtered by name, target type, or initiator as needed
2. Retrieve the activities you're interested in for full detail
3. For a Schedule, filter the list to that Schedule (Operations > History), or open the Schedule Execution, which links each step to its Activity

## Manage Activities

- **JIM portal**<br /> Activities area of the admin UI
- **PowerShell**<br /> [Activities cmdlets](../powershell/activities.md) (`Get-JIMActivity`, etc.)
- **REST API**<br /> Activities endpoints in the [interactive API reference](../../api/reference/)

## See also

- [Run Profiles](run-profiles.md): the operations that produce most activities
- [Schedules](schedules.md): the Activities a Schedule's steps produce, and the Schedule Execution view that links them
- [Security Audit Events](../administration/security-audit-events.md): interactive sign-in, API key and SSO access token authentication events, aggregation, and their own retention period
