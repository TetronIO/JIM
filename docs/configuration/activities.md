---
title: Activities
---

# Activities

An **activity** is a tracked operation in JIM. Every significant action creates an activity record with status, timing, and summary statistics: Run Profile executions, schema imports, data generation, certificate management, and configuration changes all produce activities. Example data generation is now its own distinct **Data Generation** activity type, separate from configuration changes to an Example Data Template.

Activities are the primary mechanism for monitoring synchronisation progress and troubleshooting issues. Run Profile activities additionally include detailed per-object execution items, so you can drill from a high-level "5 errors" counter down to the specific objects that failed.

## Lifecycle

Activities move through a small set of statuses:

- **In progress**<br /> Currently executing.
- **Complete**<br /> Finished successfully.
- **Complete with warning**<br /> Finished with non-fatal warnings.
- **Complete with error**<br /> Finished, but some individual objects had errors.
- **Failed with error**<br /> Failed due to a critical error before or during execution.
- **Cancelled**<br /> Cancelled before completion.

Most monitoring code only cares about whether the activity has reached a terminal status, and whether errors were recorded along the way.

## Initiated by

Every activity records who or what triggered it: a user (with their Metaverse Object reference), an API key, the system itself (for example, a schedule), or an unidentified, unauthenticated caller (**Anonymous**) for a failed sign-in or API key attempt. This is the audit trail.

## Summary statistics

Activities for Run Profile executions carry counters relevant to the operation type:

- **Imports**<br /> `Total Added`, `Total Updated`, `Total Deleted`.
- **Synchronisation**<br /> `Total Projected`, `Total Joined`, `Total Attribute Flows`, `Total Disconnected`, `Total Provisioned`.
- **Exports**<br /> `Total Exported`, `Total Deprovisioned`, `Total Pending Exports`.
- **All operations**<br /> `Total Errors`, `Total Activity Time`, `Execution Time`.

The exact field set depends on the operation; the [interactive API reference](../../api/reference/) documents the full schema.

## Execution items

For Run Profile activities, JIM stores a per-object record of what happened (with any error details) for the most recent run. These let you go from a high-level error counter to the specific Connected System Objects that failed and the reason for each failure. Execution items are the right place to look when diagnosing why a particular identity didn't sync as expected.

An execution item's detail page opens with a causality panel that answers "what happened to this object, and what did it cause?" without any digging. A summary band leads with a single plain-English sentence describing the run's effect on the object, with every entity mentioned (Connected Systems, Identities, Synchronisation Rules) highlighted as a clickable token, and a strip of colour-coded outcome pills beneath it summarising the outcomes at a glance (for example "Identity created", "11 attributes flowed", "Export queued").

Below the summary, the same chain of outcomes can be explored in three switchable views; the view you choose is remembered for your next visit:

- **Flow**<br /> A left-to-right pipeline showing what came in, what JIM did, and what it caused, with downstream effects grouped per Connected System. The best starting point for understanding a typical import or synchronisation.
- **Timeline**<br /> A vertical narrative read from top to bottom, with attribute change detail expanding inline beneath each event. Useful when you want the whole story, every attribute included, in one scroll.
- **Graph**<br /> A node-and-edge rendering of the underlying outcome tree. Useful for seeing the branching structure of a complex causal chain in one picture.

Every event is named in plain language first (for example "Identity created") with the technical term alongside ("MVO Projected"); a technical-names toggle swaps the emphasis for practitioners who prefer the underlying vocabulary. Attribute change detail is built for scanning: each change carries a Set, Add or Remove operation badge and a monospace value (with the previous value struck through where one existed), and a search box plus count-annotated filter chips narrow large change sets quickly.

Everything mentioned links to its detail page: Connected Systems, Connected System Objects, Identities, Synchronisation Rules and Pending Exports are all one click away. A **Pending Export** outcome links to the individual Pending Export it created, not to the target Connected System's whole queue, which matters on a deprovisioning cascade where that queue can hold thousands of rows. Destructive outcomes keep their story even after the object is gone: when a disconnection triggers a Metaverse Object Deletion Rule, the resulting **MVO Deleted** or **MVO Deletion Scheduled** event shows the deleted Identity's display name (captured before deletion), why the Deletion Rule fired (for example "last connector disconnected"), the grace period for scheduled deletions, and a link to the deletion record browser, so the full story of a deleted Identity survives the deletion itself. **View deletion record** opens that object's own change history directly, rather than the unfiltered browser, and a **CSO Deleted** event offers the same link for the record that was removed.

The deletion rule context shown on the detail page is recorded **when the decision was made**, not looked up from today's configuration: the rule, the trigger mode, the selected authoritative sources, the grace period, the system whose disconnection triggered the evaluation, which sources were still connected at that moment, and the date a scheduled deletion becomes due. It therefore stays accurate after an administrator edits the object type's deletion rules, and it is recorded even when a listed source disconnected but the trigger mode decided not to delete, so "why was this NOT deleted" is answerable too. For a grace period deletion, the context carried on the final housekeeping deletion record is the one captured when the deletion was scheduled. Records written before this capture existed fall back to showing the object type's current configuration, labelled as such.

How much detail the panel can show depends on the `ChangeTracking.SyncOutcomes.Level` [Service Setting](../administration/configuration.md#service-settings) (None, Standard or Detailed): lower levels record fewer outcomes and less attribute detail for each execution item.

Deleting an Identity also has downstream consequences, and those are reported too. Each account queued for removal by an export Synchronisation Rule's [Deprovisioning Action](synchronisation-rules.md#deprovisioning-action) appears as a **Deprovision queued** outcome nested beneath the **MVO Deleted** outcome that caused it, naming the Connected System the account is being removed from, so the tree shows the deletion and everything it set in motion in one place. Membership removals staged on groups that referenced the deleted Identity are reported separately, as their own **Pending Export** execution items (the referencing group is a different object, with no execution item of its own on the run); each is named after the group concerned and records its Connected System Object's external ID and object type, so it stays readable long after the objects themselves are gone. Both are counted in the Activity's Pending Exports total.

A queued deprovision is distinguished from an ordinary **Export queued** because the two look alike in the data but mean opposite things. A staged delete carries the target's identifying attribute (its distinguished name, for a directory) so the connector can still find the entry after the Connected System Object has been disconnected from its Identity; without the distinction that payload was reported as an attribute change, and an account being removed read as an account having one attribute set. Expanding a **Deprovision queued** outcome therefore shows those values under **Target identified by** rather than as a change count: they are how the record will be located, not something being written to it.

!!! note "Applies to new runs"
    Outcomes recorded before this distinction existed remain as they were written, so a deprovisioning cascade on an older Activity still reads **Export queued**. Runs from this version onwards use the new outcome.

## Metaverse Object Housekeeping

When a Metaverse Object's [deletion grace period](metaverse.md) expires, a background housekeeping process on the worker deletes it, queues deletes for any accounts covered by an export Synchronisation Rule whose [Deprovisioning Action](synchronisation-rules.md#deprovisioning-action) is Delete, and stages membership-removal Pending Exports for any objects (such as groups) that referenced it. Each housekeeping batch that actually does work is recorded as a **Metaverse Object Housekeeping** activity, with an execution item per deleted Metaverse Object, per staged membership-removal Pending Export, and per per-object failure, so grace-period deletions are auditable from the Activities page rather than only visible in service logs. Deprovisioning deletes are reported on the deleted object's own item, nested beneath its **MVO Deleted** outcome, exactly as on a synchronisation run. A quiet housekeeping pass with nothing to delete records no activity.

The activity's detail page shows the batch like a Run Profile execution: summary cards (Metaverse Objects Deleted, Recall Pending Exports, Object Types, Errors) above a searchable, filterable table listing each deleted object by name and type, with any per-object errors alongside.

## Parent and child activities

A Schedule Execution typically appears as a parent activity with one child activity per step. Use the children listing to walk down a schedule's execution tree from the top-level run into the individual operations it triggered.

## Schedule context

An Activity that a [Schedule](schedules.md) produced says so, naming the Schedule and which of its steps this was ("step 3 of 6") and linking back to the Schedule Execution it belonged to. It appears on the Activity's detail page and in the Operations History side panel, and is absent for work nobody scheduled.

The attribution is recorded on the Activity itself rather than looked up through the execution, so it survives the Schedule later being renamed or deleted. Activities are a permanent audit record; deleting a Schedule does not rewrite the history of what it did.

The same attribution reaches automation: `Get-JIMActivity` and the Activities REST endpoints carry `ScheduledByScheduleName`, `ScheduledByScheduleId`, `ScheduleExecutionId` and `ScheduleStepIndex` on each Activity, and leave them empty for work nobody scheduled. See the [Activity cmdlets](../powershell/activities.md#get-jimactivity).

## Target links

On an activity's detail page, the Target links to where that object is managed: a Synchronisation Rule change opens the rule's detail page, a schema import opens the Connected System's Schema tab, and so on. Service Settings have no page of their own, so their Target link opens the Service Settings page with a matching search already applied, taking you straight to that setting instead of the full list.

## Filtering the Activity list

The Activity page in the admin portal filters a busy list down to what you are reviewing:

- **Category quick-filter**<br /> One click isolates a whole class of activity: **Configuration** (Connected Systems, Synchronisation Rules, Schedules, schema, settings), **Identity** (Metaverse Objects), **Synchronisation** (Run Profile executions), **System** (housekeeping, resets, data generation), or **Security** ([interactive sign-in and API key authentication events](../administration/security-audit-events.md)). Selecting a category sets the Type filter to the matching target types; you can then fine-tune individual types.
- **Detail filters**<br /> Operation, outcome, type, status, initiator (user, API key, or system), a created date range, and a target/initiator search.
- **Schedule filters**<br /> On the Operations > History tab, **Scheduled only** narrows the list to work a [Schedule](schedules.md) produced, and the Schedule filter narrows it to particular ones. Combined with the outcome filter, this answers whether a step has been failing repeatedly or only once: filter to the Schedule, set the outcome to the failure you saw, and read down the dates.
- **Shareable URLs**<br /> The filter state is reflected in the page URL, so a filtered view can be bookmarked or shared; opening the link reproduces the same view. For example, reviewing user-made configuration changes over the last week is one URL an auditor can return to each review cycle.

## Configuration change history

Changes to configuration objects are recorded on the Activity itself. When you create, update, or delete a Synchronisation Rule, Connected System, Schedule, Metaverse Object Type, Metaverse Attribute, Trusted Certificate, API Key, Role, Predefined Search (including its criteria groups and criteria), Connector Definition (its capabilities, setting definitions, and files), Example Data Set, or Example Data Template, or update or revert a Service Setting, JIM captures a complete, versioned snapshot of the object's post-change state and carries it on the originating Activity, alongside who made the change, when, and an optional reason. This is how JIM answers "what did this rule look like last week, and who changed it" without a separate audit store.

A few properties of this model:

- **Versioned snapshots, not diffs**<br /> Each change stores the full post-change state and a per-object version number, so any two versions can be compared and the change rendered as a structured diff.
- **Secrets are redacted**<br /> Sensitive values (for example encrypted Connected System settings, encrypted Service Setting values, or a Schedule step's SQL connection string) are never stored. A changed secret is recorded as changed, using a keyed hash that proves it differs without revealing it; its value is never written to, or shown from, the history. Trusted Certificate history likewise stores only metadata, never the certificate material itself. API Key history stores metadata and Role assignments only; the key secret never appears in the history in any form, not even as a hash.
- **Carried with the Activity**<br /> Because the snapshot lives on the Activity, retrieving the full Activity record also retrieves its change payload; no separate call is needed.
- **Retained on its own schedule**<br /> Configuration change history is kept for the `History.ConfigurationChangeRetentionPeriod` [Service Setting](../administration/configuration.md#service-settings) (default ~10 years), independently of, and typically much longer than, the general history retention period. The routine history cleanup never touches it; only its own retention period removes it.

!!! note "Coverage"
    Configuration change history now covers **every** administrator-mutable configuration type: Synchronisation Rules, Connected Systems, Schedules (including their steps), Service Settings, Metaverse Object Types, Metaverse Attributes, Trusted Certificates, API Keys, Roles (definitions and assignments), Predefined Searches (including their criteria groups and criteria), Connector Definitions, Example Data Sets, and Example Data Templates. It is enabled by default (set the `ChangeTracking.ConfigurationChanges.Enabled` [Service Setting](../powershell/service-settings.md) to disable it; disabling does not delete existing history). Connected System Object and Metaverse Object change history is a separate, related capability.

JIM's own seeding of built-in configuration (built-in Roles, Schedules, and similar) is recorded under a single System Initialisation Activity per startup that applies changes, with each seeded object appearing as a child Activity carrying its version-1 snapshot. A normal restart that changes nothing records nothing.

Retrieve configuration change history with the `Get-JIMConfigurationChangeHistory` [cmdlet](../powershell/history.md) (paged summary, single-version diff, or compare two versions) or the equivalent `change-history` endpoints in the [interactive API reference](../../api/reference/). To record a reason with a change, enter it in the optional "Reason for change" prompt that appears when saving from the admin portal, pass `-ChangeReason` to the write cmdlets, or use the optional reason field on the REST write requests. The reason is optional in all three; cancelling the admin portal prompt abandons the save.

When an object is **deleted**, its final captured state is shown on the delete Activity itself, rendered as a removal, together with who deleted it and any reason given. This is where to look for the history of something that no longer exists: the object's own Changes tab and its by-id change-history lookup are gone with it, but opening the delete Activity from the Activities list shows exactly what the object looked like at the moment it was removed. As with every snapshot, secrets are recorded as changed but never stored.

## Live progress

While a Run Profile executes, its progress is available in real time on every surface:

- **JIM portal**<br /> The Activity detail page updates as the run progresses (pushed over the real-time notification channel, with polling as a fallback): the current phase, a progress bar with the percentage beside it, a labelled readout of objects processed, throughput and time remaining beneath it, and live operation counts (for example CSOs added, updated and deleted). Each figure is stated once; the message under the steps narrates what is happening rather than repeating the numbers. The readout names the step it measures ("Step 2 of 3: Processing Connected System Objects"), matching what `Get-JIMActivity -Follow` prints.
- **REST API**<br /> `GET /api/v1/activities/{id}/progress` returns a lightweight progress snapshot: status, phase message, object counts, percentage complete, throughput, estimated seconds remaining, and a live operation-type breakdown. It is designed for frequent polling and is much cheaper to serve than the full Activity detail endpoint; stop polling once the status reaches a terminal value.
- **PowerShell**<br /> [`Get-JIMActivity -Follow`](../powershell/activities.md) follows an in-progress Activity's live progress until it completes, and [`Start-JIMRunProfile -Wait`](../powershell/run-profiles.md) displays the same live progress while blocking until completion.

**Every live figure describes the step running now, not the whole run.** The objects processed, the percentage, the throughput and the time remaining are all reset by each step that counts its own work, which is why the readout names its step. There is no whole-run estimate: steps differ too much in cost, and several cannot know their totals in advance, so any run-level figure would be invented rather than measured. How far through the run you are is answered by the steps themselves.

Throughput and the estimated time remaining are derived from recent progress samples, so they reflect the current phase of the run rather than a whole-run average; they appear once enough samples exist and adapt as the run moves between phases. Where a step cannot know its total in advance, the progress bar runs indeterminate and the readout reports how many objects have been processed so far, without a percentage or a time remaining. Whether an import knows depends on the Connected System: a file's records are counted before they are read, so a file import shows a percentage throughout, while a directory cannot be asked how many objects a search will return without running it twice, so those imports show the count and the rate without a percentage rather than a figure JIM guessed. When the counter reaches its total but the step is still finishing its work, the time remaining reads "Finishing up" rather than counting down to a moment that has already passed.

### The steps of a run

A Run Profile execution is a journey through several steps, and the Activity shows all of them: what is done, what is running now, and what is still to come. An import, for example, connects to the Connected System, imports objects (fetching them, then matching them against the Connected System Objects already held), processes deletions, resolves references, saves changes, reconciles Pending Exports and records its results. An export prepares, writes the changes, makes a second pass over anything it had to defer, resolves change history references, selects any containers it created, and delivers initial passwords to the accounts it provisioned. A Synchronisation prepares, processes Connected System Objects, resolves cross-page references and reviews export scope.

- **Completed steps** carry how long they took, so a run that took four hours can be read afterwards to see where the four hours went.
- **The step running now** is highlighted. The steps inside it and the message describing what it is doing appear beneath, under the line naming the step and above the figures measuring it, so each reads after the thing it belongs to.
- **Steps still to come** are greyed, so "how much is left" is answerable at a glance.
- **A green ring with a dash** marks a step that was not needed on this run: a Delta Import performs no deletion detection, for example. It is green because the run is past it; it is a ring rather than a filled tick because it did no work of its own. Hovering the step says so. This is normal, not a problem. Work a run could never do at all (a file-based import opens no connection) is not shown as a step.
- **A failed run** marks the step it failed in, which is where to look first.

The progress bar beneath the steps counts objects within the step currently running, and the leg of the rail leaving that step fills to match, so the same progress reads at a glance and in exact numbers. Several steps count their own work, so the bar restarts as the run moves between them: that is the step advancing, not progress being lost.

### Connector steps and messages

Some of a run's wall-clock time is spent inside the Connector, on work JIM cannot count objects for: reading an export file before merging changes into it, writing the merged file back out, querying a directory's root DSE, or fetching a page of objects from a container. Connectors declare these as their own steps, shown inside the step that called them, and narrate what they are doing as they go:

- **File connector**<br /> "Loading existing export file", "Merging changes into file" and "Writing the output file" during an export; "Reading the file" during an import, with the records counted before they are read so the bar has a total from the start.
- **LDAP connector**<br /> "Querying the directory" and "Fetching objects" during a Full Import, with messages naming the container and page ("Fetching User objects from Employees (page 3)..."); a Delta Import adds "Querying changes" and "Querying deleted objects", with the watermark in the message ("Querying changes since USN 1,204,933...").

A Connector can report how many objects it has read while it is still working, and both built-in Connectors do, so the counts move during these steps rather than standing still until the Connector returns. A Connector that reports nothing leaves the steps and messages to do that job on their own: something that keeps changing is how you tell a healthy long-running phase from a stuck one. A Connector's steps finish the moment its call returns, so nothing is shown as running once JIM has taken over.

The steps are also available to automation: the Activity progress endpoint reports the current step and its position in the run, and `Start-JIMRunProfile -Wait` and `Get-JIMActivity -Follow` display it as "Step 3 of 7: Saving changes".

## Common workflows

**Monitoring a Run Profile execution:**

1. Trigger the Run Profile; capture the returned activity ID
2. Follow its live progress (the portal's Activity page, `Get-JIMActivity -Follow`, or the progress endpoint) until it reaches a terminal status
3. If it finished with errors, retrieve the execution items to inspect the per-object failures

**Reviewing recent operations:**

1. List activities, filtered by name, target type, or initiator as needed
2. Retrieve the activities you're interested in for full detail
3. For schedule executions specifically, walk the child activities to see what each step did

## Manage Activities

- **JIM portal**<br /> Activities area of the admin UI
- **PowerShell**<br /> [Activities cmdlets](../powershell/activities.md) (`Get-JIMActivity`, etc.)
- **REST API**<br /> Activities endpoints in the [interactive API reference](../../api/reference/)

## See also

- [Run Profiles](run-profiles.md) -- the operations that produce most activities
- [Schedules](schedules.md) -- the parent-and-child activity model originates here
- [Security Audit Events](../administration/security-audit-events.md) -- interactive sign-in and API key authentication events, aggregation, and their own retention period
