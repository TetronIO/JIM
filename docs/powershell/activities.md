---
title: Activities
---

# Activities

Activity cmdlets retrieve and inspect the execution history of operations within JIM. Activities track all operations: synchronisation runs, data generation, certificate management, and other administrative actions. Example data generation is now its own distinct **Data Generation** activity type, separate from configuration changes to an Example Data Template. Use these cmdlets to review activity logs, retrieve execution statistics, find out why an object in a run failed, and inspect child activities spawned by parent operations.

---

## Get-JIMActivity

Retrieves activity history from JIM. Activities are created automatically whenever an operation runs, providing a full audit trail of synchronisation runs, data generation tasks, certificate management operations, and other administrative actions.

### Syntax

```powershell
# List recent activities (default), optionally narrowed by any combination of filters
Get-JIMActivity [-Search <string>] [-Page <int>] [-PageSize <int>]
                [-Operation <string[]>] [-Outcome <string[]>] [-Status <string[]>]
                [-InitiatedById <guid>] [-InitiatedBy <string>] [-HasChildActivities <bool>]
                [-CreatedFrom <datetime>] [-CreatedTo <datetime>]
                [-ConnectedSystem <string[]>] [-RunProfile <string[]>]
                [-ScheduledOnly <bool>] [-ScheduleId <guid[]>]

# Get a specific activity by ID
Get-JIMActivity -Id <guid>

# Get execution items for a Run Profile activity
Get-JIMActivity -Id <guid> -ExecutionItems

# Follow an in-progress activity's live progress until it completes
Get-JIMActivity -Id <guid> -Follow [-IntervalSeconds <int>] [-MaxPolls <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (ById, ExecutionItems, Follow sets) | | ID of the activity to retrieve. Alias: `ActivityId`. Accepts pipeline input by property name. |
| `Search` | `string` | No (List set) | | Filters activities by target name or type. For example, searching for "Active Directory" returns activities related to that Connected System. |
| `Page` | `int` | No (List set) | `1` | Page number for paginated results. |
| `PageSize` | `int` | No (List set) | `20` | Number of activities per page. |
| `Operation` | `string[]` | No (List set) | | Filters by the operation the Activity performed: `Create`, `Read`, `Update`, `Delete`, `Clear`, `Execute`, `ImportHierarchy`, `ImportSchema`, `Revert`, `Reset`, `Authenticate`, `Preview`. Accepts several values. |
| `Outcome` | `string[]` | No (List set) | | Filters to Activities that recorded at least one of the given outcomes: `Added`, `Updated`, `Deleted`, `Projected`, `Joined`, `AttributeFlows`, `Disconnected`, `DriftCorrections`, `Provisioned`, `Exported`, `Deprovisioned`, `Created`, `PendingExports`, `Errors`. Accepts several values. |
| `Status` | `string[]` | No (List set) | | Filters by status: `InProgress`, `Complete`, `CompleteWithWarning`, `CompleteWithError`, `FailedWithError`, `Cancelled`. Accepts several values. |
| `InitiatedById` | `guid` | No (List set) | | Filters to the exact principal (Metaverse Object or API key) that initiated the Activity. |
| `InitiatedBy` | `string` | No (List set) | | Filters by part of the initiator's recorded name, case-insensitively. Use `InitiatedById` to match an exact principal instead. |
| `HasChildActivities` | `bool` | No (List set) | | `$true` returns only Activities that have child Activities, `$false` only those that have none. Omit to return both. |
| `CreatedFrom` | `datetime` | No (List set) | | Only Activities created at or after this time. Converted to UTC before being sent. |
| `CreatedTo` | `datetime` | No (List set) | | Only Activities created at or before this time. Converted to UTC before being sent. |
| `ConnectedSystem` | `string[]` | No (List set) | | Filters by Connected System name (the Activity's target context). Exact match; accepts several values. |
| `RunProfile` | `string[]` | No (List set) | | Filters by Run Profile name (the Activity's target name). Exact match; accepts several values. |
| `ScheduledOnly` | `bool` | No (List set) | | `$true` returns only work a [Schedule](../configuration/schedules.md) produced, `$false` only work nobody scheduled. Omit to return both. |
| `ScheduleId` | `guid[]` | No (List set) | | Filters to the Activities particular Schedules produced. Accepts several values. |
| `ExecutionItems` | `switch` | Yes (ExecutionItems set) | | Retrieves the Run Profile execution items (RPEIs) associated with the activity, providing detailed per-object processing results. |
| `Follow` | `switch` | Yes (Follow set) | | Follows the activity's live progress (like `tail -f`): renders a progress bar with the current phase, object counts, throughput and estimated time remaining until the activity completes. Press Ctrl+C to stop early. |
| `IntervalSeconds` | `int` | No (Follow set) | `2` | Polling interval in seconds when following. Range 1-300. |
| `MaxPolls` | `int` | No (Follow set) | | Maximum number of progress polls before following stops, whether or not the activity has completed. Useful for scripts that must not block indefinitely. |

### Output

When using the **List** or **ById** parameter sets, returns one or more `PSCustomObject` instances representing activities, each containing properties such as `Id`, `Created`, `Executed`, `Status`, `TargetType`, `TargetOperationType`, `TargetName`, `InitiatedByName`, and per-run totals such as `TotalErrors`.

An Activity that a [Schedule](../configuration/schedules.md) produced also carries its attribution:

| Property | Type | Description |
|---|---|---|
| `ScheduledByScheduleName` | `String` | The producing Schedule's name, snapshotted when the Activity was created, so it still reads correctly after the Schedule is renamed or deleted. |
| `ScheduledByScheduleId` | `Guid` | The producing Schedule. |
| `ScheduleExecutionId` | `Guid` | The Schedule Execution this Activity belonged to. Pass it to `Get-JIMScheduleExecution -Id` for the whole run. |
| `ScheduleStepIndex` | `Int32` | Which step of that execution this was, 0-based. |

All four are empty for work nobody scheduled.

An Activity for a [Connector Space clear](../configuration/connector-space.md#clearing-the-connector-space) carries what the clear did, retrieved with `-Id`:

| Property | Type | Description |
|---|---|---|
| `ClearedConnectedSystemObjectCount` | `Int32` | How many Connected System Objects the clear deleted. |
| `ClearedPendingExportCount` | `Int32` | How many Pending Exports to this Connected System the clear discarded. |
| `ClearedJoinRecordCount` | `Int32` | How many Metaverse Objects were joined at the moment of the clear, and so recorded for the post-clear reconciliation to expect back. Not a removal. |

All three are empty for every other kind of Activity.

A configuration change made after reading a [Configuration Change Preview](../configuration/configuration-changes.md#previewing-a-change-before-you-make-it) (including a Connected System deletion recorded with `Remove-JIMConnectedSystem -PreviewActivityId`) carries `PreviewActivityId` (`Guid`, `-Id` only): the preview's own Activity id, which `Get-JIMConfigurationChangePreview -ActivityId` reads back. It is empty when no preview informed the change. A Full Synchronisation run or Connected System deletion made on a preview that something had since overtaken also carries `PreviewOvertakenAt` (`DateTime`, `-Id` only), when the latest overtaking run or configuration change was recorded, and `PreviewOvertakenBy` (`String`, `-Id` only), what it was, for example `Run Profile 'Delta Import' ran on Connected System 'HR Import'`. Both are empty when the preview was current.

An **Export** Activity carries how many Pending Exports of each change type were withheld by the Run Profile's [Safeguards](../configuration/run-profiles.md#safeguards) (`-Id` only):

| Property | Type | Description |
|---|---|---|
| `ExportCreatesWithheld` | `Int32` | How many creates the Run Profile's Max creates limit withheld this run. `0` when nothing was withheld. |
| `ExportUpdatesWithheld` | `Int32` | How many updates the Run Profile's Max updates limit withheld this run. `0` when nothing was withheld. |
| `ExportDeletesWithheld` | `Int32` | How many deletes the Run Profile's Max deletes limit withheld this run. `0` when nothing was withheld. |

All three are populated (zero when nothing was withheld) on every Export Activity, and empty for every other kind.

A **Full Import** Activity that ran deletion detection carries how many Connected System Objects were withheld by the Run Profile's [Safeguards](../configuration/run-profiles.md#full-import) (`-Id` only):

| Property | Type | Description |
|---|---|---|
| `DetectedDeletionsWithheld` | `Int32` | How many Connected System Objects the Run Profile's Max detected deletions/Max detected deletions percent limits withheld this run. `0` when nothing was withheld. |

Populated (zero when nothing was withheld) on every Full Import Activity that ran deletion detection; empty on every other kind, including a Full Import that imported zero objects and so skipped deletion detection entirely.

When using the **ExecutionItems** parameter set, returns `PSCustomObject` instances representing individual execution items, each containing properties such as `ExternalIdValue`, `DisplayName`, `ConnectedSystemObjectType`, `ObjectChangeType`, `ErrorType`, and `OutcomeSummary`. An item recorded by an export scope review (`ObjectChangeType` `ExportScopeReview`) has no Connected System Object; its `MetaverseObjectId` names the Metaverse Object it is about.

When using the **Follow** parameter set, progress renders to the host while following; when following ends, the final activity object is emitted (the same shape as **ById**).

### Examples

```powershell title="List recent activities"
Get-JIMActivity
```

```powershell title="List activities with pagination"
Get-JIMActivity -Page 2 -PageSize 50
```

```powershell title="Search for activities by target name"
Get-JIMActivity -Search "Active Directory"
```

```powershell title="Search for activities by type"
Get-JIMActivity -Search "FullImport"
```

```powershell title="Find the runs that failed"
Get-JIMActivity -Status FailedWithError, CompleteWithError
```

```powershell title="Narrow to one Connected System and Run Profile"
Get-JIMActivity -ConnectedSystem 'Contoso AD' -RunProfile 'Full Import' -Status FailedWithError
```

```powershell title="Has a Schedule been failing all week, or was last night a one-off?"
Get-JIMActivity -ScheduledOnly $true -Outcome Errors -CreatedFrom (Get-Date).AddDays(-7)
```

```powershell title="The Activities a particular Schedule produced"
Get-JIMSchedule -Name 'Nightly Sync' | ForEach-Object { Get-JIMActivity -ScheduleId $_.Id }
```

```powershell title="Review one person's recent activity"
Get-JIMActivity -InitiatedBy 'alice' -CreatedFrom (Get-Date).AddDays(-30)
```

```powershell title="Get a specific activity by ID"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Get execution items for a Run Profile activity"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -ExecutionItems
```

```powershell title="Pipeline from Start-JIMRunProfile"
$result = Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Wait -PassThru
Get-JIMActivity -Id $result.ActivityId
```

```powershell title="Follow an in-progress activity until it completes"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Follow
```

```powershell title="Follow with a bounded duration for scripts"
# Polls every 5 seconds, for at most 60 polls (5 minutes)
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Follow -IntervalSeconds 5 -MaxPolls 60
```

```powershell title="Review errors in execution items"
$result = Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Wait -PassThru
Get-JIMActivity -Id $result.ActivityId -ExecutionItems |
    Where-Object { $null -ne $_.ErrorType }
```

---

## Get-JIMActivityStats

Retrieves execution statistics for a Run Profile activity, including counts of processed items, errors, and timing information. This is useful for monitoring synchronisation health and identifying performance trends.

### Syntax

```powershell
Get-JIMActivityStats -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes | | ID of the activity to retrieve statistics for. Alias: `ActivityId`. Accepts pipeline input by property name. |

### Output

Returns a `PSCustomObject` containing execution statistics with properties such as `TotalObjectsProcessed`, `TotalObjectChangeCount`, `TotalUnchanged`, `TotalObjectErrors`, and `TotalObjectTypes`, plus per-operation breakdowns (`TotalCsoAdds`, `TotalJoins`, `TotalAttributeFlows`, `TotalExported`, and similar).

An import that read entries and discarded them because a [Container Scope exclusion](../connectors/jim-ldap-connector-topology.md#excluding-a-container) carved them out also carries `TotalEntriesDiscardedByExclusion` and `EntriesDiscardedByExcludedContainer`, the latter keyed by the excluded Container's ID. Both are absent, or zero and empty, for every run that discarded nothing. Resolve a Container ID to a name with [`Get-JIMConnectedSystemPartition`](connected-systems.md#get-jimconnectedsystempartition); the counts are keyed by ID because the Container can be renamed after the run.

### Examples

```powershell title="Get statistics by activity ID"
Get-JIMActivityStats -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Pipeline from Get-JIMActivity"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" |
    Get-JIMActivityStats
```

```powershell title="Get stats after a Run Profile completes"
$result = Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Wait -PassThru
Get-JIMActivityStats -Id $result.ActivityId
```

```powershell title="Check for errors after execution"
$result = Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 42 -Wait -PassThru
$stats = Get-JIMActivityStats -Id $result.ActivityId
if ($stats.TotalObjectErrors -gt 0) {
    Write-Warning "Sync completed with $($stats.TotalObjectErrors) errors"
    Get-JIMActivity -Id $result.ActivityId -ExecutionItems |
        Where-Object { $_.ErrorType -and $_.ErrorType -ne 'NotSet' } |
        Get-JIMActivityExecutionItem |
        Select-Object DisplayNameSnapshot, ErrorType, ErrorMessage
}
```

---

## Get-JIMActivityExecutionItem

Retrieves one execution item of a Run Profile activity: what happened to one object and, when it failed, why. The list returned by `Get-JIMActivity -ExecutionItems` names each failed item's error type; this cmdlet returns the error message and stack trace as well, which is usually what tells you how to fix it. For a generated value that ran out, for example, the message names the attribute, the last value tried and what already holds it.

With `-Causes` it returns what caused the item instead, and with `-Consequences` what the item went on to cause, in later runs as well as its own: the same story the [Lineage view](../configuration/causality.md) tells, one row per cohort.

### Syntax

```powershell
# Get one execution item (default)
Get-JIMActivityExecutionItem -Id <guid>

# Get what caused it
Get-JIMActivityExecutionItem -Id <guid> -Causes

# Get what it went on to cause
Get-JIMActivityExecutionItem -Id <guid> -Consequences
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes | | ID of the execution item. Accepts pipeline input by property name, so the rows of `Get-JIMActivity -ExecutionItems` can be piped straight in. |
| `Causes` | `switch` | No | | Return what caused the item, one row per cohort of causes. |
| `Consequences` | `switch` | No | | Return what the item went on to cause, one row per cohort of consequences. |

### Output

Returns a `PSCustomObject` describing the item:

| Property | Description |
|----------|-------------|
| `Id` | The execution item's ID. |
| `ActivityId` | The Run Profile activity the item belongs to. |
| `ObjectChangeType` | What happened to the object, for example `Projected`, `Updated` or `NoChange`. |
| `NoChangeReason` | Why nothing changed, when nothing did. |
| `ConnectedSystemObjectId` | The Connected System Object the item is about, while it still exists. |
| `MetaverseObjectId` | The Metaverse Object the item is about, where it records one. Kept as history, so the object may since have been deleted. |
| `PendingExportId` | The Pending Export the item is about, for an export. |
| `ExternalIdSnapshot` | The object's external ID when the item was recorded. |
| `DisplayNameSnapshot` | The object's display name when the item was recorded. |
| `ObjectTypeSnapshot` | The object's type when the item was recorded. |
| `ErrorType` | The kind of failure, for example `GeneratedValueExhausted`; `NotSet` or empty when the object did not fail. |
| `ErrorMessage` | What went wrong, in words; empty when the object did not fail. |
| `ErrorStackTrace` | The stack trace, when the failure came from an exception. |
| `AttributeFlowCount` | How many Metaverse attributes changed alongside a join, projection or disconnection. |
| `UnresolvedReferenceCount` | How many of the object's reference values the import could not resolve to a Connected System Object; empty when none. Set whatever `ErrorType` the item carries and under every [Unresolved Reference Handling](../configuration/unresolved-references.md) mode. The values themselves are on the Connected System Object. |
| `AttributeValueErrors` | Every value the import found on the object but could not read as its attribute's type, each with `AttributeName`, `AttributeType`, `Value` (as received) and `Reason`; empty when there were none. JIM left each attribute as it already held it. |
| `AttributeValueErrorTotal` | How many values could not be read; more than `AttributeValueErrors` holds where the item keeps only the first thousand. Empty when there were none. |
| `SourceLocation` | Where in the Connected System the object was read from, such as a file and row, where the Connector says. |
| `OutcomeSummary` | The item's outcomes with counts, for example `Projected:1,AttributeFlow:12`. |
| `MatchCandidates` | For an `AmbiguousMatch` or `AmbiguousProvisioningMatch`, the objects the match found; empty otherwise. See below. |
| `JoinObjectMatchingRuleId`, `JoinObjectMatchingRuleDescriptionSnapshot` | For an object joined to an existing Metaverse Object by matching, the [Object Matching Rule](../configuration/object-matching-rules.md) that found it, and what the rule compared when the join was made, for example `Employee ID equals employeeNumber`; empty for any other item. |
| `JoinMatchedValue` | For an object joined by matching, the value the join matched on: what the rule computed from the object. Empty for any other item. |

The snapshots record the object as it was when the item was recorded, so they still describe a Connected System Object that has since been deleted. For the object's current state, look it up by `ConnectedSystemObjectId` or `MetaverseObjectId`.

An object can join only one Metaverse Object, so when its Object Matching Rule finds several, the object is neither joined nor projected and the item lists up to 10 of them in `MatchCandidates`, ordered by display name. When matching before provisioning finds several existing Connected System Objects, JIM neither joins nor provisions the Metaverse Object, and its `AmbiguousProvisioningMatch` item lists them in `MatchCandidates`:

| Property | Description |
|----------|-------------|
| `CandidateKind` | `MetaverseObject` or `ConnectedSystemObject`. |
| `CandidateId` | The candidate's ID. |
| `DisplayNameSnapshot`, `TypeNameSnapshot` | The candidate's display name and type when the item was recorded. |
| `ConnectedSystemId`, `ConnectedSystemNameSnapshot` | For a Connected System Object candidate, its Connected System. |
| `MatchedValue` | The value or values the candidate matched on. |
| `ObjectMatchingRuleId`, `ObjectMatchingRuleDescriptionSnapshot` | The rule, and what it compared, for example `Employee ID equals employeeNumber`. |
| `Ordinal` | The candidate's position in the list. |
| `CandidateTotal` | How many candidates the match found in all, which can be more than the 10 listed. |
| `Exists` | Whether the candidate still exists. |
| `JoinedObjects` | For a Metaverse Object candidate, the Connected System Objects joined to it now, each with `ConnectedSystemObjectId`, `ConnectedSystemId`, `ConnectedSystemName`, `TypeName`, `DisplayName` and `ExternalId`. |

With `-Causes` or `-Consequences`, each row is a cohort: objects sharing a cause, its reason, Connected System and Synchronisation Rule read as one row with a `Count`. `Level` is 1 for the nearest cohorts and one more per step away.

| Property | `-Causes` | `-Consequences` | Description |
|----------|:---:|:---:|-------------|
| `ExecutionItemId` | ✅ | ✅ | The item the walk started from. |
| `Level` | ✅ | ✅ | How many steps from the item. A `-Consequences` row at level 0 gives the item's own ending when it caused nothing. |
| `HopKind` | ❌ | ✅ | How the consequence was reached: `Edge`, `Confirmation`, `NextSynchronisation`, `OutstandingPendingExport` or `ScheduledDeletion`. |
| `EdgeType`, `ReasonCode` | ✅ | ✅ | The recorded cause and why it produced its effect. |
| `SourceImportChangeType`, `MetaverseChangeType` | ✅ | ❌ | For the import that fed a synchronisation, or the Metaverse Object's creation. |
| `Count` | ✅ | ✅ | How many objects the cohort holds, counted exactly. |
| `ObjectType`, `Attribute` | ✅ | ✅ | The objects' type, and the reference attribute a removal acted through. |
| `ConnectedSystem`, `SyncRule` | ✅ | ✅ | The Connected System and Synchronisation Rule the cohort is attributed to. |
| `OutcomeSummary`, `PendingExportStatus` | ❌ | ✅ | The members' shared outcomes, and the live status of work not yet finished. |
| `Ending` | ❌ | ✅ | Where the branch stopped, for example `Confirmed`, `WaitingForExport`, `NeedsAttention` or `NoFurtherConsequences (2)`; empty where it carries on. |
| `FirstOccurred`, `LastOccurred`, `DueAt` | ❌ | ✅ | When the members happened, and when outstanding work is next due (a retry, or a scheduled deletion). |
| `Members` | ✅ | ✅ | The first members' names. Page through all of a consequence cohort with `Get-JIMCausalCohortMember`. |
| `Resolution` | ✅ | ❌ | Why the walk stopped at these causes, for example `CauseNotRetained`; empty where it carries on. |
| `CohortKey` | ❌ | ✅ | The cohort's key, for `Get-JIMCausalCohortMember`. |

Consequences can still be happening, so `-Consequences` is a reading taken now: an `Ending` that starts with `Waiting` or reads `DeletionScheduled` will move on without anyone acting, and a later call can show more. `NeedsAttention` will not; open the Pending Export it names.

### Examples

```powershell title="Get one execution item"
Get-JIMActivityExecutionItem -Id "b2c3d4e5-f6a7-8901-bcde-f12345678901"
```

```powershell title="List why each object in a run failed"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -ExecutionItems |
    Where-Object { $_.ErrorType -and $_.ErrorType -ne 'NotSet' } |
    Get-JIMActivityExecutionItem |
    Format-List DisplayNameSnapshot, ErrorType, ErrorMessage
```

```powershell title="List every object an import left with unresolved references"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -ExecutionItems |
    Get-JIMActivityExecutionItem |
    Where-Object { $_.UnresolvedReferenceCount -gt 0 } |
    Format-Table DisplayNameSnapshot, UnresolvedReferenceCount, ErrorType
```

This finds objects whose item carries a different error as well, which filtering on `ErrorType` would miss.

```powershell title="List every value an import could not read, and where"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -ExecutionItems |
    Where-Object { $_.ErrorType -eq 'ImportAttributeValueError' } |
    Get-JIMActivityExecutionItem |
    ForEach-Object {
        $item = $_
        $item.AttributeValueErrors | Select-Object @{ Name = 'Object'; Expression = { $item.DisplayNameSnapshot } },
            @{ Name = 'Source'; Expression = { $item.SourceLocation } }, AttributeName, Value, Reason
    }
```

```powershell title="List the objects an ambiguous match found"
(Get-JIMActivityExecutionItem -Id "b2c3d4e5-f6a7-8901-bcde-f12345678901").MatchCandidates |
    Select-Object DisplayNameSnapshot, MatchedValue, Exists,
        @{ Name = 'JoinedTo'; Expression = { $_.JoinedObjects.ConnectedSystemName -join ', ' } }
```

```powershell title="Show what caused an execution item"
Get-JIMActivityExecutionItem -Id "b2c3d4e5-f6a7-8901-bcde-f12345678901" -Causes |
    Format-Table Level, EdgeType, ReasonCode, Count, ObjectType, ConnectedSystem, Members
```

```powershell title="Show which consequences are still waiting, and on what"
Get-JIMActivityExecutionItem -Id "b2c3d4e5-f6a7-8901-bcde-f12345678901" -Consequences |
    Where-Object Ending -like 'Waiting*' |
    Format-Table Level, HopKind, Count, ObjectType, ConnectedSystem, Ending, DueAt
```

---

## Get-JIMCausalCohortMember

Pages through every member of one cohort of an execution item's consequences. `Get-JIMActivityExecutionItem -Consequences` counts a cohort exactly ("4218 Groups") but returns only its first members; this returns the rest, earliest first and then by name.

### Syntax

```powershell
# Get a page of members (default)
Get-JIMCausalCohortMember -ExecutionItemId <guid> -CohortKey <string> [-Search <string>] [-Page <int>] [-PageSize <int>]

# Get every member, paginating automatically
Get-JIMCausalCohortMember -ExecutionItemId <guid> -CohortKey <string> [-Search <string>] -All [-PageSize <int>] [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ExecutionItemId` | `guid` | Yes | | The execution item the consequences were read for. Accepts pipeline input by property name. |
| `CohortKey` | `string` | Yes | | The cohort's key, from `Get-JIMActivityExecutionItem -Consequences`. Accepts pipeline input by property name, so consequence rows can be piped straight in. |
| `Search` | `string` | No | | Text a member's name must contain, case-insensitively. |
| `Page` | `int` | No | `1` | The page to return. |
| `PageSize` | `int` | No | `50` (`100` with `-All`) | Members per page, from 1 to 100. |
| `All` | `switch` | No | | Return every member, fetching page after page. |
| `Force` | `switch` | No | | With `-All`, continue past the page ceiling. |

### Output

Returns one `PSCustomObject` per member, with `RunProfileExecutionItemId` (the item that recorded the consequence, where it is one), `PendingExportId` (outstanding work), `MetaverseObjectId` (a pending deletion), `DisplayName`, `Occurred` and `DueAt`.

### Examples

```powershell title="List every Group a deletion removed a member from"
Get-JIMActivityExecutionItem -Id "b2c3d4e5-f6a7-8901-bcde-f12345678901" -Consequences |
    Where-Object { $_.Level -eq 1 -and $_.Attribute -eq 'Static Members' } |
    Get-JIMCausalCohortMember -All |
    Select-Object DisplayName, Occurred
```

---

## Get-JIMActivityChildren

Retrieves child activities spawned by a parent activity. For example, a schedule execution activity creates child activities for each individual Run Profile step within that schedule. This cmdlet is useful for drilling into multi-step operations to inspect each step independently.

The underlying API returns a paginated response envelope, but this cmdlet unwraps it internally and still emits one object per child activity to the pipeline, so existing scripts that pipe its output are unaffected by the paginated response shape.

### Syntax

```powershell
# Get a page of child activities (default)
Get-JIMActivityChildren -Id <guid> [-Page <int>] [-PageSize <int>]

# Get every child activity, paginating automatically
Get-JIMActivityChildren -Id <guid> -All [-Force] [-PageSize <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes | | ID of the parent activity whose children to retrieve. Accepts pipeline input by property name. |
| `Page` | `int` | No (Page set) | `1` | Page number for the child activity list. Cannot be used with `-All`. |
| `PageSize` | `int` | No | `50` | Number of child activities per page. Maximum is 100. |
| `All` | `switch` | Yes (All set) | | Automatically paginates through all child activities and returns every one. Cannot be used with `-Page`. Fetches at most 1000 pages and then stops with a warning; use `-Force` to fetch beyond the cap, up to the API's maximum retrieval depth of 1,000,000 rows. |
| `Force` | `switch` | No (All set) | | Override the `-All` 1000-page ceiling and fetch every page regardless of size. Only valid with `-All`. |

### Output

Returns one or more `PSCustomObject` instances representing child activities, each containing the same properties as a standard activity object: `Id`, `Created`, `Executed`, `Status`, `TargetType`, `TargetOperationType`, and `TargetName`.

### Examples

```powershell title="Get the first page of child activities by parent ID"
Get-JIMActivityChildren -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Get every child activity, paginating automatically"
Get-JIMActivityChildren -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -All
```

```powershell title="Pipeline from Get-JIMActivity"
Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" |
    Get-JIMActivityChildren
```

```powershell title="Inspect each step of a schedule execution"
Get-JIMActivity -Search "Nightly Sync Schedule" |
    Select-Object -First 1 |
    Get-JIMActivityChildren -All |
    Format-Table TargetName, Status, Created, Executed
```

```powershell title="Find failed child activities"
Get-JIMActivityChildren -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -All |
    Where-Object { $_.Status -eq "FailedWithError" }
```

```powershell title="Get statistics for each child activity"
Get-JIMActivityChildren -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -All |
    ForEach-Object { Get-JIMActivityStats -Id $_.Id }
```

---

## See also

- [Activities](../configuration/activities.md): what activities are, lifecycle, summary statistics, and parent/child execution model
- [Run Profiles](run-profiles.md): cmdlets for managing and executing synchronisation Run Profiles
- [Schedules](schedules.md): cmdlets for managing automated Run Profile schedules
