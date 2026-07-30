# Configuration Change Classification

> How JIM decides whether a configuration change is destructive, sync-affecting, or cosmetic, and what every developer must do when adding or altering a configuration property.

- **Status:** Done
- **Applies to:** every configuration property captured in a `ConfigurationSnapshot`
- **Related:** [`plans/CONFIGURATION_CHANGE_PREVIEW.md`](plans/CONFIGURATION_CHANGE_PREVIEW.md) (the framework this feeds), issue #827

## Why this exists

An administrator saving a Synchronisation Rule can be renaming it or can be switching its Outbound Deprovision Action from Disconnect to Delete. The first is harmless; the second can delete thousands of accounts on the next synchronisation run. JIM must be able to tell those apart **before** it decides whether to warn, to demand confirmation, or to say nothing at all.

Classification is what makes that possible. It is deliberately **property-level, not page-level**: a single edit page mixes harmless fields with dangerous ones, so classifying by page would either cry wolf on every rename or stay silent on the dangerous field sitting next to it. A warning that fires on cosmetic edits is worse than no warning, because administrators learn to dismiss it.

## The model

Every configuration property is assigned one of three classes.

| Class | Meaning | Save-time behaviour | Examples |
|---|---|---|---|
| **A: Destructive** | Can cascade deletions or mass deprovisioning | Preview and a count-stating confirmation are **mandatory**; the administrator must consent to the stated consequences | Outbound Deprovision Action, Metaverse Object Type Deletion Rule, Connected System Object Type deselection |
| **B: Sync-affecting** | Changes synchronisation outcomes without directly destroying data | Preview is **offered**; the save is never blocked | Scoping criteria, Object Matching Rules, Attribute Flow, schema selection |
| **C: Cosmetic / operational** | No effect on synchronisation outcomes | Never prompts; the save proceeds untouched | Names, descriptions, icons, schedule timing, page sizes, UI preferences |

**A change is classified by the highest class among the properties that actually changed.** A save that renames a Synchronisation Rule *and* switches its Outbound Deprovision Action is Class A. A save touching only Class C properties never produces a preview, an acknowledgement, or a confirmation.

### Where the line sits between A and B

The distinction is **directness**, not severity.

- Class A means the change itself, once applied, causes objects to be deleted or deprovisioned. Nothing else has to go wrong.
- Class B means the change alters what synchronisation will do, and a later run may well remove objects as a consequence, but the change on its own does not.

Switching a Metaverse Object Type's Deletion Rule is Class A: deletion eligibility changes the moment it is saved. Narrowing a Synchronisation Rule's scope is Class B: objects fall out of scope, and what happens to them then depends on the Inbound Out-of-Scope Action (itself Class A).

When genuinely torn, choose the higher class. Over-warning costs an administrator one dialog; under-warning costs them their data.

## How classification is computed

Classification is a pure function over machinery JIM already has. Nothing new is diffed or intercepted.

1. `ConfigurationSnapshotService` builds a `ConfigurationSnapshot` of the object being saved, a tree of nodes each carrying a stable string `Key` (`"outboundDeprovisionAction"`, `"deletionRule"`, and so on).
2. `ConfigurationDiffService.Diff(old, new)` returns the nodes that actually changed.
3. `ConfigurationChangeClassifier` maps each changed node's key to its class and returns the highest.
4. `ConfigurationChangeCaptureService` persists the result on `Activity.ConfigurationChangeClass`.

Because the class is persisted, downstream consumers (the "Configuration changed since last full synchronisation" indicator, apply-time acknowledgement, and later the preview adapters) read a single column rather than deserialising and re-diffing every stored snapshot. It also records what JIM judged at the time, which a later re-diff could not reproduce if the classification changes.

Classification keys off the **snapshot node key**, not the C# property name or the REST DTO. The snapshot is the one representation shared by every write surface (portal, REST API, PowerShell), so classifying there means all three surfaces are covered by construction.

## What you must do as a developer

**Adding a new configuration property, or changing an existing one, is not complete until it is classified.**

1. Add the property to its `ConfigurationSnapshotService` builder as usual, giving it a stable key.
2. Add that key to the matching table in `ConfigurationChangeClassifier`, choosing A, B or C using the model above.
3. Add a row to the relevant table in **this document**, with the same class and a one-line reason.
4. **If you chose Class A**, add curated copy to `ConfigurationChangeConsequences` saying what the change will do, in the terms an administrator would use. Write it direction-aware: the same property switched back is the opposite consequence, and warning about deletion when the administrator has just prevented it is how a dialog earns the reflex dismissal that makes it useless. The class stays destructive in both directions, so that what was consented to and what the change history records cannot disagree; only the wording moves.
5. Run the unit tests. `ConfigurationChangeClassificationCompletenessTests` enumerates every key the snapshot service can emit and fails when one has no explicit classification, naming the key. Its sibling assertion fails a Class A key that has no stated consequence.

There is **no default class.** An unclassified key fails the build rather than being silently assumed harmless or silently assumed dangerous. This is deliberate: a default would let the map rot, and a rotten map produces a framework that warns about the wrong things, which is worse than one that does not warn at all.

If you are changing an existing property's *behaviour* such that its class should change (for example, making a previously advisory setting actually destructive), update the class and this document in the same change, and say why in the commit message.

### Choosing a class: a short checklist

Ask, in order:

1. **Does saving this cause objects to be deleted, disconnected, or deprovisioned?** If yes, Class A.
2. **Does it change which objects synchronisation touches, how they join, or what values flow?** If yes, Class B.
3. **Would a synchronisation run produce byte-identical outcomes before and after?** If yes, Class C.

Performance and recording settings (page sizes, parallelism, verbosity, history retention) are Class C: they change how synchronisation runs, not what it produces.

## Classification by object type

Nine of the fourteen snapshot object types do not participate in synchronisation at all. These are classified **wholly Class C** at object-type level, with the reason recorded here. Per-key tables are not kept for them, because there is no property within them that could be anything other than Class C.

| Object type | Class | Reason |
|---|---|---|
| Schedule (and its steps) | C | Determines *when* work runs, never what it does. A schedule change cannot alter a synchronisation outcome. |
| Trusted Certificate | C | Transport trust; affects whether a Connected System can be reached, not what synchronisation decides. |
| API Key | C | Authentication credential for the REST API; no bearing on synchronisation. |
| Role | C | Authorisation within JIM's own portal and API; no bearing on synchronisation. |
| Predefined Search (and criteria) | C | A saved view over Metaverse Objects. Read-only; changes what an administrator sees, never what synchronisation does. |
| Connector Definition (and capabilities, settings, files) | C | Package metadata describing what a connector can do. Not administrator-set configuration; changes only when a connector is upgraded. |
| Example Data Set | C | Test data generation input; never consulted by synchronisation. |
| Example Data Template (and object types, attributes, referenced sets) | C | As above. Its *execution* is an operational activity, not a configuration change. |
| Service Setting (structural fields) | C | The `key`, `displayName`, `category`, `valueType`, `defaultValue` and `overridden` nodes are metadata. Only the `value` node carries meaning, and it is classified by setting key (see below). |

The remaining five types are classified per key.

## Synchronisation Rule

| Key | Class | Reason |
|---|---|---|
| `name` | C | Label only. |
| `description` | C | Label only. |
| `direction` | B | Reverses which way attribute values flow. |
| `enabled` | B | Stops or starts the rule contributing; downstream removal depends on the out-of-scope and deprovision actions. |
| `provisionToConnectedSystem` | B | Governs whether new objects are created in the Connected System. |
| `projectToMetaverse` | B | Governs whether new Metaverse Objects are created. |
| `outboundDeprovisionAction` | **A** | Disconnect to Delete converts deprovisioning into deletion in the Connected System. |
| `inboundOutOfScopeAction` | **A** | Remain Joined to Disconnect disconnects every object that falls out of scope. |
| `enforceState` | B | Changes whether JIM overwrites divergent values in the Connected System. |
| `connectedSystemId` | B | Repoints the rule at a different Connected System. |
| `connectedSystemObjectTypeId` | B | Repoints the rule at a different Connected System Object Type. |
| `metaverseObjectTypeId` | B | Repoints the rule at a different Metaverse Object Type. |

### Attribute Flow (`attributeFlowRules`, `sources`)

| Key | Class | Reason |
|---|---|---|
| `targetMetaverseAttributeId` | B | Changes which attribute receives flowed values. |
| `targetConnectedSystemAttributeId` | B | Changes which attribute receives flowed values. |
| `inboundValueProcessing` | B | Alters the value written (trimming, whitespace handling). |
| `caseNormalisation` | B | Alters the value written. |
| `priority` | B | Changes which contributing rule wins an attribute. |
| `nullIsValue` | B | Decides whether a null clears the target or is ignored. |
| `initialExportOnly` | B | Decides whether the value flows once or on every run. |
| `order` | B | Ordering of mapping sources changes the computed value. |
| `metaverseAttributeId` | B | Changes the source attribute. |
| `connectedSystemAttributeId` | B | Changes the source attribute. |
| `expression` | B | Changes the computed value. |

### Object Matching Rules (`objectMatchingRules`, `sources`)

| Key | Class | Reason |
|---|---|---|
| `order` | B | Changes which matching rule is evaluated first, so which objects join. |
| `caseSensitive` | B | Changes which objects match. |
| `metaverseObjectTypeId` | B | Changes what the rule matches against. |
| `targetMetaverseAttributeId` | B | Changes the attribute matched on, so which objects join. |
| `connectedSystemAttributeId` | B | As above, on the Connected System side. |
| `expression` | B | Changes the computed matching value. |

### Scoping (`objectScopingCriteriaGroups`, `criteria`)

Every scoping key is Class B: scoping determines which objects the rule applies to, so any change moves objects in or out of scope. What then happens to the objects that left is governed by `inboundOutOfScopeAction`, which is Class A.

| Key | Class |
|---|---|
| `type`, `position` | B |
| `childGroups` (nested groups) | B |
| `metaverseAttributeId`, `connectedSystemAttributeId` | B |
| `comparisonType`, `caseSensitive` | B |
| `stringValue`, `intValue`, `longValue`, `decimalValue`, `dateTimeValue`, `boolValue`, `guidValue` | B |

## Connected System

| Key | Class | Reason |
|---|---|---|
| `name` | C | Label only. |
| `description` | C | Label only. |
| `connectorDefinitionId` | B | Changes the connector driving import and export. |
| `objectMatchingRuleMode` | B | Changes how matching rules combine, so which objects join. |
| `unresolvedReferenceHandling` | B | Changes what happens to references that cannot be resolved. |
| `objectMatchingRules` | B | Matching rules held on the system change which objects join. |
| `settingValues` | B | Connector settings drive what the connector reads and writes. |
| `maxExportParallelism` | C | Throughput only; explicitly excluded from preview scope by #827. |

### Run Profiles (`runProfiles`)

| Key | Class | Reason |
|---|---|---|
| `name` | C | Label only. |
| `runType` | B | Changes what the profile does (full versus delta, import versus sync versus export). |
| `pageSize` | C | Throughput only. |
| `filePath` | B | Changes the source data a file-based import reads, so what lands in the connector space. |
| `partitionId` | B | Changes which partition the profile operates on. |

### Connected System schema (`objectTypes`, `attributes`)

| Key | Class | Reason |
|---|---|---|
| `objectTypes.name` | C | Label only. |
| `objectTypes.selected` | **A** | Deselecting an Object Type removes its Connected System Objects and deprovisions what they were joined to (#827 gap G4). |
| `removeContributedAttributesOnObsoletion` | B | Decides whether contributed values are withdrawn when an object obsoletes. |
| `attributes.name` | C | Label only. |
| `attributes.type` | B | Changes how values are interpreted and flowed. |
| `attributes.attributePlurality` | B | Single versus multi-valued changes what flows. |
| `attributes.isExternalId` | B | Changes the anchor used to correlate objects, so join outcomes. |
| `attributes.isSecondaryExternalId` | B | As above. |
| `attributes.writability` | B | Decides whether JIM may export the attribute. |

### Partitions and Containers (`partitions`, `containers`)

| Key | Class | Reason |
|---|---|---|
| `partitions.name` | C | Label only. |
| `partitions.externalId` | C | Identifier recorded from the Connected System, not administrator-set. |
| `partitions.selected` | **A** | Deselecting a partition removes the Connected System Objects imported from it (#827 gap G4). |
| `containers.name` | C | Label only. |
| `containers.externalId` | C | Identifier recorded from the Connected System, not administrator-set. |
| `containers.hidden` | C | Portal display only. |

> **Known gap.** Container *selection* is not currently captured in the Connected System snapshot; only `hidden` is. Until selection is added to `ConfigurationSnapshotService`, the container half of #827 gap G4 cannot be classified or previewed. Capturing it is a prerequisite for the G4 adapter.

## Metaverse Object Type

| Key | Class | Reason |
|---|---|---|
| `name` | C | Label only. |
| `pluralName` | C | Label only. |
| `builtIn` | C | System flag; not administrator-editable. |
| `icon` | C | Portal display only. |
| `deletionRule` | **A** | Governs when a Metaverse Object is deleted; changing it makes objects deletion-eligible immediately (#827 gap G5). |
| `deletionGracePeriod` | **A** | Shortening the period brings forward deletions that were pending (#827 gap G5). |
| `deletionTriggerConnectedSystemIds` | **A** | Changes which system disconnections trigger deletion (#827 gap G5). |
| `attributes`, `attributeId` | B | Binding or unbinding an attribute changes what can flow to objects of this type. |

## Metaverse Attribute

| Key | Class | Reason |
|---|---|---|
| `name` | C | Label only. |
| `type` | B | Changes how values are interpreted and flowed. |
| `attributePlurality` | B | Single versus multi-valued changes what flows. |
| `builtIn` | C | System flag; not administrator-editable. |
| `renderingHint` | C | Portal display only. |
| `metaverseObjectTypes`, `metaverseObjectTypeId` | B | Changes which Object Types the attribute is available to. |
| `standardMappings.standard` | C | Interoperability hint; advisory metadata only. |
| `standardMappings.counterpartName` | C | Interoperability hint; advisory metadata only. |
| `standardMappings.notes` | C | Free text. |

## Service Settings

The Service Setting snapshot's structural nodes are Class C (see the object-type table). The `value` node is classified by the **setting key**, because a Service Setting is a key/value pair whose significance lives entirely in which setting it is.

| Setting key | Class | Reason |
|---|---|---|
| `PartitionValidationMode` | B | Switching Error to Warning lets a Run Profile whose partition is missing proceed and import zero objects, which a full import then treats as everything having disappeared. |
| `SyncPageSize` | C | Throughput only. |
| `VerboseNoChangeRecording` | C | Recording verbosity; no outcome change. |
| `MaintenanceMode` | C | Blocks operations from starting; does not change their outcome. |
| `HistoryRetentionPeriod` | C | Retention of records, not synchronisation behaviour. |
| `ConfigurationChangeRetentionPeriod` | C | As above. |
| `SecurityEventRetentionPeriod` | C | As above. |
| `HistoryCleanupBatchSize` | C | Throughput of the cleanup job. |
| `ChangeTrackingCsoChangesEnabled` | C | Governs what history is recorded, not what synchronisation does. |
| `ChangeTrackingMvoChangesEnabled` | C | As above. |
| `ChangeTrackingConfigurationChangesEnabled` | C | As above. |
| `ChangeTrackingSyncOutcomesLevel` | C | As above. |
| `SsoAuthority` | C | Portal and API authentication. |
| `SsoClientId` | C | As above. |
| `SsoSecret` | C | As above. |
| `SsoApiScope` | C | As above. |
| `SsoClaimType` | C | As above. |
| `SsoMvAttribute` | C | Selects the attribute used to identify the signing-in user; affects sign-in, not synchronisation. |
| `SsoUniqueIdentifierClaimType` | C | As above. |
| `SsoEnableLogOut` | C | As above. |
| `CredentialEncryptionEnabled` | C | Protection of stored credentials at rest. |
| `EncryptionKeyPath` | C | As above. |
| `RateLimitingEnabled` | C | API throughput control. |
| `RateLimitingAuthenticatedRequestsPerMinute` | C | As above. |
| `RateLimitingUnauthenticatedRequestsPerMinute` | C | As above. |
| `ProgressUpdateInterval` | C | Portal refresh cadence. |
| `ServiceName` | C | Instance branding. |

> `PartitionValidationMode` is the one Service Setting worth revisiting. It is Class B because it does not itself remove anything, but it removes a guard that exists to prevent a destructive outcome. If the preview framework later shows administrators reaching for it without understanding the consequence, promote it to Class A.

## Creation and deletion

Classification applies to **updates**, where a diff exists between two snapshots.

- **Create** has no prior snapshot, so nothing is being changed and no existing object is at risk. Creates are recorded with no class.
- **Delete** is inherently destructive and is handled by the shared `ConsequenceConfirmationDialog` and each surface's own deletion-impact evaluation, which state the consequences directly. `CaptureDeletionAsync` records Class A so history can be filtered consistently, but the deletion dialogs, not the classifier, are what gate the action.

Activities predating this feature carry `NotClassified`: their class was never computed and cannot be reconstructed reliably, so the column is left honest rather than backfilled with a guess.

## Enforcement

`ConfigurationChangeClassificationCompletenessTests` (JIM.Worker.Tests) reflects over every key `ConfigurationSnapshotService` can emit and asserts each has an explicit classification, and that every key classified A has curated consequence copy. It runs in every unit pass and in `build-and-test` on every PR, so adding a configuration property without classifying it fails the build with a message naming the key.

This is the same enforcement pattern as `BulkInsertColumnCompletenessTests`, and it exists for the same reason: a hand-maintained map that nothing checks will drift, and the drift is invisible until it produces a wrong answer in front of a customer.

The guard earned its place immediately: the first run caught four keys that a careful manual read of `ConfigurationSnapshotService` had missed (`childGroups` on nested scoping groups, `attributeId` and `metaverseObjectTypeId` on the association collections, and `objectMatchingRules` on Connected Systems). Do not classify by reading the source; let the test tell you what the snapshot actually emits.
