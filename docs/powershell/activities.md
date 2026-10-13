---
title: Activities
---

# Activities

Activity cmdlets retrieve and inspect the execution history of operations within JIM. Activities track all operations: synchronisation runs, data generation, certificate management, and other administrative actions. Example data generation is recorded as a **Data Generation** Activity, separate from changes to an Example Data Template. Use these cmdlets to review Activities, retrieve a Run Profile execution's statistics, and inspect the child Activities a parent operation spawned. To find out what happened to one object in a run, and why, see [Execution Items](execution-items.md).

---

## Get-JIMActivity

Retrieves activity history from JIM. Activities are created automatically whenever an operation runs, providing a full audit trail of synchronisation runs, data generation tasks, certificate management operations, and other administrative actions.

<!-- BEGIN GENERATED: Get-JIMActivity -->
### Syntax

```powershell
# List (default)
Get-JIMActivity [-Search <string>] [-Page <int>] [-PageSize <int>] [-Operation <string[]>] [-Outcome <string[]>] [-Status <string[]>] [-InitiatedById <guid>] [-InitiatedBy <string>] [-HasChildActivities <bool>] [-CreatedFrom <datetime>] [-CreatedTo <datetime>] [-ConnectedSystem <string[]>] [-RunProfile <string[]>] [-ScheduledOnly <bool>] [-ScheduleId <guid[]>]

# Follow
Get-JIMActivity -Id <guid> -Follow [-IntervalSeconds <int>] [-MaxPolls <int>]

# ExecutionItems
Get-JIMActivity -Id <guid> -ExecutionItems

# ById
Get-JIMActivity -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (Follow, ExecutionItems, ById) |  | The Activity to get. Accepts pipeline input by property name. |
| `Search` | `string` | No (List) |  | Return only the Activities whose target name or type contains this text; "Active Directory", for example, finds the Activities of that Connected System. |
| `Page` | `int` | No (List) | `1` | The page of Activities to return. At least 1. |
| `PageSize` | `int` | No (List) | `20` | How many Activities per page. From 1 to 100. |
| `Operation` | `string[]` | No (List) |  | Return only the Activities that performed one of these operations, for example Execute for a Run Profile execution. One of `Create`, `Read`, `Update`, `Delete`, `Clear`, `Execute`, `ImportHierarchy`, `ImportSchema`, `Revert`, `Reset`, `Authenticate` or `Preview`. |
| `Outcome` | `string[]` | No (List) |  | Return only the Activities that recorded at least one of these outcomes, for example Errors for runs that recorded an error. One of `Added`, `Updated`, `Deleted`, `Projected`, `Joined`, `AttributeFlows`, `Disconnected`, `DriftCorrections`, `Provisioned`, `Exported`, `Deprovisioned`, `Created`, `PendingExports` or `Errors`. |
| `Status` | `string[]` | No (List) |  | Return only the Activities with one of these statuses. One of `InProgress`, `Complete`, `CompleteWithWarning`, `CompleteWithError`, `FailedWithError` or `Cancelled`. |
| `InitiatedById` | `guid` | No (List) |  | Return only the Activities initiated by this principal (a Metaverse Object or API key). `-InitiatedBy` matches on the recorded name instead. |
| `InitiatedBy` | `string` | No (List) |  | Return only the Activities whose initiator's recorded name contains this text, case-insensitively. |
| `HasChildActivities` | `bool` | No (List) |  | Return only the Activities that have child Activities (`$true`), or only those that have none (`$false`). Omit it to return both. |
| `CreatedFrom` | `datetime` | No (List) |  | Return only the Activities created at or after this time, which is converted to UTC. |
| `CreatedTo` | `datetime` | No (List) |  | Return only the Activities created at or before this time, which is converted to UTC. |
| `ConnectedSystem` | `string[]` | No (List) |  | Return only the Activities of these Connected Systems, by exact name. |
| `RunProfile` | `string[]` | No (List) |  | Return only the executions of these Run Profiles, by exact name. |
| `ScheduledOnly` | `bool` | No (List) |  | Return only the Activities a Schedule produced (`$true`), or only those nobody scheduled (`$false`). Omit it to return both. |
| `ScheduleId` | `guid[]` | No (List) |  | Return only the Activities these Schedules produced. |
| `ExecutionItems` | `switch` | Yes (ExecutionItems) |  | Return the Activity's Run Profile Execution Items: what happened to each object. |
| `Follow` | `switch` | Yes (Follow) |  | Follow the Activity's progress until it finishes, like tail -f, showing the step it is on, object counts, throughput and the estimated time remaining. Ctrl+C stops following early. |
| `IntervalSeconds` | `int` | No (Follow) | `2` | How many seconds to wait between reads of the progress. From 1 to 300. |
| `MaxPolls` | `int` | No (Follow) |  | How many times to read the progress before following stops, whether or not the Activity has finished, for a script that must not wait indefinitely. Omit it to follow until the Activity finishes. At least 1. |

### Output

A list returns each Activity's header, and `-Id` the Activity in full. `-ExecutionItems` returns each of the Activity's Run Profile Execution Items. `-Follow` shows progress while it follows, then returns the Activity in full.

**By default**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The unique identifier of the Activity. |
| `Created` | `datetime` | When the Activity was created. |
| `Executed` | `datetime` | When the Activity started executing. |
| `Status` | `string` | The current status of the Activity. One of `NotSet`, `InProgress`, `Complete`, `CompleteWithWarning`, `CompleteWithError`, `FailedWithError` or `Cancelled`. |
| `TargetType` | `string` | The type of target this Activity operates on. One of `NotSet`, `ExampleDataTemplate`, `ConnectedSystem`, `ConnectedSystemRunProfile`, `SynchronisationRule`, `MetaverseObject`, `TrustedCertificate`, `ObjectMatchingRule`, `MetaverseAttribute`, `ServiceSetting`, `HistoryRetentionCleanup`, `MetaverseObjectType`, `System`, `Schedule`, `TemporalScopeReconciliation`, `ApiKey`, `Role`, `PredefinedSearch`, `ConnectorDefinition`, `ExampleDataSet`, `SystemInitialisation`, `DataGeneration`, `Authentication`, `MetaverseObjectHousekeeping`, `ConnectedSystemObject` or `PasswordSynchronisation`. |
| `TargetOperationType` | `string` | The operation being performed. One of `Create`, `Read`, `Update`, `Delete`, `Clear`, `Execute`, `ImportHierarchy`, `ImportSchema`, `Revert`, `Reset`, `Authenticate`, `Preview`, `SetPassword`, `SchemaRefreshRemoval`, `RetryPasswordDelivery`, `CancelPasswordDelivery`, `DiscoverAuxiliaryClasses`, `RecallAttributeValues`, `Deprovision`, `RestartGeneratedValues`, `AllowGeneratedValueRename` or `RetryGeneratedValue`. |
| `TargetName` | `string` | The name of the target object. |
| `TargetContext` | `string` | Additional context for the target, such as the parent entity name. For example, for a Run Profile Activity this would be the Connected System name. |
| `Message` | `string` | Progress message for the Activity. |
| `InitiatedByType` | `string` | The type of security principal that initiated this Activity. One of `NotSet`, `User`, `ApiKey`, `System` or `Anonymous`. |
| `InitiatedById` | `guid` | The unique identifier of the security principal that initiated this Activity. |
| `InitiatedByName` | `string` | Name of the user or API key that initiated this Activity. |
| `ObjectsToProcess` | `int` | Number of objects to process (for progress tracking). |
| `ObjectsProcessed` | `int` | Number of objects processed so far (for progress tracking). |
| `ExecutionTime` | `string` | How long the Activity took to execute (once complete). |
| `TotalActivityTime` | `string` | Total time from creation to completion. |
| `ConnectedSystemRunType` | `string` | The run type, for a Run Profile execution. One of `NotSet`, `FullImport`, `DeltaImport`, `FullSynchronisation`, `DeltaSynchronisation` or `Export`. |
| `TotalAdded` | `int` | Count of new Connected System Objects added to the Connector Space during import. |
| `TotalUpdated` | `int` | Count of existing Connected System Objects updated during import. |
| `TotalDeleted` | `int` | Count of Connected System Objects marked as deleted (source system deletion detected). |
| `TotalProjected` | `int` | Count of new Metaverse Objects created via projection. |
| `TotalJoined` | `int` | Count of Connected System Objects joined to existing Metaverse Objects. |
| `TotalAttributeFlows` | `int` | Count of Attribute Flow operations. |
| `TotalDisconnected` | `int` | Count of Connected System Objects disconnected from Metaverse Objects. |
| `TotalDisconnectedOutOfScope` | `int` | Count of Connected System Objects disconnected because they fell out of scope of import Synchronisation Rule scoping criteria. |
| `TotalOutOfScopeRetainJoin` | `int` | Count of Connected System Objects that fell out of scope but remained joined (InboundOutOfScopeAction = RemainJoined). |
| `TotalDriftCorrections` | `int` | Count of Connected System Objects where drift was detected and corrective Pending Exports were created. |
| `TotalProvisioned` | `int` | Count of Connected System Objects provisioned to target Connected Systems during synchronisation. |
| `TotalExported` | `int` | Count of objects exported to target systems (includes both initial creation and subsequent updates). |
| `TotalDeprovisioned` | `int` | Count of objects deprovisioned from target systems. |
| `TotalCreated` | `int` | Count of Metaverse Objects created directly (by example data generation or in the portal) rather than by projection. |
| `TotalPendingExports` | `int` | Count of Pending Exports staged for the next export run. |
| `TotalErrors` | `int` | Count of Run Profile Execution Items with errors. |
| `ChildActivityCount` | `int` | The number of direct child Activities linked to this Activity. Zero for Activities with no children. |
| `ScheduleExecutionId` | `guid` | If a Schedule Execution produced this Activity, the execution's unique identifier. Empty otherwise. |
| `ScheduleStepIndex` | `int` | The step within the Schedule Execution that produced this Activity (0-based). Read with `ScheduleExecutionId` to identify exactly which step ran. Empty when no Schedule produced it. |
| `ScheduledByScheduleId` | `guid` | If a Schedule produced this Activity, the Schedule's unique identifier. Empty otherwise. |
| `ScheduledByScheduleName` | `string` | The producing Schedule's name, as a snapshot taken when the Activity was created, so history still reads correctly after the Schedule has been renamed or deleted. Empty when no Schedule produced this Activity. |

**With `-Id` or `-Follow`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The unique identifier of the Activity. |
| `ParentActivityId` | `guid` | Parent Activity ID if this was triggered by another Activity. |
| `Created` | `datetime` | When the Activity was created. |
| `Executed` | `datetime` | When the Activity started executing. |
| `Status` | `string` | The current status of the Activity. One of `NotSet`, `InProgress`, `Complete`, `CompleteWithWarning`, `CompleteWithError`, `FailedWithError` or `Cancelled`. |
| `TargetType` | `string` | The type of target this Activity operates on. One of `NotSet`, `ExampleDataTemplate`, `ConnectedSystem`, `ConnectedSystemRunProfile`, `SynchronisationRule`, `MetaverseObject`, `TrustedCertificate`, `ObjectMatchingRule`, `MetaverseAttribute`, `ServiceSetting`, `HistoryRetentionCleanup`, `MetaverseObjectType`, `System`, `Schedule`, `TemporalScopeReconciliation`, `ApiKey`, `Role`, `PredefinedSearch`, `ConnectorDefinition`, `ExampleDataSet`, `SystemInitialisation`, `DataGeneration`, `Authentication`, `MetaverseObjectHousekeeping`, `ConnectedSystemObject` or `PasswordSynchronisation`. |
| `TargetOperationType` | `string` | The operation being performed. One of `Create`, `Read`, `Update`, `Delete`, `Clear`, `Execute`, `ImportHierarchy`, `ImportSchema`, `Revert`, `Reset`, `Authenticate`, `Preview`, `SetPassword`, `SchemaRefreshRemoval`, `RetryPasswordDelivery`, `CancelPasswordDelivery`, `DiscoverAuxiliaryClasses`, `RecallAttributeValues`, `Deprovision`, `RestartGeneratedValues`, `AllowGeneratedValueRename` or `RetryGeneratedValue`. |
| `TargetName` | `string` | The name of the target object. |
| `TargetContext` | `string` | Additional context for the target, such as the parent entity name. For example, for a Run Profile Activity this would be the Connected System name. |
| `Message` | `string` | Progress message for the Activity. |
| `InitiatedByType` | `string` | The type of security principal that initiated this Activity. One of `NotSet`, `User`, `ApiKey`, `System` or `Anonymous`. |
| `InitiatedById` | `guid` | The unique identifier of the security principal that initiated this Activity. |
| `InitiatedByName` | `string` | Name of the user or API key that initiated this Activity. |
| `ObjectsToProcess` | `int` | Number of objects to process (for progress tracking). |
| `ObjectsProcessed` | `int` | Number of objects processed so far (for progress tracking). |
| `ExecutionTime` | `string` | How long the Activity took to execute (once complete). |
| `TotalActivityTime` | `string` | Total time from creation to completion. |
| `WarningMessage` | `string` | A warning that did not stop the Activity, for example that a Delta Import fell back to a Full Import. |
| `ErrorMessage` | `string` | Error message if the Activity failed. |
| `ErrorStackTrace` | `string` | Error stack trace if the Activity failed. |
| `ErrorDetail` | `string` | Structured detail about the failure, as JSON, where there is something specific worth acting on beyond the message. An LDAPS connection refused because of the directory server's certificate records that certificate here: its subject, issuer, the names it was issued for, its validity dates, its thumbprint, and which check it failed. Empty for failures with no structured detail. |
| `ConnectedSystemRunType` | `string` | The run type, for a Run Profile execution. One of `NotSet`, `FullImport`, `DeltaImport`, `FullSynchronisation`, `DeltaSynchronisation` or `Export`. |
| `ConnectedSystemId` | `int` | The Connected System ID if applicable. |
| `ConnectedSystemRunProfileId` | `int` | The Run Profile ID if applicable. |
| `SyncRuleId` | `int` | The Synchronisation Rule ID if applicable. |
| `MetaverseObjectId` | `guid` | The Metaverse Object ID if applicable. |
| `ExampleDataTemplateId` | `int` | The data generation template ID if applicable. |
| `ExecutionStats` | `object` | Execution statistics for Run Profile Activities. It has `TotalObjectsProcessed`, `TotalObjectChangeCount`, `TotalUnchanged`, `TotalObjectErrors`, `TotalObjectTypes`, `TotalCsoAdds`, `TotalCsoUpdates`, `TotalCsoDeletes`, `TotalProjections`, `TotalJoins`, `TotalAttributeFlows`, `TotalDisconnections`, `TotalDisconnectedOutOfScope`, `TotalOutOfScopeRetainJoin`, `TotalDriftCorrections`, `TotalProvisioned`, `TotalGeneratedValues`, `TotalGeneratedValueFailures`, `TotalGeneratedValuesRemediated`, `TotalGeneratedValuesNeedingDecision`, `TotalGeneratedValuesWaiting`, `TotalExported`, `TotalDeprovisioned`, `TotalPendingExports`, `TotalPendingExportsConfirmed`, `TotalPendingExportsRetrying`, `TotalPendingExportsFailed`, `TotalCreated`, `EntriesDiscardedByExcludedContainer` and `TotalEntriesDiscardedByExclusion`. |
| `Phases` | `object[]` | The steps the Run Profile execution moved through, in run order, each with how it turned out and how long it took. This is what answers "where did the four hours go?" on a run that finished days ago. Empty for other Activity types, and for runs that predate phase recording. Each has `Key`, `Name`, `ParentKey`, `Order`, `Status`, `Started`, `Ended` and `DurationSeconds`. |
| `ScheduleExecutionId` | `guid` | If a Schedule Execution produced this Activity, the execution's unique identifier. Empty otherwise. |
| `ScheduleStepIndex` | `int` | The step within the Schedule Execution that produced this Activity (0-based). Read with `ScheduleExecutionId` to identify exactly which step ran. Empty when no Schedule produced it. |
| `ScheduledByScheduleId` | `guid` | If a Schedule produced this Activity, the Schedule's unique identifier. Empty otherwise. |
| `ScheduledByScheduleName` | `string` | The producing Schedule's name, as a snapshot taken when the Activity was created, so history still reads correctly after the Schedule has been renamed or deleted. Empty when no Schedule produced this Activity. |
| `ChangeReason` | `string` | For a configuration-change Activity, the optional reason supplied for the change. |
| `ConfigurationChangeVersion` | `int` | For a configuration-change Activity, the per-object version number this change produced. |
| `ConfigurationChangeSnapshot` | `object` | For a configuration-change Activity, the redacted snapshot of the object's state at this change; empty otherwise. It has `SchemaVersion`, `ObjectType`, `ObjectId`, `ObjectGuidId`, `ObjectKey`, `ObjectName` and `Root`. |
| `PreviewActivityId` | `guid` | For a change made after reading a Configuration Change Preview, the preview's Activity id, as recorded by the change's `previewActivityId`; read it with `GET /api/v1/previews/{activityId}`. Empty when no preview informed the change, and for every other kind of Activity. |
| `PreviewOvertakenAt` | `datetime` | When the preview in `PreviewActivityId` had been overtaken by the time the change was made: the latest run or configuration change recorded after the preview started that could change its answer. Empty when the preview was current, or when no preview informed the change. |
| `PreviewOvertakenBy` | `string` | What had overtaken the preview in `PreviewActivityId` by the time the change was made, for example "Run Profile 'Delta Import' ran on Connected System 'HR Import'". Empty when `PreviewOvertakenAt` is. |
| `ClearedPendingExportCount` | `int` | For a Connector Space clear, how many Pending Exports to the Connected System the clear discarded; empty for every other kind of Activity. |
| `ClearedConnectedSystemObjectCount` | `int` | For a Connector Space clear, how many Connected System Objects the clear deleted; empty for every other kind of Activity. |
| `ClearedJoinRecordCount` | `int` | For a Connector Space clear, how many Metaverse Objects were joined at the moment of the clear and so recorded for the post-clear reconciliation to expect back; empty for every other kind of Activity. Not a removal: these objects are what the next Full Synchronisation compares against once a Full Import has rebuilt the Connector Space. |
| `ExportCreatesWithheld` | `int` | How many creates the Run Profile's Max creates limit withheld this run; they stay pending for a later Export run. Populated (zero when nothing was withheld) on every Export Activity; empty otherwise. |
| `ExportUpdatesWithheld` | `int` | How many updates the Run Profile's Max updates limit withheld this run; they stay pending for a later Export run. Populated (zero when nothing was withheld) on every Export Activity; empty otherwise. |
| `ExportDeletesWithheld` | `int` | How many deletes the Run Profile's Max deletes limit withheld this run; they stay pending for a later Export run. Populated (zero when nothing was withheld) on every Export Activity; empty otherwise. |
| `DetectedDeletionsWithheld` | `int` | How many Connected System Objects the Run Profile's Max detected deletions and Max detected deletions percent limits withheld this run; those objects are left as they are, not marked as deleted. Populated (zero when nothing was withheld) on every Full Import Activity that ran deletion detection; empty on every other Activity, including a Full Import that imported zero objects and so skipped detection entirely. |

**With `-ExecutionItems`**:

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `guid` | The item's unique identifier. |
| `ExternalIdValue` | `string` | The object's external ID: its Connected System Object's current value, or the value recorded with the item where that object no longer exists. |
| `DisplayName` | `string` | The object's display name: its Connected System Object's current value, or the name recorded with the item where that object no longer exists. |
| `ConnectedSystemObjectType` | `string` | The object's type: its Connected System Object's current type, or the type recorded with the item where that object no longer exists. |
| `ObjectChangeType` | `string` | What happened to the object. One of `NotSet`, `Added`, `Updated`, `Deleted`, `Projected`, `Joined`, `AttributeFlow`, `Disconnected`, `DisconnectedOutOfScope`, `OutOfScopeRetainJoin`, `DriftCorrection`, `Exported`, `Deprovisioned`, `NoChange`, `PendingExport`, `PendingExportConfirmed`, `Created` or `ExportScopeReview`. |
| `ErrorType` | `string` | The kind of failure; `NotSet` or empty when the object did not fail. One of `NotSet`, `AmbiguousMatch`, `CouldNotMatchObjectType`, `CouldNotJoinDueToExistingJoin`, `DuplicateImportedAttributes`, `MissingExternalIdAttributeValue`, `UnexpectedAttribute`, `UnresolvedReference`, `UnsupportedExternalIdAttributeType`, `ExportNotConfirmed`, `ExportConfirmationFailed`, `CsoCreationFailed`, `DuplicateObject`, `InvalidGeneratedExternalId`, `UnhandledError`, `ExpressionEvaluationError`, `ExpressionMissingInput`, `MultiValuedToSingleValued`, `DeltaImportFallbackToFullImport`, `ImportHashVerificationFailed`, `ImportAttributeValueError`, `ConnectorConfigurationError`, `CouldNotExportDueToExistingConnectedSystemObject`, `ClassMembershipRequirementsNotMet`, `GeneratedValueExhausted`, `GeneratedValueWidthExceeded`, `GeneratedValueCollisionUnresolved`, `UniqueValueAlreadyInUse`, `ObjectMatchingRuleFailed`, `ObjectTypeChanged`, `PasswordRequiredToEnableAccount`, `AmbiguousProvisioningMatch` or `ScopingCriterionFailed`. |
| `MetaverseObjectId` | `guid` | The Metaverse Object the item is about, where it records one rather than a Connected System Object (an export scope review). Empty otherwise. |
| `OutcomeSummary` | `string` | Outcome types with counts, for example `Projected:1,AttributeFlow:12,PendingExportCreated:2`. Empty for an item recorded before outcomes were tracked, or when outcome tracking is disabled. |
<!-- END GENERATED: Get-JIMActivity -->

Which of an Activity's properties are filled in depends on what it records. [Schedules](../configuration/schedules.md) explains the Schedule attribution: pass `ScheduleExecutionId` to `Get-JIMScheduleExecution -Id` for the whole Schedule run. [Clearing the Connector Space](../configuration/connector-space.md#clearing-the-connector-space) explains the counts of what a clear removed, and [Safeguards](../configuration/run-profiles.md#safeguards) the counts of what a Run Profile's limits withheld.

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

<!-- BEGIN GENERATED: Get-JIMActivityStats -->
### Syntax

```powershell
Get-JIMActivityStats -Id <guid>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The Run Profile Activity to get statistics for. Accepts pipeline input by property name, `Id` or `ActivityId`. |

### Output

The Run Profile execution's statistics.

| Property | Type | Description |
|----------|------|-------------|
| `TotalObjectsProcessed` | `int` | Total number of objects that were in scope for processing. |
| `TotalObjectChangeCount` | `int` | Total number of object changes (Run Profile Execution Items created). |
| `TotalUnchanged` | `int` | Number of objects that were unchanged. Calculated as TotalObjectsProcessed - TotalObjectChangeCount. |
| `TotalObjectErrors` | `int` | Number of objects with errors. |
| `TotalObjectTypes` | `int` | Number of distinct object types affected. |
| `TotalCsoAdds` | `int` | Number of Connected System Objects added to the Connector Space during import. |
| `TotalCsoUpdates` | `int` | Number of existing Connected System Objects updated during import. |
| `TotalCsoDeletes` | `int` | Number of Connected System Objects marked as deleted. |
| `TotalProjections` | `int` | Number of new Metaverse Objects created via projection. |
| `TotalJoins` | `int` | Number of Connected System Objects joined to existing Metaverse Objects. |
| `TotalAttributeFlows` | `int` | Number of Attribute Flows between Connected System Objects and Metaverse Objects. |
| `TotalDisconnections` | `int` | Number of Connected System Objects disconnected from Metaverse Objects. |
| `TotalDisconnectedOutOfScope` | `int` | Number of Connected System Objects disconnected from Metaverse Objects because they fell out of scope of import Synchronisation Rule scoping criteria. |
| `TotalOutOfScopeRetainJoin` | `int` | Number of Connected System Objects that fell out of scope but remained joined (InboundOutOfScopeAction = RemainJoined). |
| `TotalDriftCorrections` | `int` | Number of Connected System Objects where drift was detected and corrective Pending Exports were created. |
| `TotalProvisioned` | `int` | Number of Connected System Objects provisioned to target Connected Systems during synchronisation. |
| `TotalGeneratedValues` | `int` | Number of values JIM generated for a target attribute during synchronisation. |
| `TotalGeneratedValueFailures` | `int` | Number of Run Profile Execution Items whose error type is a Unique Value Generation failure (exhausted, width exceeded, or an unresolved collision). |
| `TotalGeneratedValuesRemediated` | `int` | Number of generated values Collision Remediation corrected after a target rejected them as already in use. |
| `TotalGeneratedValuesNeedingDecision` | `int` | Number of rejected generated values that could not safely be corrected and wait on an administrator's decision. |
| `TotalGeneratedValuesWaiting` | `int` | Number of generated values held back because a value built from them needs another input the object has no value for yet. Not a failure: a later synchronisation generates the value once the input arrives. |
| `TotalExported` | `int` | Number of objects exported to target systems (includes both initial creation and subsequent updates). |
| `TotalDeprovisioned` | `int` | Number of objects deprovisioned from target systems. |
| `TotalPendingExports` | `int` | Number of Pending Exports staged for the next export run. These are exports that were previously executed but not yet confirmed, giving operators visibility into what changes will be made to Connected Systems. |
| `TotalPendingExportsConfirmed` | `int` | Number of Pending Exports that were fully confirmed and deleted. The exported attribute values matched the imported values. |
| `TotalPendingExportsRetrying` | `int` | Number of Pending Exports with unconfirmed attributes that will be retried. Some attribute values did not match; they will be re-exported on the next export run. |
| `TotalPendingExportsFailed` | `int` | Number of Pending Exports that failed after their last retry. JIM does not attempt one again; it clears once an import shows its values in place, for example after the change is made in the Connected System directly. |
| `TotalCreated` | `int` | Number of Metaverse Objects created directly (by example data generation or in the portal) rather than by projection. |
| `EntriesDiscardedByExcludedContainer` | `hashtable` | How many entries this import read from the Connected System and discarded because an excluded Container carved them out, keyed by that Container's id. |
| `TotalEntriesDiscardedByExclusion` | `int` | Entries discarded across every exclusion, which is the figure that answers "why was this import slow?". |
<!-- END GENERATED: Get-JIMActivityStats -->

An import that discarded entries because a [Container Scope exclusion](../connectors/jim-ldap-connector-topology.md#excluding-a-container) carved them out counts them in `TotalEntriesDiscardedByExclusion` and `EntriesDiscardedByExcludedContainer`, the latter keyed by the excluded Container's ID because the Container can be renamed after the run. Resolve an ID to a name with [`Get-JIMConnectedSystemPartition`](partitions-and-containers.md#get-jimconnectedsystempartition).

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

## Get-JIMActivityChildren

Retrieves child activities spawned by a parent activity. For example, a schedule execution activity creates child activities for each individual Run Profile step within that schedule. This cmdlet is useful for drilling into multi-step operations to inspect each step independently.

The underlying API returns a paginated response envelope, but this cmdlet unwraps it internally and still emits one object per child activity to the pipeline, so existing scripts that pipe its output are unaffected by the paginated response shape.

<!-- BEGIN GENERATED: Get-JIMActivityChildren -->
### Syntax

```powershell
# Page (default)
Get-JIMActivityChildren -Id <guid> [-Page <int>] [-PageSize <int>]

# All
Get-JIMActivityChildren -Id <guid> -All [-Force] [-PageSize <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes |  | The parent Activity. Accepts pipeline input by property name. |
| `All` | `switch` | Yes (All) |  | Returns every child Activity, fetching page after page. Stops with a warning after 1,000 pages unless `-Force` is given. |
| `Force` | `switch` | No (All) |  | With `-All`, fetches every page however many there are, up to the API's limit of 1,000,000 rows. |
| `Page` | `int` | No (Page) | `1` | The page of child Activities to return. At least 1. |
| `PageSize` | `int` | No | `50` | How many child Activities per page. From 1 to 100. |

### Output

Each child Activity's header.

Properties as under [Get-JIMActivity](#get-jimactivity).
<!-- END GENERATED: Get-JIMActivityChildren -->

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
- [Execution Items](execution-items.md): cmdlets for what happened to each object in a Run Profile execution, and what caused it
- [Run Profiles](run-profiles.md): cmdlets for managing and executing synchronisation Run Profiles
- [Schedules](schedules.md): cmdlets for managing automated Run Profile schedules
