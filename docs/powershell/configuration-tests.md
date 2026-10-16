---
title: Configuration Tests
description: "The cmdlets that save, list, change, delete and run configuration test cases for Attribute Flows, Generated Values, Scope and Object Matching Rules."
type: reference
audience: automation-author
---

# Configuration Tests

The configuration test cmdlets save test cases and run them. A test case holds inputs, and what an Attribute Flow, a Generated Value's base, a Synchronisation Rule's Scope or an Object Matching Rule should give for them. Running the cases after a change shows whether the change broke what they expect. What a test case is, how a run compares results and when a case needs review is covered in [Configuration tests](../configuration/configuration-tests.md).

A case's inputs take the shape `Test-JIMExpression`, `Test-JIMScope` and `Test-JIMMatchingRule` take, so a test you tried can be saved with the same values. Inputs are stored unencrypted, as other configuration is: prefer made-up values to a real person's.

---

## Get-JIMConfigurationTestCase

Gets saved configuration test cases, each with its inputs: every case, those covering a Synchronisation Rule, those under a Connected System, or one by ID.

<!-- BEGIN GENERATED: Get-JIMConfigurationTestCase -->
### Syntax

```powershell
# All (default)
Get-JIMConfigurationTestCase [-Name <string>]

# ById
Get-JIMConfigurationTestCase -Id <int>

# BySyncRule
Get-JIMConfigurationTestCase -SyncRuleId <int> [-Name <string>]

# ByConnectedSystem
Get-JIMConfigurationTestCase -ConnectedSystemId <int> [-Name <string>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes (ById) |  | The ID of the test case to get. Accepts pipeline input by property name. |
| `SyncRuleId` | `int` | Yes (BySyncRule) |  | Lists the cases covering this Synchronisation Rule: its Scope, its Attribute Flows and its own Object Matching Rules. |
| `ConnectedSystemId` | `int` | Yes (ByConnectedSystem) |  | Lists the cases under this Connected System: each of its Synchronisation Rules', and its Simple mode Object Matching Rules'. |
| `Name` | `string` | No (All, BySyncRule, ByConnectedSystem) |  | Lists only the cases whose name matches. Supports wildcards. |

### Output

Each test case, with its inputs (CsAttributes and MvAttributes for an Attribute Flow or Generated Value case, Attributes otherwise) and its Expectation.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `int` | The ID of the test case. |
| `Name` | `string` | What the case checks, in the words of whoever saved it. |
| `Description` | `string` | A longer description, or empty. |
| `Kind` | `string` | What the case tests: an Attribute Flow's Expression, a Generated Value's base, a Synchronisation Rule's Scope or an Object Matching Rule's match values. One of `AttributeFlow`, `GeneratedValue`, `Scope` or `ObjectMatchingRule`. |
| `SyncRuleMappingId` | `int` | The Attribute Flow an `AttributeFlow` or `GeneratedValue` case covers. |
| `SyncRuleId` | `int` | The Synchronisation Rule a `Scope` case covers. |
| `ObjectMatchingRuleId` | `int` | The Object Matching Rule an `ObjectMatchingRule` case covers. |
| `RunAsOf` | `datetime` | The instant the case runs as of, in UTC: what `Now` and `Today` read, and what relative date criteria resolve against. Empty runs it as of the time it runs. |
| `CsAttributes` | `hashtable` | For an Attribute Flow or Generated Value case, the Connected System Object's values by attribute name: a list of text values, empty for an attribute with no value. Left out of a list unless it asks for `include=inputs`. |
| `MvAttributes` | `hashtable` | For an Attribute Flow or Generated Value case, the Metaverse Object's values by attribute name. Left out of a list unless it asks for `include=inputs`. |
| `Attributes` | `hashtable` | For a Scope or Object Matching Rule case, the object's values by attribute name. Left out of a list unless it asks for `include=inputs`. |
| `Expectation` | `object` | What the case expects. It has `Kind`, `Values` and `InOrder`. |
| `Created` | `datetime` | When the case was saved, in UTC. |
| `CreatedByName` | `string` | Who saved the case. |
| `LastUpdated` | `datetime` | When the case was last changed, in UTC, or empty when it never has been. |
| `LastUpdatedByName` | `string` | Who last changed the case. |
<!-- END GENERATED: Get-JIMConfigurationTestCase -->

### Examples

```powershell title="List the test cases covering a Synchronisation Rule"
Get-JIMConfigurationTestCase -SyncRuleId 12
```

```powershell title="List the test cases under a Connected System by name"
Get-JIMConfigurationTestCase -ConnectedSystemId 3 -Name 'Hire date*' | Select-Object Id, Name, Kind
```

---

## New-JIMConfigurationTestCase

Saves a test case covering one Attribute Flow (`-SyncRuleMappingId`, with `-Kind GeneratedValue` for a Generated Value's base), one Synchronisation Rule's Scope (`-SyncRuleId`) or one Object Matching Rule (`-ObjectMatchingRuleId`). Give every attribute the configuration reads, with `$null` for one that should have no value.

<!-- BEGIN GENERATED: New-JIMConfigurationTestCase -->
### Syntax

```powershell
# AttributeFlow (default)
New-JIMConfigurationTestCase -Name <string> -SyncRuleMappingId <int> -Expect <string> [-Description <string>] [-Kind <string>] [-CsAttributes <hashtable>] [-MvAttributes <hashtable>] [-RunAsOf <datetime>] [-ExpectedValues <object[]>] [-InOrder] [-WhatIf] [-Confirm]

# Scope
New-JIMConfigurationTestCase -Name <string> -SyncRuleId <int> -Expect <string> [-Description <string>] [-Attributes <hashtable>] [-RunAsOf <datetime>] [-ExpectedValues <object[]>] [-InOrder] [-WhatIf] [-Confirm]

# ObjectMatchingRule
New-JIMConfigurationTestCase -Name <string> -ObjectMatchingRuleId <int> -Expect <string> [-Description <string>] [-Attributes <hashtable>] [-RunAsOf <datetime>] [-ExpectedValues <object[]>] [-InOrder] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | Yes |  | What the case checks, in your own words. |
| `Description` | `string` | No |  | A longer description. |
| `SyncRuleMappingId` | `int` | Yes (AttributeFlow) |  | The Attribute Flow the case covers. The case tests its Expression, or for a Generated Value its base. |
| `Kind` | `string` | No (AttributeFlow) | `'AttributeFlow'` | For an Attribute Flow, AttributeFlow (the default) or GeneratedValue, which tests a Generated Value's base: the value before the uniqueness token is added. One of `AttributeFlow` or `GeneratedValue`. |
| `SyncRuleId` | `int` | Yes (Scope) |  | The Synchronisation Rule whose Scope the case covers. |
| `ObjectMatchingRuleId` | `int` | Yes (ObjectMatchingRule) |  | The Object Matching Rule the case covers. The case compares the rule's match values and never looks them up. |
| `CsAttributes` | `hashtable` | No (AttributeFlow) |  | For an Attribute Flow or Generated Value case, the Connected System Object's values, keyed by attribute name. `$null` is no value, and an array gives several values. A [datetime] is sent as ISO 8601. |
| `MvAttributes` | `hashtable` | No (AttributeFlow) |  | For an Attribute Flow or Generated Value case, the Metaverse Object's values, keyed by attribute name, as for CsAttributes. |
| `Attributes` | `hashtable` | No (Scope, ObjectMatchingRule) |  | For a Scope or Object Matching Rule case, the object's values, keyed by attribute name, as for CsAttributes. |
| `RunAsOf` | `datetime` | No |  | The instant to run the case as of: what Now() and Today() read, and what relative date criteria resolve against. Omit it to run as of the time the case runs. A time without a time zone is read as UTC. |
| `Expect` | `string` | Yes |  | The outcome expected. Values, NoValue, Error or AnyValue for an Attribute Flow, Generated Value or Object Matching Rule case; InScope or OutOfScope for a Scope case. One of `Values`, `NoValue`, `Error`, `AnyValue`, `InScope` or `OutOfScope`. |
| `ExpectedValues` | `object[]` | No |  | With `-Expect` Values, the values expected. Each is compared with what the configuration gives as a value of its type, so a date can be written 2026-03-02. |
| `InOrder` | `switch` | No |  | With `-Expect` Values, requires the values to come in the order given. By default they compare in any order, each repeat counted. |

### Output

The saved test case.

Properties as under [Get-JIMConfigurationTestCase](#get-jimconfigurationtestcase).
<!-- END GENERATED: New-JIMConfigurationTestCase -->

### Examples

```powershell title="Check an Attribute Flow's result"
New-JIMConfigurationTestCase -Name 'Hire date as ISO' -SyncRuleMappingId 9 -CsAttributes @{ hireDate = '2026-03-02' } -Expect Values -ExpectedValues '2026-03-02'
```

```powershell title="Check a Scope on a fixed date"
New-JIMConfigurationTestCase -Name 'Leavers drop out of scope' -SyncRuleId 12 -Attributes @{ leaveDate = '2026-01-15' } -RunAsOf '2026-03-01' -Expect OutOfScope
```

```powershell title="Check a Generated Value's base"
New-JIMConfigurationTestCase -Name 'Account name base' -SyncRuleMappingId 14 -Kind GeneratedValue -MvAttributes @{ 'First Name' = 'Joe'; 'Last Name' = 'Bloggs' } -Expect Values -ExpectedValues 'jbloggs'
```

```powershell title="Check an Object Matching Rule's match value"
New-JIMConfigurationTestCase -Name 'Matches on employee number' -ObjectMatchingRuleId 6 -Attributes @{ employeeNumber = ' 00123 ' } -Expect Values -ExpectedValues '00123'
```

---

## Set-JIMConfigurationTestCase

Changes a test case's name, description, inputs, run-as-of time or expectation; anything not given stays as it is. What the case covers and what it tests cannot change.

<!-- BEGIN GENERATED: Set-JIMConfigurationTestCase -->
### Syntax

```powershell
Set-JIMConfigurationTestCase [-Id] <int> [-Name <string>] [-Description <string>] [-CsAttributes <hashtable>] [-MvAttributes <hashtable>] [-Attributes <hashtable>] [-RunAsOf <datetime>] [-ClearRunAsOf] [-Expect <string>] [-ExpectedValues <object[]>] [-InOrder] [-PassThru] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes |  | The ID of the test case to change. Can be given by position, as the first argument. Accepts pipeline input by property name. |
| `Name` | `string` | No |  | A new name. |
| `Description` | `string` | No |  | A new description. `$null` (or '') clears it. |
| `CsAttributes` | `hashtable` | No |  | For an Attribute Flow or Generated Value case, the Connected System Object's values, replacing those the case holds for that side. `$null` is no value, and an array gives several values. |
| `MvAttributes` | `hashtable` | No |  | For an Attribute Flow or Generated Value case, the Metaverse Object's values, replacing those the case holds for that side. |
| `Attributes` | `hashtable` | No |  | For a Scope or Object Matching Rule case, the object's values, replacing those the case holds. |
| `RunAsOf` | `datetime` | No |  | A new instant to run the case as of. A time without a time zone is read as UTC. |
| `ClearRunAsOf` | `switch` | No |  | Runs the case as of the time it runs rather than a fixed instant. |
| `Expect` | `string` | No |  | A new expected outcome, replacing the case's expectation: Values, NoValue, Error or AnyValue for an Attribute Flow, Generated Value or Object Matching Rule case; InScope or OutOfScope for a Scope case. One of `Values`, `NoValue`, `Error`, `AnyValue`, `InScope` or `OutOfScope`. |
| `ExpectedValues` | `object[]` | No |  | With `-Expect` Values, the values expected. |
| `InOrder` | `switch` | No |  | With `-Expect` Values, requires the values to come in the order given. |
| `PassThru` | `switch` | No |  | Returns the changed test case. |

### Output

With `-PassThru`, the changed test case; otherwise nothing.

Properties as under [Get-JIMConfigurationTestCase](#get-jimconfigurationtestcase).
<!-- END GENERATED: Set-JIMConfigurationTestCase -->

### Examples

```powershell title="Change what a test case expects"
Set-JIMConfigurationTestCase -Id 4 -Expect Values -ExpectedValues 'BLOGGS'
```

```powershell title="Replace one side's inputs"
Set-JIMConfigurationTestCase -Id 4 -CsAttributes @{ sn = 'Smith'; givenName = $null }
```

```powershell title="Clear a description"
Set-JIMConfigurationTestCase -Id 4 -Description $null
```

---

## Remove-JIMConfigurationTestCase

Deletes a test case. Nothing it covered changes.

<!-- BEGIN GENERATED: Remove-JIMConfigurationTestCase -->
### Syntax

```powershell
Remove-JIMConfigurationTestCase [-Id] <int> [-Force] [-WhatIf] [-Confirm]
```

It asks for confirmation before it acts: `-WhatIf` shows what it would do, and `-Force` or `-Confirm:$false` skips the prompt.

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Id` | `int` | Yes |  | The ID of the test case to delete. Can be given by position, as the first argument. Accepts pipeline input by property name. |
| `Force` | `switch` | No |  | Suppresses the confirmation prompt. |

### Output

None.
<!-- END GENERATED: Remove-JIMConfigurationTestCase -->

### Examples

```powershell title="Delete a test case"
Remove-JIMConfigurationTestCase -Id 4
```

```powershell title="Delete every matching test case without asking"
# Deletes every case covering Synchronisation Rule 12 whose name starts with "Draft".
# Run the Get-JIMConfigurationTestCase part alone first to see which cases match.
Get-JIMConfigurationTestCase -SyncRuleId 12 -Name 'Draft*' | Remove-JIMConfigurationTestCase -Force
```

---

## Invoke-JIMConfigurationTest

Runs the test cases covering a Synchronisation Rule, those under a Connected System, or those piped in, and returns each result. `-FailOnUnsuccessful` also writes an error for each case that did not pass, so a script can stop on it.

<!-- BEGIN GENERATED: Invoke-JIMConfigurationTest -->
### Syntax

```powershell
# ById (default)
Invoke-JIMConfigurationTest -Id <int[]> [-FailOnUnsuccessful]

# BySyncRule
Invoke-JIMConfigurationTest -SyncRuleId <int> [-FailOnUnsuccessful]

# ByConnectedSystem
Invoke-JIMConfigurationTest -ConnectedSystemId <int> [-FailOnUnsuccessful]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `SyncRuleId` | `int` | Yes (BySyncRule) |  | Runs the cases covering this Synchronisation Rule: its Scope, its Attribute Flows and its own Object Matching Rules. |
| `ConnectedSystemId` | `int` | Yes (ByConnectedSystem) |  | Runs the cases under this Connected System: each of its Synchronisation Rules', and its Simple mode Object Matching Rules'. |
| `Id` | `int[]` | Yes (ById) |  | The IDs of the test cases to run. Cases piped in are run together, in one request. Accepts pipeline input by property name. |
| `FailOnUnsuccessful` | `switch` | No |  | Writes an error for each case that did not pass, after returning every result. |

### Output

Each case's result.

| Property | Type | Description |
|----------|------|-------------|
| `TestCaseId` | `int` | The ID of the test case that ran. |
| `TestCaseName` | `string` | The name of the test case that ran. |
| `Kind` | `string` | What the case tests. One of `AttributeFlow`, `GeneratedValue`, `Scope` or `ObjectMatchingRule`. |
| `Status` | `string` | How the run came out. Failed: `expected` and `actual` say how. NeedsReview: the case no longer fits the configuration and was not run, and `problems` says how. Error: the case could not be run. One of `Passed`, `Failed`, `NeedsReview` or `Error`. |
| `Summary` | `string` | One sentence on how the run came out and why. |
| `Problems` | `string[]` | For NeedsReview, each way the case no longer fits the configuration; empty otherwise. |
| `Expected` | `object` | What the case expects. It has `Kind`, `Values` and `InOrder`. |
| `Actual` | `object` | What the configuration gave; empty when the case was not run. It has `Outcome`, `Values` and `ErrorMessage`. |
| `RanAsOf` | `datetime` | The instant, in UTC, the case ran as of. |
| `RanAsOfNow` | `bool` | True when the case has no run-as-of time and what it covers reads the clock, so its result can change from one run to the next. |
<!-- END GENERATED: Invoke-JIMConfigurationTest -->

### Examples

```powershell title="Run the test cases covering a Synchronisation Rule"
Invoke-JIMConfigurationTest -SyncRuleId 12
```

```powershell title="List the test cases that did not pass"
Invoke-JIMConfigurationTest -ConnectedSystemId 3 | Where-Object Status -ne 'Passed' | Select-Object TestCaseName, Status, Summary
```

```powershell title="Stop a deployment script when a test case does not pass"
Invoke-JIMConfigurationTest -ConnectedSystemId 3 -FailOnUnsuccessful -ErrorAction Stop
```
