---
title: Attribute Mappings
description: "The cmdlets that list, create, change and delete the Attribute Flows on a Synchronisation Rule, including generated values."
type: reference
audience: automation-author
---

# Attribute Mappings

The Attribute Mapping cmdlets list, create, change and delete the Attribute Flows on a Synchronisation Rule: what each Connected System or Metaverse attribute, expression or generated value flows to. How Attribute Flows work, and how the value that wins is chosen, is covered in [Attribute Mappings](../configuration/attribute-mappings.md).

---

## Get-JIMSyncRuleMapping

Retrieves Attribute Flow mappings for a Synchronisation Rule. Returns all mappings for the rule, or a specific mapping by ID.

### Syntax

```powershell
# All mappings for a Synchronisation Rule
Get-JIMSyncRuleMapping -SyncRuleId <int>

# Specific mapping
Get-JIMSyncRuleMapping -SyncRuleId <int> -MappingId <int>
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes | | The ID of the Synchronisation Rule. Accepts pipeline input. Alias: `Id`. |
| `MappingId` | `int` | No | | The ID of a specific mapping to retrieve |

### Output

Returns one or more mapping objects representing Attribute Flow Rules. Each mapping includes the source attribute(s) or expression, the target attribute, and the flow direction.

`Derived` is present on a mapping whose import Expression reads `mv["..."]` (a [derived Attribute Flow](../configuration/derived-attributes.md)), and is `$null` on every other mapping:

| Property | Type | Description |
|----------|------|-------------|
| `Derived.Step` | `int` | The step the flow is evaluated at, from 2 (step 1 is the ordinary Attribute Flow); `$null` when the flow is on, or depends on, a loop |
| `Derived.StepCount` | `int` | How many steps the Metaverse Object Type's evaluation has |
| `Derived.MetaverseInputs` | `string[]` | The Metaverse attributes the Expression reads, as written |

`Generation` is present on a [generated mapping](../configuration/generated-values.md), and is `$null` on every other mapping. Besides the uniqueness token settings, it carries where the value is [checked for availability](../configuration/generated-values-in-target-systems.md#checking-availability-in-target-systems):

| Property | Type | Description |
|----------|------|-------------|
| `Generation.CollisionRemediation` | `bool` | Whether JIM corrects the value when a target rejects it as already in use ([Collision Remediation](../configuration/generated-values-in-target-systems.md#when-a-target-rejects-a-value)) |
| `Generation.Exclusions` | `int[]` | The IDs of the Connected Systems excluded from the value's availability checks; empty when none is |
| `Generation.Participants` | `object[]` | Every Connected System attribute the value is exported to, ordered by Connected System name then attribute name |
| `Generation.RetiredValueCount` | `int` | How many values the target attribute's retired values register holds |
| `Generation.BuiltValues` | `object[]` | Every [value built from](../configuration/generated-values.md#values-built-from-a-generated-value) this generated value, on any Synchronisation Rule, each shaped as `BuiltFromGeneratedValue` below. Import generated mappings only |

Each entry in `Generation.Participants` has:

| Property | Type | Description |
|----------|------|-------------|
| `ConnectedSystemId` | `int` | The Connected System the value is exported to |
| `ConnectedSystemName` | `string` | Its name |
| `ConnectorName` | `string` | Its Connector's name |
| `ConnectedSystemObjectTypeAttributeId` | `int` | The attribute the value is exported as |
| `AttributeName` | `string` | That attribute's name |
| `IsExcluded` | `bool` | Whether the Connected System is excluded |
| `CanBeExcluded` | `bool` | Whether it can be: only where the value is exported to it unchanged from an import generated mapping |
| `Check` | `string` | `JimRecordsAndProbe` (JIM's records of the system, and a probe of the system itself), `JimRecordsOnly` or `NotChecked` |
| `Reason` | `string` | Why the check is less than `JimRecordsAndProbe`: `ConnectorCannotProbe`, `AttributeNotProbed`, `NotTextValue`, `ExportedThroughExpression`, `CombinedWithOtherSources` or `Excluded`; `None` when it is not |
| `ReportsCollisions` | `bool` | Whether the Connected System's Connector reports a value as already in use, so Collision Remediation can act on a rejection there; elsewhere a collision is an ordinary export error |

`KeepUnique` (`bool`) is on every mapping: the **Keep unique** switch of an Expression mapping whose value is [built from a generated value](../configuration/generated-values.md#values-built-from-a-generated-value), `$true` unless switched off. `BuiltFromGeneratedValue` is present on such a mapping, and is `$null` on every other mapping:

| Property | Type | Description |
|----------|------|-------------|
| `BuiltFromGeneratedValue.SyncRuleMappingId` | `int` | The Attribute Flow that builds the value |
| `BuiltFromGeneratedValue.SyncRuleId` | `int` | The Synchronisation Rule holding it |
| `BuiltFromGeneratedValue.SyncRuleName` | `string` | That Synchronisation Rule's name |
| `BuiltFromGeneratedValue.GeneratedSyncRuleMappingId` | `int` | The generated Attribute Flow the value is built from |
| `BuiltFromGeneratedValue.GeneratedAttributeName` | `string` | The generated Metaverse attribute the value is built from, for example `Account Name` |
| `BuiltFromGeneratedValue.BuiltFromAttributeNames` | `string[]` | Every generated Metaverse attribute the value is built from, `GeneratedAttributeName` first |
| `BuiltFromGeneratedValue.MetaverseAttributeName` | `string` | The Metaverse attribute the value is held in; `$null` for an export expression |
| `BuiltFromGeneratedValue.ConnectedSystemAttributeName` | `string` | The Connected System attribute an export expression writes; `$null` for an import mapping |
| `BuiltFromGeneratedValue.Via` | `string[]` | The Metaverse attributes the value is built through, nearest the generated value first; empty when it reads the generated value itself |
| `BuiltFromGeneratedValue.Kind` | `string` | `Form` (built on the generated value's own Synchronisation Rule), `BuiltOnAnotherRule` or `BuiltFromSeveralGeneratedValues` (never checked or corrected) |
| `BuiltFromGeneratedValue.KeepUnique` | `bool` | The value's **Keep unique** switch |
| `BuiltFromGeneratedValue.CollisionRemediation` | `bool` | The generated Attribute Flow's own Collision Remediation switch; while it is `$false`, nothing built from the generated value is corrected, whatever `KeepUnique` says |
| `BuiltFromGeneratedValue.ExportedTo` | `object[]` | Every Connected System attribute the value is written to unchanged, each with `ConnectedSystemId`, `ConnectedSystemName`, `AttributeId` and `AttributeName` |
| `BuiltFromGeneratedValue.CheckedBeforeIssuing` | `bool` | Whether the value is [checked before the generated value is issued](../configuration/generated-values.md#values-built-from-a-generated-value) |
| `BuiltFromGeneratedValue.ProbedIn` | `string[]` | The Connected Systems it is probed in then, besides JIM's records |
| `BuiltFromGeneratedValue.MoveToSyncRuleName` | `string` | For a value built on another Synchronisation Rule from Metaverse attributes alone, the rule to move its flow to so it is checked; `$null` otherwise |

`Warnings` and `DependentDerivedFlows` are always empty on a read; they describe a save.

### Examples

```powershell title="List all mappings for a Synchronisation Rule"
Get-JIMSyncRuleMapping -SyncRuleId 5
```

```powershell title="Get a specific mapping"
Get-JIMSyncRuleMapping -SyncRuleId 5 -MappingId 12
```

```powershell title="Pipeline from Get-JIMSyncRule"
Get-JIMSyncRule -Id 5 | Get-JIMSyncRuleMapping
```

```powershell title="See where a generated value is checked for availability, and how"
(Get-JIMSyncRuleMapping -SyncRuleId 5 -MappingId 12).Generation.Participants |
    Select-Object ConnectedSystemName, AttributeName, Check, Reason
```

```powershell title="List the values built from a generated value, and whether each is checked before it is issued"
(Get-JIMSyncRuleMapping -SyncRuleId 5 -MappingId 12).Generation.BuiltValues |
    Select-Object MetaverseAttributeName, ConnectedSystemAttributeName, SyncRuleName, KeepUnique, CheckedBeforeIssuing, ProbedIn
```

---

## New-JIMSyncRuleMapping

Creates a new Attribute Flow mapping on a Synchronisation Rule. Mappings can be direct Attribute Flows (one or more source attributes to a target) or expression-based transformations.

### Syntax

```powershell
# Import: direct Attribute Flow (CS -> MV)
New-JIMSyncRuleMapping -SyncRuleId <int>
    -SourceConnectedSystemAttributeId <int[]>
    -TargetMetaverseAttributeId <int>

# Import: expression-based flow (CS -> MV)
New-JIMSyncRuleMapping -SyncRuleId <int>
    -Expression <string>
    -TargetMetaverseAttributeId <int>
    [-KeepUnique <bool>]

# Export: direct Attribute Flow (MV -> CS)
New-JIMSyncRuleMapping -SyncRuleId <int>
    -SourceMetaverseAttributeId <int[]>
    -TargetConnectedSystemAttributeId <int>

# Export: expression-based flow (MV -> CS)
New-JIMSyncRuleMapping -SyncRuleId <int>
    -Expression <string>
    -TargetConnectedSystemAttributeId <int>
    [-KeepUnique <bool>]

# Import: generated (Unique Value Generation)
New-JIMSyncRuleMapping -SyncRuleId <int>
    -TargetMetaverseAttributeId <int>
    -Generate
    [-Expression <string>]
    [-TokenKind <string>] [-SuffixStyle <string>] [-SuffixStart <int>]
    [-SequenceStart <long>] [-SequenceIncrement <int>] [-FixedWidth <int>] [-OnWidthExceeded <string>]
    [-RandomFormat <string>] [-RandomLength <int>]
    [-Separator <string>] [-AttemptLimit <int>] [-NeverReuse <bool>]
    [-CollisionRemediation <bool>] [-ExcludeConnectedSystemId <int[]>]

# Export: generated (Unique Value Generation)
New-JIMSyncRuleMapping -SyncRuleId <int>
    -TargetConnectedSystemAttributeId <int>
    -Generate
    [-Expression <string>]
    [-TokenKind <string>] [-SuffixStyle <string>] [-SuffixStart <int>]
    [-SequenceStart <long>] [-SequenceIncrement <int>] [-FixedWidth <int>] [-OnWidthExceeded <string>]
    [-RandomFormat <string>] [-RandomLength <int>]
    [-Separator <string>] [-AttemptLimit <int>] [-NeverReuse <bool>]
    [-CollisionRemediation <bool>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes | | The ID of the Synchronisation Rule. Accepts pipeline input. Alias: `Id`. |
| `TargetMetaverseAttributeId` | `int` | Yes (Import sets) | | The Metaverse attribute to write to (import direction) |
| `TargetConnectedSystemAttributeId` | `int` | Yes (Export sets) | | The Connected System attribute to write to (export direction) |
| `SourceConnectedSystemAttributeId` | `int[]` | Yes (ImportAttribute set) | | One or more Connected System attribute IDs to read from |
| `SourceMetaverseAttributeId` | `int[]` | Yes (ExportAttribute set) | | One or more Metaverse attribute IDs to read from |
| `Expression` | `string` | Yes (ImportExpression, ExportExpression sets); optional (ImportGenerated, ExportGenerated sets) | | A DynamicExpresso expression. Use `mv["Name"]` for Metaverse attributes and `cs["Name"]` for Connected System attributes. For a generated mapping this is the base value the uniqueness token is appended to; a Sequence or Random mapping can omit it entirely. |
| `MissingInputBehaviour` | `string` | No (ImportExpression, ExportExpression sets) | `EvaluateAnyway` | What to do when an attribute the expression reads has no value on the object: `EvaluateAnyway`, `ContributeNoValue`, `FailMapping` or `FailObject`. See [Missing Input Behaviour](../concepts/expressions.md#5-missing-input-behaviour-have-jim-refuse-rather-than-guess). |
| `Enabled` | `bool` | No | `$true` | Create the mapping disabled with `-Enabled $false`, ready to switch on later with `Set-JIMSyncRuleMapping -Enabled $true`. A disabled Attribute Flow is skipped by synchronisation in both directions. |
| `KeepUnique` | `bool` | No (ImportExpression, ExportExpression sets) | `$true` | For an Expression whose value is [built from a generated value](../configuration/generated-values.md#values-built-from-a-generated-value) (an Email built from a generated Account Name): whether the value is checked before the generated value is issued, and a target's refusal of it corrected by correcting the generated value. Supply `$false` to have it neither checked nor corrected: a refusal is then an ordinary export error. Has no effect on an Expression that reads no generated value. |
| `Generate` | `switch` | Yes (ImportGenerated, ExportGenerated sets) | | Makes this a "Generated Value" mapping: the mapping's value is its (optional) base expression plus a uniqueness token, instead of an ordinary attribute or Expression mapping. |
| `TokenKind` | `string` | No | `OnlyIfTaken` | Which uniqueness token to append: `OnlyIfTaken` (try the base value; suffix only if taken; needs `-Expression`), `Sequence` (always append the next counter number, never reused) or `Random` (always append a cryptographic random token). |
| `SuffixStyle` | `string` | No | `Number` | `OnlyIfTaken` only: `Number` or `Letter` for the collision suffix. |
| `SuffixStart` | `int` | No | `1` | `OnlyIfTaken` only: the first suffix value tried once the bare base value is taken. |
| `SequenceStart` | `long` | No | `1` | `Sequence` only: the lowest number this flow will ever issue. If it stands above the target attribute's counter, the save moves the counter forward and warns naming the old and new positions. |
| `SequenceIncrement` | `int` | No | `1` | `Sequence` only: how much the counter advances per issued number. |
| `FixedWidth` | `int` | No | (none) | `Sequence` only: zero-pads the number to this many digits. |
| `OnWidthExceeded` | `string` | No | `StopAndReport` | `Sequence` only, with `-FixedWidth`: `StopAndReport` stops the object with an error; `AllowLonger` lets the number grow past the width. |
| `RandomFormat` | `string` | No | `Guid` | `Random` only: `Guid`, `Hex` or `Digits`. `-RandomLength` is required for `Hex` and `Digits`, and must be omitted for `Guid`. |
| `RandomLength` | `int` | No | (none) | `Random` only: the token length in characters. |
| `Separator` | `string` | No | (none) | The characters between the base value and the token, when both are present. Never valid for a Number target. |
| `AttemptLimit` | `int` | No | `1000` | The maximum number of candidates tried, per object per synchronisation run, before generation fails hard for that object. A sequence skips a run of numbers already held in one step, so the run's length does not count towards the limit. |
| `NeverReuse` | `bool` | No | `$true` | Whether a value whose assignment is deleted is retired and never issued again by this flow. Always treated as `$true` for a `Sequence` token, whatever is supplied. |
| `CollisionRemediation` | `bool` | No | `$true` | Whether JIM corrects a value a target rejects as already in use ([Collision Remediation](../configuration/generated-values-in-target-systems.md#when-a-target-rejects-a-value)): while no other Connected System holds it, JIM chooses the next free value and exports it again; once another does, the export is held for a decision. Acts only where the Connector reports collisions (`Generation.Participants[].ReportsCollisions`). |
| `ExcludeConnectedSystemId` | `int[]` | No (ImportGenerated set only) | (none) | The IDs of Connected Systems to leave out of the value's [availability checks](../configuration/generated-values-in-target-systems.md#checking-availability-in-target-systems): values already in use there do not stop JIM choosing them. Each must be a Connected System the value is exported to unchanged, otherwise the request is refused naming it. Not offered for an export generated mapping, which is checked only in its own Connected System. |

### Output

Returns the created mapping object. A generated mapping's `Generation` property carries its uniqueness token settings, `Generation.Exclusions` and `Generation.Participants` (see [Get-JIMSyncRuleMapping](#get-jimsyncrulemapping)); `Generation.SequenceSkippedAhead` (with `From` and `To`) is present only when `-SequenceStart` raised the target attribute's counter on this save, and a matching warning is written. `Warnings` lists any non-blocking warnings the save raised (empty when there were none); each is also written with `Write-Warning`.

**ShouldProcess impact level:** Medium.

### Notes

- When multiple source attributes are provided, they are automatically ordered by position (0, 1, 2, and so on).
- Expressions use DynamicExpresso syntax with `mv["AttributeName"]` and `cs["AttributeName"]` accessors.
- `MissingInputBehaviour` applies to expression mappings only; a direct Attribute Flow has no inputs to be missing. Omit it to leave the mapping on `EvaluateAnyway`, which is how every mapping created before this parameter existed behaves.
- **Unique Value Generation.** `-Generate` and `-CollisionRemediation` apply to import and export generated mappings alike; `-ExcludeConnectedSystemId` to import generated mappings only.
- Every generation setting is optional: an omitted one leaves the server's own default in place (shown in the table above). Send only the settings you want to change.

### Examples

```powershell title="Direct import: map CS 'givenName' to MV 'firstName'"
New-JIMSyncRuleMapping -SyncRuleId 5 `
    -SourceConnectedSystemAttributeId 10 `
    -TargetMetaverseAttributeId 3
```

```powershell title="Expression import: concatenate CS attributes into MV 'displayName'"
New-JIMSyncRuleMapping -SyncRuleId 5 `
    -Expression 'cs["givenName"] + " " + cs["sn"]' `
    -TargetMetaverseAttributeId 7
```

```powershell title="Expression export: refuse to build a Distinguished Name from a missing value"
New-JIMSyncRuleMapping -SyncRuleId 8 `
    -Expression '"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=company,DC=local"' `
    -TargetConnectedSystemAttributeId 30 `
    -MissingInputBehaviour FailObject
```

```powershell title="Direct export: map MV 'email' to CS 'mail'"
New-JIMSyncRuleMapping -SyncRuleId 8 `
    -SourceMetaverseAttributeId 15 `
    -TargetConnectedSystemAttributeId 22
```

```powershell title="Multiple source attributes for import"
New-JIMSyncRuleMapping -SyncRuleId 5 `
    -SourceConnectedSystemAttributeId 10, 11 `
    -TargetMetaverseAttributeId 7
```

```powershell title="Generated import: try 'first.last' bare, suffix only on collision"
New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 `
    -Expression 'Lower(cs["FirstName"]) + "." + Lower(cs["LastName"])' -Generate
```

```powershell title="Generated import: a zero-padded Employee Number Sequence with no base expression"
New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 12 `
    -Generate -TokenKind Sequence -SequenceStart 100000 -FixedWidth 6
```

```powershell title="Generated export: a random hexadecimal token with no base expression"
New-JIMSyncRuleMapping -SyncRuleId 2 -TargetConnectedSystemAttributeId 40 `
    -Generate -TokenKind Random -RandomFormat Hex -RandomLength 12
```

---

## Set-JIMSyncRuleMapping

Changes the settings on an existing Attribute Flow, leaving what it reads and writes alone. Only the parameters you supply are changed.

!!! note "Generated-mapping parameters"
    `-TokenKind` and the other generated-mapping settings below apply only to a mapping already using **Generated Value**; create one with `New-JIMSyncRuleMapping -Generate`.

### Syntax

```powershell
# By IDs
Set-JIMSyncRuleMapping -SyncRuleId <int>
    -MappingId <int>
    [-Expression <string>]
    [-MissingInputBehaviour <string>]
    [-NullIsValue <bool>]
    [-InboundValueProcessing <string>]
    [-CaseNormalisation <string>]
    [-InitialExportOnly <bool>]
    [-Enabled <bool>]
    [-KeepUnique <bool>]
    [-TokenKind <string>] [-SuffixStyle <string>] [-SuffixStart <int>]
    [-SequenceStart <long>] [-SequenceIncrement <int>] [-FixedWidth <int>] [-OnWidthExceeded <string>]
    [-RandomFormat <string>] [-RandomLength <int>]
    [-Separator <string>] [-AttemptLimit <int>] [-NeverReuse <bool>]
    [-CollisionRemediation <bool>] [-ExcludeConnectedSystemId <int[]>]
    [-PassThru]

# From the pipeline
Get-JIMSyncRuleMapping -SyncRuleId <int> | Set-JIMSyncRuleMapping -SyncRuleId <int> [-MissingInputBehaviour <string>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes | | The ID of the Synchronisation Rule the mapping belongs to |
| `MappingId` | `int` | Yes (ById set) | | The ID of the mapping to update. Accepts pipeline input by property name. Alias: `Id` |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject set) | | A mapping object from the pipeline |
| `Expression` | `string` | No | | Replaces the mapping's expression. Expression mappings only |
| `MissingInputBehaviour` | `string` | No | | `EvaluateAnyway`, `ContributeNoValue`, `FailMapping` or `FailObject`. Expression mappings only. See [Missing Input Behaviour](../concepts/expressions.md#5-missing-input-behaviour-have-jim-refuse-rather-than-guess) |
| `NullIsValue` | `bool` | No | | Whether a contribution of no value is authoritative. Import mappings only |
| `InboundValueProcessing` | `string` | No | | Comma-separated flag names, e.g. `'TreatWhitespaceAsNoValue, TrimWhitespace'`. Import mappings only |
| `CaseNormalisation` | `string` | No | | `None`, `Upper`, `Lower` or `Title`. Import mappings only |
| `InitialExportOnly` | `bool` | No | | Whether the mapping flows only during the initial provisioning export. Export mappings only |
| `Enabled` | `bool` | No | | Enables or disables the mapping. A disabled mapping is skipped by synchronisation in both directions; re-enabling clears any recorded disabled reason. Import and export mappings alike |
| `KeepUnique` | `bool` | No | | The **Keep unique** switch of an Expression whose value is [built from a generated value](../configuration/generated-values.md#values-built-from-a-generated-value): `$true` checks the built value before the generated value is issued and corrects the generated value when a target refuses it; `$false` does neither, so a refusal is an ordinary export error. Changing it releases values of the generated value [held for a decision](generated-values.md#get-jimgeneratedvaluedecision), so the next export tries again under the new setting. Expression mappings only; refused for a direct or generated mapping |
| `TokenKind` | `string` | No | | Changes which uniqueness token a generated mapping appends: `OnlyIfTaken`, `Sequence` or `Random`. Generated mappings only |
| `SuffixStyle` | `string` | No | | `Number` or `Letter`. `OnlyIfTaken` mappings only |
| `SuffixStart` | `int` | No | | The first suffix value tried once the base value is taken. `OnlyIfTaken` mappings only |
| `SequenceStart` | `long` | No | | Raising it above the target attribute's counter moves the counter forward at save time and warns naming the old and new positions; a lower or equal value has no effect. `Sequence` mappings only |
| `SequenceIncrement` | `int` | No | | How much the counter advances per issued number. `Sequence` mappings only |
| `FixedWidth` | `int` | No | | Zero-pads the number to this many digits. Supply `0` to clear the padding; omit to leave it unchanged. `Sequence` mappings only |
| `OnWidthExceeded` | `string` | No | | `StopAndReport` or `AllowLonger`. `Sequence` mappings only |
| `RandomFormat` | `string` | No | | `Guid`, `Hex` or `Digits`. `Random` mappings only |
| `RandomLength` | `int` | No | | The token length in characters. `Random` mappings only |
| `Separator` | `string` | No | | The characters between the base value and the token. Supply `''` or `$null` to clear it; omit to leave it unchanged |
| `AttemptLimit` | `int` | No | | The maximum number of candidates tried, per object per synchronisation run, before generation fails hard for that object. A sequence skips a run of numbers already held in one step, so the run's length does not count towards the limit |
| `NeverReuse` | `bool` | No | | Whether a value whose assignment is deleted is retired and never issued again by this flow. Always treated as `$true` for a `Sequence` token |
| `CollisionRemediation` | `bool` | No | | Whether JIM corrects a value a target rejects as already in use. Changing it releases this Attribute Flow's values [held for a decision](generated-values.md#get-jimgeneratedvaluedecision), so the next export tries again under the new setting. Generated mappings only |
| `ExcludeConnectedSystemId` | `int[]` | No | | Replaces the IDs of the Connected Systems left out of the value's [availability checks](../configuration/generated-values-in-target-systems.md#checking-availability-in-target-systems). Supply `@()` to clear every exclusion; omit to leave them unchanged. Each must be a Connected System the value is exported to unchanged, otherwise the update is refused naming it. Refused for an export generated mapping. Generated mappings only |
| `PassThru` | `switch` | No | `$false` | Returns the updated mapping |

### Output

Nothing by default; the updated mapping when `-PassThru` is supplied. A generated mapping's `Generation` property carries its uniqueness token settings, `Generation.Exclusions` and `Generation.Participants` (see [Get-JIMSyncRuleMapping](#get-jimsyncrulemapping)); `Generation.SequenceSkippedAhead` is present only when `-SequenceStart` raised the target attribute's counter on this save, and a matching warning is written. `Warnings` lists any non-blocking warnings the save raised (empty when there were none); each is written with `Write-Warning` whether or not `-PassThru` is supplied. `Derived` is as for `Get-JIMSyncRuleMapping`. `DependentDerivedFlows` names the Attribute Flows deriving Metaverse attributes that the update left with a missing input (for example by disabling the mapping), written as [derived flow warnings](#derived-attribute-flow-warnings).

**ShouldProcess impact level:** Medium.

### Notes

- What a mapping **targets**, and whether its source is an attribute or an expression, cannot be changed here. Those revalidate against attribute types and plurality, and for an import mapping they reopen its place in the [Attribute Priority](../concepts/attribute-priority.md) order, so they remain a `Remove-JIMSyncRuleMapping` followed by a `New-JIMSyncRuleMapping`.
- A setting that does not apply to the mapping is refused rather than ignored: `-NullIsValue` on an export mapping, `-InitialExportOnly` on an import mapping, or any expression setting on a direct Attribute Flow all return an error. The same applies to a generation setting on a mapping that is not currently a generated mapping; converting a mapping to or from generated is not supported here either (delete and create).
- A call naming no setting is refused too, rather than reported as a successful update.
- Attribute Priority is ordered through its own endpoint and is not settable here.

### Examples

```powershell title="Refuse to export a Distinguished Name built around a missing value"
Set-JIMSyncRuleMapping -SyncRuleId 2 -MappingId 15 -MissingInputBehaviour FailObject
```

```powershell title="Rewrite an import mapping's expression"
Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 8 -Expression 'Lower(cs["mail"])' -PassThru
```

```powershell title="Disable one Attribute Flow without touching the Synchronisation Rule"
Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 8 -Enabled $false
```

```powershell title="Report every expression mapping on a Rule that meets a missing input"
Get-JIMSyncRuleMapping -SyncRuleId 1 |
    Where-Object { $_.sourceType -eq 'ExpressionMapping' } |
    Set-JIMSyncRuleMapping -SyncRuleId 1 -MissingInputBehaviour FailMapping
```

```powershell title="Raise a generated Sequence mapping's Sequence Start"
Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 12 -SequenceStart 500000
```

```powershell title="Clear a generated Sequence mapping's fixed width"
Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 12 -FixedWidth 0
```

```powershell title="Leave two Connected Systems out of a generated value's availability checks"
Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 12 -ExcludeConnectedSystemId 4, 7
```

```powershell title="Check every Connected System again (clear the exclusions)"
Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 12 -ExcludeConnectedSystemId @()
```

```powershell title="Switch Collision Remediation off, so a rejected value is reported as an export error instead"
Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 12 -CollisionRemediation $false
```

```powershell title="Stop checking and correcting the User Principal Name built from Account Name"
Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 14 -KeepUnique $false
```

---

## Remove-JIMSyncRuleMapping

Deletes an Attribute Flow mapping from a Synchronisation Rule.

### Syntax

```powershell
# By IDs
Remove-JIMSyncRuleMapping -SyncRuleId <int> -MappingId <int> [-KeepContributedValues] [-Force]

# By input object
Remove-JIMSyncRuleMapping -SyncRuleId <int> -InputObject <PSCustomObject> [-KeepContributedValues] [-Force]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes | | The ID of the Synchronisation Rule |
| `MappingId` | `int` | Yes (by ID) | | The ID of the mapping to delete. Accepts pipeline input. Alias: `Id`. |
| `InputObject` | `PSCustomObject` | Yes (by object) | | A mapping object from `Get-JIMSyncRuleMapping`. Accepts pipeline input. |
| `KeepContributedValues` | `switch` | No | `$false` | Keeps the Metaverse attribute values the mapping contributed instead of recalling them; their provenance is severed before the mapping is deleted, so nothing can ever recall them. Omit to recall (the default): the values are withdrawn at the next Full Synchronisation of the contributing Connected System, with any other contributing Attribute Flow taking over. |
| `Force` | `switch` | No | `$false` | Suppresses the confirmation prompt (and the impact lookup that would populate it) |

### Output

None. Attribute Flows deriving Metaverse attributes that the deletion leaves with a missing input are written as [derived flow warnings](#derived-attribute-flow-warnings).

**ShouldProcess impact level:** High. Prompts for confirmation unless `-Force` is specified. When the mapping contributed values, the confirmation states how many values, on how many Metaverse Objects, will be recalled or kept.

### Examples

```powershell title="Delete a specific mapping"
Remove-JIMSyncRuleMapping -SyncRuleId 5 -MappingId 12
```

```powershell title="Delete a mapping KEEPING the values it contributed"
Remove-JIMSyncRuleMapping -SyncRuleId 5 -MappingId 12 -KeepContributedValues
```

```powershell title="Force delete without confirmation"
Remove-JIMSyncRuleMapping -SyncRuleId 5 -MappingId 12 -Force
```

```powershell title="Pipeline: remove all mappings for a Synchronisation Rule"
Get-JIMSyncRuleMapping -SyncRuleId 5 |
    Remove-JIMSyncRuleMapping -SyncRuleId 5 -Force
```

---

## Derived Attribute Flow warnings

Deleting or disabling a mapping or a Synchronisation Rule can take away the last enabled contributor of a Metaverse attribute that a [derived Attribute Flow](../configuration/derived-attributes.md) reads. The change still goes ahead; the response's `DependentDerivedFlows` names each derived flow it left with a missing input, and `Remove-JIMSyncRuleMapping`, `Set-JIMSyncRuleMapping`, `Set-JIMSyncRule`, `Remove-JIMSyncRule` and `Import-JIMConnectedSystemSchema` write them with `Write-Warning`: a summary line, then one line per flow. They never prompt and never stop.

```text
WARNING: This change left 2 Attribute Flow(s) deriving Metaverse attributes with a missing input. The change went ahead; each flow's Missing Input Behaviour now decides what it contributes.
WARNING: Email (Synchronisation Rule 'Directory Import', Connected System 'Directory', mapping 2) reads Account Name, which no longer has an enabled contributor.
WARNING: Display Name (Synchronisation Rule 'Directory Import', Connected System 'Directory', mapping 3) reads Account Name through Email, which no longer has an enabled contributor.
```

Each `DependentDerivedFlows` entry has `MappingId`, `TargetMetaverseAttributeName`, `SyncRuleId`, `SyncRuleName`, `ConnectedSystemId`, `ConnectedSystemName` and `MissingInputs`; each missing input has `MetaverseAttributeName` (the attribute that lost its last contributor), `Indirect`, and `Via` (the derived attributes in between, empty when read directly). Collect the warnings with `-WarningVariable` to act on them in a script.

---

## See also

- [Attribute Mappings](../configuration/attribute-mappings.md): sources, expressions, value processing and Attribute Priority
- [Deriving Metaverse Attributes](../configuration/derived-attributes.md): Attribute Flows that build a Metaverse attribute from others
- [Generated Values](generated-values.md): the values a generated Attribute Flow issues, and the decisions they can wait on
- [Synchronisation Rules](synchronisation-rules.md): the rules the Attribute Flows belong to
- [Metaverse Attributes](metaverse-attributes.md): the attributes an import flow writes, and their priority
