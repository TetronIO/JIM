---
title: Connected Systems
---

# Connected Systems

The Connected Systems cmdlets manage the full lifecycle of Connected Systems in JIM: creating, configuring, clearing and deleting systems, and their server certificates and connector definitions. Importing a schema and choosing Object Types and attributes are on [Connected System Schema](connected-system-schema.md), auxiliary object classes on [Auxiliary Object Classes](connected-system-auxiliary-classes.md), partitions and containers on [Partitions and Containers](partitions-and-containers.md), the objects in the Connector Space on [Connected System Objects](connected-system-objects.md), and the changes waiting to be exported on [Pending Exports](pending-exports.md). Most cmdlets support pipeline input for scripting and automation workflows.

---

## Get-JIMConnectedSystem

Retrieves one or more Connected Systems, their object types, or a deletion impact preview.

### Syntax

```powershell
# List (default)
Get-JIMConnectedSystem [-Name <string>]

# ById
Get-JIMConnectedSystem -Id <int>

# ObjectTypes
Get-JIMConnectedSystem -Id <int> -ObjectTypes

# DeletionPreview
Get-JIMConnectedSystem -Id <int> -DeletionPreview
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById, ObjectTypes, DeletionPreview) | | Connected System identifier. Accepts pipeline input by property name. |
| `Name` | `string` | No (List only) | | Filter by name; supports wildcard characters (`*`, `?`) |
| `ObjectTypes` | `switch` | No | `$false` | Returns the object types configured on the Connected System |
| `DeletionPreview` | `switch` | No | `$false` | Returns a deletion impact preview for the Connected System |

### Output

- **List**: Connected System headers with properties such as `Id`, `Name`, `Description`, `Status`, `ObjectCount`, `ConnectorName`, and `ConnectorId`.
- **ById**: the full Connected System, including its nested `Connector` (use `$cs.Connector.Id` for the connector definition ID), configuration state, and a nested `ConfigurationDrift` object (see below).
- **ObjectTypes**: Object type definitions for the specified Connected System.
- **DeletionPreview**: Deletion impact preview with counts and warnings.

### Examples

```powershell title="List all Connected Systems"
Get-JIMConnectedSystem
```

```powershell title="Filter by name using wildcards"
Get-JIMConnectedSystem -Name "HR*"
```

```powershell title="Get a specific Connected System by ID"
Get-JIMConnectedSystem -Id 3
```

```powershell title="Retrieve object types for a Connected System"
Get-JIMConnectedSystem -Id 3 -ObjectTypes
```

```powershell title="Find Connected Systems needing a Full Synchronisation"
Get-JIMConnectedSystem |
    ForEach-Object { Get-JIMConnectedSystem -Id $_.Id } |
    Where-Object { $_.ConfigurationDrift.HasPendingChanges } |
    Select-Object Name, @{n='Changes';e={$_.ConfigurationDrift.ChangeCount}},
                        @{n='Highest';e={$_.ConfigurationDrift.HighestChangeClass}}
```

#### ConfigurationDrift (ById only)

Whether the configuration has changed in a way that needs a Full Synchronisation to take effect. Only Sync-affecting
and Destructive changes count, so a rename never registers here.

| Property | Type | Description |
|----------|------|-------------|
| `HasPendingChanges` | `bool` | Qualifying changes have been recorded since the last completed Full Synchronisation |
| `IsDeterminable` | `bool` | The question has a meaningful answer; see the caution below |
| `NeverFullySynchronised` | `bool` | No Full Synchronisation has ever completed, so there is no reference point |
| `TrackingDisabled` | `bool` | Configuration change tracking is off, so JIM holds no record of what changed |
| `LastFullSynchronisation` | `datetime?` | When the last completed Full Synchronisation started, or `$null` |
| `MostRecentChange` | `datetime?` | When the most recent qualifying change was recorded, or `$null` |
| `ChangeCount` | `int` | How many qualifying changes there are |
| `HighestChangeClass` | `string` | `Cosmetic`, `SyncAffecting` or `Destructive`; `NotClassified` when there are no changes |

### Get-JIMConnectedSystemPasswordPolicy

Reports what the Connected System itself said it will accept, read during a previous connection. Nothing here
opens a new connection or changes anything.

```powershell
Get-JIMConnectedSystemPasswordPolicy -Id 3
Get-JIMConnectedSystem -Id 3 | Get-JIMConnectedSystemPasswordPolicy
```

| Property | Type | Description |
|----------|------|-------------|
| `discovered` | `datetime?` | When JIM last read this from the system |
| `minimumLength` | `int?` | The shortest password the system will accept |
| `complexityRequired` | `bool?` | Whether the system enforces a complexity rule |
| `requiredCharacterClassCount` | `int?` | How many character categories a password must draw on |
| `recognisedCharacterClasses` | `string[]` | The categories this system counts towards that rule |
| `passwordHistoryLength` | `int?` | How many previous passwords it remembers and refuses |
| `maximumPasswordAgeDays` | `int?` | How long a password may live |
| `minimumPasswordAgeDays` | `int?` | How soon it may be changed again |
| `policyOverrideSignal` | `string` | `Absent`, `Present` or `CouldNotDetermine`: whether some accounts may be governed by a policy other than this one |
| `furtherChecksApply` | `bool` | Whether the directory applies checks JIM cannot see, such as a dictionary check, so a password satisfying every figure can still be refused |
| `discoveryOutcome` | `string?` | `Read`, `NotPublished`, `ConfigurationNotReadable` or `NoPolicyConfigured`; `$null` when nothing has been read yet |
| `hasAnyDiscoveredConstraint` | `bool` | Whether JIM discovered anything at all |

!!! warning "A null means JIM could not read that rule, not that no such rule exists"
    A directory withholds what a caller may not see by omitting it rather than refusing, so a null minimum
    length does not mean any length is acceptable. Check `hasAnyDiscoveredConstraint` before treating the
    figures as a description of what the system will accept. Where `policyOverrideSignal` is `Present` or
    `CouldNotDetermine`, the figures are a floor rather than a guarantee, because some accounts may be governed
    by a stricter policy (Active Directory's Fine-Grained Password Policies, OpenLDAP's per-entry policy
    subentries, 389 Directory Server's subtree policies). Where nothing was discovered, `discoveryOutcome` says
    why, and whether there is anything to do about it: `NotPublished` means the directory has nothing to read,
    `ConfigurationNotReadable` means the account JIM connects as needs read access to the server configuration.

### Get-JIMConnectedSystemPasswordSynchronisation

Reports whether a Connected System receives synchronised passwords, and the settings governing how hard JIM
tries to deliver them. A system that has never been configured is reported with `configured` set to `$false`
and JIM's defaults in the remaining fields, so a script comparing systems does not have to special-case the
untouched ones.

```powershell
Get-JIMConnectedSystemPasswordSynchronisation -Id 3
Get-JIMConnectedSystem -Id 3 | Get-JIMConnectedSystemPasswordSynchronisation
```

| Property | Type | Description |
|----------|------|-------------|
| `Configured` | `bool` | Whether Password Synchronisation has been configured here at all |
| `ConnectorSupportsPasswordSet` | `bool` | Whether this Connector can set passwords; `$false` means it cannot be configured |
| `Enabled` | `bool` | Whether queued password changes are delivered |
| `TargetObjectTypeId` | `int` | The Connected System Object Type that receives passwords |
| `TargetObjectTypeName` | `string` | Its name |
| `MaxRetries` | `int` | Attempts before a change is parked; `0` means use JIM's default |
| `EffectiveMaxRetries` | `int` | The retry count actually applied |
| `RetryBackoffBase` | `timespan` | The first retry interval; `0` means use JIM's default |
| `EffectiveRetryBackoffBase` | `timespan` | The backoff base actually applied |
| `RequireSecureTransport` | `bool` | Whether an unconfirmed-encryption connection is refused. Reported here, set on the Connected System with `Set-JIMConnectedSystem` |
| `EffectiveTimeToLive` | `timespan` | How long a queued change waits before JIM expires it |

Auditing which systems are switched on:

```powershell
Get-JIMConnectedSystem | ForEach-Object {
    $p = Get-JIMConnectedSystemPasswordSynchronisation -Id $_.Id
    [PSCustomObject]@{ System = $_.Name; Configured = $p.Configured; Enabled = $p.Enabled }
}
```

### Set-JIMConnectedSystemPasswordSynchronisation

Creates or updates the configuration. Omitted parameters leave the stored value unchanged; `-TargetObjectType`
is required when creating one.

```powershell
# Stage the configuration without switching it on
Set-JIMConnectedSystemPasswordSynchronisation -Id 3 -TargetObjectType 7 -Enabled $false

# Switch it on during the change window, delivering everything queued while it was off
Set-JIMConnectedSystemPasswordSynchronisation -Id 3 -Enabled $true -ChangeReason 'CHG0041288'

# Allow ten attempts before a change is parked
Set-JIMConnectedSystemPasswordSynchronisation -Id 3 -MaxRetries 10 -PassThru
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `-Id` | `int` | The Connected System (required; also accepts a Connected System from the pipeline as `-InputObject`) |
| `-Enabled` | `bool` | Whether to deliver queued password changes. Enabling delivers what accumulated while it was off |
| `-TargetObjectType` | `int` | The Object Type that receives passwords; must be selected for synchronisation. Alias: `-TargetObjectTypeId` |
| `-MaxRetries` | `int` | Attempts before parking a change; `0` uses JIM's default |
| `-RetryBackoffBase` | `timespan` | The first retry interval; `0` uses JIM's default |
| `-ChangeReason` | `string` | Recorded against the Connected System's configuration change history |
| `-PassThru` | `switch` | Return the updated configuration |

!!! note "Require Secure Transport is set on the Connected System"
    It governs every password JIM sends to the system, not only synchronised ones, so it is set with
    `Set-JIMConnectedSystem -Id 3 -RequireSecureTransport` and turned off with
    `-RequireSecureTransport:$false`. It is reported here as well, because it governs this feature too.

!!! note "There is no Remove cmdlet, and that is deliberate"
    Removing a configuration would discard every password change queued against it. Disabling it keeps them,
    and is reversible, so `-Enabled $false` is the supported way to stop delivery.

#### Stranded-value sweep (ById only)

Whether a stranded-value sweep is armed following a Connector Space clear, and what it is waiting for. See
[Clearing the connector space](../configuration/connector-space.md#clearing-the-connector-space).

| Property | Type | Description |
|----------|------|-------------|
| `StrandedValueSweepArmedAt` | `datetime?` | When the Connector Space was last cleared, or `$null` if no sweep is armed |
| `LastSuccessfulFullImportCompletedAt` | `datetime?` | When the most recent Full Import of this Connected System completed successfully, or `$null` if none ever has |

The sweep runs at the first Full Synchronisation after `LastSuccessfulFullImportCompletedAt` is later than
`StrandedValueSweepArmedAt`. A Full Synchronisation run before that leaves the arming in place and states so
on its Activity.

```powershell title="Find systems with a stranded-value sweep waiting on a Full Import"
Get-JIMConnectedSystem |
    ForEach-Object { Get-JIMConnectedSystem -Id $_.Id } |
    Where-Object {
        $_.StrandedValueSweepArmedAt -and
        (-not $_.LastSuccessfulFullImportCompletedAt -or $_.LastSuccessfulFullImportCompletedAt -le $_.StrandedValueSweepArmedAt)
    } |
    Select-Object Name, StrandedValueSweepArmedAt, LastSuccessfulFullImportCompletedAt
```

!!! warning "Check `IsDeterminable` before treating `HasPendingChanges` as false"
    `HasPendingChanges` is also `$false` when JIM cannot tell: when the Connected System has never completed a Full
    Synchronisation, and when configuration change tracking is switched off. Scripts that gate a run on
    `-not $_.ConfigurationDrift.HasPendingChanges` will skip those systems silently. Test `IsDeterminable` first.

```powershell title="Preview the impact of deleting a Connected System"
Get-JIMConnectedSystem -Id 3 -DeletionPreview
```

---

## New-JIMConnectedSystem

Creates a new Connected System.

### Syntax

```powershell
New-JIMConnectedSystem [-Name] <string> -ConnectorDefinitionId <int>
    [-Description <string>] [-ChangeReason <string>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes (Position 0) | | Display name for the Connected System |
| `ConnectorDefinitionId` | `int` | Yes | | Identifier of the connector definition to use |
| `Description` | `string` | No | | Optional description |
| `ChangeReason` | `string` | No | | Optional reason ("commit message") recorded with this change and shown in the configuration change history. Maximum 2000 characters. |
| `PassThru` | `switch` | No | `$false` | Returns the created Connected System Object |

### Output

When `-PassThru` is specified, returns the newly created Connected System Object. Otherwise, no output.

### Examples

```powershell title="Create a Connected System"
New-JIMConnectedSystem -Name "Active Directory" -ConnectorDefinitionId 1
```

```powershell title="Create and capture the result"
$cs = New-JIMConnectedSystem "HR Database" -ConnectorDefinitionId 2 -Description "Primary HR source" -PassThru
```

### Notes

- Supports `ShouldProcess` (Medium impact). Use `-WhatIf` or `-Confirm` to preview or prompt before creation.

---

## Set-JIMConnectedSystem

Updates the configuration of an existing Connected System.

### Syntax

```powershell
# ById (default)
Set-JIMConnectedSystem -Id <int> [-Name <string>] [-Description <string>]
    [-SettingValues <hashtable>] [-MaxExportParallelism <int>]
    [-RequireSecureTransport] [-InitialPasswordTimeToLive <timespan>]
    [-UnresolvedReferenceHandling <string>] [-PassThru]

# ByInputObject
Set-JIMConnectedSystem -InputObject <PSCustomObject> [-Name <string>]
    [-Description <string>] [-SettingValues <hashtable>]
    [-MaxExportParallelism <int>] [-RequireSecureTransport]
    [-InitialPasswordTimeToLive <timespan>] [-UnresolvedReferenceHandling <string>]
    [-ChangeReason <string>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | Connected System identifier |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | Connected System Object from the pipeline |
| `Name` | `string` | No | | New display name |
| `Description` | `string` | No | | New description |
| `SettingValues` | `hashtable` | No | | Connector-specific settings. Keys are setting IDs; values are hashtables with `stringValue`, `intValue`, or `checkboxValue`. |
| `MaxExportParallelism` | `int` | No | | Maximum number of parallel export threads (1 to 16). Leave unset to let the connector recommend a conservative value (the LDAP Connector recommends 2 for capable directories, those tuned to a high Export Concurrency); JIM stays sequential (1) if the connector offers no recommendation. An explicitly set value always takes precedence. |
| `RequireSecureTransport` | `switch` | No | `$false` | Refuse to send a password to this Connected System over a connection JIM cannot confirm is encrypted. Governs every password JIM sends here: the first password on a Connected System Object it provisions, one set by hand, and a synchronised password change. Nothing is discarded when JIM refuses; queued changes wait and Connected System Objects stay owed their first password. Turn it off with `-RequireSecureTransport:$false`. See [Passwords](../concepts/password-synchronisation.md). |
| `InitialPasswordTimeToLive` | `timespan` | No | 7 days | How long a Connected System Object provisioned into this Connected System stays owed an initial password before JIM records an expiry and stops trying. Raise it ahead of a planned outage longer than the current window; Connected System Objects provisioned meanwhile otherwise expire without a password. See [Passwords](../concepts/passwords.md#how-long-jim-keeps-trying). |
| `UnresolvedReferenceHandling` | `string` | No | `Error` | How import-time reference values that cannot be resolved to a Connected System Object are treated: `Error`, `Warn`, or `Ignore`. See [Unresolved reference handling](../configuration/unresolved-references.md). |
| `ChangeReason` | `string` | No | | Optional reason ("commit message") recorded with this change and shown in the configuration change history. Maximum 2000 characters. |
| `PassThru` | `switch` | No | `$false` | Returns the updated Connected System Object |

### Output

When `-PassThru` is specified, returns the updated Connected System Object. Otherwise, no output.

### Examples

```powershell title="Rename a Connected System"
Set-JIMConnectedSystem -Id 3 -Name "AD Production"
```

```powershell title="Update connector settings"
Set-JIMConnectedSystem -Id 3 -SettingValues @{
    1 = @{ stringValue = "ldaps://dc01.example.com" }
    2 = @{ intValue = 636 }
    3 = @{ checkboxValue = $true }
}
```

```powershell title="Pipeline input from Get-JIMConnectedSystem"
Get-JIMConnectedSystem -Id 3 | Set-JIMConnectedSystem -MaxExportParallelism 8 -PassThru
```

```powershell title="Update a setting and record why (shown in the change history)"
Set-JIMConnectedSystem -Id 3 -Description "Point at DR domain controller" -ChangeReason "Failover for DC maintenance (CHG0101)"
```

### Notes

- Supports `ShouldProcess` (Medium impact). Use `-WhatIf` or `-Confirm` to preview or prompt before changes.

---

## Remove-JIMConnectedSystem

Deletes a Connected System and all its associated data.

### Syntax

```powershell
# ById (default)
Remove-JIMConnectedSystem -Id <int> [-DeleteImmediately] [-ChangeReason <string>]
    [-PreviewActivityId <guid>] [-PassThru] [-Force]

# ByInputObject
Remove-JIMConnectedSystem -InputObject <PSCustomObject> [-DeleteImmediately] [-ChangeReason <string>]
    [-PreviewActivityId <guid>] [-PassThru] [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | Connected System identifier. Accepts pipeline input by property name. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | Connected System object from the pipeline |
| `DeleteImmediately` | `switch` | No | `$false` | Deletes immediately and **keeps** the attribute values the system contributed, instead of deprovisioning through synchronisation. The kept values lose their provenance, so nothing can ever recall them, and downstream systems are not corrected. |
| `ChangeReason` | `string` | No | | Reason for the deletion, recorded on its Activity and on the configuration change history tombstone |
| `PreviewActivityId` | `guid` | No | | The deletion preview this deletion was made after reading, as returned by [`New-JIMConfigurationChangePreview -ConnectedSystemId <id> -Deletion`](previews.md#new-jimconfigurationchangepreview). Recorded on the deletion's Activity. JIM refuses the deletion, deleting nothing, when the id is not a deletion preview of this same Connected System. A preview that a later run or configuration change has overtaken is still recorded: the deletion goes ahead, its Activity says the preview was out of date, and the cmdlet writes a warning naming what overtook it. |
| `PassThru` | `switch` | No | `$false` | Returns the deletion result for a deletion that completes immediately |
| `Force` | `switch` | No | `$false` | Suppresses the confirmation prompt, and the lookup of headline counts the prompt uses |

By default the deletion deprovisions the system through synchronisation: the system is fenced and a background run takes every Connected System Object through the same obsoletion a synchronisation disconnect would, so other systems take over the values they also contribute, values nothing else contributes are cleared, Deletion Rules are evaluated, and corrections are staged for the other Connected Systems, before the system itself is deleted. To see all of that before it happens, run [`New-JIMConfigurationChangePreview -Deletion`](previews.md#new-jimconfigurationchangepreview) first and pass its `ActivityId` to `-PreviewActivityId`.

If a deprovisioning run fails partway, the system stays fenced (its Status remains `Deleting`). Running the cmdlet again retries the run from its checkpoint; running it with `-DeleteImmediately` finishes the deletion immediately, abandoning the remaining deprovisioning work.

### Output

When the deletion queues (always the case by default), returns a tracking object:

| Property | Description |
|----------|-------------|
| `ActivityId` | The deletion Activity's id; monitor it with `Get-JIMActivity` |
| `WorkerTaskId` | The queued Worker Task's id |
| `Outcome` | `QueuedAsBackgroundJob`, or `QueuedAfterSync` when a running synchronisation delays it |
| `ConnectedSystemObjectCount`, `ContributedValueCount`, `ContributedValueObjectCount` | Headline counts from the deletion's impact summary, or `$null` when `-Force` skipped the lookup |

When an immediate deletion completes synchronously, nothing is returned unless `-PassThru` is specified, in which case the deletion result (`Outcome`, `ActivityId`) is returned.

### Examples

```powershell title="Deprovision a Connected System with confirmation"
Remove-JIMConnectedSystem -Id 3
```

```powershell title="Preview the deletion, then delete and record the preview"
$preview = New-JIMConfigurationChangePreview -ConnectedSystemId 3 -Deletion -Wait
$preview.ImpactCounts | Format-Table TransitionType, ObjectCount
Remove-JIMConnectedSystem -Id 3 -PreviewActivityId $preview.ActivityId -ChangeReason "Decommissioned (CHG0123)"
```

```powershell title="Deprovision without confirmation and follow the run"
$tracking = Remove-JIMConnectedSystem -Id 3 -Force
Get-JIMActivity -Id $tracking.ActivityId
```

```powershell title="Delete immediately, keeping the values the system contributed"
Remove-JIMConnectedSystem -Id 3 -DeleteImmediately -Force
```

```powershell title="Delete every Connected System matching a name pattern, immediately"
# -Name supports wildcards, so this deletes ALL matching Connected Systems and
# their connector spaces. Run it without -Force first to confirm the matches.
Get-JIMConnectedSystem -Name "Decommissioned*" | Remove-JIMConnectedSystem -DeleteImmediately -Force
```

### Notes

- Supports `ShouldProcess` (High impact). Without `-Force`, you will be prompted for confirmation, and the prompt states the headline impact of the chosen mode.
- Deprovisioning always runs as a background job. An immediate deletion of a small Connected System (fewer than 1,000 objects) completes straight away; a larger one is queued as a background job.

---

## Get-JIMConnectedSystemServerCertificate

Reads the certificate the Connected System's server is presenting, without storing anything.

JIM connects to the endpoint the Connected System is configured for, purely to look at the certificate the server offers, and refuses the connection. The endpoint is always worked out by the Connected System's own connector from that system's settings; it is never named directly, so this cannot be used to make JIM connect to an address of your choosing.

### Syntax

```powershell
Get-JIMConnectedSystemServerCertificate -ConnectedSystemId <int> [-SettingValues <hashtable>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier. Accepts a Connected System from the pipeline. |
| `SettingValues` | `hashtable` | No | | Connectivity settings entered but not yet saved, keyed by Connector Definition Setting identifier. |

### Output

An object with a `certificate` property and a `readAt` timestamp. The certificate carries `host`, `port`, `subject`, `issuer`, `subjectAlternativeNames`, `validFrom`, `validTo`, `thumbprint`, `signatureAlgorithm`, `isSelfSigned`, `issuerThumbprint`, `isIssuerCertificateAvailable`, `chain`, `isChainComplete`, `missingIssuer`, `rootThumbprint`, `rootSubject`, `failureReason` and `remediation`.

`chain` lists the certificate chain JIM found, the server's own certificate first and the root (where JIM reached one) last. Each entry carries `subject`, `issuer`, `thumbprint`, `validFrom`, `validTo`, `isCertificateAuthority`, `isSelfSigned`, `source` and `downloadedFrom`. `source` is `SentByServer`, `Downloaded` (from the address in the certificate below it, given in `downloadedFrom`), `JimCertificateStore` or `OperatingSystem`. When JIM could not reach a root, `isChainComplete` is `$false` and `missingIssuer` names the certificate it could not find.

`failureReason` is one of `None`, `UntrustedIssuer`, `NameMismatch`, `Expired`, `NotYetValid`, `NoCertificatePresented`, `InvalidChain` or `Unknown`. Only `UntrustedIssuer` is fixed by trusting a certificate. `InvalidChain` means a certificate authority in the chain has expired, is not marked as one, or has a signature that does not verify, which has to be fixed on the server.

### Examples

```powershell title="Read the certificate the configured server presents"
Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42
```

```powershell title="Show the identifying details and which check it fails"
Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 |
    Select-Object -ExpandProperty certificate |
    Select-Object host, subject, thumbprint, failureReason
```

```powershell title="List the certificate chain and where each certificate came from"
(Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42).certificate.chain |
    Format-Table subject, source, downloadedFrom
```

```powershell title="Read an endpoint that has been entered but not saved"
Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 -SettingValues @{ 40 = 'https://hr.corp.local/scim/v2' }
```

### Notes

- **Why `-SettingValues` exists.** JIM does not save settings that fail validation, and a certificate JIM does not trust is a validation failure. A Connected System being configured for the first time therefore has the address you typed and nothing in the database, so without these JIM would look at the endpoint last saved, or report that the system is not configured for an encrypted connection. Setting identifiers come from `Get-JIMConnectorDefinition`. The values are never persisted, and values for encrypted settings are ignored.
- Reading stores nothing. Trusting the certificate is a separate call to `Approve-JIMConnectedSystemServerCertificate`.

---

## Approve-JIMConnectedSystemServerCertificate

Trusts the certificate the Connected System's server is presenting, adding it to the Trusted Certificates store.

JIM reads the certificate from the server again, checks it against the thumbprint you supply, and adds it through the audited path, so the addition carries an Activity naming who trusted it and why.

### Syntax

```powershell
Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId <int> -Thumbprint <string>
    [-ChangeReason <string>] [-SettingValues <hashtable>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier. Accepts a Connected System from the pipeline. |
| `Thumbprint` | `string` | Yes | | The thumbprint being trusted, as read from the server: the server's own certificate, or any certificate in its chain. Spaces and colons between the pairs are ignored. |
| `ChangeReason` | `string` | No | | Reason recorded on the audit Activity. JIM records a sentence naming the Connected System when none is given. |
| `SettingValues` | `hashtable` | No | | Connectivity settings entered but not yet saved, keyed by Connector Definition Setting identifier. |
| `PassThru` | `switch` | No | `$false` | Returns the outcome, including the certificate as it now sits in the store. |

### Output

When `-PassThru` is specified, returns an object with `outcome` (`Trusted`, `AlreadyTrusted`, `ThumbprintMismatch` or `InvalidChain`), `message`, `certificate`, `storedIntermediates`, `expectedThumbprint` and `presentedThumbprint`. `storedIntermediates` lists any certificate authorities JIM downloaded and stored alongside the one you trusted, because the server does not send them. Otherwise, no output.

### Examples

```powershell title="Trust the certificate you have checked"
Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 -Thumbprint '7B44E1902CF6A83D5518BE7719A0C4D62F8E3B01'
```

```powershell title="Trust the root of the chain, rather than the server's own certificate"
$reading = Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42
$reading.certificate.chain | Format-Table subject, source, downloadedFrom

Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 `
    -Thumbprint $reading.certificate.rootThumbprint `
    -ChangeReason 'Unblocking the HR Cloud connection test.'
```

```powershell title="Trust an endpoint that has been entered but not saved"
Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 `
    -Thumbprint '7B44E1902CF6A83D5518BE7719A0C4D62F8E3B01' `
    -SettingValues @{ 40 = 'https://hr.corp.local/scim/v2' } -PassThru
```

### Notes

- **Check the thumbprint against the server's administrator before running this.** It is the only thing standing between an unattended script and trusting whatever is presented.
- **Any certificate in the chain works; the root lasts longest.** JIM accepts the server when any certificate in its chain is in the Trusted Certificates store. `rootThumbprint` is populated when JIM reached the root, and trusting it survives the renewal of every certificate beneath it; trusting the server's own certificate has to be repeated at every renewal. Where `isChainComplete` is `$false`, trust the highest certificate JIM found, or add `missingIssuer` under Admin > Certificates. A self-signed certificate is its own root.
- **A changed certificate stops the action.** If the server is presenting anything other than the thumbprint you named, nothing is trusted and the outcome is `ThumbprintMismatch`, with both values returned so you can compare them. Expected after a renewal; worth investigating otherwise.
- Only an untrusted issuer is fixed by trusting a certificate. An expired certificate has to be renewed on the server, and a name mismatch means connecting by a name the certificate carries. A chain through a broken certificate authority is refused with `InvalidChain` and nothing is trusted.
- Supports `ShouldProcess`.
- Remove a certificate later with `Remove-JIMCertificate`.

---

## Get-JIMConnectorDefinition

Retrieves available connector definitions, including their settings and capabilities.

### Syntax

```powershell
# List all (default)
Get-JIMConnectorDefinition

# By ID
Get-JIMConnectorDefinition -Id <int>

# By name (exact match)
Get-JIMConnectorDefinition -Name <string>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | Connector definition identifier. Accepts pipeline input. |
| `Name` | `string` | Yes (ByName) | | Connector definition name. Must be an exact match. |

### Output

Connector definition objects including name, description, available settings, and supported capabilities (e.g. full import, delta import, export, hierarchy).

### Examples

```powershell title="List all connector definitions"
Get-JIMConnectorDefinition
```

```powershell title="Get a connector definition by name"
Get-JIMConnectorDefinition -Name "JIM File Connector"
```

```powershell title="Get a specific connector definition by ID"
Get-JIMConnectorDefinition -Id 1
```

```powershell title="Find connectors that support delta import"
# The list form returns headers, which carry no capability flags; fetch each
# definition by ID to see what it supports.
Get-JIMConnectorDefinition |
    ForEach-Object { Get-JIMConnectorDefinition -Id $_.Id } |
    Where-Object { $_.SupportsDeltaImport }
```

---

## Get-JIMConnectedSystemCapability

Retrieves the Connector-detected capabilities for a Connected System, e.g. an LDAP directory's type, vendor, DNS host name, and paging support. These are facts read from the target system during a previous connection and persisted by JIM; calling this cmdlet does not open a new connection.

### Syntax

```powershell
Get-JIMConnectedSystemCapability -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier. Alias: `Id`. Accepts pipeline input by property name. |

### Output

Zero or more `PSCustomObject` instances, one per detected capability, each with `Name` and `Value` properties. Empty when the Connector does not detect any capabilities, or when nothing has been detected yet (for example, before the first successful connection).

### Examples

```powershell title="Get the detected capabilities for a Connected System"
Get-JIMConnectedSystemCapability -ConnectedSystemId 1
```

```powershell title="Get capabilities for a named Connected System via pipeline"
Get-JIMConnectedSystem -Name "Active Directory" | Get-JIMConnectedSystemCapability
```

### Notes

- These facts mirror the **Detected** strip on the Connected System's Details tab in the portal; see the [JIM LDAP Connector](../connectors/jim-ldap-connector.md#detected-facts) documentation for what each fact means.

---

## Clear-JIMConnectedSystem

Removes all connector space objects (CSOs) and associated data from a Connected System without deleting the system itself. The Connected System configuration, schema, and Synchronisation Rules are preserved.

The clear runs as a queued background task, tracked by an Activity, exactly like the portal: the cmdlet returns as soon as the task is queued rather than running the deletion inline, so the operation is audited and trackable regardless of which surface started it. Use `-Wait` to block until it has finished.

### Syntax

```powershell
# ById (default)
Clear-JIMConnectedSystem -Id <int> [-KeepChangeHistory] [-Wait] [-Timeout <int>] [-Force]

# ByInputObject
Clear-JIMConnectedSystem -InputObject <PSCustomObject> [-KeepChangeHistory] [-Wait] [-Timeout <int>] [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | Connected System identifier |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | Connected System Object from the pipeline |
| `KeepChangeHistory` | `switch` | No | `$false` | Preserves change history records; by default, change history is also deleted |
| `Wait` | `switch` | No | `$false` | Waits for the queued clear to finish before returning |
| `Timeout` | `int` | No | | Maximum seconds to wait when `-Wait` is supplied. Omit to wait indefinitely |
| `Force` | `switch` | No | `$false` | Suppresses the confirmation prompt |

### Output

A tracking object for the queued clear:

| Property | Type | Description |
|----------|------|--------------|
| `ActivityId` | `guid` | The clear Activity's id; monitor it with `Get-JIMActivity` |
| `TaskId` | `guid` | The queued Worker Task's id |
| `Message` | `string` | A human-readable confirmation naming the Connected System |

### Examples

```powershell title="Clear a Connected System with confirmation"
Clear-JIMConnectedSystem -Id 3
```

```powershell title="Clear without confirmation, keeping history"
Clear-JIMConnectedSystem -Id 3 -KeepChangeHistory -Force
```

```powershell title="Clear and wait for it to finish"
Clear-JIMConnectedSystem -Id 3 -Force -Wait
```

```powershell title="Pipeline: clear a system by name"
Get-JIMConnectedSystem -Name "Staging AD" | Clear-JIMConnectedSystem -Force
```

### Notes

- Supports `ShouldProcess` (High impact). Without `-Force`, you will be prompted for confirmation.
- Removes all CSOs, attribute values, Pending Exports, and deferred references from the Connected System.
- Metaverse Objects are **not** deleted by the clear itself; their links to this Connected System are severed.
- By default, change history is also deleted. Use `-KeepChangeHistory` to retain it for auditing purposes.
- Without `-Wait`, the cmdlet returns as soon as the clear is queued; a script that immediately re-imports, or reads the Connector Space back, races the clear task.
- The clear records which Metaverse Objects were joined and arms the stranded-value sweep, which runs automatically at this Connected System's next Full Synchronisation once a Full Import has completed successfully: it recalls any Metaverse attribute value the system contributed whose object never returned, and applies that object type's Deletion Rule to objects that never returned. Run a Full Import before that synchronisation so objects that do return can reclaim their values and avoid the Deletion Rule firing on them. If far fewer objects return than were cleared, the sweep refuses rather than deleting most of the population; see `Sync.PostClearReconciliation.MaxMissingPercent` in [`Get-JIMServiceSetting`](service-settings.md). See [Clearing the connector space](../configuration/connector-space.md#clearing-the-connector-space).

---

## Get-JIMConnectedSystemDeletionPreview

Retrieves a preview of the impact of deleting a Connected System, including counts of affected objects and warnings.

### Syntax

```powershell
Get-JIMConnectedSystemDeletionPreview -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier. Alias: `Id`. Accepts pipeline input by property name. |

### Output

A deletion impact preview object with counts of connector space objects, Pending Exports, Synchronisation Rules, and other dependent data that would be removed.

### Examples

```powershell title="Preview deletion impact"
Get-JIMConnectedSystemDeletionPreview -ConnectedSystemId 3
```

```powershell title="Pipeline: preview before deleting"
Get-JIMConnectedSystem -Id 3 | Get-JIMConnectedSystemDeletionPreview
```

```powershell title="Check all systems for deletion impact"
Get-JIMConnectedSystem | ForEach-Object {
    $preview = $_ | Get-JIMConnectedSystemDeletionPreview
    [PSCustomObject]@{
        Name = $_.Name
        CSOCount = $preview.ConnectedSystemObjectCount
        SyncRules = $preview.SyncRuleCount
    }
}
```

---

## See also

- [Connected Systems](../configuration/connected-systems.md): what Connected Systems are, the connector space, partitions and containers, and common workflows
- [Connected System Schema](connected-system-schema.md): import a Connected System's schema, and choose its Object Types and attributes
- [Auxiliary Object Classes](connected-system-auxiliary-classes.md): the auxiliary classes an LDAP Connected System's Object Types carry
- [Partitions and Containers](partitions-and-containers.md): import a Connected System's hierarchy, and choose which partitions and containers JIM imports from
- [Connected System Objects](connected-system-objects.md): read the objects in the Connector Space, and set their passwords
- [Pending Exports](pending-exports.md): read the changes waiting to be written to a Connected System
- [Run Profiles](run-profiles.md): execute import, sync, and export operations on Connected Systems
- [Synchronisation Rules](synchronisation-rules.md): define attribute mappings and scoping for Connected System synchronisation, including the initial password set on provisioned accounts
- [Connection](connection.md): establish a session before using these cmdlets
