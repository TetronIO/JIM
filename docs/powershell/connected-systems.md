---
title: Connected Systems
---

# Connected Systems

The Connected Systems cmdlets manage the full lifecycle of Connected Systems in JIM: creating and configuring systems, importing schemas and hierarchy, selecting object types and attributes, browsing connector space objects, and reviewing Pending Exports. Most cmdlets support pipeline input for scripting and automation workflows.

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
| `fineGrainedPolicySignal` | `string` | `Absent`, `Present` or `CouldNotDetermine` |
| `hasAnyDiscoveredConstraint` | `bool` | Whether JIM discovered anything at all |

!!! warning "A null means JIM could not read that rule, not that no such rule exists"
    A directory withholds what a caller may not see by omitting it rather than refusing, so a null minimum
    length does not mean any length is acceptable. Check `hasAnyDiscoveredConstraint` before treating the
    figures as a description of what the system will accept. Where `fineGrainedPolicySignal` is `Present` or
    `CouldNotDetermine`, the figures are a floor rather than a guarantee, because some accounts may be governed
    by a stricter policy.

#### Initial password attention (ById only)

How many accounts in the Connected System are waiting on a person over their initial password.

| Property | Type | Description |
|----------|------|-------------|
| `ParkedInitialPasswordCount` | `int?` | Accounts whose target refused the password and which JIM has stopped retrying |
| `ExpiredInitialPasswordCount` | `int?` | Accounts never given an initial password within its time to live |

The two are never summed, because they ask for different things. Parked accounts are released by correcting the
initial password settings on the [Synchronisation Rule](synchronisation-rules.md) that provisioned them and saving;
`Get-JIMSyncRuleInitialPassword` reports what the target actually said. Expired accounts cannot be helped that way at
all and need a password set by other means.

```powershell title="Find the systems with initial password work waiting"
Get-JIMConnectedSystem -All |
    ForEach-Object { Get-JIMConnectedSystem -Id $_.Id } |
    Where-Object { $_.ParkedInitialPasswordCount -or $_.ExpiredInitialPasswordCount } |
    Select-Object Name, ParkedInitialPasswordCount, ExpiredInitialPasswordCount
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
    [-InitialPasswordTimeToLive <timespan>]
    [-UnresolvedReferenceHandling <string>] [-PassThru]

# ByInputObject
Set-JIMConnectedSystem -InputObject <PSCustomObject> [-Name <string>]
    [-Description <string>] [-SettingValues <hashtable>]
    [-MaxExportParallelism <int>] [-InitialPasswordTimeToLive <timespan>]
    [-UnresolvedReferenceHandling <string>]
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
| `InitialPasswordTimeToLive` | `timespan` | No | 7 days | How long an account provisioned into this Connected System stays owed an initial password before JIM records an expiry and stops trying. Raise it ahead of a planned outage longer than the current window; accounts provisioned meanwhile otherwise expire without a password. See [Passwords](../concepts/passwords.md#how-long-jim-keeps-trying). |
| `UnresolvedReferenceHandling` | `string` | No | `Error` | How import-time reference values that cannot be resolved to a Connected System Object are treated: `Error`, `Warn`, or `Ignore`. See [Unresolved reference handling](../configuration/connected-systems.md#unresolved-reference-handling). |
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
Remove-JIMConnectedSystem -Id <int> [-Force] [-PassThru]

# ByInputObject
Remove-JIMConnectedSystem -InputObject <PSCustomObject> [-Force] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | Connected System identifier |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | Connected System Object from the pipeline |
| `Force` | `switch` | No | `$false` | Suppresses the confirmation prompt |
| `PassThru` | `switch` | No | `$false` | Returns the deleted Connected System Object |

### Output

When `-PassThru` is specified, returns the deleted Connected System Object. Otherwise, no output.

### Examples

```powershell title="Delete a Connected System with confirmation"
Remove-JIMConnectedSystem -Id 3
```

```powershell title="Delete without confirmation"
Remove-JIMConnectedSystem -Id 3 -Force
```

```powershell title="Delete every Connected System matching a name pattern"
# -Name supports wildcards, so this deletes ALL matching Connected Systems and
# their connector spaces. Run it without -Force first to confirm the matches.
Get-JIMConnectedSystem -Name "Decommissioned*" | Remove-JIMConnectedSystem -Force
```

### Notes

- Supports `ShouldProcess` (High impact). Without `-Force`, you will be prompted for confirmation.
- Small Connected Systems (fewer than 1,000 objects) are deleted immediately. Large systems are queued as a background job; you can monitor progress in the activities log.

---

## Import-JIMConnectedSystemSchema

Imports (or re-imports) the schema from the connected data source. This discovers available object types and attributes.

### Syntax

```powershell
# ById (default)
Import-JIMConnectedSystemSchema -Id <int> [-PassThru]

# ByInputObject
Import-JIMConnectedSystemSchema -InputObject <PSCustomObject> [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | Connected System identifier |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | Connected System Object from the pipeline |
| `PassThru` | `switch` | No | `$false` | Returns the Connected System Object after schema import |

### Output

When `-PassThru` is specified, returns the Connected System Object. Otherwise, no output.

### Examples

```powershell title="Import schema for a Connected System"
Import-JIMConnectedSystemSchema -Id 3
```

```powershell title="Pipeline: create a system, then import its schema"
New-JIMConnectedSystem "LDAP Directory" -ConnectorDefinitionId 1 -PassThru |
    Import-JIMConnectedSystemSchema -PassThru
```

### Notes

- This operation is **destructive**: it replaces the existing schema. Any object type or attribute selections that no longer match the new schema are removed.
- Schema import is required before creating Synchronisation Rules for a Connected System.
- Supports `ShouldProcess` (Medium impact).

---

## Import-JIMConnectedSystemHierarchy

Imports (or re-imports) the partition and container hierarchy from the connected data source.

### Syntax

```powershell
# ById (default)
Import-JIMConnectedSystemHierarchy -Id <int> [-PassThru]

# ByInputObject
Import-JIMConnectedSystemHierarchy -InputObject <PSCustomObject> [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | Connected System identifier |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | Connected System Object from the pipeline |
| `PassThru` | `switch` | No | `$false` | Returns the Connected System Object after hierarchy import |

### Output

When `-PassThru` is specified, returns the Connected System Object. Otherwise, no output.

### Examples

```powershell title="Import hierarchy"
Import-JIMConnectedSystemHierarchy -Id 3
```

```powershell title="Pipeline: import schema, then hierarchy"
Get-JIMConnectedSystem -Id 3 |
    Import-JIMConnectedSystemSchema |
    Import-JIMConnectedSystemHierarchy -PassThru
```

### Notes

- This operation is **destructive**: it replaces the existing partition and container configuration.
- Supports `ShouldProcess` (Medium impact).

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

An object with a `certificate` property and a `readAt` timestamp. The certificate carries `host`, `port`, `subject`, `issuer`, `subjectAlternativeNames`, `validFrom`, `validTo`, `thumbprint`, `signatureAlgorithm`, `isSelfSigned`, `issuerThumbprint`, `isIssuerCertificateAvailable`, `failureReason` and `remediation`.

`failureReason` is one of `None`, `UntrustedIssuer`, `NameMismatch`, `Expired`, `NotYetValid` or `NoCertificatePresented`. Only `UntrustedIssuer` is fixed by trusting the certificate.

### Examples

```powershell title="Read the certificate the configured server presents"
Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42
```

```powershell title="Show the identifying details and which check it fails"
Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 |
    Select-Object -ExpandProperty certificate |
    Select-Object host, subject, thumbprint, failureReason
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
| `Thumbprint` | `string` | Yes | | The thumbprint being trusted, as read from the server. Spaces and colons between the pairs are ignored. |
| `ChangeReason` | `string` | No | | Reason recorded on the audit Activity. JIM records a sentence naming the Connected System when none is given. |
| `SettingValues` | `hashtable` | No | | Connectivity settings entered but not yet saved, keyed by Connector Definition Setting identifier. |
| `PassThru` | `switch` | No | `$false` | Returns the outcome, including the certificate as it now sits in the store. |

### Output

When `-PassThru` is specified, returns an object with `outcome` (`Trusted`, `AlreadyTrusted` or `ThumbprintMismatch`), `message`, `certificate`, `expectedThumbprint` and `presentedThumbprint`. Otherwise, no output.

### Examples

```powershell title="Trust the certificate you have checked"
Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 -Thumbprint '7B44E1902CF6A83D5518BE7719A0C4D62F8E3B01'
```

```powershell title="Trust the authority that issued it, rather than the server's own certificate"
$reading = Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42
$reading.certificate | Select-Object subject, issuer, thumbprint, issuerThumbprint

Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 `
    -Thumbprint $reading.certificate.issuerThumbprint `
    -ChangeReason 'Unblocking the HR Cloud connection test.'
```

```powershell title="Trust an endpoint that has been entered but not saved"
Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 `
    -Thumbprint '7B44E1902CF6A83D5518BE7719A0C4D62F8E3B01' `
    -SettingValues @{ 40 = 'https://hr.corp.local/scim/v2' } -PassThru
```

### Notes

- **Check the thumbprint against the server's administrator before running this.** It is the only thing standing between an unattended script and trusting whatever is presented.
- **Trust the issuer where there is one.** `issuerThumbprint` is populated when the server sent the authority that issued its certificate. Trusting the authority survives the server's certificate being renewed; trusting the server's own certificate has to be repeated at every renewal. A self-signed certificate has no separate authority, and `isIssuerCertificateAvailable` is then `$false`.
- **A changed certificate stops the action.** If the server is presenting anything other than the thumbprint you named, nothing is trusted and the outcome is `ThumbprintMismatch`, with both values returned so you can compare them. Expected after a renewal; worth investigating otherwise.
- Only an untrusted issuer is fixed by trusting a certificate. An expired certificate has to be renewed on the server, and a name mismatch means connecting by a name the certificate carries.
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
Get-JIMConnectorDefinition -Name "CSV File"
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

## Get-JIMConnectedSystemObjectType

Retrieves the object types and their attributes for a Connected System.

Object Types the Connected System classified as internal (a directory's own configuration and operational classes) are omitted by default, matching what the portal's Schema tab shows. Pass `-IncludeInternal` to return them as well. An Object Type that is already selected is always returned, whatever its classification.

### Syntax

```powershell
Get-JIMConnectedSystemObjectType -ConnectedSystemId <int> [-IncludeInternal]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier. Alias: `Id`. Accepts pipeline input by property name. |
| `IncludeInternal` | `switch` | No | Off | Also return Object Types the Connected System classified as internal. |

### Output

Object type definitions with their attributes, selection state, and external ID configuration.

Each Object Type also carries `Tags`, the classification key/value pairs the Connected System reported (for example `class-kind` = `structural`, `visibility` = `internal`), and `IsInternal`, derived from them.

Each attribute carries `writability`, one of `Writable`, `ReadOnly` or `WritableOnCreate`. See [Attribute writability](../configuration/connected-systems.md#attribute-writability) for what each one means for Attribute Flow.

### Examples

```powershell title="Get object types for a Connected System"
Get-JIMConnectedSystemObjectType -ConnectedSystemId 3
```

```powershell title="List the attributes JIM may only set when it creates the object"
Get-JIMConnectedSystemObjectType -ConnectedSystemId 3 |
    ForEach-Object { $_.attributes } |
    Where-Object { $_.writability -eq 'WritableOnCreate' } |
    Select-Object name, type
```

```powershell title="Include the directory's own internal object types"
Get-JIMConnectedSystemObjectType -ConnectedSystemId 3 -IncludeInternal
```

```powershell title="Pipeline from Get-JIMConnectedSystem"
Get-JIMConnectedSystem -Id 3 | Get-JIMConnectedSystemObjectType
```

```powershell title="List selected object types only"
Get-JIMConnectedSystem -Id 3 |
    Get-JIMConnectedSystemObjectType |
    Where-Object { $_.Selected }
```

---

## Set-JIMConnectedSystemObjectType

Updates the configuration of an object type on a Connected System.

### Syntax

```powershell
Set-JIMConnectedSystemObjectType -ConnectedSystemId <int> -ObjectTypeId <int>
    [-Selected <bool>] [-RemoveContributedAttributesOnObsoletion <bool>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier |
| `ObjectTypeId` | `int` | Yes | | Object type identifier. Alias: `Id`. Accepts pipeline input by property name. |
| `Selected` | `bool` | No | | Whether this object type is selected for synchronisation |
| `RemoveContributedAttributesOnObsoletion` | `bool` | No | | Whether to remove attributes contributed by this system when an object becomes obsolete |
| `PassThru` | `switch` | No | `$false` | Returns the updated object type |

### Output

When `-PassThru` is specified, returns the updated object type. Otherwise, no output.

### Examples

```powershell title="Select an object type for synchronisation"
Set-JIMConnectedSystemObjectType -ConnectedSystemId 3 -ObjectTypeId 1 -Selected $true
```

```powershell title="Deselect an object type"
Set-JIMConnectedSystemObjectType -ConnectedSystemId 3 -ObjectTypeId 2 -Selected $false
```

### Notes

- Supports `ShouldProcess` (Medium impact).

---

## Set-JIMConnectedSystemAttribute

Updates the selection, external ID configuration and data type of attributes on a Connected System Object Type. Supports updating a single attribute or multiple attributes in bulk.

### Syntax

```powershell
# Single (default)
Set-JIMConnectedSystemAttribute -ConnectedSystemId <int> -ObjectTypeId <int>
    -AttributeId <int> [-Selected <bool>] [-IsExternalId <bool>]
    [-IsSecondaryExternalId <bool>] [-Type <string>] [-PassThru]

# Bulk
Set-JIMConnectedSystemAttribute -ConnectedSystemId <int> -ObjectTypeId <int>
    -AttributeUpdates <hashtable> [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier |
| `ObjectTypeId` | `int` | Yes | | Object type identifier |
| `AttributeId` | `int` | Yes (Single) | | Attribute identifier. Alias: `Id`. Accepts pipeline input by property name. |
| `Selected` | `bool` | No (Single) | | Whether this attribute is selected for synchronisation |
| `IsExternalId` | `bool` | No (Single) | | Whether this attribute is the primary external identifier |
| `IsSecondaryExternalId` | `bool` | No (Single) | | Whether this attribute is a secondary external identifier |
| `Type` | `string` | No (Single) | | Overrides the data type schema discovery inferred. One of `Text`, `Integer`, `LongNumber`, `Decimal`, `DateTime`, `Boolean`, `Reference`, `Guid`, `Binary`. `Integer` is the friendly name for the Number type. |
| `AttributeUpdates` | `hashtable` | Yes (Bulk) | | Hashtable of updates. Keys are attribute IDs; values are hashtables with `selected`, `isExternalId`, and/or `isSecondaryExternalId`. A data type cannot be set in bulk. |
| `PassThru` | `switch` | No | `$false` | Returns the updated attribute(s) |

`-Type` is accepted only where the Connector's schema cannot state a type definitively, which today means the JIM File Connector and the JIM SQL Connector. It is refused once the attribute is referenced by a Synchronisation Rule or holds values. See [Attribute data types](../configuration/connected-systems.md#attribute-data-types) for when an override is needed and why.

### Output

When `-PassThru` is specified, returns the updated attribute object(s). Otherwise, no output.

### Examples

```powershell title="Select a single attribute"
Set-JIMConnectedSystemAttribute -ConnectedSystemId 3 -ObjectTypeId 1 -AttributeId 5 -Selected $true
```

```powershell title="Mark an attribute as the primary external ID"
Set-JIMConnectedSystemAttribute -ConnectedSystemId 3 -ObjectTypeId 1 -AttributeId 10 -IsExternalId $true
```

```powershell title="Correct the data type of an Oracle NUMBER column"
# Oracle has one numeric type, so a NUMBER(10) employee identifier is read as a Long Number by default.
# Recording it as a whole number lets it flow into the built-in Employee Number Metaverse Attribute.
Set-JIMConnectedSystemAttribute -ConnectedSystemId 3 -ObjectTypeId 1 -AttributeId 5 -Type Integer
```

```powershell title="Bulk-update multiple attributes"
Set-JIMConnectedSystemAttribute -ConnectedSystemId 3 -ObjectTypeId 1 -AttributeUpdates @{
    5  = @{ selected = $true }
    10 = @{ selected = $true; isExternalId = $true }
    12 = @{ selected = $true; isSecondaryExternalId = $true }
}
```

### Notes

- Supports `ShouldProcess` (Medium impact).
- Only one attribute per object type can be the primary external ID. Setting `IsExternalId` on an attribute automatically clears it from the previous primary.

---

## Get-JIMConnectedSystemPartition

Retrieves the partitions and their containers for a Connected System.

### Syntax

```powershell
Get-JIMConnectedSystemPartition -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier. Alias: `Id`. Accepts pipeline input by property name. |

### Output

Partition objects with their container hierarchy and selection state.

### Examples

```powershell title="Get partitions for a Connected System"
Get-JIMConnectedSystemPartition -ConnectedSystemId 3
```

```powershell title="Pipeline from Get-JIMConnectedSystem"
Get-JIMConnectedSystem -Id 3 | Get-JIMConnectedSystemPartition
```

---

## Get-JIMConnectedSystemDirectoryServer

Discovers the domain controllers in a Connected System's directory, with the Active Directory Site each belongs to. Only Connected Systems using the LDAP connector against an Active Directory or Samba AD directory support this; other connectors, and non-AD-family LDAP directories (OpenLDAP, Generic), return an error naming why. Purely informational: it never writes anything. Aliased as `Get-JIMConnectedSystemDomainController`.

### Syntax

```powershell
Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier. Alias: `Id`. Accepts pipeline input by property name. |

### Output

One object per discovered domain controller: `hostName` (its FQDN) and `site` (the Active Directory Site it belongs to, or `$null` for directories without Sites).

### Examples

```powershell title="Discover domain controllers for a Connected System"
Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId 3
```

```powershell title="Filter to a specific Active Directory Site"
Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId 3 | Where-Object { $_.site -eq 'London' }
```

```powershell title="Pipeline from Get-JIMConnectedSystem"
Get-JIMConnectedSystem -Name "Corp AD" | Get-JIMConnectedSystemDirectoryServer
```

### Notes

- This is a discovery aid, not a configuration write: use `Set-JIMConnectedSystem` to set the Preferred Domain Controller setting once you have chosen one.

---

## Set-JIMConnectedSystemPartition

Updates the selection state of a partition on a Connected System.

### Syntax

```powershell
Set-JIMConnectedSystemPartition -ConnectedSystemId <int> -PartitionId <int>
    [-Selected <bool>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier |
| `PartitionId` | `int` | Yes | | Partition identifier. Alias: `Id`. Accepts pipeline input by property name. |
| `Selected` | `bool` | No | | Whether this partition is selected for synchronisation |
| `PassThru` | `switch` | No | `$false` | Returns the updated partition |

### Output

When `-PassThru` is specified, returns the updated partition. Otherwise, no output.

### Examples

```powershell title="Select a partition"
Set-JIMConnectedSystemPartition -ConnectedSystemId 3 -PartitionId 1 -Selected $true
```

```powershell title="Deselect a partition"
Set-JIMConnectedSystemPartition -ConnectedSystemId 3 -PartitionId 1 -Selected $false -PassThru
```

### Notes

- Supports `ShouldProcess` (Medium impact).

---

## Set-JIMConnectedSystemContainer

Updates the selection state, exclusion and scope of a container within a partition.

### Syntax

```powershell
Set-JIMConnectedSystemContainer -ConnectedSystemId <int> -ContainerId <int>
    [-Selected <bool>] [-Excluded <bool>] [-Scope <string>] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier |
| `ContainerId` | `int` | Yes | | Container identifier. Alias: `Id`. Accepts pipeline input by property name. |
| `Selected` | `bool` | No | | Whether this container is selected for synchronisation |
| `Excluded` | `bool` | No | | Whether this container is carved out of a selection an ancestor made, leaving the objects within it deliberately unimported. Omit to leave the stored exclusion unchanged. |
| `Scope` | `string` | No | | How far beneath the container objects are imported from: `Subtree` or `OneLevel`. Omit to leave the stored scope unchanged. |
| `PassThru` | `switch` | No | `$false` | Returns the updated container |

### Output

When `-PassThru` is specified, returns the updated container. Otherwise, no output.

### Examples

```powershell title="Select a container"
Set-JIMConnectedSystemContainer -ConnectedSystemId 3 -ContainerId 7 -Selected $true
```

```powershell title="Select a container without its child containers"
Set-JIMConnectedSystemContainer -ConnectedSystemId 3 -ContainerId 7 -Selected $true -Scope OneLevel
```

```powershell title="Widen an already selected container back to its whole subtree"
Set-JIMConnectedSystemContainer -ConnectedSystemId 3 -ContainerId 7 -Scope Subtree
```

```powershell title="Exclude a container from the selection above it"
Set-JIMConnectedSystemContainer -ConnectedSystemId 3 -ContainerId 12 -Excluded $true
```

```powershell title="Replace a selection with an exclusion"
Set-JIMConnectedSystemContainer -ConnectedSystemId 3 -ContainerId 12 -Selected $false -Excluded $true
```

```powershell title="Hand an excluded container back into scope"
Set-JIMConnectedSystemContainer -ConnectedSystemId 3 -ContainerId 12 -Excluded $false
```

```powershell title="Select multiple containers via pipeline"
@(7, 8, 9) | ForEach-Object {
    Set-JIMConnectedSystemContainer -ConnectedSystemId 3 -ContainerId $_ -Selected $true
}
```

### Notes

- The parent partition must also be selected for container selection to take effect during import operations.
- `Scope` defaults to `Subtree` on containers that have never had it set, which is how container selection behaved before the option existed.
- Narrowing a container to `OneLevel` takes the objects beneath it out of scope, exactly as deselecting those containers would. The Connected System Objects already imported from them become obsolete on the next import.
- `Selected` and `Excluded` are mutually exclusive: a container states one thing about itself. A request that would leave both set is rejected with a 400, whether it names both or names one against a stored other, so moving a container from a selection to an exclusion means setting both in the same call.
- Excluding a container takes the objects within it, and within every container beneath it, out of scope. A container beneath an exclusion can be selected in its own right to bring that branch back, because whichever statement is nearest to an object decides its fate. See [Excluding a Container](../connectors/jim-ldap-connector.md#excluding-a-container).
- Supports `ShouldProcess` (Medium impact).

---

## Get-JIMConnectedSystemContainerScopeText

Reads a Connected System's Container Scope as text, one statement per line.

### Syntax

```powershell
Get-JIMConnectedSystemContainerScopeText -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier. Accepts pipeline input by property name. |

### Output

A `string`: the Container Scope in canonical form, one statement per line, in hierarchy order. Empty where nothing is selected.

Text read here can be passed straight back to `Set-JIMConnectedSystemContainerScopeText`, which leaves the scope exactly as it was.

### Examples

```powershell title="Read the Container Scope"
Get-JIMConnectedSystemContainerScopeText -ConnectedSystemId 3
```

```text
include OU=Corp,DC=example,DC=com
exclude OU=Service Accounts,OU=Corp,DC=example,DC=com
include OU=App1,OU=Service Accounts,OU=Corp,DC=example,DC=com
```

```powershell title="Save the Container Scope to a file"
Get-JIMConnectedSystemContainerScopeText -ConnectedSystemId 3 | Set-Content ./scope.txt
```

```powershell title="Copy the Container Scope to another Connected System"
Get-JIMConnectedSystemContainerScopeText -ConnectedSystemId 3 |
    Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 4
```

### Notes

- Every path is the Container's identifier in the Connected System's own terms, which for a directory is its Distinguished Name.
- Copying a scope between Connected Systems requires the target to have discovered the same Containers; a path naming one it has not is refused.

---

## Set-JIMConnectedSystemContainerScopeText

States a Connected System's whole Container Scope as text.

### Syntax

```powershell
Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId <int> -Text <string> [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier |
| `Text` | `string` | Yes | | The Container Scope to apply. Accepts pipeline input. Empty text clears every selection and exclusion. |
| `PassThru` | `switch` | No | `$false` | Returns the canonical text for the scope now in force |

Each line is a directive, an optional `one-level`, then the Container's path:

| Statement | Means |
|---|---|
| `include <path>` | Manage this Container and everything beneath it. `+` is accepted as shorthand. |
| `include one-level <path>` | Manage the objects held directly in this Container, and no Container beneath it. |
| `exclude <path>` | Carve this Container out of the selection an ancestor made. `-` is accepted as shorthand. |
| `exclude one-level <path>` | Carve out the objects held directly in this Container only. |

Blank lines and whole lines beginning with `#` are ignored.

### Output

When `-PassThru` is specified, returns the canonical Container Scope text as a `string`. Otherwise, no output.

### Examples

```powershell title="State a Container Scope with a carve-out and a re-inclusion"
Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 3 -Text @"
include OU=Corp,DC=example,DC=com
exclude OU=Service Accounts,OU=Corp,DC=example,DC=com
include OU=App1,OU=Service Accounts,OU=Corp,DC=example,DC=com
"@
```

```powershell title="Apply a Container Scope held in a file"
Get-Content ./scope.txt -Raw | Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 3
```

```powershell title="Manage only the objects held directly in a container"
Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 3 -Text 'include one-level OU=Corp,DC=example,DC=com' -PassThru
```

### Notes

- The text states the whole of Container Scope rather than a change to it. A Container it does not name states nothing, so omitting a line is how a Container is deselected, and empty text clears the scope entirely.
- Partition selection is left alone, except that naming a Container selects the partition holding it.
- It is applied all-or-nothing. A path naming no Container, a Container named twice, and a statement an ancestor already makes are each refused with the line that caused them, and nothing is changed.
- This is a synchronisation-affecting change: taking a Container out of scope obsoletes the objects imported through it on the next Full Import, and the synchronisation after that disconnects them. Preview it first with [`New-JIMConfigurationChangePreview`](previews.md).
- Supports `ShouldProcess` (High impact), so it prompts before applying unless you pass `-Confirm:$false`.

---

## Get-JIMConnectedSystemObject

Retrieves connector space objects (CSOs) from a Connected System, with support for paging and attribute value drill-down.

### Syntax

```powershell
# List (default)
Get-JIMConnectedSystemObject -ConnectedSystemId <int> [-Search <string>] [-Status <string>]
    [-ObjectTypeId <int>] [-JoinType <string>] [-SortBy <string>] [-Ascending]
    [-Page <int>] [-PageSize <int>]

# ListAll
Get-JIMConnectedSystemObject -ConnectedSystemId <int> -All [-Force] [-Search <string>] [-Status <string>]
    [-ObjectTypeId <int>] [-JoinType <string>] [-SortBy <string>] [-Ascending] [-PageSize <int>]

# ById
Get-JIMConnectedSystemObject -ConnectedSystemId <int> -Id <guid>

# AttributeValues
Get-JIMConnectedSystemObject -ConnectedSystemId <int> -Id <guid>
    -AttributeName <string> [-Search <string>] [-Page <int>] [-PageSize <int>]

# AttributeValuesAll
Get-JIMConnectedSystemObject -ConnectedSystemId <int> -Id <guid>
    -AttributeName <string> [-Search <string>] -All [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier |
| `Id` | `guid` | Yes (ById/AttributeValues sets) | | Connector space object identifier |
| `AttributeName` | `string` | No | | Name of a multi-valued attribute to page through |
| `Search` | `string` | No | | Filter attribute values, or filter the object list by display name/external ID |
| `Status` | `string` | No | | Filter the object list by status: `Normal`, `Obsolete`, `PendingProvisioning` |
| `ObjectTypeId` | `int` | No | | Filter the object list by Connected System Object Type |
| `JoinType` | `string` | No | | Filter the object list by join type: `NotJoined`, `Projected`, `Provisioned`, `Joined` |
| `SortBy` | `string` | No | | Property name to sort the object list by |
| `Ascending` | `switch` | No | `$false` | Sort the object list ascending instead of the default descending |
| `Page` | `int` | No | `1` | Page number for paginated results |
| `PageSize` | `int` | No | `50` | Number of results per page (maximum 100) |
| `All` | `switch` | No | `$false` | Returns all objects, or all attribute values, auto-paginating. Fetches at most 1000 pages (~100,000 items at the default page size) and then stops with a warning; a warning is also emitted up front when the result set is large |
| `Force` | `switch` | No | `$false` | Override the `-All` 1000-page ceiling and fetch every page regardless of size. Only valid with `-All` |

### Output

- **List / ListAll**: Lightweight headers for each Connected System Object matching the filters.
- **ById**: A connector space object with its attributes and current values.
- **AttributeValues / AttributeValuesAll**: Paged or complete list of values for the specified multi-valued attribute.

### Examples

```powershell title="List objects in a Connected System"
Get-JIMConnectedSystemObject -ConnectedSystemId 3
```

```powershell title="Find Obsolete objects matching a search term"
Get-JIMConnectedSystemObject -ConnectedSystemId 3 -Search "smith" -Status Obsolete
```

```powershell title="Get every object in a Connected System"
Get-JIMConnectedSystemObject -ConnectedSystemId 3 -All
```

```powershell title="Get every object in a very large connector space, overriding the -All safety cap"
# -All stops after 1000 pages (~100,000 objects) by default; -Force fetches everything up to the
# API's maximum retrieval depth of 1,000,000 rows.
Get-JIMConnectedSystemObject -ConnectedSystemId 3 -All -Force
```

```powershell title="Get a specific connector space object"
Get-JIMConnectedSystemObject -ConnectedSystemId 3 -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Page through a multi-valued attribute"
Get-JIMConnectedSystemObject -ConnectedSystemId 3 -Id "a1b2c3d4-..." -AttributeName "member" -Page 2 -PageSize 25
```

```powershell title="Get all values of a multi-valued attribute"
Get-JIMConnectedSystemObject -ConnectedSystemId 3 -Id "a1b2c3d4-..." -AttributeName "member" -All
```

### Notes

- Multi-valued attributes are capped at 10 values in the default detail response. Use the `-AttributeName` parameter to page through all values of a large multi-valued attribute.

---

## Get-JIMConnectedSystemObjectChangeHistory

Retrieves the change history for a Connected System Object. Each record carries the initiator and Run Profile context, plus the per-attribute value changes, ordered by change time descending (most recent first).

### Syntax

```powershell
# Page (default)
Get-JIMConnectedSystemObjectChangeHistory -ConnectedSystemId <int> -Id <guid>
    [-Page <int>] [-PageSize <int>]

# All
Get-JIMConnectedSystemObjectChangeHistory -ConnectedSystemId <int> -Id <guid> -All [-Force] [-PageSize <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier. Accepts pipeline input by property name. |
| `Id` | `guid` | Yes | | Connector space object identifier. Accepts pipeline input by property name. |
| `All` | `switch` | No | `$false` | Automatically paginates through all results. Cannot be used with `-Page`. Fetches at most 1000 pages (~50,000 records at the default page size) and then stops with a warning; use `-Force` to fetch beyond the cap, up to the API's maximum retrieval depth of 1,000,000 rows. |
| `Force` | `switch` | No | `$false` | Override the `-All` 1000-page ceiling and fetch every page regardless of size. Only valid with `-All`. |
| `Page` | `int` | No | `1` | Page number for paginated results. Cannot be used with `-All`. |
| `PageSize` | `int` | No | `50` | Number of items per page. Maximum: `100`. |

### Output

Returns one `PSCustomObject` per change record, including the initiator, Run Profile context, and per-attribute value changes.

### Examples

```powershell title="Get the most recent page of changes"
Get-JIMConnectedSystemObjectChangeHistory -ConnectedSystemId 3 -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Page through all changes for a CSO"
Get-JIMConnectedSystemObjectChangeHistory -ConnectedSystemId 3 -Id "a1b2c3d4-..." -All
```

```powershell title="Use a larger page size"
Get-JIMConnectedSystemObjectChangeHistory -ConnectedSystemId 3 -Id "a1b2c3d4-..." -PageSize 100
```

---

## Get-JIMConnectedSystemObjectAttributeValue

Pages through the values of a multi-valued attribute on a connector space object. This is the dedicated cmdlet for browsing large multi-valued attributes.

### Syntax

```powershell
# Page (default)
Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId <int> -CsoId <guid>
    -AttributeName <string> [-Search <string>] [-Page <int>] [-PageSize <int>]

# All
Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId <int> -CsoId <guid>
    -AttributeName <string> [-Search <string>] -All [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier |
| `CsoId` | `guid` | Yes | | Connector space object identifier |
| `AttributeName` | `string` | Yes | | Name of the multi-valued attribute |
| `Search` | `string` | No | | Filter values by search term |
| `Page` | `int` | No | `1` | Page number |
| `PageSize` | `int` | No | `50` | Number of values per page (maximum 100) |
| `All` | `switch` | No | `$false` | Returns all values, auto-paginating. Fetches at most 1000 pages (~50,000 values at the default page size) and then stops with a warning; use `-Force` to fetch beyond the cap, up to the API's maximum retrieval depth of 1,000,000 rows. |
| `Force` | `switch` | No | `$false` | Override the `-All` 1000-page ceiling and fetch every page regardless of size. Only valid with `-All`. |

### Output

Attribute values for the specified multi-valued attribute, with paging metadata when not using `-All`.

### Examples

```powershell title="Page through group members"
Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId 3 `
    -CsoId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
    -AttributeName "member" -Page 1 -PageSize 100
```

```powershell title="Search within attribute values"
Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId 3 `
    -CsoId "a1b2c3d4-..." -AttributeName "member" -Search "admin"
```

```powershell title="Get all values at once"
Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId 3 `
    -CsoId "a1b2c3d4-..." -AttributeName "proxyAddresses" -All
```

---

## Get-JIMConnectedSystemUnresolvedReferenceCount

Returns the count of unresolved references in a Connected System's connector space.

### Syntax

```powershell
Get-JIMConnectedSystemUnresolvedReferenceCount -ConnectedSystemId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier. Alias: `Id`. Accepts pipeline input by property name. |

### Output

An integer representing the number of unresolved references.

### Examples

```powershell title="Check for unresolved references"
Get-JIMConnectedSystemUnresolvedReferenceCount -ConnectedSystemId 3
```

```powershell title="Pipeline check across all systems"
Get-JIMConnectedSystem | ForEach-Object {
    [PSCustomObject]@{
        Name  = $_.Name
        Unresolved = Get-JIMConnectedSystemUnresolvedReferenceCount -ConnectedSystemId $_.Id
    }
} | Where-Object { $_.Unresolved -gt 0 }
```

### Notes

- A non-zero count indicates data integrity issues in the connector space. This commonly occurs after a partial import. Running a full import typically resolves outstanding references.

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

- These facts mirror the **Directory Capabilities** card on the Connected System's Details page in the portal; see the [JIM LDAP Connector](../connectors/jim-ldap-connector.md#directory-capabilities-card) documentation for what each fact means.

---

## Clear-JIMConnectedSystem

Removes all connector space objects (CSOs) and associated data from a Connected System without deleting the system itself. The Connected System configuration, schema, and Synchronisation Rules are preserved.

### Syntax

```powershell
# ById (default)
Clear-JIMConnectedSystem -Id <int> [-KeepChangeHistory] [-Force]

# ByInputObject
Clear-JIMConnectedSystem -InputObject <PSCustomObject> [-KeepChangeHistory] [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) | | Connected System identifier |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) | | Connected System Object from the pipeline |
| `KeepChangeHistory` | `switch` | No | `$false` | Preserves change history records; by default, change history is also deleted |
| `Force` | `switch` | No | `$false` | Suppresses the confirmation prompt |

### Output

None.

### Examples

```powershell title="Clear a Connected System with confirmation"
Clear-JIMConnectedSystem -Id 3
```

```powershell title="Clear without confirmation, keeping history"
Clear-JIMConnectedSystem -Id 3 -KeepChangeHistory -Force
```

```powershell title="Pipeline: clear a system by name"
Get-JIMConnectedSystem -Name "Staging AD" | Clear-JIMConnectedSystem -Force
```

### Notes

- Supports `ShouldProcess` (High impact). Without `-Force`, you will be prompted for confirmation.
- Removes all CSOs, attribute values, Pending Exports, and deferred references from the Connected System.
- Metaverse Objects are **not** deleted; their links to this Connected System are severed.
- By default, change history is also deleted. Use `-KeepChangeHistory` to retain it for auditing purposes.

---

## Get-JIMPendingExport

Retrieves Pending Export operations queued for a Connected System.

### Syntax

```powershell
# List (default)
Get-JIMPendingExport -ConnectedSystemId <int> [-Search <string>]
    [-Page <int>] [-PageSize <int>]

# ListAll
Get-JIMPendingExport -ConnectedSystemId <int> [-Search <string>] -All [-Force]

# ById
Get-JIMPendingExport -Id <guid>

# AttributeChanges
Get-JIMPendingExport -Id <guid> -AttributeName <string>
    [-Search <string>] [-Page <int>] [-PageSize <int>]

# AttributeChangesAll
Get-JIMPendingExport -Id <guid> -AttributeName <string> [-Search <string>] -All [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes (List, ListAll) | | Connected System identifier |
| `Id` | `guid` | Yes (ById, AttributeChanges, AttributeChangesAll) | | Pending Export operation identifier |
| `AttributeName` | `string` | No | | Name of a multi-valued attribute to page through its changes |
| `Search` | `string` | No | | Filter results by search term |
| `Page` | `int` | No | `1` | Page number |
| `PageSize` | `int` | No | `50` | Number of results per page (maximum 100) |
| `All` | `switch` | No | `$false` | Returns all results, auto-paginating. Fetches at most 1000 pages and then stops with a warning; use `-Force` to fetch beyond the cap, up to the API's maximum retrieval depth of 1,000,000 rows. |
| `Force` | `switch` | No | `$false` | Override the `-All` 1000-page ceiling and fetch every page regardless of size. Only valid with `-All`. |

### Output

- **List / ListAll**: Pending Export operations with export type (Add, Update, Delete) and summary of changes.
- **ById**: Detailed view of a single Pending Export, including all attribute changes. `UnresolvedReferences` lists each reference change not yet written (`AttributeName`, `ReferencedMetaverseObjectId`, `ReferencedMetaverseObjectDisplayName`) with its `Reason`: `Resolvable` (written on the next export run), `AwaitingAnchor` (the referenced object exists in this Connected System but has no anchor yet) or `NotInTargetSystem` (the referenced object has no Connected System Object in this Connected System). See [Unresolved reference handling on export](../configuration/connected-systems.md#on-export).
- **AttributeChanges / AttributeChangesAll**: Paged or complete list of changes for a specific multi-valued attribute.

### Examples

```powershell title="List Pending Exports for a Connected System"
Get-JIMPendingExport -ConnectedSystemId 3
```

```powershell title="Search Pending Exports"
Get-JIMPendingExport -ConnectedSystemId 3 -Search "jsmith" -PageSize 25
```

```powershell title="Get all Pending Exports"
Get-JIMPendingExport -ConnectedSystemId 3 -All
```

```powershell title="View details of a specific Pending Export"
Get-JIMPendingExport -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Page through member additions on a group export"
Get-JIMPendingExport -Id "a1b2c3d4-..." -AttributeName "member" -Page 1 -PageSize 100
```

### Notes

- For large multi-valued attribute changes (e.g. adding hundreds of members to a group), use the `-AttributeName` parameter to page through the individual changes rather than loading them all at once.

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

## Set-JIMConnectedSystemObjectPassword

Sets the password on one Connected System Object.

The password is written straight to the Connected System: nothing is staged as a Pending Export, nothing is retried, and JIM stores nothing. The attempt is recorded as an Activity against the object, carrying the outcome and, where the system refused, its verbatim reason.

This is the automation counterpart of the **Set Password** action in the administration portal. Supply the password with `-Password`, or have JIM generate one that follows the Connected System's discovered policy with `-Generate`. A generated password is returned to you, once, because you asked for it; JIM stores it nowhere.

### Syntax

```powershell
Set-JIMConnectedSystemObjectPassword -ConnectedSystemId <int> -Id <guid> -Password <securestring>
    [-ExpiryBehaviour <string>] [-EnableAccount] [-Force] [-PassThru]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ConnectedSystemId` | `int` | Yes | | Connected System identifier. Accepts a Connected System Object from the pipeline. |
| `Id` | `guid` | Yes | | Connected System Object identifier. Accepts a Connected System Object from the pipeline. |
| `Password` | `securestring` | Yes | | The password to set. Sent to the Connected System and nowhere else. |
| `ExpiryBehaviour` | `string` | No | `RequireChangeAtNextSignIn` | `RequireChangeAtNextSignIn`, `ExpiresAccordingToTargetPolicy` or `NeverExpires`. |
| `EnableAccount` | `switch` | No | `$false` | Enables the account as part of setting the password. Omitting it leaves the account's enabled state untouched. |
| `Force` | `switch` | No | `$false` | Skips the confirmation prompt. |
| `PassThru` | `switch` | No | `$false` | Returns the outcome. |

### Output

When `-PassThru` is specified, returns an object with these properties. No property carries the password.

| Property | Description |
|----------|-------------|
| `AppliedExpiryBehaviour` | The expiry behaviour really applied, which is not always the one asked for |
| `ExpiryBehaviourWarning` | Why the requested behaviour could not be honoured, or null if it was |

### Examples

```powershell title="Set a password, requiring a change at the next sign-in"
$password = Read-Host -AsSecureString "New password"
Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id 3f2a91c4-5b6d-4e7f-8a90-1b2c3d4e5f60 -Password $password
```

```powershell title="Set a password and enable the account, reporting what the directory applied"
$password = Read-Host -AsSecureString "New password"
Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id 3f2a91c4-5b6d-4e7f-8a90-1b2c3d4e5f60 -Password $password -EnableAccount -Force -PassThru
```

```powershell title="Pipeline: set the password on a retrieved Connected System Object"
$password = Read-Host -AsSecureString "New password"
Get-JIMConnectedSystemObject -ConnectedSystemId 1 -Id 3f2a91c4-5b6d-4e7f-8a90-1b2c3d4e5f60 |
    Set-JIMConnectedSystemObjectPassword -Password $password -ExpiryBehaviour NeverExpires
```

### Notes

- **This resets the password on whichever account you point it at.** Anyone who can call it can reset the password of any account in this connector space, subject only to what the Connected System's own service account is permitted to do.
- The password is taken as a `SecureString` so it does not sit in your session's command history in clear text. It is unwrapped only to be sent over TLS.
- A Connected System that cannot honour the requested expiry behaviour applies what it can and reports the difference in `ExpiryBehaviourWarning`; the password is still set.
- A rejected password returns an error carrying the system's own reason. A Connected System that could not be reached is reported distinctly, because nothing was established about the password itself and the same request is worth repeating.
- Routine initial passwords belong on the Synchronisation Rule that provisions the account; see `Set-JIMSyncRuleInitialPassword`.
- Pass `-Generate` instead of `-Password` to have JIM produce a password satisfying the policy it discovered on
  the Connected System. Prefer this to inventing one in your own script: JIM knows what the target demands, and
  a hand-rolled generator rediscovers the passphrase trap, where three words offer two character categories
  against a directory that wants three. The generated password comes back on the result's `password` property
  as a SecureString, whether or not `-PassThru` is given, and **that is the only chance to capture it**; JIM
  stores nothing and cannot return it again.

```powershell title="Set a compliant password without choosing one"
$result = Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 3 -Id $csoId -Generate -EnableAccount -Force
ConvertFrom-SecureString -SecureString $result.password -AsPlainText
```

---

## See also

- [Connected Systems](../configuration/connected-systems.md): what Connected Systems are, the connector space, partitions and containers, and common workflows
- [Run Profiles](run-profiles.md): execute import, sync, and export operations on Connected Systems
- [Synchronisation Rules](synchronisation-rules.md): define attribute mappings and scoping for Connected System synchronisation, including the initial password set on provisioned accounts
- [Connection](connection.md): establish a session before using these cmdlets
