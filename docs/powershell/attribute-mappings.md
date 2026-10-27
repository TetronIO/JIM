---
title: Attribute Mappings
description: "The cmdlets that list, create, change and delete the Attribute Flows on a Synchronisation Rule, including generated values."
type: reference
audience: automation-author
---

# Attribute Mappings

The Attribute Mapping cmdlets list, create, change and delete the Attribute Flows on a Synchronisation Rule: what each Connected System or Metaverse attribute, expression or generated value flows to. How Attribute Flows work, and how the value that wins is chosen, is covered in [Attribute Mappings](../configuration/attribute-mappings.md).

---

## :material-swap-vertical: Get-JIMSyncRuleMapping

Retrieves Attribute Flow mappings for a Synchronisation Rule. Returns all mappings for the rule, or a specific mapping by ID.

<!-- BEGIN GENERATED: Get-JIMSyncRuleMapping -->
### Syntax

```powershell
# List (default)
Get-JIMSyncRuleMapping -SyncRuleId <int>

# ById
Get-JIMSyncRuleMapping -SyncRuleId <int> [-MappingId <int>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes |  | The ID of the Synchronisation Rule. Accepts pipeline input by property name, `SyncRuleId` or `Id`. |
| `MappingId` | `int` | No (ById) |  | The ID of the mapping to get. Without it, every mapping on the rule comes back. |

### Output

The rule's mappings, or the one mapping. On a generated mapping, Generation.Participants says where its value is checked for availability, and how; Generation.Exclusions lists the Connected Systems left out.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The ID of the mapping. |
| `Created` | `datetime` | When the mapping was created (UTC). |
| `TargetMetaverseAttributeId` | `int` | The ID of the Metaverse attribute an import mapping writes; empty on an export mapping. |
| `TargetMetaverseAttributeName` | `string` | The name of that Metaverse attribute; empty on an export mapping. |
| `TargetConnectedSystemAttributeId` | `int` | The ID of the Connected System attribute an export mapping writes; empty on an import mapping. |
| `TargetConnectedSystemAttributeName` | `string` | The name of that Connected System attribute; empty on an import mapping. |
| `SourceType` | `string` | What the mapping reads: AttributeMapping for attributes, ExpressionMapping for an expression, AdvancedMapping for a mix of the two, GeneratedMapping for a generated value, or NotSet when it has no source. |
| `InboundValueProcessing` | `string` | The text processing an import mapping applies on the way into the Metaverse, as a comma-separated set of flags such as TreatWhitespaceAsNoValue and TrimWhitespace. Only meaningful on an import mapping to a Text attribute. |
| `CaseNormalisation` | `string` | The case an import mapping converts a text value to. Only meaningful on an import mapping to a Text attribute. One of `None`, `Upper`, `Lower` or `Title`. |
| `Priority` | `int` | The mapping's place in its attribute's Attribute Priority order: the lowest number with a value wins. 2147483647 marks a mapping not yet put in order, which never wins. Only meaningful on an import mapping. |
| `NullIsValue` | `bool` | Whether an import mapping that applies to a joined, in-scope object but has no value clears the Metaverse attribute, rather than letting a lower-priority mapping supply one. Only meaningful on an import mapping. |
| `InitialExportOnly` | `bool` | Whether an export mapping writes only when it creates the Connected System Object; after that JIM leaves the attribute alone, and Drift Correction does not put it back. Only meaningful on an export mapping. |
| `Enabled` | `bool` | Whether the mapping takes part in synchronisation; a disabled mapping is skipped in both directions. |
| `DisabledReason` | `string` | Why JIM disabled the mapping, when it did so itself; empty when the mapping is enabled, or an administrator disabled it. |
| `KeepUnique` | `bool` | On an Expression mapping whose value is built from one generated value, whether a target refusing the value as already in use is fixed by correcting the generated value. True by default; no effect on any other mapping. |
| `BuiltFromGeneratedValue` | `object` | On an Expression mapping whose value is built from a generated value: which one, through which attributes, and how JIM treats it. Empty on every other mapping. It has `SyncRuleMappingId`, `SyncRuleId`, `SyncRuleName`, `GeneratedSyncRuleMappingId`, `GeneratedAttributeName`, `BuiltFromAttributeNames`, `MetaverseAttributeName`, `ConnectedSystemAttributeName`, `Via`, `Kind`, `KeepUnique`, `CollisionRemediation`, `ExportedTo`, `CheckedBeforeIssuing`, `ProbedIn` and `MoveToSyncRuleName`. |
| `Sources` | `object[]` | What the mapping reads: the attributes or expression its value comes from. Empty on a generated mapping with no base expression. Each has `Id`, `Order`, `MetaverseAttributeId`, `MetaverseAttributeName`, `ConnectedSystemAttributeId`, `ConnectedSystemAttributeName`, `Expression` and `MissingInputBehaviour`. |
| `Generation` | `object` | The settings of a generated mapping: its token, how it is checked for availability, and what is built from it. Empty unless SourceType is GeneratedMapping. It has `TokenKind`, `SuffixStyle`, `SuffixStart`, `SequenceStart`, `SequenceIncrement`, `FixedWidth`, `OnWidthExceeded`, `RandomFormat`, `RandomLength`, `Separator`, `AttemptLimit`, `NeverReuse`, `CollisionRemediation`, `SequenceSkippedAhead`, `RetiredValueCount`, `Exclusions`, `Participants` and `BuiltValues`. |
| `Warnings` | `string[]` | Warnings the create or update raised about the mapping without stopping it, such as a derived Attribute Flow calling a function that returns a different value each time. Empty on a read. |
| `Derived` | `object` | On a Metaverse-Derived Attribute Flow (an import expression reading mv["..."]): the Metaverse attributes it reads and its step in the evaluation order. Empty on every other mapping. It has `Step`, `StepCount` and `MetaverseInputs`. |
| `DependentDerivedFlows` | `object[]` | On the response to an update that disabled the mapping, rewrote its expression or changed what it writes: the Metaverse-Derived Attribute Flows the change left with a missing input. The update went ahead; each flow's Missing Input Behaviour decides what it contributes. Empty on a read. Each has `MappingId`, `TargetMetaverseAttributeName`, `SyncRuleId`, `SyncRuleName`, `ConnectedSystemId`, `ConnectedSystemName` and `MissingInputs`. |
<!-- END GENERATED: Get-JIMSyncRuleMapping -->

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

## :material-plus-circle-outline: New-JIMSyncRuleMapping

Creates a new Attribute Flow mapping on a Synchronisation Rule. Mappings can be direct Attribute Flows (one or more source attributes to a target) or expression-based transformations.

<!-- BEGIN GENERATED: New-JIMSyncRuleMapping -->
### Syntax

```powershell
# ExportGenerated
New-JIMSyncRuleMapping -SyncRuleId <int> -Generate [-TargetConnectedSystemAttributeId <int>] [-Expression <string>] [-TokenKind <string>] [-SuffixStyle <string>] [-SuffixStart <int>] [-SequenceStart <long>] [-SequenceIncrement <int>] [-FixedWidth <int>] [-OnWidthExceeded <string>] [-RandomFormat <string>] [-RandomLength <int>] [-Separator <string>] [-AttemptLimit <int>] [-NeverReuse <bool>] [-CollisionRemediation <bool>] [-Enabled <bool>] [-WhatIf] [-Confirm]

# ImportGenerated
New-JIMSyncRuleMapping -SyncRuleId <int> -Generate [-TargetMetaverseAttributeId <int>] [-Expression <string>] [-TokenKind <string>] [-SuffixStyle <string>] [-SuffixStart <int>] [-SequenceStart <long>] [-SequenceIncrement <int>] [-FixedWidth <int>] [-OnWidthExceeded <string>] [-RandomFormat <string>] [-RandomLength <int>] [-Separator <string>] [-AttemptLimit <int>] [-NeverReuse <bool>] [-CollisionRemediation <bool>] [-ExcludeConnectedSystemId <int[]>] [-Enabled <bool>] [-WhatIf] [-Confirm]

# ExportExpression
New-JIMSyncRuleMapping -SyncRuleId <int> [-TargetConnectedSystemAttributeId <int>] [-Expression <string>] [-MissingInputBehaviour <string>] [-InitialExportOnly] [-Enabled <bool>] [-KeepUnique <bool>] [-WhatIf] [-Confirm]

# ExportAttribute
New-JIMSyncRuleMapping -SyncRuleId <int> [-TargetConnectedSystemAttributeId <int>] [-SourceMetaverseAttributeId <int[]>] [-InitialExportOnly] [-Enabled <bool>] [-WhatIf] [-Confirm]

# ImportExpression
New-JIMSyncRuleMapping -SyncRuleId <int> [-TargetMetaverseAttributeId <int>] [-Expression <string>] [-MissingInputBehaviour <string>] [-PreserveWhitespace] [-TrimWhitespace] [-CollapseInternalWhitespace] [-CaseNormalisation <string>] [-NullIsValue] [-Enabled <bool>] [-KeepUnique <bool>] [-WhatIf] [-Confirm]

# ImportAttribute
New-JIMSyncRuleMapping -SyncRuleId <int> [-TargetMetaverseAttributeId <int>] [-SourceConnectedSystemAttributeId <int[]>] [-PreserveWhitespace] [-TrimWhitespace] [-CollapseInternalWhitespace] [-CaseNormalisation <string>] [-NullIsValue] [-Enabled <bool>] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes |  | The ID of the Synchronisation Rule to add the mapping to. Accepts pipeline input by property name, `SyncRuleId` or `Id`. |
| `TargetMetaverseAttributeId` | `int` | No (ImportGenerated, ImportExpression, ImportAttribute) |  | On an import rule, the ID of the Metaverse attribute the mapping writes. |
| `TargetConnectedSystemAttributeId` | `int` | No (ExportGenerated, ExportExpression, ExportAttribute) |  | On an export rule, the ID of the Connected System attribute the mapping writes. |
| `SourceConnectedSystemAttributeId` | `int[]` | No (ImportAttribute) |  | On an import rule, the IDs of the Connected System attributes the mapping reads. Cannot be used with `-Expression`. |
| `SourceMetaverseAttributeId` | `int[]` | No (ExportAttribute) |  | On an export rule, the IDs of the Metaverse attributes the mapping reads. Cannot be used with `-Expression`. |
| `Expression` | `string` | No (ExportGenerated, ImportGenerated, ExportExpression, ImportExpression) |  | The expression the mapping evaluates, reading attributes as mv["Name"] and cs["Name"]: for example '"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=domain,DC=local"'. With `-Generate`, the base value the token is added to. |
| `MissingInputBehaviour` | `string` | No (ExportExpression, ImportExpression) |  | On an expression mapping, what happens when an attribute the expression reads has no value: EvaluateAnyway runs it regardless and contributes what it returns; ContributeNoValue skips it and contributes nothing, so Attribute Priority decides; FailMapping skips it and records an error, while the object's other attributes still flow; and FailObject leaves the whole object untouched and errored. EvaluateAnyway when omitted. One of `EvaluateAnyway`, `ContributeNoValue`, `FailMapping` or `FailObject`. |
| `Generate` | `switch` | Yes (ExportGenerated, ImportGenerated) |  | Makes the mapping a generated value: a base value from `-Expression`, which a Sequence or Random token can do without, plus a uniqueness token. Give `-TargetMetaverseAttributeId` for an import mapping, or `-TargetConnectedSystemAttributeId` for an export one. |
| `TokenKind` | `string` | No (ExportGenerated, ImportGenerated) |  | The uniqueness token: OnlyIfTaken tries the base value first and adds a suffix only when it is taken, and needs `-Expression`; Sequence adds the next number from the attribute's counter, never reused; Random adds a random token (see `-RandomFormat`). OnlyIfTaken when omitted. One of `OnlyIfTaken`, `Sequence` or `Random`. |
| `SuffixStyle` | `string` | No (ExportGenerated, ImportGenerated) |  | OnlyIfTaken only: whether the collision suffix is a Number or a Letter. Omit for Number. One of `Number` or `Letter`. |
| `SuffixStart` | `int` | No (ExportGenerated, ImportGenerated) |  | OnlyIfTaken only: the first suffix value tried once the bare base value is taken. Omit for 1. |
| `SequenceStart` | `long` | No (ExportGenerated, ImportGenerated) |  | Sequence only: the lowest number this flow will ever issue. If this stands above the target attribute's counter, the save moves the counter forward to it and the result's Generation.SequenceSkippedAhead reports the move; a warning is written naming the old and new positions. Omit for 1. |
| `SequenceIncrement` | `int` | No (ExportGenerated, ImportGenerated) |  | Sequence only: how much the counter advances per issued number. Omit for 1. |
| `FixedWidth` | `int` | No (ExportGenerated, ImportGenerated) |  | Sequence only: zero-pads the number to this many digits. Omit for no padding. |
| `OnWidthExceeded` | `string` | No (ExportGenerated, ImportGenerated) |  | Sequence only, and only meaningful with `-FixedWidth`: what happens when a number would no longer fit. StopAndReport (the default) stops the object with an attributed error; AllowLonger lets the number grow past the configured width. One of `StopAndReport` or `AllowLonger`. |
| `RandomFormat` | `string` | No (ExportGenerated, ImportGenerated) |  | Random only: the shape of the token. Guid (the default, a 36-character GUID), Hex (lower-case hexadecimal) or Digits (decimal digits). `-RandomLength` is required for Hex and Digits. One of `Guid`, `Hex` or `Digits`. |
| `RandomLength` | `int` | No (ExportGenerated, ImportGenerated) |  | Random only: the length of the token in characters. Required for `-RandomFormat` Hex or Digits; must be omitted for Guid, whose length is fixed. |
| `Separator` | `string` | No (ExportGenerated, ImportGenerated) |  | The characters placed between the base value and the token, when both are present. Omit for no separator. Never valid for a Number target. |
| `AttemptLimit` | `int` | No (ExportGenerated, ImportGenerated) |  | How many candidates are tried for one object in one synchronisation run before generation fails for it. 1000 when omitted. |
| `NeverReuse` | `bool` | No (ExportGenerated, ImportGenerated) |  | Whether a value whose assignment is deleted is retired and never issued again by this flow. Omit for `$true` (the server default and recommended setting). Always treated as true for a Sequence token, regardless of what is supplied, because its forward-only counter makes reuse impossible. |
| `CollisionRemediation` | `bool` | No (ExportGenerated, ImportGenerated) |  | Whether JIM corrects a generated value a target rejects as already in use (Collision Remediation). While no other Connected System holds the value, JIM chooses the next free one and exports it again; once another does, the export is held for a decision instead (see `Get-JIMGeneratedValueDecision`). Acts only on a rejection from a Connected System whose Connector reports collisions (Generation.Participants' ReportsCollisions); elsewhere a collision is an ordinary export error. Omit for `$true`. |
| `ExcludeConnectedSystemId` | `int[]` | No (ImportGenerated) |  | Import generated mappings only: the IDs of Connected Systems to leave out of the value's availability checks. Values already in use in an excluded Connected System do not stop JIM choosing them; JIM neither checks its own records of that system nor probes it. Each must be a Connected System the value is exported to unchanged (an export Attribute Flow taking the generated Metaverse attribute as its only source); otherwise the request is refused naming the Connected System. Omit to check every Connected System the value is exported to. `Get-JIMSyncRuleMapping`'s Generation.Participants lists them. |
| `PreserveWhitespace` | `switch` | No (ImportExpression, ImportAttribute) |  | On an import mapping, keeps a text value that is empty or only whitespace as a value. Without it, such a value counts as no value: it does not flow, and it clears the Metaverse attribute. |
| `TrimWhitespace` | `switch` | No (ImportExpression, ImportAttribute) |  | On an import mapping, removes leading and trailing whitespace from a text value. |
| `CollapseInternalWhitespace` | `switch` | No (ImportExpression, ImportAttribute) |  | On an import mapping, collapses each run of whitespace inside a text value to one space. |
| `CaseNormalisation` | `string` | No (ImportExpression, ImportAttribute) | `'None'` | On an import mapping, the case a text value is converted to. One of `None`, `Upper`, `Lower` or `Title`. |
| `NullIsValue` | `switch` | No (ImportExpression, ImportAttribute) |  | On an import mapping, makes no value authoritative: when the rule applies to a joined, in-scope Connected System Object that has no value, the Metaverse attribute is cleared rather than taken from a lower-priority mapping. Change it later with `Set-JIMMetaverseAttributePriority` or `Move-JIMMetaverseAttributePriority`. |
| `InitialExportOnly` | `switch` | No (ExportExpression, ExportAttribute) |  | On an export mapping, writes the attribute only when the Connected System Object is created; after that JIM leaves it alone, and Drift Correction does not put it back. |
| `Enabled` | `bool` | No |  | Whether the mapping takes part in synchronisation from the start. `$false` creates it disabled, so it can be put in order and reviewed first; enable it later with `Set-JIMSyncRuleMapping`. |
| `KeepUnique` | `bool` | No (ExportExpression, ImportExpression) |  | On an Expression mapping whose value is built from a generated value (an Email built from a generated Account Name), whether a target refusing the value as already in use is fixed by correcting the generated value. `$false` reports the refusal as an ordinary export error instead. Not accepted on a generated mapping. |

### Output

The mapping created. Any warnings the save raised are written to the warning stream as well.

Properties as under [Get-JIMSyncRuleMapping](#get-jimsyncrulemapping).
<!-- END GENERATED: New-JIMSyncRuleMapping -->

### Notes

- Several source attributes are kept in the order given.
- Expressions read attributes as `mv["AttributeName"]` and `cs["AttributeName"]`; [Expressions](../concepts/expressions.md) lists the functions.
- Every generation setting is optional: an omitted one takes the default its parameter states.

### Examples

```powershell title="Direct import: one Connected System attribute into a Metaverse attribute"
New-JIMSyncRuleMapping -SyncRuleId 5 `
    -SourceConnectedSystemAttributeId 10 `
    -TargetMetaverseAttributeId 3
```

```powershell title="Expression import: build a Metaverse attribute from two Connected System attributes"
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

```powershell title="Direct export: one Metaverse attribute out to a Connected System attribute"
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

## :material-pencil-outline: Set-JIMSyncRuleMapping

Changes the settings on an existing Attribute Flow, leaving what it reads and writes alone. Only the parameters you supply are changed.

!!! note "Generated-mapping parameters"
    `-TokenKind` and the other generated-mapping settings below apply only to a mapping already using **Generated Value**; create one with `New-JIMSyncRuleMapping -Generate`.

<!-- BEGIN GENERATED: Set-JIMSyncRuleMapping -->
### Syntax

```powershell
# ById (default)
Set-JIMSyncRuleMapping -SyncRuleId <int> -MappingId <int> [-Expression <string>] [-MissingInputBehaviour <string>] [-NullIsValue <bool>] [-InboundValueProcessing <string>] [-CaseNormalisation <string>] [-InitialExportOnly <bool>] [-Enabled <bool>] [-KeepUnique <bool>] [-TokenKind <string>] [-SuffixStyle <string>] [-SuffixStart <int>] [-SequenceStart <long>] [-SequenceIncrement <int>] [-FixedWidth <int>] [-OnWidthExceeded <string>] [-RandomFormat <string>] [-RandomLength <int>] [-Separator <string>] [-AttemptLimit <int>] [-NeverReuse <bool>] [-CollisionRemediation <bool>] [-ExcludeConnectedSystemId <int[]>] [-SaveAnyway] [-AcceptNewResult] [-PassThru] [-WhatIf] [-Confirm]

# ByInputObject
Set-JIMSyncRuleMapping -SyncRuleId <int> -InputObject <PSCustomObject> [-Expression <string>] [-MissingInputBehaviour <string>] [-NullIsValue <bool>] [-InboundValueProcessing <string>] [-CaseNormalisation <string>] [-InitialExportOnly <bool>] [-Enabled <bool>] [-KeepUnique <bool>] [-TokenKind <string>] [-SuffixStyle <string>] [-SuffixStart <int>] [-SequenceStart <long>] [-SequenceIncrement <int>] [-FixedWidth <int>] [-OnWidthExceeded <string>] [-RandomFormat <string>] [-RandomLength <int>] [-Separator <string>] [-AttemptLimit <int>] [-NeverReuse <bool>] [-CollisionRemediation <bool>] [-ExcludeConnectedSystemId <int[]>] [-SaveAnyway] [-AcceptNewResult] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes |  | The ID of the Synchronisation Rule the mapping belongs to. |
| `MappingId` | `int` | Yes (ById) |  | The ID of the mapping to change. Accepts pipeline input by property name, `MappingId` or `Id`. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | A mapping from `Get-JIMSyncRuleMapping`, through the pipeline. Accepts pipeline input. |
| `Expression` | `string` | No |  | Replaces the mapping's expression. Expression mappings only. |
| `MissingInputBehaviour` | `string` | No |  | On an expression mapping, what happens when an attribute the expression reads has no value: EvaluateAnyway runs it regardless and contributes what it returns; ContributeNoValue skips it and contributes nothing, so Attribute Priority decides; FailMapping skips it and records an error, while the object's other attributes still flow; and FailObject leaves the whole object untouched and errored. One of `EvaluateAnyway`, `ContributeNoValue`, `FailMapping` or `FailObject`. |
| `NullIsValue` | `bool` | No |  | On an import mapping, whether no value is authoritative: when the rule applies to a joined, in-scope Connected System Object that has no value, the Metaverse attribute is cleared rather than taken from a lower-priority mapping. |
| `InboundValueProcessing` | `string` | No |  | Text value-processing transforms applied as the value flows to the Metaverse, as a comma-separated set of flag names (for example 'TreatWhitespaceAsNoValue, TrimWhitespace'). Import mappings only. |
| `CaseNormalisation` | `string` | No |  | On an import mapping, the case a text value is converted to. One of `None`, `Upper`, `Lower` or `Title`. |
| `InitialExportOnly` | `bool` | No |  | On an export mapping, whether the attribute is written only when the Connected System Object is created; after that JIM leaves it alone, and Drift Correction does not put it back. |
| `Enabled` | `bool` | No |  | Enables or disables the mapping. A disabled mapping is skipped in both directions; enabling it clears the reason JIM recorded for disabling it, if any. |
| `KeepUnique` | `bool` | No |  | On an Expression mapping whose value is built from a generated value (an Email built from a generated Account Name), whether a target refusing the value as already in use is fixed by correcting the generated value. `$false` reports the refusal as an ordinary export error instead. Changing it releases any decision the generated value is waiting on. Refused on any other mapping. |
| `TokenKind` | `string` | No |  | Changes a generated mapping's uniqueness token. A mapping cannot be made generated, or stop being generated, here: delete it and create it again. One of `OnlyIfTaken`, `Sequence` or `Random`. |
| `SuffixStyle` | `string` | No |  | OnlyIfTaken only: whether the collision suffix is a Number or a Letter. One of `Number` or `Letter`. |
| `SuffixStart` | `int` | No |  | OnlyIfTaken only: the first suffix value tried once the bare base value is taken. |
| `SequenceStart` | `long` | No |  | Sequence only: the lowest number this flow will ever issue. Raising it above the target attribute's counter moves the counter forward at save time; the response's Generation.SequenceSkippedAhead reports the move, and a warning names the old and new positions. A lower or equal value has no effect. |
| `SequenceIncrement` | `int` | No |  | Sequence only: how much the counter advances per issued number. |
| `FixedWidth` | `int` | No |  | Sequence only: zero-pads the number to this many digits. Supply 0 to clear the padding; omit to leave it unchanged. |
| `OnWidthExceeded` | `string` | No |  | Sequence only: what happens when a number would no longer fit `-FixedWidth`. StopAndReport stops the object with an attributed error; AllowLonger lets the number grow past the width. One of `StopAndReport` or `AllowLonger`. |
| `RandomFormat` | `string` | No |  | Random only: the shape of the token. One of `Guid`, `Hex` or `Digits`. |
| `RandomLength` | `int` | No |  | Random only: the length of the token in characters. Required alongside `-RandomFormat` Hex or Digits; must not be supplied for Guid. |
| `Separator` | `string` | No |  | The characters placed between the base value and the token. Supply an empty string ('' or `$null`) to clear it; omit to leave it unchanged. |
| `AttemptLimit` | `int` | No |  | The maximum number of candidates tried, per object per synchronisation run, before generation fails hard for that object. |
| `NeverReuse` | `bool` | No |  | Whether a value whose assignment is deleted is retired and never issued again by this flow. Always treated as true for a Sequence token, whatever is supplied. |
| `CollisionRemediation` | `bool` | No |  | Whether JIM corrects a generated value a target rejects as already in use (Collision Remediation). Omit to leave it unchanged. Changing it releases any of this Attribute Flow's values held for a decision, so the next export tries again under the new setting. |
| `ExcludeConnectedSystemId` | `int[]` | No |  | Generated import mappings only: replaces the IDs of the Connected Systems left out of the value's availability checks. Values already in use in an excluded Connected System do not stop JIM choosing them. Each must be a Connected System the value is exported to unchanged, otherwise the update is refused naming the Connected System. Pass @() to clear every exclusion, so every Connected System is checked again; omit to leave the exclusions unchanged. Refused for a generated value on an export Synchronisation Rule, which is checked only in its own Connected System. |
| `SaveAnyway` | `switch` | No |  | Saves even though the change stops configuration test cases passing. Without it, or `-AcceptNewResult`, such a change is refused, nothing is saved, and the error names each case. Configuration History records the cases saved past, by name. |
| `AcceptNewResult` | `switch` | No |  | Takes what the configuration now gives as what each configuration test case that stopped passing expects, rewriting those cases, and saves. A case whose new result cannot be expected (it needs review, could not run, or its Scope cannot be determined) still refuses the change unless `-SaveAnyway` is given too. |
| `PassThru` | `switch` | No |  | Returns the mapping as changed. |

### Output

With `-PassThru`, the mapping as changed; otherwise nothing. Its warnings, and any derived Attribute Flows the change left with a missing input, are written to the warning stream either way.

Properties as under [Get-JIMSyncRuleMapping](#get-jimsyncrulemapping).
<!-- END GENERATED: Set-JIMSyncRuleMapping -->

### Notes

- What a mapping **targets**, and whether its source is an attribute or an expression, cannot be changed here. Those revalidate against attribute types and plurality, and for an import mapping they reopen its place in the [Attribute Priority](../concepts/attribute-priority.md) order, so they remain a `Remove-JIMSyncRuleMapping` followed by a `New-JIMSyncRuleMapping`.
- A setting that does not apply to the mapping is refused rather than ignored: `-NullIsValue` on an export mapping, `-InitialExportOnly` on an import mapping, an expression setting on a direct Attribute Flow, or a generation setting on a mapping that is not generated.
- A call naming no setting is refused too, rather than reported as a successful update.
- A mapping's place in the Attribute Priority order is changed with `Set-JIMMetaverseAttributePriority` or `Move-JIMMetaverseAttributePriority`, not here.

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

```powershell title="Make every expression mapping on a Synchronisation Rule record an error on a missing input"
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

## :material-delete-outline: Remove-JIMSyncRuleMapping

Deletes an Attribute Flow mapping from a Synchronisation Rule.

<!-- BEGIN GENERATED: Remove-JIMSyncRuleMapping -->
### Syntax

```powershell
# ById (default)
Remove-JIMSyncRuleMapping -SyncRuleId <int> -MappingId <int> [-KeepContributedValues] [-Force] [-WhatIf] [-Confirm]

# ByInputObject
Remove-JIMSyncRuleMapping -SyncRuleId <int> -InputObject <PSCustomObject> [-KeepContributedValues] [-Force] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes |  | The ID of the Synchronisation Rule the mapping belongs to. |
| `MappingId` | `int` | Yes (ById) |  | The ID of the mapping to delete. Accepts pipeline input by property name, `MappingId` or `Id`. |
| `InputObject` | `PSCustomObject` | Yes (ByInputObject) |  | A mapping from `Get-JIMSyncRuleMapping`, through the pipeline. Accepts pipeline input. |
| `KeepContributedValues` | `switch` | No |  | Keeps the Metaverse attribute values the mapping contributed, instead of leaving them to be recalled at the next Full Synchronisation of the contributing Connected System. The kept values have no provenance, so nothing can ever recall them, and the other contributors to those attributes are not re-elected. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt, and the count of contributed values it states. |

### Output

None. Derived Attribute Flows the deletion left with a missing input are written to the warning stream.
<!-- END GENERATED: Remove-JIMSyncRuleMapping -->

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

```powershell title="Delete every mapping on a Synchronisation Rule, without confirmation"
Get-JIMSyncRuleMapping -SyncRuleId 5 |
    Remove-JIMSyncRuleMapping -SyncRuleId 5 -Force
```

This deletes every mapping on the rule. Run it with `-WhatIf` in place of `-Force` first to see what it would delete.

---

## :material-alert-octagon-outline: Derived Attribute Flow warnings

Deleting or disabling a mapping or a Synchronisation Rule can take away the last enabled contributor of a Metaverse attribute that a [derived Attribute Flow](../configuration/derived-attributes.md) reads. The change still goes ahead; the response's `DependentDerivedFlows` names each derived flow it left with a missing input, and `Remove-JIMSyncRuleMapping`, `Set-JIMSyncRuleMapping`, `Set-JIMSyncRule`, `Remove-JIMSyncRule` and `Import-JIMConnectedSystemSchema` write them with `Write-Warning`: a summary line, then one line per flow. They never prompt and never stop.

```text
WARNING: This change left 2 Attribute Flow(s) deriving Metaverse attributes with a missing input. The change went ahead; each flow's Missing Input Behaviour now decides what it contributes.
WARNING: Email (Synchronisation Rule 'Directory Import', Connected System 'Directory', mapping 2) reads Account Name, which no longer has an enabled contributor.
WARNING: Display Name (Synchronisation Rule 'Directory Import', Connected System 'Directory', mapping 3) reads Account Name through Email, which no longer has an enabled contributor.
```

Each `DependentDerivedFlows` entry has `MappingId`, `TargetMetaverseAttributeName`, `SyncRuleId`, `SyncRuleName`, `ConnectedSystemId`, `ConnectedSystemName` and `MissingInputs`; each missing input has `MetaverseAttributeName` (the attribute that lost its last contributor), `Indirect`, and `Via` (the derived attributes in between, empty when read directly). Collect the warnings with `-WarningVariable` to act on them in a script.

---

## :material-book-open-variant-outline: See also

- [Attribute Mappings](../configuration/attribute-mappings.md): sources, expressions, value processing and Attribute Priority
- [Deriving Metaverse Attributes](../configuration/derived-attributes.md): Attribute Flows that build a Metaverse attribute from others
- [Generated Values](generated-values.md): the values a generated Attribute Flow issues, and the decisions they can wait on
- [Synchronisation Rules](synchronisation-rules.md): the rules the Attribute Flows belong to
- [Metaverse Attributes](metaverse-attributes.md): the attributes an import flow writes, and their priority
