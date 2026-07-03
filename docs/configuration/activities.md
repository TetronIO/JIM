---
title: Activities
---

# Activities

An **activity** is a tracked operation in JIM. Every significant action creates an activity record with status, timing, and summary statistics: Run Profile executions, schema imports, data generation, certificate management, and configuration changes all produce activities.

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

Every activity records who or what triggered it: a user (with their Metaverse Object reference), an API key, or the system itself (for example, a schedule). This is the audit trail.

## Summary statistics

Activities for Run Profile executions carry counters relevant to the operation type:

- **Imports**<br /> `Total Added`, `Total Updated`, `Total Deleted`.
- **Synchronisation**<br /> `Total Projected`, `Total Joined`, `Total Attribute Flows`, `Total Disconnected`, `Total Provisioned`.
- **Exports**<br /> `Total Exported`, `Total Deprovisioned`, `Total Pending Exports`.
- **All operations**<br /> `Total Errors`, `Total Activity Time`, `Execution Time`.

The exact field set depends on the operation; the [interactive API reference](../../api/reference/) documents the full schema.

## Execution items

For Run Profile activities, JIM stores a per-object record of what happened (with any error details) for the most recent run. These let you go from a high-level error counter to the specific Connected System Objects that failed and the reason for each failure. Execution items are the right place to look when diagnosing why a particular identity didn't sync as expected.

## Parent and child activities

A schedule execution typically appears as a parent activity with one child activity per step. Use the children listing to walk down a schedule's execution tree from the top-level run into the individual operations it triggered.

## Filtering the Activity list

The Activity page in the admin portal filters a busy list down to what you are reviewing:

- **Category quick-filter**<br /> One click isolates a whole class of activity: **Configuration** (Connected Systems, Synchronisation Rules, Schedules, schema, settings), **Identity data** (Metaverse Objects), **Sync runs** (Run Profile executions), or **System** (housekeeping, resets, data generation). Selecting a category sets the Type filter to the matching target types; you can then fine-tune individual types.
- **Detail filters**<br /> Operation, outcome, type, status, initiator (user, API key, or system), a created date range, and a target/initiator search.
- **Shareable URLs**<br /> The filter state is reflected in the page URL, so a filtered view can be bookmarked or shared; opening the link reproduces the same view. For example, reviewing user-made configuration changes over the last week is one URL an auditor can return to each review cycle.

## Configuration change history

Changes to configuration objects are recorded on the Activity itself. When you create, update, or delete a Synchronisation Rule, Connected System, or Schedule, JIM captures a complete, versioned snapshot of the object's post-change state and carries it on the originating Activity, alongside who made the change, when, and an optional reason. This is how JIM answers "what did this rule look like last week, and who changed it" without a separate audit store.

A few properties of this model:

- **Versioned snapshots, not diffs**<br /> Each change stores the full post-change state and a per-object version number, so any two versions can be compared and the change rendered as a structured diff.
- **Secrets are redacted**<br /> Sensitive values (for example encrypted Connected System settings, or a Schedule step's SQL connection string) are never stored. A changed secret is recorded as changed, using a keyed hash that proves it differs without revealing it; its value is never written to, or shown from, the history.
- **Carried with the Activity**<br /> Because the snapshot lives on the Activity, retrieving the full Activity record also retrieves its change payload; no separate call is needed.
- **Retained on its own schedule**<br /> Configuration change history is kept for the `History.ConfigurationChangeRetentionPeriod` [Service Setting](../administration/configuration.md#service-settings) (default ~10 years), independently of, and typically much longer than, the general history retention period. The routine history cleanup never touches it; only its own retention period removes it.

!!! note "Coverage"
    Configuration change history covers Synchronisation Rules, Connected Systems, and Schedules, and is enabled by default (set the `ChangeTracking.ConfigurationChanges.Enabled` [Service Setting](../powershell/service-settings.md) to disable it; disabling does not delete existing history). Connected System Object and Metaverse Object change history is a separate, related capability.

Retrieve configuration change history with the `Get-JIMConfigurationChangeHistory` [cmdlet](../powershell/history.md) (paged summary, single-version diff, or compare two versions) or the equivalent `change-history` endpoints in the [interactive API reference](../../api/reference/). To record a reason with a change, pass `-ChangeReason` to the write cmdlets, or the optional reason field on the REST write requests.

## Common workflows

**Monitoring a Run Profile execution:**

1. Trigger the Run Profile; capture the returned activity ID
2. Poll the activity to watch its status and progress counters until it reaches a terminal status
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
