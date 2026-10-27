---
title: Expressions
description: "The cmdlets that list the built-in expression functions and test an expression against sample attribute values before a mapping uses it."
type: reference
audience: automation-author
---

# Expressions

Look up the built-in expression functions, and test Synchronisation Rule expressions with sample data before deploying them to production mappings. Expressions use DynamicExpresso syntax with `mv["AttributeName"]` and `cs["AttributeName"]` for attribute access.

## :material-function-variant: Get-JIMExpressionFunction

Lists the built-in functions an expression can call, with each one's signature, parameters, what it returns, a description and an example: the same list the expression editor's **Insert function** menu offers in the portal.

<!-- BEGIN GENERATED: Get-JIMExpressionFunction -->
### Syntax

```powershell
Get-JIMExpressionFunction [[-Name] <string>] [-Category <string>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Name` | `string` | No |  | Only functions with this name, ignoring case. Can be given by position, as the first argument. Supports wildcards. |
| `Category` | `string` | No |  | Only functions in this category. One of `Text`, `Pattern`, `Conditional`, `Conversion`, `Date`, `DistinguishedName`, `Password`, `MultiValued` or `AccountControl`. |

### Output

One object per function.

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | The name an expression calls it by, such as StartsWith. Function names are exact: an expression must use this case. |
| `Category` | `string` | The group it belongs to. One of `Text`, `Pattern`, `Conditional`, `Conversion`, `Date`, `DistinguishedName`, `Password`, `MultiValued` or `AccountControl`. |
| `CategoryName` | `string` | The group's name as the expression editor and the Expressions reference show it, such as "String Functions". |
| `Signature` | `string` | How it is called, with optional parameters in square brackets, such as StartsWith(value, prefix[, ignoreCase]). |
| `Parameters` | `object[]` | Its parameters in order; optional ones last. Each has `Name`, `Description` and `IsOptional`. |
| `Returns` | `string` | What it returns, in a few words, such as "true or false". |
| `Description` | `string` | What it does. May contain Markdown. |
| `Example` | `string` | One call as an administrator would write it. |
| `IsRepeatable` | `bool` | False where it returns a different value each time it is evaluated, such as Now and RandomPassword. An Attribute Flow whose expression calls one is warned about, since its value changes on every synchronisation. |
<!-- END GENERATED: Get-JIMExpressionFunction -->

### Examples

```powershell title="Look up a function's signature and parameters"
$function = Get-JIMExpressionFunction -Name 'RegexReplace'
$function.Signature
$function.Parameters | Select-Object Name, IsOptional, Description
```

```powershell title="Find functions by part of their name"
Get-JIMExpressionFunction -Name '*Date*' | Select-Object Signature, Description
```

```powershell title="List one category"
Get-JIMExpressionFunction -Category MultiValued | Format-Table Name, Returns, Example
```

```powershell title="Find functions that return a different value each time"
# An Attribute Flow whose expression calls one of these gets a new value on every synchronisation.
Get-JIMExpressionFunction | Where-Object { -not $_.IsRepeatable } | Select-Object Name, Description
```

Over REST, the same list is `GET /api/v1/synchronisation/expression-functions`, optionally narrowed with `name` (any part of the name, ignoring case) and `category`.

## :material-test-tube: Test-JIMExpression

Tests an expression with sample Metaverse and Connected System attribute values.

<!-- BEGIN GENERATED: Test-JIMExpression -->
### Syntax

```powershell
# Expression (default)
Test-JIMExpression [-Expression] <string> [-MvAttributes <hashtable>] [-CsAttributes <hashtable>] [-AsOf <datetime>]

# AttributeFlow
Test-JIMExpression [[-Expression] <string>] -SyncRuleId <int> -MappingId <int> [-MvAttributes <hashtable>] [-CsAttributes <hashtable>] [-ConnectedSystemObjectId <guid>] [-MetaverseObjectId <guid>] [-AsOf <datetime>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `Expression` | `string` | Yes (Expression); No (AttributeFlow) |  | The expression to test. Uses DynamicExpresso syntax with mv["AttributeName"] and cs["AttributeName"] for attribute access. It can be left out only for a Generated Value with no base expression, which generates its value from a sequence or random token alone. Can be given by position, as the first argument. |
| `MvAttributes` | `hashtable` | No |  | A hashtable of sample Metaverse attribute values. Keys are attribute names, values are the attribute values. `$null` is no value and an array gives the attribute several values; with an Attribute Flow, empty text is a value distinct from `$null`. |
| `CsAttributes` | `hashtable` | No |  | A hashtable of sample Connected System attribute values. Keys are attribute names, values are the attribute values. `$null` is no value and an array gives the attribute several values; with an Attribute Flow, empty text is a value distinct from `$null`. |
| `SyncRuleId` | `int` | Yes (AttributeFlow) |  | The ID of the Synchronisation Rule whose Attribute Flow to test the expression as. Give it with MappingId. |
| `MappingId` | `int` | Yes (AttributeFlow) |  | The ID of the Attribute Flow to test the expression as. The expression tested is Expression, so an edit can be tried before it is saved. |
| `ConnectedSystemObjectId` | `guid` | No (AttributeFlow) |  | The ID of a Connected System Object whose values to test with, for an import Attribute Flow. Its values for the attributes the expression reads are the inputs, and a value in CsAttributes replaces the object's value for that attribute. An expression that also reads mv["..."] takes those inputs from the Metaverse Object it is joined to. |
| `MetaverseObjectId` | `guid` | No (AttributeFlow) |  | The ID of a Metaverse Object whose values to test with, for an export Attribute Flow. A value in MvAttributes replaces the object's value for that attribute. |
| `AsOf` | `datetime` | No |  | The instant to test as of: what Now() and Today() read. A time without a time zone is read as UTC. |

### Output

The result of evaluating the expression against the sample values. ResultLines gives it as the portal's tester shows it, one value per line. With an Attribute Flow, Outcome, OutcomeDescription and ValueLines say what a synchronisation would give the target attribute, Inputs lists the values each input was given, and IfInputHasNoValue says what each input's absence would do. For a Generated Value, BaseOutcome and BaseValue take the place of Outcome and ValueLines.

| Property | Type | Description |
|----------|------|-------------|
| `IsValid` | `bool` | Indicates whether the expression is valid and evaluated successfully. |
| `Result` | `object` | The result of evaluating the expression (if successful). |
| `ResultLines` | `string[]` | The result as the portal's expression tester shows it: one line per value, each written as JIM writes the value as text (a date as 2026-03-15 10:30:00Z, a Binary value described by its size). Empty when the expression produced no value. Not set when it failed; `ErrorMessage` says why. |
| `ResultType` | `string` | The type of the result (e.g., "String", "Int32", "Boolean"). |
| `ErrorMessage` | `string` | Error message if the expression is invalid or evaluation failed. |
| `ErrorPosition` | `int` | Position in the expression where an error occurred (if applicable). |
| `Outcome` | `string` | What the Attribute Flow's target attribute would be given: values, no value, or an error. Set only when an Attribute Flow was given and its sample values could be used. One of `Values`, `NoValue`, `Error` or `LeftAsHeld`. |
| `ErrorReason` | `string` | Why the outcome is an error. Set only when `Outcome` is Error. One of `MissingInputFailedMapping`, `MissingInputFailedObject`, `EvaluationFailed`, `ResultNotConvertible`, `ResultMultiValued` or `GeneratedBaseNotSingleValue`. |
| `OutcomeDescription` | `string` | What synchronisation would do, in one sentence. Set only when an Attribute Flow was given. |
| `ValueLines` | `string[]` | The values the target attribute would hold after value processing and conversion to its type, one line per value in JIM's text form. Set only when `Outcome` is Values. |
| `TargetAttributeName` | `string` | The Attribute Flow's target attribute. Set only when an Attribute Flow was given. |
| `TargetAttributeType` | `string` | The target attribute's type, which the values are converted to. Set only when an Attribute Flow was given. One of `NotSet`, `Text`, `Number`, `DateTime`, `Binary`, `Reference`, `Guid`, `Boolean`, `LongNumber` or `Decimal`. |
| `MissingInputs` | `string[]` | The inputs the expression reads that have no value, such as `cs["sn"]`, when the Attribute Flow's Missing Input Behaviour stopped it being evaluated. |
| `InputErrors` | `string[]` | Why the sample values could not be used, one message each, such as text that is not a date for a date and time attribute. When present the expression was not evaluated. |
| `Inputs` | `object[]` | The inputs the expression read, with the values they were given: the samples, and any loaded from an object. Set only when an Attribute Flow was given. Each has `Input`, `Type`, `Plurality` and `ValueLines`. |
| `ValueProcessingSteps` | `string[]` | The value processing the Attribute Flow applies to the expression's result, in the order it applies them. Set only for an import Attribute Flow to a Text attribute whose expression returned a result; empty when none is set. Each is one of `TrimWhitespace`, `CollapseInternalWhitespace`, `UpperCase`, `LowerCase`, `TitleCase` or `TreatWhitespaceAsNoValue`. |
| `ValueProcessingChanged` | `bool` | Whether value processing changed the expression's result. Set with `ValueProcessingSteps`. |
| `IfInputHasNoValue` | `object[]` | For each input given a value, what the Attribute Flow would do with that input absent. Set only when an Attribute Flow was given and its sample values could be used. Each has `Input`, `Outcome`, `BaseOutcome`, `ErrorReason` and `OutcomeDescription`. |
| `GeneratedValue` | `bool` | True when the Attribute Flow generates its value (a Generated Value). The test then stops before generation, as synchronisation does: `BaseOutcome` and `BaseValue` say what generation would be given, and `Outcome` is not set. Set only when an Attribute Flow was given. |
| `BaseOutcome` | `string` | For a Generated Value, what its base expression gives generation: a base, a wait (no value yet), no base expression (the token alone is generated), or an error. One of `Base`, `Waits`, `NoBaseExpression` or `Error`. |
| `BaseValue` | `string` | For a Generated Value, the base the uniqueness token would be added to, after value processing. Set only when `BaseOutcome` is Base. Which candidate is free is decided when synchronisation runs. |
<!-- END GENERATED: Test-JIMExpression -->

### Examples

```powershell title="Simple string concatenation"
Test-JIMExpression -Expression 'cs["FirstName"] + " " + cs["LastName"]' -CsAttributes @{
    FirstName = "Jane"
    LastName  = "Smith"
}
```

```powershell title="Generate email address"
Test-JIMExpression -Expression 'Lower(cs["FirstName"]) + "." + Lower(cs["LastName"]) + "@company.com"' -CsAttributes @{
    FirstName = "Jane"
    LastName  = "Smith"
}
```

```powershell title="DN construction with escaping"
Test-JIMExpression -Expression '"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=domain,DC=local"' -MvAttributes @{
    "Display Name" = "O'Brien, Jane"
}
```

```powershell title="Conditional expression"
Test-JIMExpression -Expression 'cs["Department"] == "IT" ? "Technology" : cs["Department"]' -CsAttributes @{
    Department = "IT"
}
```

```powershell title="Test an expression as an Attribute Flow"
# Samples are read as their attributes' types, and the result goes through the Attribute Flow's
# Missing Input Behaviour, value processing and conversion, as a synchronisation would.
$result = Test-JIMExpression -Expression 'FormatDate((DateTime)cs["hireDate"], "yyyy-MM-dd")' -SyncRuleId 12 -MappingId 40 -CsAttributes @{
    hireDate = "2026-03-02"
}
$result.Outcome             # Values, NoValue or Error
$result.ValueLines          # 2026-03-02
$result.OutcomeDescription  # what a synchronisation would do, in one sentence
```

```powershell title="Test an Attribute Flow with a real object's values"
# Loads the Connected System Object's values for the attributes the expression reads, then shows what
# a synchronisation would do and what each input's absence would do.
$result = Test-JIMExpression -Expression 'Lower(cs["givenName"]) + "." + Lower(cs["sn"])' -SyncRuleId 12 -MappingId 41 -ConnectedSystemObjectId '0b0e1c5a-6d3a-4c7e-9a43-6a7b5d2f1e01'
$result.Inputs | Select-Object Input, Type, ValueLines
$result.IfInputHasNoValue | Select-Object Input, Outcome, OutcomeDescription
```

```powershell title="Check for expression errors"
$result = Test-JIMExpression -Expression 'mv["Missing"'
if (-not $result.IsValid) {
    Write-Warning "Expression error at position $($result.ErrorPosition): $($result.ErrorMessage)"
}
```

---

!!! tip "Expression Syntax"
    Expressions use DynamicExpresso syntax. Use `mv["AttributeName"]` for Metaverse attributes and `cs["AttributeName"]` for Connected System attributes. Built-in functions include `Lower()`, `Upper()`, `Trim()`, `EscapeDN()`, `Left()`, `Right()`, `Mid()`, and more. See the [Expressions concept guide](../concepts/expressions.md) for the full function reference.

!!! warning "Test the missing-input case"
    A null result clears the target, and a concatenation with a missing operand produces a malformed value rather than null. Use `Test-JIMExpression` with the relevant attribute **omitted** from `-MvAttributes` / `-CsAttributes` (or, with an Attribute Flow, set to `$null`) to see what happens when an input is absent, before deploying the mapping. See [Nulls, Missing Inputs, and Whitespace](../concepts/expressions.md#nulls-missing-inputs-and-whitespace).

## :material-book-open-variant-outline: See also

- [Concepts: Expressions](../concepts/expressions.md)
- [Synchronisation Rules](synchronisation-rules.md)
