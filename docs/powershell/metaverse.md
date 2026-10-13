---
title: Metaverse
---

# Metaverse

The Metaverse is the central identity store in JIM. These cmdlets query Metaverse Objects and review pending deletions. The Metaverse schema is managed with the [Metaverse Object Type](metaverse-object-types.md) and [Metaverse Attribute](metaverse-attributes.md) cmdlets.

## Objects

### Search-JIMMetaverseObject

Searches for Metaverse Objects using a predefined search definition, returning lightweight headers with only the attributes configured in the search. Optimised for fast responses at scale (100k+ objects).

Use this cmdlet for fast list views and searches. Use `Get-JIMMetaverseObject` when you need full object details or custom attribute selection.

#### Syntax

```powershell
# List (default)
Search-JIMMetaverseObject -PredefinedSearchUri <string> [-Search <string>] [-HasAttribute <string>]
    [-SortBy <string>] [-SortDirection <string>] [-Page <int>] [-PageSize <int>]

# ListAll
Search-JIMMetaverseObject -PredefinedSearchUri <string> [-Search <string>] [-HasAttribute <string>]
    [-SortBy <string>] [-SortDirection <string>] [-PageSize <int>] -All [-Force]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `PredefinedSearchUri` | `string` | Yes | | URI identifier of the predefined search (e.g. `users`, `groups`) |
| `Search` | `string` | No | | Search query to filter across all string attribute values (case-insensitive, partial match) |
| `HasAttribute` | `string` | No | | Return only objects that hold a value for the named Metaverse Attribute. Matched case-insensitively; a multi-valued attribute counts once; an unrecognised name yields no results. |
| `SortBy` | `string` | No | | Attribute name to sort results by (defaults to creation date) |
| `SortDirection` | `string` | No | `desc` | Sort direction: `asc` or `desc` |
| `All` | `switch` | No | `false` | Automatically paginate through all results. Fetches at most 1000 pages (~100,000 objects at the default page size) and then stops with a warning; a warning is also emitted up front when the result set is large |
| `Force` | `switch` | No | `false` | Override the `-All` 1000-page ceiling and fetch every page regardless of size. Only valid with `-All` |
| `Page` | `int` | No | `1` | Page number for paginated results (cannot be used with `-All`) |
| `PageSize` | `int` | No | `100` | Number of items per page (maximum 100) |

#### Output

Lightweight Metaverse Object headers including ID, display name, object type, and the attributes defined in the predefined search.

#### Examples

```powershell title="Search for users"
Search-JIMMetaverseObject -PredefinedSearchUri "users"
```

```powershell title="Search with a query"
Search-JIMMetaverseObject -PredefinedSearchUri "users" -Search "Smith"
```

```powershell title="Get all users with auto-pagination"
Search-JIMMetaverseObject -PredefinedSearchUri "users" -All
```

```powershell title="Get all users, overriding the -All safety cap for a very large result set"
# -All stops after 1000 pages (~100,000 objects) by default; -Force fetches everything up to the
# API's maximum retrieval depth of 1,000,000 rows.
Search-JIMMetaverseObject -PredefinedSearchUri "users" -All -Force
```

```powershell title="Find users that hold a value for an attribute"
Search-JIMMetaverseObject -PredefinedSearchUri "users" -HasAttribute "costCentre"
```

```powershell title="Sort groups by display name"
Search-JIMMetaverseObject -PredefinedSearchUri "groups" -SortBy "Display Name" -SortDirection asc
```

---

### Get-JIMMetaverseObject

Retrieves Metaverse Objects. Supports searching by ID, object type, attribute values, and wildcard patterns.

#### Syntax

```powershell
# List (default)
Get-JIMMetaverseObject [-ObjectTypeId <int>] [-ObjectTypeName <string>] [-Search <string>]
    [-AttributeName <string> -AttributeValue <string>] [-Attributes <string[]>]
    [-Page <int>] [-PageSize <int>]

# ById
Get-JIMMetaverseObject -Id <guid> [-Attributes <string[]>]

# ListAll
Get-JIMMetaverseObject [-ObjectTypeId <int>] [-ObjectTypeName <string>] [-Search <string>]
    [-AttributeName <string> -AttributeValue <string>] [-Attributes <string[]>] -All [-Force]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes (ById) | | The GUID of a specific Metaverse Object. Accepts pipeline input. |
| `ObjectTypeId` | `int` | No | | Filter by object type ID |
| `ObjectTypeName` | `string` | No | | Filter by object type name |
| `Search` | `string` | No | | Search string; supports wildcards |
| `AttributeName` | `string` | No | | Attribute name to search on; requires `AttributeValue` |
| `AttributeValue` | `string` | No | | Attribute value to match, exactly but ignoring case (`_` and `%` are literal characters, not wildcards); requires `AttributeName` |
| `Attributes` | `string[]` | No | | Attribute names to include in results; use `"*"` to return all attributes |
| `All` | `switch` | No | `false` | Automatically paginate through all results. Fetches at most 1000 pages (~100,000 objects at the default page size) and then stops with a warning; a warning is also emitted up front when the result set is large |
| `Force` | `switch` | No | `false` | Override the `-All` 1000-page ceiling and fetch every page regardless of size. Only valid with `-All` |
| `Page` | `int` | No | `1` | Page number for paginated results |
| `PageSize` | `int` | No | `100` | Number of results per page (maximum 100) |

#### Output

Metaverse Objects including their ID, object type, and requested attributes.

The object type is returned as a nested `Type` object with `Id` and `Name` properties (for example `$obj.Type.Name`), identical in both the list and single-object responses.

When retrieved by ID, `ConnectedSystemObjects` lists every Connected System Object joined to the Metaverse Object, carrying the same data the portal's Connections tab shows: `Id`, `ConnectedSystemId`, `ConnectedSystemName`, `DisplayName`, `ObjectTypeName`, `JoinType`, `DateJoined`, `Status`, `State`, `IsSource`, `IsTarget`, `PendingAttributeChangeCount` and `LastSynchronised`. `IsSource` and `IsTarget` say whether an enabled Import or Export Synchronisation Rule exists for that object's Connected System and Object Type, so an object can be both, or neither. `State` is derived from the object's status and any queued Pending Export: `InSync`, `UpdatePending`, `ProvisioningExportPending`, `ProvisioningAwaitingConfirmation`, `DeletePending`, `ExportFailed` or `Obsolete`. `PendingAttributeChangeCount` is populated only for `UpdatePending`; it is `$null` for every other state, including a pending Create or Delete. The list form does not carry these rows; only the `-Id` form does.

When retrieved by ID, `CreatedByType`, `CreatedById` and `CreatedByName` name who made the Metaverse Object's earliest recorded change, and `LastUpdatedByType`, `LastUpdatedById` and `LastUpdatedByName` who made its latest, as the portal's Properties tab shows them. The type is `User`, `ApiKey` or `System`, and the name is as it was at the time. All six are `$null` when no change history is recorded (change tracking off, or the history purged).

When retrieved by ID, each attribute value also carries its provenance: `ContributedBySystemId`/`ContributedBySystemName` identify the Connected System, and `ContributedBySyncRuleId`/`ContributedBySyncRuleName` identify the exact Synchronisation Rule that won [attribute priority resolution](../concepts/attribute-priority.md) and contributed the value. A value row with `NullValue` set to `true` is an asserted null: a deliberate, authoritative "no value" assertion carrying provenance only; treat it as no value present, distinct from the attribute having no row at all.

#### Examples

```powershell title="Get a specific object by ID"
Get-JIMMetaverseObject -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Attributes "*"
```

```powershell title="Search for Person objects by display name"
Get-JIMMetaverseObject -ObjectTypeName "Person" -Search "Smith*" -Attributes @("Display Name", "Mail")
```

```powershell title="Find objects by attribute value"
Get-JIMMetaverseObject -AttributeName "Employee Id" -AttributeValue "12345" -Attributes @("Display Name", "Department")
```

```powershell title="Retrieve all Group objects with auto-pagination"
Get-JIMMetaverseObject -ObjectTypeName "Group" -All -Attributes @("Display Name", "Member")
```

```powershell title="Fetch a very large metaverse, overriding the -All safety cap"
# -All stops after 1000 pages (~100,000 objects) by default; -Force fetches everything up to the
# API's maximum retrieval depth of 1,000,000 rows.
Get-JIMMetaverseObject -ObjectTypeName "Person" -All -Force
```

```powershell title="See which systems an Identity is joined to, and how"
(Get-JIMMetaverseObject -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890").ConnectedSystemObjects |
    Format-Table ConnectedSystemName, ObjectTypeName, JoinType, State, IsSource, IsTarget
```

```powershell title="Find an Identity's accounts whose export has failed"
(Get-JIMMetaverseObject -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890").ConnectedSystemObjects |
    Where-Object { $_.State -eq "ExportFailed" }
```

```powershell title="Page through results manually"
Get-JIMMetaverseObject -ObjectTypeName "Person" -Page 3 -PageSize 50
```

---

### Get-JIMMetaverseObjectChangeHistory

Retrieves the change history for a Metaverse Object. Each record carries the initiator, Synchronisation Rule, and Run Profile context, plus the per-attribute value changes, ordered by change time descending (most recent first).

#### Syntax

```powershell
# Page (default)
Get-JIMMetaverseObjectChangeHistory -Id <guid> [-Page <int>] [-PageSize <int>]

# All
Get-JIMMetaverseObjectChangeHistory -Id <guid> -All [-Force] [-PageSize <int>]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes | | Metaverse Object identifier. Accepts pipeline input by property name. |
| `All` | `switch` | No | `$false` | Automatically paginates through all results. Cannot be used with `-Page`. Fetches at most 1000 pages (~50,000 records at the default page size) and then stops with a warning; use `-Force` to fetch beyond the cap, up to the API's maximum retrieval depth of 1,000,000 rows. |
| `Force` | `switch` | No | `$false` | Override the `-All` 1000-page ceiling and fetch every page regardless of size. Only valid with `-All`. |
| `Page` | `int` | No | `1` | Page number for paginated results. Cannot be used with `-All`. |
| `PageSize` | `int` | No | `50` | Number of items per page. Maximum: `100`. |

#### Output

Returns one `PSCustomObject` per change record, including the initiator, Synchronisation Rule, Run Profile context, and per-attribute value changes. Each value change carries `ContributedBySyncRuleId` and `ContributedBySyncRuleName`, naming the Synchronisation Rule that contributed that specific value (a single change record can flow attributes from several rules); both are `$null` when the value was not contributed by a rule. `ContributedBySystemId` and `ContributedBySystemName` name the Connected System that rule belongs to, resolved from the still-live rule; both are `$null` once the contributing rule has since been deleted, even though `ContributedBySyncRuleName`'s snapshot survives. `IsGeneratedValue` is `$true` when a Generated Value Attribute Flow produced the value.

#### Examples

```powershell title="Get the most recent page of changes"
Get-JIMMetaverseObjectChangeHistory -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Page through every change for a Metaverse Object"
Get-JIMMetaverseObjectChangeHistory -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -All
```

```powershell title="Pipe a Metaverse Object into the cmdlet"
Get-JIMMetaverseObject -ObjectTypeName "Group" -Search "Project-Alpha" |
    Get-JIMMetaverseObjectChangeHistory -All
```

---

### Get-JIMMetaverseObjectProvenance

Shows where a Metaverse Object's attribute values came from: which Connected System and Synchronisation Rule contributed each one, or that no contributor is recorded.

With just `-Id`, returns a summary: one entry per attribute holding at least one value, with its distinct origins ordered by value count descending. With `-AttributeName` or `-AttributeId`, returns full provenance for that one attribute: the current value(s) and their origin, the joined Connected System Object the value came from, the change that most recently set it, every Synchronisation Rule mapping that could contribute to it (in priority order, each with the value it would currently supply and its standing against the value in use), and the attribute's change history.

#### Syntax

```powershell
# Summary (default)
Get-JIMMetaverseObjectProvenance -Id <guid>

# ByAttributeName
Get-JIMMetaverseObjectProvenance -Id <guid> -AttributeName <string>

# ByAttributeId
Get-JIMMetaverseObjectProvenance -Id <guid> -AttributeId <int>
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes | | Metaverse Object identifier. Accepts pipeline input by property name. |
| `AttributeName` | `string` | No | | Name of the attribute to get detailed provenance for. Resolved against the attributes the object holds a value for (an exact, case-insensitive match); use `-AttributeId` for an attribute with no value. Cannot be used with `-AttributeId`. |
| `AttributeId` | `int` | No | | Identifier of the attribute to get detailed provenance for. Cannot be used with `-AttributeName`. |

#### Output

With just `-Id`: a `PSCustomObject` with `MetaverseObjectId` and `Attributes` (each with `AttributeId`, `AttributeName` and `Origins`, an array of `{Kind, ConnectedSystemId, ConnectedSystemName, SyncRuleId, SyncRuleName, SyncRuleDeleted, AssertsNoValue, Corrected, PersonId, PersonName}`). `Kind` is one of `NotRecorded`, `SynchronisationRule`, `GeneratedValue` or `SetByPerson`; a `GeneratedValue` origin still names the Connected System and Synchronisation Rule, and `Corrected` is true once a collision has revised the value.

With `-AttributeName` or `-AttributeId`: a `PSCustomObject` with `MetaverseObjectId`, `MetaverseObjectTypeId`, `AttributeId`, `AttributeName`, `AttributeType`, `AttributePlurality`, `CurrentValues` (each with `DisplayValue`, `ReferenceMetaverseObjectId`, `ReferenceTypeName`, `Origin`), `CurrentValueTotalCount`, `ContributingConnectedSystemObject`, `LastSet` (the Activity that set the current value), `Sources` (each mapping's `MappingId`, `Rank`, `SyncRuleId`, `SyncRuleName`, `ConnectedSystemId`, `ConnectedSystemName`, `IsExpression`, `IsGeneratedValue`, `Expression`, `State`, `CandidateValues`, `Note`; `State` is one of `InUse`, `Outranked`, `NoValue`, `NotJoined`, `Disabled` or `NotEvaluated`), `History` (newest first, up to 50 entries, each with `IsGeneratedValue` set when a Generated Value Attribute Flow produced the value) and `HistoryTruncated`.

#### Examples

```powershell title="Get the origin of every attribute holding a value"
Get-JIMMetaverseObjectProvenance -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Get full provenance for one attribute by name"
Get-JIMMetaverseObjectProvenance -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -AttributeName Department
```

```powershell title="List the Synchronisation Rules that could contribute to an attribute but are currently losing Attribute Priority"
(Get-JIMMetaverseObjectProvenance -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -AttributeId 42).Sources |
    Where-Object State -eq 'Outranked'
```

```powershell title="Pipe a Metaverse Object into the cmdlet"
Get-JIMMetaverseObject -AttributeName "Account Name" -AttributeValue jsmith |
    Get-JIMMetaverseObjectProvenance
```

---

### Get-JIMMetaverseObjectConnection

Explains why a Metaverse Object is connected where it is, and why it is not connected elsewhere. It answers the same question as the Metaverse Object's Connections tab in the portal, in the same words; see [Why it is connected, and why it is not](../configuration/metaverse-objects.md#why-it-is-connected-and-why-it-is-not) for what each reason means.

For each Connected System Object joined to the Metaverse Object, it returns how the object was joined (the method, the Synchronisation Rule responsible, the date and the Activity) and the scoping of every relevant enabled Synchronisation Rule, evaluated now against current values. With `-IncludeNotConnected`, it also returns one entry per enabled export Synchronisation Rule whose Connected System holds no object joined to this one, with the reason, a one-line hint, what would change it, and a plain-text summary to paste into a ticket.

#### Syntax

```powershell
Get-JIMMetaverseObjectConnection -Id <guid> [-ConnectedSystemName <string>] [-IncludeNotConnected]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes | | Metaverse Object identifier. Accepts pipeline input by property name, so a Metaverse Object from `Get-JIMMetaverseObject` can be piped in. |
| `ConnectedSystemName` | `string` | No | | Return only the entries for the Connected System with this name (an exact, case-insensitive match). Errors when nothing matches. |
| `IncludeNotConnected` | `switch` | No | `$false` | Also explain the enabled export Synchronisation Rules whose Connected System holds no object joined to this Metaverse Object. |

#### Output

One `PSCustomObject` per entry. Joined connections and not-connected entries share the same properties, so they can be listed, filtered and sorted together:

| Property | Description |
|----------|-------------|
| `MetaverseObjectId`, `MetaverseObjectName` | The Metaverse Object explained. |
| `ConnectedSystem`, `ConnectedSystemId` | The Connected System's name and identifier. |
| `Connected` | `$true` for a joined connection, `$false` for a not-connected entry. |
| `Object`, `ConnectedSystemObjectId` | The joined Connected System Object's external id and identifier; empty when not connected. |
| `ObjectType` | The Connected System Object Type. |
| `Role` | `Source`, `Target`, `Source and Target` or `None`: whether enabled import or export Synchronisation Rules use the object. A not-connected entry is always `Target`. |
| `State` | For a joined connection, its state (`InSync`, `UpdatePending`, `ProvisioningExportPending`, `ProvisioningAwaitingConfirmation`, `DeletePending`, `ExportFailed` or `Obsolete`). For a not-connected entry, the reason: `NotInScope`, `ProvisioningDisabled`, `RuleMisconfigured` or `NotYetProvisioned`. |
| `SyncRule` | The export Synchronisation Rule a not-connected entry is about; empty for a joined connection. |
| `Join` | How a joined connection was joined: `JoinType`, `Method` (`Projection`, `Provisioning`, `InboundMatching` or `ExportMatching`), `DateJoined`, `SyncRuleId`, `SyncRuleName`, `Source` (`Recorded`, `Derived` from Activity history, or `NotRecorded`), `Description` (one sentence, for example *Projected by the Synchronisation Rule "HR Users Import"*), `ActivityId`, `RunProfileExecutionItemId`, and for a join made by matching `ObjectMatchingRuleId` and `MatchValue` (what it matched on). Empty for a not-connected entry. |
| `Hint` | A not-connected entry's one-line qualifier, for example *Fails on Department; Cost Centre or Job Title*. |
| `BulletsTitle`, `Bullets` | What a not-connected entry needs, or what happens next, as plain-text lines. |
| `Summary` | A not-connected entry as plain text to paste into a ticket, ending with the evaluation time in UTC. |
| `Scoping` | One explanation per Synchronisation Rule: `SyncRuleId`, `SyncRuleName`, `Direction`, `Outcome` (`InScope`, `OutOfScope` or `Undetermined`), `HasCriteria`, `EvaluatedAt`, `Hint`, `Groups` (the criteria tree) and `Criteria`, a flat list of every criterion with `Path`, `Met`, `Outcome`, `AttributeName`, `AttributeType`, `ComparisonType`, `Expected`, `Actual` (the value the outcome turned on; empty when several values all went the same way), `ValueCount`, `Masked`, `Description` and `ActualDescription`. |
| `Conflicts` | Enabled export Synchronisation Rules that cannot connect because this connection holds the Metaverse Object's one slot in the Connected System with an object of another type: `SyncRuleId`, `SyncRuleName`, `TargetObjectTypeName`, `ExistingObjectTypeName`, `Description` and `Scoping`. |
| `EvaluatedAt` | When every scoping evaluation was made, in UTC. |

A criterion's `Path` locates it in the tree: the top-level group's position, then each child's position within its group, one-based and dot-separated, criteria counted before child groups (`1.3.2` is the second child of the third child of the first top-level group). Values of credential attributes are withheld: `Masked` is `$true` and `Expected` and `Actual` are empty.

#### Examples

```powershell title="See where a Metaverse Object is connected, and why it is not connected elsewhere"
Get-JIMMetaverseObjectConnection -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -IncludeNotConnected |
    Format-Table ConnectedSystem, Connected, State, Hint
```

```powershell title="Copy the explanation for one Connected System into a ticket"
(Get-JIMMetaverseObjectConnection -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -IncludeNotConnected -ConnectedSystemName "Finance App").Summary
```

```powershell title="List every scoping criterion the Metaverse Object currently fails"
Get-JIMMetaverseObjectConnection -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -IncludeNotConnected |
    ForEach-Object { $_.Scoping } |
    ForEach-Object { $_.Criteria } |
    Where-Object { -not $_.Met } |
    Format-Table Path, Description, ActualDescription
```

```powershell title="See how each connection was joined"
Get-JIMMetaverseObject -AttributeName "Account Name" -AttributeValue jsmith |
    Get-JIMMetaverseObjectConnection |
    Format-Table ConnectedSystem, Object, @{ Name = 'Joined'; Expression = { $_.Join.Description } }
```

```powershell title="Report everyone of a type who is out of scope of an export rule, and why"
Get-JIMMetaverseObject -ObjectTypeName "Person" -All |
    Get-JIMMetaverseObjectConnection -IncludeNotConnected -ConnectedSystemName "Finance App" -ErrorAction SilentlyContinue |
    Where-Object { $_.State -eq 'NotInScope' } |
    Format-Table MetaverseObjectName, SyncRule, Hint
```

---

### Get-JIMGeneratedValue

Lists the generated values a Metaverse Object currently holds (Unique Value Generation): the
committed value, which uniqueness token produced it, the Synchronisation Rule and mapping responsible,
and its state. Empty when the object holds none. Configure a generated Attribute Flow with `New-JIMSyncRuleMapping -Generate`; see
[Synchronisation Rules](synchronisation-rules.md#new-jimsyncrulemapping).

#### Syntax

```powershell
Get-JIMGeneratedValue -MetaverseObjectId <guid>
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `MetaverseObjectId` | `guid` | Yes | | The Metaverse Object's ID. Accepts pipeline input by property name. Alias: `Id` |

#### Output

One `PSCustomObject` per generated value:

| Property | Description |
|----------|--------------|
| `AssignmentId` | The assignment's own identifier |
| `MetaverseAttributeId` | The Metaverse Attribute this value was generated for |
| `AttributeName` | Its name |
| `Value` | The committed value |
| `TokenKind` | `OnlyIfTaken`, `Sequence` or `Random` |
| `SyncRuleId` | The Synchronisation Rule whose generated mapping produced this value |
| `SyncRuleName` | Its name |
| `SyncRuleMappingId` | The mapping responsible |
| `State` | `Proposed`, `Committed`, `Remediated` or `NeedsDecision` |
| `AssignedDate` | When the assignment was created |

#### Examples

```powershell title="List everything JIM generated for an Identity"
Get-JIMGeneratedValue -MetaverseObjectId "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
```

```powershell title="Pipe a Metaverse Object straight in"
Get-JIMMetaverseObject -ObjectTypeName "person" -Search "j.smith" | Get-JIMGeneratedValue
```

### Get-JIMRetiredGeneratedValue

Lists an attribute's retired generated values, newest first: the values JIM issued for it and will never issue again, whichever Attribute Flow generates it. Every page is read, so the whole list is returned. Read-only; the only way to forget retired values is `Restart-JIMGeneratedValues` on a sequence. See [Retired values](../configuration/generated-values.md#retired-values) for when a value is retired.

#### Syntax

```powershell
# An import flow's Metaverse Attribute, by ID
Get-JIMRetiredGeneratedValue -MetaverseAttributeId <int> [-Search <string>]

# An import flow's Metaverse Attribute, by name
Get-JIMRetiredGeneratedValue -MetaverseAttributeName <string> [-Search <string>]

# An export flow's Connected System attribute
Get-JIMRetiredGeneratedValue -ConnectedSystemId <int> -ObjectTypeId <int> -AttributeId <int> [-Search <string>]
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `MetaverseAttributeId` | `int` | Yes (by ID) | | The Metaverse Attribute's ID. Accepts pipeline input by property name, so a Metaverse Attribute from `Get-JIMMetaverseAttribute` pipes straight in. Alias: `Id` |
| `MetaverseAttributeName` | `string` | Yes (by name) | | The Metaverse Attribute's name, for example `Account Name` |
| `ConnectedSystemId` | `int` | Yes (export) | | The Connected System whose attribute an export flow generates |
| `ObjectTypeId` | `int` | Yes (export) | | The Connected System Object Type the attribute belongs to |
| `AttributeId` | `int` | Yes (export) | | The Connected System attribute's ID |
| `Search` | `string` | No | | Text matched, ignoring case, against the value and the name of the object that held it |

#### Output

One `PSCustomObject` per retired value:

| Property | Description |
|----------|--------------|
| `Id` | The entry's own identifier |
| `MetaverseAttributeId` | The Metaverse Attribute (import flow); empty for an export flow |
| `ConnectedSystemObjectTypeAttributeId` | The Connected System attribute (export flow); empty for an import flow |
| `AttributeName` | The attribute's name |
| `Value` | The value as it was issued |
| `RetiredAt` | When it was retired (UTC) |
| `Reason` | `ObjectDeleted`, `Superseded` or `Recalled` (`Regenerated` is reserved for a later release) |
| `FromObjectId` | The object that held it: a Metaverse Object, or for an export flow a Connected System Object |
| `FromObjectDisplayName` | That object's name, as it was when the value was retired |
| `FromObjectExists` | Whether that object still exists |
| `ActivityId` | The Activity during which it was retired, when one was recorded (a removed flow records none) |
| `HeldBy` | `FromObjectDisplayName` for reading, with `(deleted)` added once the object is gone |

#### Examples

```powershell title="List every retired Account Name"
Get-JIMRetiredGeneratedValue -MetaverseAttributeName "Account Name"
```

```powershell title="Find out whether a leaver's account name is retired, and why"
Get-JIMRetiredGeneratedValue -MetaverseAttributeName "Account Name" -Search "fenwick" |
    Select-Object Value, RetiredAt, Reason, HeldBy
```

```powershell title="Pipe a Metaverse Attribute straight in"
Get-JIMMetaverseAttribute -Name "Employee Number" | Get-JIMRetiredGeneratedValue
```

---

## Set-JIMMetaverseObjectPassword

Sets a Metaverse Object's password, on the Connected System Objects you name or on every Connected System configured for Password Synchronisation.

One command, aimed one of two ways. With `-ConnectedSystemId`, the password goes to the Metaverse Object's Connected System Objects in those systems: the reset case, where you chose the password for the person. Without it, the password goes to every Connected System configured for [Password Synchronisation](../concepts/password-synchronisation.md) in which the Metaverse Object has a Connected System Object: the event case, where their password changed somewhere and the rest should hold it. Both go through the same queue and the same [Password Delivery Service](../concepts/password-synchronisation.md#the-password-delivery-service), which makes the first attempt within about a second, whatever the synchronisation engine is doing; what differs is the defaults, set out under Parameters.

Supply the password with `-Password`, or have JIM generate one that satisfies the discovered policy of every Connected System the Metaverse Object has a Connected System Object in with `-Generate`. A generated password is returned to you, once, on `GeneratedPassword`; JIM holds its own copy, encrypted, only until the systems have it.

### Syntax

```powershell
# Named Connected System Objects: expires at next sign-in, waits up to 10 seconds
Set-JIMMetaverseObjectPassword -Id <guid> -ConnectedSystemId <int[]> -Password <securestring>
    [-ExpiryBehaviour <string>] [-EnableAccount] [-Wait <int>] [-Force]

Set-JIMMetaverseObjectPassword -Id <guid> -ConnectedSystemId <int[]> -Generate
    [-ExpiryBehaviour <string>] [-EnableAccount] [-Wait <int>] [-Force]

# Every configured system: expiry left to each system, returns on enqueue
Set-JIMMetaverseObjectPassword -Id <guid> -Password <securestring>
    [-ExpiryBehaviour <string>] [-Wait <int>] [-Force]

Set-JIMMetaverseObjectPassword -Id <guid> -Generate
    [-ExpiryBehaviour <string>] [-Wait <int>] [-Force]
```

!!! tip "Use `-Generate` rather than choosing a password yourself"
    One password has to satisfy the strictest of several systems at once, and their password policies are not
    something you can see in order to reason about them. JIM can: `-Generate` has it reconcile the discovered
    policies of every Connected System the password is going to (the longest minimum length any of them demands,
    and only the character categories all of them count) and produce a password that satisfies all of them.

    Where no single password can satisfy them all, JIM refuses outright rather than handing back one that would
    be accepted on the first Connected System Object and refused on the second, after the first has already changed. A system
    JIM could read no policy from is reported as a warning, because the password is about to go there and JIM
    cannot promise it will be accepted.

    The generated password is returned on the result's `GeneratedPassword` property as a SecureString.
    **That is the only chance to capture it.**

```powershell title="Set one compliant password across two named systems, and read it back"
$result = Set-JIMMetaverseObjectPassword -Id 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f -ConnectedSystemId 1,2 -Generate -Force
ConvertFrom-SecureString -SecureString $result.GeneratedPassword -AsPlainText
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `guid` | Yes | | Metaverse Object identifier. Accepts pipeline input by property name. |
| `ConnectedSystemId` | `int[]` | No | | The Connected Systems to set the password in. The Metaverse Object must have a Connected System Object in every one named; the command refuses, and sets nothing, where it does not. Omit it to propagate to every Connected System configured for Password Synchronisation. |
| `Password` | `securestring` | Yes (unless `-Generate`) | | The password. Encrypted before JIM stores it and held only until delivered. |
| `Generate` | `switch` | Yes (unless `-Password`) | | Have JIM generate the password. With `-ConnectedSystemId`, against the named systems' policies; without, against every system the Metaverse Object has a Connected System Object in. |
| `ExpiryBehaviour` | `string` | No | `RequireChangeAtNextSignIn` with `-ConnectedSystemId`; `ExpiresAccordingToTargetPolicy` without | `RequireChangeAtNextSignIn`, `ExpiresAccordingToTargetPolicy` or `NeverExpires`. The defaults follow who chose the password: you did when naming Connected System Objects; the person did otherwise. |
| `EnableAccount` | `switch` | No | `$false` | Enables the named Connected System Objects as part of setting the password. Without it, the password still enables a new Active Directory account that JIM created disabled only until it had a password ([Enabling new Active Directory accounts](../connectors/jim-ldap-connector-passwords.md#enabling-new-active-directory-accounts)). Only available with `-ConnectedSystemId`: a password propagated to every configured system enables no other Connected System Object, because it reaches objects an administrator may have disabled on purpose. |
| `Wait` | `int` | No | `10` with `-ConnectedSystemId`; `0` without | Seconds, 0 to 30, to wait for the systems to answer. The wait ends early once every target has settled. A script that needs to watch for longer should poll `Get-JIMPendingPasswordChange -MetaverseObjectId` instead. |
| `Force` | `switch` | No | `$false` | Skips the confirmation prompt. |

To set a password on every Connected System Object the Metaverse Object has regardless of which systems are configured for Password Synchronisation, name its systems: `-ConnectedSystemId (Get-JIMMetaverseObject -Id $id).ConnectedSystemObjects.ConnectedSystemId`.

### Output

One `PSCustomObject` describing the change. No property carries the password you supplied.

| Property | Description |
|----------|-------------|
| `ActivityId` | The Activity recording the change. Its child Activities hold each system's outcome once delivery has been attempted. |
| `Origin` | `Explicit` when Connected System Objects were named, `Propagated` when the password went to every configured system. |
| `Settled` | Whether every target had reached an outcome you need not wait on by the time the command returned. A target that is retrying counts as settled: its next attempt is minutes away. |
| `QueuedForNoSystems` | `$true` when a propagated change found no Connected System configured for Password Synchronisation in which the Metaverse Object has a Connected System Object, so nothing was queued. Worth checking: silence here would let a script believe a password propagated when nothing was recorded. |
| `Targets` | One entry per Connected System the change was queued for, in name order. |
| `GeneratedPassword` | The password JIM produced, as a SecureString. Present only with `-Generate`. |

Each entry under `Targets`:

| Property | Description |
|----------|-------------|
| `ConnectedSystemId`, `ConnectedSystemName` | Where it is going. |
| `ConnectedSystemObjectId` | The Connected System Object the password is aimed at, or `$null` where the Metaverse Object has none in this system yet; a propagated change is queued regardless, bounded by its time to live, so it lands when provisioning catches up. |
| `Enabled` | Whether the system is currently taking propagated passwords. `$false` on a propagated change means it is held until somebody switches the system on; a named Connected System Object is delivered to either way. |
| `State` | `Queued`, `Delivering`, `Set`, `Retrying`, `Parked`, `Held`, `Expired` or `Cancelled`. |
| `NextAttemptAt` | When the next attempt falls due, for a target that is `Retrying`; `$null` otherwise. |
| `Message` | The system's own words on its most recent outcome (why it refused, or that the password was set), or `$null` before anything has been said. |
| `AttemptCount` | How many delivery attempts this system has had. |
| `FailureReason` | Why the most recent attempt failed: `Transient`, `ConfigurationFault`, `PolicyRejection`, `TargetObjectNotFound` or `UnsupportedOperation`. Empty before any attempt and once the password is set. |

A `Parked` target is also reported as a non-terminating error carrying the result as its `TargetObject`, so a script that stops on errors stops on a refusal and can still read the other targets from the exception it caught. A target still in flight when a wait ran out is reported as a warning.

### Examples

```powershell title="Reset a password in two named Connected Systems and see what each did with it"
$password = Read-Host -AsSecureString "New password"
$result = Set-JIMMetaverseObjectPassword -Id 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f -ConnectedSystemId 1,2 -Password $password
$result.Targets | Select-Object ConnectedSystemName, State, Message
```

```powershell title="Record a password change and return at once"
$password = Read-Host -AsSecureString "New password"
Set-JIMMetaverseObjectPassword -Id 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f -Password $password -Force
```

```powershell title="Propagate, wait up to ten seconds, and report which systems took the password"
$result = Set-JIMMetaverseObjectPassword -Id $id -Password $password -Wait 10 -Force
$result.Targets | Select-Object ConnectedSystemName, State, Message
if (-not $result.Settled) {
    Write-Warning "Not every system had answered after 10 seconds; check the Metaverse Object's Password tab."
}
```

A service desk script uses this to tell the caller their reset has landed before they hang up: `State` is `Set` where it has, `Retrying` with a `NextAttemptAt` where a directory was unreachable, and `Parked` with the directory's own `Message` where it refused.

```powershell title="Catch the case where nothing was queued"
$result = Set-JIMMetaverseObjectPassword -Id $id -Password $password -Force
if ($result.QueuedForNoSystems) { Write-Warning "No system is configured to receive this Metaverse Object's password changes." }
```

```powershell title="Set one generated password on every Connected System Object the Metaverse Object has, whatever is configured"
$systems = (Get-JIMMetaverseObject -Id $id).ConnectedSystemObjects.ConnectedSystemId
Set-JIMMetaverseObjectPassword -Id $id -ConnectedSystemId $systems -Generate -Force
```

### Notes

- **This resets the passwords on whichever Connected System Objects you point it at.** Anyone who can call it can reset the password of any Connected System Object in these Connector Spaces, subject only to what each Connected System's service account is permitted to do.
- Each Connected System is delivered to on its own. One refusing does not stop the others, and the person is left with a different password there until you deal with it, so check every target rather than the first.
- A refused password will be refused again if you resend it. Generate a different one, and set it on every Connected System Object rather than only the one that failed, or the person ends up with two.
- A named system whose Password Synchronisation is switched off is still delivered to; the switch governs propagated changes, and you named the Connected System Object. A propagated change to that system is held until it is switched on.

---

## Pending Deletions

### Get-JIMPendingDeletion

Retrieves Metaverse Objects that are pending deletion. Supports listing individual items, returning a count, or a summary breakdown by object type.

#### Syntax

```powershell
# List (default)
Get-JIMPendingDeletion [-ObjectTypeId <int>] [-Page <int>] [-PageSize <int>]

# Count
Get-JIMPendingDeletion [-ObjectTypeId <int>] -Count

# Summary
Get-JIMPendingDeletion -Summary
```

#### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `ObjectTypeId` | `int` | No | | Filter by object type ID (List and Count parameter sets only) |
| `Page` | `int` | No | `1` | Page number for paginated results (List parameter set only) |
| `PageSize` | `int` | No | `25` | Number of results per page, maximum 100 (List parameter set only) |
| `Count` | `switch` | No | `false` | Return only the total count of pending deletions |
| `Summary` | `switch` | No | `false` | Return a summary breakdown by object type |

#### Output

Depending on the parameter set:

- **List**: pending deletion items including object details and scheduled deletion date. The object type is returned as a nested `Type` object with `Id` and `Name` properties.
- **Count**: total number of pending deletions as an integer
- **Summary**: breakdown of pending deletion counts grouped by object type

#### Examples

```powershell title="List all pending deletions"
Get-JIMPendingDeletion
```

```powershell title="Get pending deletions for a specific object type"
$personType = Get-JIMMetaverseObjectType -Name "Person"
Get-JIMPendingDeletion -ObjectTypeId $personType.Id -PageSize 50
```

```powershell title="Get a count of all pending deletions"
Get-JIMPendingDeletion -Count
```

```powershell title="Get a summary breakdown by object type"
Get-JIMPendingDeletion -Summary
```

---

## See also

- [Metaverse Object Types](metaverse-object-types.md): the Object Types Metaverse Objects belong to
- [Metaverse Attributes](metaverse-attributes.md): the attributes they hold, and Attribute Priority
- [Metaverse](../configuration/metaverse.md): object types, attributes, objects, and pending deletions
- [Synchronisation Rules](synchronisation-rules.md)
- [Connected Systems](connected-systems.md)
