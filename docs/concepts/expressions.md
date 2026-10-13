---
title: Expressions
description: The expression language Synchronisation Rules use to build and transform values, with its functions, null handling and common recipes.
type: concept
audience: administrator
---

# Expressions

When a Synchronisation Rule flows an attribute from one system to another, it often needs to do more than copy a value across unchanged. You might want to build an email address from a first and last name, force a username to lower case, decide whether an account should be enabled based on someone's employment status, or skip an object entirely unless it meets a condition. JIM's **expression language** is how you describe these transformations.

An expression is a short, readable formula that JIM evaluates during synchronisation to produce the value for a target attribute. If you have ever written a formula in a spreadsheet cell, this will feel familiar: you reference attributes much as a spreadsheet references cells, combine them with operators and functions, and JIM works out the result for each object as it is synchronised. No programming experience is required.

## Where Expressions Fit

Expressions live inside **Synchronisation Rules**, in what is known as the **Attribute Flow**: the part of a rule that decides which value lands in each target attribute. Most flows are a direct mapping (copy `cs["mail"]` straight into `mv["Email"]`); you only reach for an expression when a plain copy is not enough.

Within a Synchronisation Rule, expressions are used in:

- **Export attribute mappings**<br /> Transform Metaverse attributes before sending them to a Connected System. An export expression produces one value or several; one value written to a Multi-Valued attribute replaces every value the attribute holds (see [Multi-valued attributes](../configuration/attribute-mappings.md#multi-valued-attributes)).
- **Import attribute mappings**<br /> Transform Connected System attributes before storing them in the Metaverse, and derive Metaverse attributes from other attributes of the same Metaverse Object.
- **Conditional logic**<br /> Choose different values based on conditions (e.g. enable or disable an account based on employee status).
- **Scoping filters**<br /> Determine which objects are in scope for a Synchronisation Rule.

Expressions also decide what an **Object Matching Rule** compares, where an identifier differs in format between systems: see [Matching on an expression](../configuration/object-matching-rules.md#matching-on-an-expression).

To add or edit these mappings in the admin UI, see [Synchronisation Rules](../configuration/synchronisation-rules.md). The rest of this guide focuses on the expressions themselves: the syntax, the built-in functions, and worked examples you can adapt.

Expressions are also used by **example data generation templates** to construct a generated attribute's value from other attributes on the same object, for example building an email address whose domain is derived from a generated company name. Only the `mv` accessor applies in that context (generation builds Metaverse Objects directly, so there is no Connected System Object), but the same syntax and functions described below apply.

## Quick Examples

Before diving into the details, here are a few examples to give you a feel for how expressions work:

```csharp
// Build an email address from first and last name
Lower(mv["First Name"]) + "." + Lower(mv["Last Name"]) + "@company.com"

// Enable or disable an account based on employee status
IIF(Eq(mv["Employee Status"], "Active"), 512, 514)

// Use the preferred name if available, otherwise fall back to the first name
Coalesce(mv["Preferred Name"], mv["First Name"])
```

## Attribute Access

Expressions work with two sources of data:

- **`mv`**<br /> Attributes on the Metaverse Object (the central identity record).
- **`cs`**<br /> Attributes on the Connected System Object (the external system record).

Access an attribute by putting its name in square brackets and quotes:

```csharp
mv["Display Name"]
mv["Employee Status"]
mv["Department"]

cs["sAMAccountName"]
cs["userAccountControl"]
```

Which accessors mean something depends on where the expression runs. An export Attribute Flow reads `mv`; an import Attribute Flow reads `cs`. An import Attribute Flow can also read `mv`, the Metaverse Object it flows to, to derive one Metaverse attribute from others: see [Deriving Metaverse attributes](../configuration/derived-attributes.md) for the evaluation order, loops, and which synchronisation evaluates the flow. An Object Matching Rule's expression reads `cs` only, because the Metaverse Object is what the rule is looking for.

In the portal, wherever you type an expression (an Attribute Flow's Expression source, or a Generated Value's base expression), it is syntax highlighted as you type. Metaverse attribute references (`mv["..."]`) and Connected System attribute references (`cs["..."]`) each have a colour of their own, distinct from text and functions, so you can see at a glance which side of the Metaverse an expression reads; the Attribute Flow table colours them the same way.

Attribute names are matched case-insensitively, so `mv["Department"]`, `mv["department"]`, and `mv["DEPARTMENT"]` all refer to the same attribute. This applies to attribute *names* only; attribute *values* are compared case-sensitively by default, which is why a case-insensitive comparison wraps the value in `Lower()` (see [String Comparison](#string-comparison)). Mirroring the casing shown in the JIM admin UI keeps expressions readable, but it is not required for them to work. For the wider picture of where JIM is case-sensitive and where it is forgiving, see [Case Sensitivity](case-sensitivity.md).

### Multi-Valued Attributes

A Multi-Valued attribute such as `proxyAddresses` or `memberOf` reads as **every value it holds**, in every expression: none reads as nothing (null), one reads as that value, and several read as every distinct value in a fixed order (text by ordinal, so upper case before lower case; numbers ascending; dates oldest first; Guids and references by their text). The order does not depend on how the Connected System returned the values, so an expression gives the same answer after every import. An attribute declared Multi-Valued but holding one value, as `cn`, `mail` and `givenName` usually do in a directory, behaves exactly like a Single-Valued one.

Functions work with several values in four ways:

- **Transforms apply to each value** and return every result, in the same order: `Trim`, `Upper`, `Lower`, `Capitalise`, `Left`, `Right`, `Substring`, `Replace`, `EscapeDN`, `ToString`, `FormatDate`, `ParseDate` and `FromFileTime`.
- **Comparisons are true when any value satisfies them**, as a [scoping criterion](../configuration/scoping-criteria.md) on a Multi-Valued attribute is: `Eq`, `StartsWith`, `EndsWith` and `Contains`. `!` in front of one therefore means "no value does".
- **Whole-set functions work on the set itself**: `Count`, `Min`, `Max`, `Join` and `Where` (see [Multi-Valued Functions](#multi-valued-functions)).
- **Everything else needs one value**: joining text with `+`, the comparison and arithmetic operators, `.ToString()`, and `Length`, `ToInt`, `ToDecimal`, `ToFileTime`, `HasBit`, `SetBit`, `ClearBit`, `EnableUser`, `DisableUser` and `Split`. One value is used as it is; several are an error on the object, naming the attribute and how many values it has. `== null` and `!= null` still test whether there is a value.

For an object whose `proxyAddresses` holds `SMTP:priya.shah@example.org` and `smtp:p.shah@example.org`:

| Expression | Result |
|---|---|
| `cs["proxyAddresses"]` into a Multi-Valued attribute | both addresses |
| `Lower(cs["proxyAddresses"])` | both addresses, lower-cased |
| `StartsWith(cs["proxyAddresses"], "SMTP:")` | `true` (one address does) |
| `!StartsWith(cs["proxyAddresses"], "x500:")` | `true` (no address does) |
| `Count(cs["proxyAddresses"])` | `2` |
| `Join(cs["proxyAddresses"], ";")` | `SMTP:priya.shah@example.org;smtp:p.shah@example.org` |
| `Where(cs["proxyAddresses"], "starts with", "SMTP:")` | `SMTP:priya.shah@example.org` |
| `Replace(Where(cs["proxyAddresses"], "starts with", "SMTP:"), "SMTP:", "")` into Email | `priya.shah@example.org` |
| `cs["proxyAddresses"]` into a Single-Valued attribute | an error on the object: `proxyAddresses` has 2 values |

A result with several values written to a Multi-Valued attribute writes every value; written to a Single-Valued attribute, it flows nothing and is reported as a `Multi-Valued to Single-Valued` error, the object's other attributes unaffected. The [tester](#testing-an-expression) shows the same, so try an expression against several sample values before saving it.

## Operators

You can use standard operators to combine values, do arithmetic, and make comparisons.

### Joining Text

Use `+` to join text values together:

```csharp
mv["First Name"] + " " + mv["Last Name"]
// If First Name is "Jane" and Last Name is "Smith", the result is "Jane Smith"

"CN=" + mv["Account Name"] + ",OU=Users,DC=company,DC=local"
```

### Arithmetic

Standard maths operators work with numbers:

| Operator | Meaning   | Example  | Result |
|----------|-----------|----------|--------|
| `+`      | Add       | `10 + 3` | `13`   |
| `-`      | Subtract  | `10 - 3` | `7`    |
| `*`      | Multiply  | `10 * 3` | `30`   |
| `/`      | Divide    | `10 / 3` | `3`    |
| `%`      | Remainder | `10 % 3` | `1`    |

### Comparisons

Compare values using these operators. The result is either `true` or `false`:

| Operator | Meaning                  | Example    | Result  |
|----------|--------------------------|------------|---------|
| `>`      | Greater than             | `10 > 3`   | `true`  |
| `<`      | Less than                | `10 < 3`   | `false` |
| `>=`     | Greater than or equal to | `10 >= 10` | `true`  |
| `<=`     | Less than or equal to    | `3 <= 10`  | `true`  |
| `==`     | Equal to                 | `10 == 10` | `true`  |
| `!=`     | Not equal to             | `10 != 3`  | `true`  |

Each side of a comparison operator is one value: comparing a Multi-Valued attribute holding several values is an error, so use `Eq()` or `Where()` for those (see [Multi-Valued Attributes](#multi-valued-attributes)).

### Logic

Combine multiple conditions using logical operators:

| Operator | Meaning | Example                | Result  |
|----------|---------|------------------------|---------|
| `&&`     | AND     | `10 > 3 && 5 > 2`     | `true`  |
| `\|\|`  | OR      | `10 > 3 \|\| 5 < 2`   | `true`  |
| `!`      | NOT     | `!(10 > 3)`            | `false` |

Example: check that an employee is both active and in the IT department:

```csharp
Eq(mv["Employee Status"], "Active") && Eq(mv["Department"], "IT")
```

## String Comparison

`==` and `Eq()` both compare text by its characters, exactly and case-sensitively:

```csharp
IIF(mv["Employee Status"] == "Active", 512, 514)
IIF(Eq(mv["Employee Status"], "Active"), 512, 514)
```

They differ in two ways. `Eq()` is true when any value of a Multi-Valued attribute is equal, where `==` needs one value. And `Eq()` reads text against a typed value as that type, so `Eq(mv["Hire Date"], "2026-03-15")` compares dates.

For case-insensitive comparisons (where "Active", "ACTIVE", and "active" should all match), wrap the value in `Lower()` first:

```csharp
Eq(Lower(mv["Status"]), "active")
```

## Built-in Functions

### String Functions

| Function | Description | Example |
|----------|-------------|---------|
| `Trim(value)` | Remove spaces from the start and end | `Trim(mv["Name"])` |
| `Upper(value)` | Convert to uppercase | `Upper(mv["Name"])` |
| `Lower(value)` | Convert to lowercase | `Lower(mv["Name"])` |
| `Capitalise(value)` | Capitalise each word (handles hyphenated names like "O'Brien-Smith") | `Capitalise(mv["Name"])` |
| `Left(value, count)` | Take the first N characters | `Left(mv["Name"], 3)` |
| `Right(value, count)` | Take the last N characters | `Right(mv["Name"], 4)` |
| `Substring(value, start, length)` | Extract a portion of text | `Substring(mv["Name"], 2, 4)` |
| `Replace(value, old, new)` | Replace one piece of text with another | `Replace(mv["Email"], "@old.com", "@new.com")` |
| `Length(value)` | Count the number of characters | `Length(mv["Name"])` |
| `IsNullOrEmpty(value)` | Check if there is no value: missing, blank, or a Multi-Valued attribute with no values | `IsNullOrEmpty(mv["Name"])` |
| `IsNullOrWhitespace(value)` | Check if there is no value, or only spaces | `IsNullOrWhitespace(mv["Name"])` |
| `StartsWith(value, prefix)` | Check if text begins with a specific value | `StartsWith(mv["Email"], "admin")` |
| `EndsWith(value, suffix)` | Check if text ends with a specific value | `EndsWith(mv["Email"], "@company.com")` |
| `Contains(value, search)` | Check if text contains a specific value | `Contains(mv["Email"], "@company")` |

### Conditional Functions

| Function | Description | Example |
|----------|-------------|---------|
| `IIF(condition, trueValue, falseValue)` | Return one value if a condition is true, another if false | `IIF(Eq(mv["Status"], "Active"), 512, 514)` |
| `Coalesce(value1, value2)` | Use the first value if it has one (not missing, blank or an empty list), otherwise use the second | `Coalesce(mv["Preferred Name"], mv["First Name"])` |
| `Eq(value1, value2)` | Check if two values are equal, true when any value of a Multi-Valued attribute is; a date compares by [instant](#dates-and-numbers-as-text) | `Eq(cs["memberOf"], "CN=Admins")` |

### Conversion Functions

| Function | Description | Example |
|----------|-------------|---------|
| `ToString(value)` | Convert a value to text | `ToString(mv["EmployeeId"])` |
| `ToInt(value)` | Convert text to a whole number (returns 0 if not a valid number) | `ToInt(mv["Age"])` |
| `ToDecimal(value)` | Convert to an exact decimal number: text is read with `.` as the decimal point, and accepts exponent notation (`1.5E3` is 1500). Returns 0 if not a valid number | `ToDecimal(cs["Salary"])` |

### Date Functions

| Function | Description | Example |
|----------|-------------|---------|
| `Now()` | Current date and time (UTC) | `Now()` |
| `Today()` | Current date at midnight (UTC) | `Today()` |
| `FormatDate(value, format)` | Write a date, or ISO 8601 text, in a [.NET date format](https://learn.microsoft.com/dotnet/standard/base-types/custom-date-and-time-format-strings); nothing for other text or no value | `FormatDate(mv["Hire Date"], "yyyy-MM-dd")` |
| `ParseDate(text, format)` | Read text in a [.NET date format](https://learn.microsoft.com/dotnet/standard/base-types/custom-date-and-time-format-strings) as a date, in UTC unless the format has an offset (`zzz` or `K`); nothing for empty text, an error for text that does not match | `ParseDate(cs["hireDate"], "dd/MM/yyyy")` |
| `ToFileTime(date)` | Convert a date, or ISO 8601 text, to Active Directory's FILETIME format | `ToFileTime(mv["Account Expires"])` |
| `FromFileTime(filetime)` | Convert an Active Directory FILETIME back to a date | `FromFileTime(cs["accountExpires"])` |

`Now()` and `Today()` return a different value as time passes, as do `RandomPassword()`, `RandomPassphrase()`, `DateTime.Now`, `DateTime.UtcNow`, `DateTime.Today` and `Guid.NewGuid()`. In an import Attribute Flow that [derives a Metaverse attribute](../configuration/derived-attributes.md), the value then changes on every synchronisation and can be exported every time, so JIM warns when you save one (it still saves it).

### Dates and Numbers as Text

**Writing.** Whatever the server's language, `ToString()`, a date flowing to a Text attribute, and the tester write a date as `2026-03-15 10:30:00Z`: UTC, with a fraction of a second only when there is one. Joining a date to text with `+` uses the same pattern in the zone the date carries. Numbers use `.` as the decimal point. For any other form, use `FormatDate()`:

```csharp
"Changed " + ToString(mv["Last Changed"])       // Changed 2026-03-15 10:30:00Z
FormatDate(mv["Last Changed"], "dd MMM yyyy")   // 15 Mar 2026
```

**Reading.** Text, including text flowing to a DateTime attribute, is read as a date only in ISO 8601: `yyyy-MM-dd`, then optionally `T` or a space and a time (`HH:mm`, optional seconds and fraction), then `Z`, an offset such as `+01:00`, or nothing, which means UTC. Every other form is refused, including `03/04/2024`, which is 3 April in one country and 4 March in another; read those with `ParseDate()`:

```csharp
ParseDate(cs["startDate"], "dd/MM/yyyy")      // 15/03/2026 is 15 March 2026, 00:00 UTC
ParseDate(cs["changed"], "yyyyMMddHHmmssK")   // 20260315103000Z is 15 March 2026, 10:30 UTC
```

A `ParseDate()` mismatch is an error, not "no value", so an unexpected form never clears the attribute. A Binary value has no text form: text functions such as `Upper()` refuse it with an error, while `IsNullOrEmpty()` and `IsNullOrWhitespace()` treat it as a value.

### Distinguished Name (DN) Functions

| Function | Description | Example |
|----------|-------------|---------|
| `EscapeDN(value)` | Safely escape special characters for use in LDAP distinguished names | `"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=domain,DC=local"` |

### Password Generation

| Function | Description | Example |
|----------|-------------|---------|
| `RandomPassword(length, extendedChars)` | Generate a random password. Set extendedChars to `true` for special characters (!@#$%^&*) | `RandomPassword(16, true)` |
| `RandomPassphrase(wordCount, separator)` | Generate a passphrase from random words | `RandomPassphrase(4, "-")` |

### Multi-Valued Functions

These functions work on every value of a [Multi-Valued attribute](#multi-valued-attributes) at once.

| Function | Description | Example |
|----------|-------------|---------|
| `Count(values)` | How many values there are: 0 for none | `Count(cs["proxyAddresses"])` |
| `Min(values)` | The smallest value: numbers by value, dates by instant, text by ordinal; nothing for no values | `Min(mv["Start Dates"])` |
| `Max(values)` | The largest value, compared as `Min` compares | `Max(cs["employeeNumbers"])` |
| `Where(values, operator, value)` | The values that meet a [scoping operator](../configuration/scoping-criteria.md#operators), named as the editor shows it (`"starts with"`) or as the REST API names it (`"StartsWith"`); text compares exactly unless a fourth argument, `true`, ignores case | `Where(cs["proxyAddresses"], "starts with", "SMTP:")` |
| `Split(value, delimiter)` | Split a delimited text value into several values, in the order they appear | `Split(cs["coursesCompleted"], "\|")` |
| `Join(values, delimiter)` | Combine values into a single delimited text value, in the order they are read | `Join(mv["Groups"], ",")` |

`Min` and `Max` refuse values they cannot order (true or false, Guids, Binary, or a mix of kinds) with an error, and `Where` refuses **has a value** and **has no value**, which test an attribute rather than each value: use `Count` or `IsNullOrEmpty`.

### Account Control Functions

These functions are primarily used with Active Directory's `userAccountControl` attribute, which stores account settings as a single number using individual bit flags.

| Function | Description | Example |
|----------|-------------|---------|
| `EnableUser(uac)` | Enable a user account | `EnableUser(cs["userAccountControl"])` |
| `DisableUser(uac)` | Disable a user account | `DisableUser(cs["userAccountControl"])` |
| `SetBit(value, bit)` | Turn on a specific flag | `SetBit(mv["uac"], 65536)` |
| `ClearBit(value, bit)` | Turn off a specific flag | `ClearBit(mv["uac"], 65536)` |
| `HasBit(value, bit)` | Check if a specific flag is turned on | `HasBit(cs["userAccountControl"], 2)` |

## ⚠️ Nulls, Missing Inputs, and Whitespace

This is the most important section to read before you write an expression that references an attribute which might be absent. How JIM treats a missing input decides whether a target value is **cleared**, **kept**, or **silently filled with malformed data**. Get this wrong and an expression can quietly clear good data, or write a broken value that passes straight through to the target system.

When you reference an attribute the object does not have (for example `cs["middleName"]` on a person with no middle name), the expression sees **null**. There are four behaviours to understand, and a per-mapping setting (point 5) that lets you have JIM refuse the outcome rather than guess at it.

### 1. A null result means "no value", never "leave it alone"

When an expression evaluates to null, JIM treats that as a **deliberate assertion that the mapping supplies no value**, not as "no opinion, leave it alone". This is intentionally different from the behaviour of some traditional ILM tools, where a function call that returned null meant "do nothing".

What happens to the target depends on how many Synchronisation Rules contribute to the attribute:

- **A single contributor (the common case).**<br /> A null result clears the target: any existing value on the Metaverse attribute is removed.
- **Multiple contributors.**<br /> [Attribute Priority](attribute-priority.md) resolves the outcome. A null from the winning rule hands the attribute to the next-priority contributor within the same synchronisation run, so a lower-priority source's value takes over rather than the attribute going blank; the target is only cleared when no contributor supplies a value. The exception is a rule with **"Null is a value"** set: its null is authoritative, the target is cleared, and lower-priority contributors are not consulted. A null from a lower-priority rule never disturbs the winning value.

The authoring takeaway is the same either way: **a null result is never "keep the current value".** If you do not want a missing input to change the target, you must guard the expression (see point 3).

### 2. Built-in string functions are null-safe and propagate null

The string functions (`Trim`, `Upper`, `Lower`, `Capitalise`, `Left`, `Right`, `Substring`, `Replace`, `EscapeDN`, and similar) are null-safe: given a null input, they return null rather than erroring. That is convenient, but it means **null travels through them unchanged**.

So a bare `Trim(cs["middleName"])` on a person with no middle name evaluates to null, which (per point 1) **clears** the target attribute:

```csharp
// HAZARD -- if middleName is absent, this evaluates to null and CLEARS the target
Trim(cs["middleName"])

// SAFE -- fall back to the existing target value, or to an empty result you control
Coalesce(Trim(cs["middleName"]), mv["Middle Name"])
```

To avoid an accidental clear, guard the expression so it never resolves to a bare null for a value you want to preserve: use `Coalesce()` to supply a fallback, or `IIF()` with an `IsNullOrEmpty()` / `IsNullOrWhitespace()` check (see point 3).

### 3. String concatenation does NOT yield null; it yields a malformed value

This is the more dangerous case, because it fails **silently with wrong data** rather than clearing. In a `+` concatenation, a null operand is treated as an **empty string**, not as null. So the whole expression produces a real, non-null value that is stored as-is and bypasses the null/clear path entirely.

```csharp
// HAZARD -- if Last Name is absent, this produces "jane." (a real value), not null
Lower(cs["First Name"]) + "." + Lower(cs["Last Name"]) + "@company.com"
// Result: "jane.@company.com" -- a malformed email, written without complaint
```

The same hazard produces malformed LDAP distinguished names when a component is missing:

```csharp
// HAZARD -- if Account Name is absent, this produces "CN=,OU=Users,..." -- a broken DN
"CN=" + mv["Account Name"] + ",OU=Users,DC=company,DC=local"
```

Steer around it by validating or supplying fallbacks **before** composing the value:

| Technique | Use it to | Example |
|-----------|-----------|---------|
| `Coalesce(a, b)` | Supply a fallback when the first value is missing | `Coalesce(mv["Preferred Name"], mv["First Name"])` |
| `IIF(condition, trueVal, falseVal)` | Build the composed value only when its inputs are present, otherwise fall back to a safe value | `IIF(IsNullOrWhitespace(cs["Last Name"]), mv["Email"], Lower(cs["First Name"]) + "." + Lower(cs["Last Name"]) + "@company.com")` |
| `IsNullOrEmpty` / `IsNullOrWhitespace` | Test an input before composing with it | `IsNullOrWhitespace(cs["Last Name"])` |
| `EscapeDN` plus careful construction | Build directory DNs safely; still guard each required component for presence | `"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=company,DC=local"` |

`EscapeDN()` protects against special characters such as commas in a value; it does **not** protect against a value being absent. A missing required component still yields a malformed DN, so guard for presence as well as escaping for safety.

Where a component has no safe fallback, guarding is not enough and you want JIM to refuse the value outright: set the mapping's **Missing Input Behaviour** (point 5).

### 4. Whitespace-only results become no value (by default)

A result that is only whitespace (spaces, tabs, newlines) is, by default, collapsed to **no value** and clears the target, exactly like null. This is governed per import mapping by the **Treat whitespace as no value** control in [inbound value processing](../configuration/attribute-mappings.md#value-processing-inbound), which is **on by default**. Switch it off for a mapping where whitespace is genuinely meaningful, and a whitespace-only result is stored as a literal value instead (the portal flags it with a `(whitespace)` indicator so it is not mistaken for an empty cell).

### 5. Missing Input Behaviour: have JIM refuse rather than guess

Points 1 to 4 are about writing an expression that copes with an absent input. **Missing Input Behaviour** is the other half: it lets you tell JIM what to do when an attribute your expression reads has no value, so a mapping you have not guarded cannot quietly produce a malformed value.

It is set per expression source on an Attribute Flow, in the portal beside the expression itself, and applies to import and export mappings alike. JIM works out which attributes the expression reads from the `mv["..."]` and `cs["..."]` accessors in it; you do not list them. An input counts as missing when the object has no value for it, when the value is null, or when it is an empty string.

| Behaviour | What happens when an input is missing | Choose it when |
|-----------|----------------------------------------|----------------|
| **Evaluate anyway** (default) | The expression runs with the input absent and contributes whatever it returns; points 1 to 4 apply in full. | The expression handles absence itself, with `IIF()`, `Coalesce()` or an `IsNullOrEmpty` check. |
| **Contribute no value** | The expression is not evaluated. The mapping contributes nothing, resolved by [Attribute Priority](attribute-priority.md) and "Null is a value" like any other no-value outcome. Nothing is reported. | The attribute is optional, and another contributor or the existing value should stand. |
| **Fail this mapping** | The expression is not evaluated and an **Expression Missing Input** error is recorded against the object. Every other Attribute Flow on the Synchronisation Rule still runs, and the target attribute keeps whatever it already held. | The attribute matters enough to investigate, but not enough to hold up the rest of the object. |
| **Fail the object** | The expression is not evaluated and nothing at all flows for the object, which is recorded as an **Expression Missing Input** error. | The value is identity-critical, such as a Distinguished Name or an account name, and a partially populated object is worse than none. |

Both failure behaviours report against the object on every run until the missing value is supplied or the configuration changes, so the errors are a work queue rather than a one-off notification. Neither behaviour writes anything: "Fail this mapping" leaves the target attribute exactly as it was, and "Fail the object" leaves the whole object untouched.

On an import Attribute Flow that [derives a Metaverse attribute](../configuration/derived-attributes.md), its `mv["..."]` inputs count exactly as its `cs["..."]` inputs do, so "Contribute no value" waits for an Account Name that has not arrived yet rather than building `@corp.local`.

The default is **Evaluate anyway** on every existing and newly created mapping, so nothing changes until you choose otherwise. Guarding the expression and setting a behaviour are complementary rather than alternatives: guard where the absence is expected and you know the right answer, and set a behaviour where it is not.

```csharp
// Guarded, so Evaluate anyway is correct: the expression knows what to do without an input
IIF(IsNullOrWhitespace(cs["Last Name"]), mv["Email"], Lower(cs["First Name"]) + "." + Lower(cs["Last Name"]) + "@company.com")

// Unguardable, so set Fail the object: there is no safe DN for a person with no Account Name
"CN=" + EscapeDN(mv["Account Name"]) + ",OU=Users,DC=company,DC=local"
```

Set it from PowerShell with `New-JIMSyncRuleMapping -MissingInputBehaviour`, or over the REST API with `missingInputBehaviour` on the mapping source. Change it on a mapping that already exists with `Set-JIMSyncRuleMapping -MissingInputBehaviour`, or `PATCH /api/v1/synchronisation/sync-rules/{syncRuleId}/mappings/{mappingId}`.

### Summary

| Expression evaluates to | Stored as | Effect on target |
|-------------------------|-----------|------------------|
| `null` (for example `Trim()` of an absent attribute) | No value | ❌ Clears any existing value |
| Whitespace only, **Treat whitespace as no value** on (default) | No value | ❌ Clears any existing value |
| Whitespace only, **Treat whitespace as no value** off | Literal whitespace | ✅ Stored (flagged `(whitespace)` in the portal) |
| Concatenation with a missing operand (for example `a + "." + null`) | A real, possibly malformed value | ✅ Stored as-is, bypassing the null/clear path |
| A non-empty value | That value, in the attribute's type | ✅ Stored |
| A value the attribute cannot hold (for example text, for a Number attribute) | Nothing | ⚠️ Kept as it was; the object is reported with an expression error |
| More than one value, for a single-valued attribute | Nothing | ⚠️ Kept as it was; the object is reported with a `Multi-Valued to Single-Valued` error |

The "Effect on target" column describes an attribute with a single contributing Synchronisation Rule. Where several rules contribute, a "no value" outcome feeds [Attribute Priority](attribute-priority.md) resolution instead of clearing outright (see point 1).

The rule of thumb: **reference a possibly-absent attribute and you risk clearing the target; concatenate one and you risk writing malformed data.** Guard with `Coalesce()`, `IIF()`, and the `IsNullOrEmpty` / `IsNullOrWhitespace` checks, set a **Missing Input Behaviour** (point 5) where there is no safe fallback, and test with sample data (including the missing-input case) before you deploy.

## Common Scenarios

This section shows practical examples of expressions you will use when setting up identity synchronisation.

### Enabling and Disabling User Accounts

Active Directory uses a `userAccountControl` attribute to control whether an account is enabled or disabled. The common values are:

| Value   | Meaning                                        |
|---------|------------------------------------------------|
| `512`   | Normal account, enabled                        |
| `514`   | Normal account, disabled                       |
| `66048` | Normal account, enabled, password never expires |

**Enable or disable based on employee status:**

```csharp
IIF(Eq(mv["Employee Status"], "Active"), 512, 514)
```

This reads as: "If the Employee Status is Active, set the value to 512 (enabled), otherwise set it to 514 (disabled)."

!!! note "Creating accounts enabled on Active Directory"
    Active Directory refuses to create an enabled account that has no password, so on a Synchronisation Rule that provisions accounts, give the rule an [Initial Password](../configuration/initial-password.md) as well. JIM then creates the account disabled and enables it once the password is set, and the expression keeps deciding the enabled state from then on. See [Enabling new Active Directory accounts](../connectors/jim-ldap-connector-passwords.md#enabling-new-active-directory-accounts).

    An account this expression creates disabled (`514`), such as a pre-hire's, stays disabled when its password is delivered, whatever the Initial Password's **Enable the Connected System Object once the password is set** says: the expression decides the enabled state. See [Accounts your Attribute Flow creates disabled](../connectors/jim-ldap-connector-passwords.md#accounts-your-attribute-flow-creates-disabled).

**Check if an account is currently disabled:**

```csharp
IIF(HasBit(cs["userAccountControl"], 2), "Disabled", "Enabled")
```

### Setting Account Expiration Dates

Active Directory stores expiration dates in a special format called FILETIME. JIM provides functions to convert between normal dates and this format automatically.

**Set the account expiration from an employee's end date:**

```csharp
ToFileTime(mv["Employee End Date"])
```

**Convert an AD expiration date back to a readable date:**

```csharp
FromFileTime(cs["accountExpires"])
```

**Important notes:**

- If the date attribute is empty or missing, these functions return nothing (null), which asserts "no value" for the target: for a sole contributor any existing value is removed; see [Nulls, Missing Inputs, and Whitespace](#nulls-missing-inputs-and-whitespace)
- AD treats `0` and very large numbers as "never expires", so `FromFileTime()` returns nothing for these values
- `ToFileTime()` safely handles empty text, missing values, and invalid dates by returning nothing

### Building Email Addresses

```csharp
Lower(mv["First Name"]) + "." + Lower(mv["Last Name"]) + "@company.com"
```

For "Jane Smith", this produces: `jane.smith@company.com`

### Building a Display Name with Title

```csharp
Coalesce(mv["Title"], "") + " " + mv["First Name"] + " " + mv["Last Name"]
```

`Coalesce` uses the title if one exists, otherwise uses an empty string, avoiding a "null" appearing in the output.

### Building Distinguished Names (DNs)

When constructing LDAP distinguished names, always use `EscapeDN()` to handle special characters (commas, quotes, etc.) in names:

```csharp
"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=domain,DC=local"
```

Without `EscapeDN()`, a name like "Smith, Jane" would break the DN because of the comma.

### Generating Initials

```csharp
Upper(Left(mv["First Name"], 1)) + Upper(Left(mv["Last Name"], 1))
```

For "Jane Smith", this produces: `JS`

### Placing Users in Different OUs by Department

```csharp
IIF(Eq(mv["Department"], "IT"),
    "OU=IT,OU=Users,DC=domain,DC=local",
    IIF(Eq(mv["Department"], "HR"),
        "OU=HR,OU=Users,DC=domain,DC=local",
        "OU=General,OU=Users,DC=domain,DC=local"))
```

This nests `IIF` functions to check multiple conditions: first IT, then HR, with a default of General for everyone else.

### Adding a Prefix to Account Names

```csharp
IIF(Eq(mv["Department"], "IT"), "tech-" + mv["Account Name"], mv["Account Name"])
```

IT department users get a "tech-" prefix; everyone else keeps their account name as-is.

### Splitting Delimited Values into a List

When a source system stores multiple values in a single field separated by a delimiter (e.g., `"COURSE1|COURSE2|COURSE3"`), use `Split()` to break them into separate values.

**Example (training courses from a CSV file):**

The source CSV has pipe-separated courses in a single column:

```csv
employeeId,coursesCompleted
E001,"SOFT101|SOFT201|SEC101"
```

Use this expression to create individual values on the Metaverse Object:

```csharp
Split(cs["coursesCompleted"], "|")
```

This creates three separate values: `SOFT101`, `SOFT201`, and `SEC101`.

**Notes:**

- Empty entries are automatically removed
- Whitespace is trimmed from each value
- Returns nothing for missing or empty input
- The delimiter can be any text (e.g., `","`, `"|"`, `";"`)
- Each value is stored in the target attribute's type (`"1|2"` into a Number attribute stores 1 and 2); if any value cannot be, nothing flows and the object is reported
- Into a single-valued attribute, a list of more than one value flows nothing and the object is reported
- `Join()` puts the values back together in the same order, so `Join(Split(x, "|"), "|")` gives back `x`

### Combining a List into a Single Value

When exporting to a system that does not support multi-valued attributes, use `Join()` to combine them into one delimited value.

```csharp
Join(mv["Group Memberships"], "|")
```

If the attribute has the values Admin, Users, and Developers, this produces `Admin|Developers|Users`: an attribute's values are joined in their fixed order (see [Multi-Valued Attributes](#multi-valued-attributes)), so the result is the same after every import.

**Notes:**

- Returns nothing for empty lists
- Empty and missing values are filtered out
- Uses a comma as the default delimiter if none is specified

## Validation and Troubleshooting

### Testing an expression

The Attribute Flow dialog tests an expression as you write it, so you find out what it produces before you save it rather than from the next synchronisation.

Choose **Expression** as the source type and type your expression. Once the Attribute Flow has a target attribute, **Test this Attribute Flow** lists each attribute the expression reads, with its type from the schema and whether it holds one value or several. Give each a value, choose **Run test**, and the panel shows what a synchronisation would do, using the same code a synchronisation runs.

**Giving the inputs values:**

- **Each value is read as its attribute's type.** Type `2026-03-02` for a date and time attribute and the expression receives a date, so `FormatDate((DateTime)cs["hireDate"], "yyyy-MM-dd")` produces `2026-03-02`. A value that is not of the attribute's type is reported, naming the input.
- **A multi-valued attribute takes several values.** Type each one and choose **Add value**.
- **No value is a tick box.** An input you leave untouched has no value. Tick **No value** to make an input absent again after typing in it. Clearing the box gives the input empty text instead, which the expression sees as empty text rather than null. Missing Input Behaviour treats both as missing, as a synchronisation does.
- **Run as of** sets the date and time (UTC) that `Now()` and `Today()` return, so an expression that depends on the date can be checked for the day it matters. Leave it empty for the current time.
- **From an object** fills the inputs from a real object: search for a Connected System Object of the rule's Connected System for an import Attribute Flow, or a Metaverse Object for an export one, and pick it. Its values for each input are loaded, every value of a multi-valued attribute included, and you can change any of them to try a variation. An import Attribute Flow that reads `mv["..."]` also takes those inputs from the Metaverse Object the Connected System Object is joined to.

**Reading the result.** **What a Synchronisation would do** traces each step:

1. **Expression returned:** the expression's own result and its type, or why it was not evaluated or failed.
2. **Value processing:** the Attribute Flow's trimming, whitespace and case settings, in the order they apply, and whether they changed the value. It applies to an import Attribute Flow into a Text attribute only.
3. **Converted for** the target: the values the target attribute would hold, in its type.
4. **Result:** what the target would be given: these values, no value, or the error a synchronisation would record, such as a result the target's type cannot hold.

**If an input has no value** then runs the flow again for each input you gave a value, with that one input absent, and shows what the Attribute Flow's Missing Input Behaviour would do. This is how you check an expression against the hazard the previous section describes:

```csharp
Lower(cs["givenName"]) + "." + Lower(cs["sn"]) + "@corp.local"
```

With both values supplied this produces `ada.lovelace@corp.local`. Without the surname it produces `ada.@corp.local`, a syntactically fine string that is not an email address, unless Missing Input Behaviour stops it. Seeing that before saving is the point.

Before a target attribute is chosen, the expression is evaluated on its own, with each value as text, and an empty box is no value.

The same test is available outside the portal, over [`Test-JIMExpression`](../powershell/expressions.md) and the REST API's `/api/v1/synchronisation/test-expression`, for testing an expression from a script. Give the Synchronisation Rule and Attribute Flow to test the expression as that Attribute Flow, and optionally a Connected System Object or Metaverse Object to load the values from; without them, the expression is evaluated on its own. Either way, `-AsOf` (`asOf` over REST) sets the time `Now()` and `Today()` return.

### Validation

JIM validates expressions when you save a Synchronisation Rule. If an expression has a syntax error, you will see an error message indicating what went wrong and where in the expression the problem is. The tester reports the same errors, so a malformed expression is caught while you are writing it.

Common errors:

- **Missing closing quote or bracket**<br /> Check that every `"` and `(` has a matching pair.
- **Unknown function name**<br /> Check the spelling; function names are case-sensitive.
- **Wrong number of parameters**<br /> Check the function reference above for the correct parameters.
- **Syntax errors**<br /> Look for misplaced operators or missing commas between function parameters.

### Troubleshooting

When an expression does not produce the result you expect:

1. **Check the attribute name spelling**<br /> Attribute names are matched case-insensitively, so `mv["department"]` and `mv["Department"]` are equivalent; casing is never the cause. Do check the name is spelled correctly and matches an attribute that exists in the JIM admin UI.
2. **Check for several values**<br /> A Multi-Valued attribute holding more than one value is an error where one value is needed, naming the attribute; choose one with `Where()`, `Min()` or `Max()`, or combine them with `Join()` (see [Multi-Valued Attributes](#multi-valued-attributes)).
3. **Check for missing values**<br /> If an attribute does not exist on the object, it returns nothing (null), which can clear the target or, in a concatenation, produce a malformed value. Use `Coalesce()` or `IsNullOrEmpty()` to handle this; see [Nulls, Missing Inputs, and Whitespace](#nulls-missing-inputs-and-whitespace) for the full picture.
4. **Test with sample data**<br /> Use the tester in the Attribute Flow dialog to try your expression with sample values, including leaving an input empty to see what an object without that attribute would produce (see [Testing an expression](#testing-an-expression)).
5. **Check the worker logs**<br /> If expressions fail during sync, the worker service logs the error details.

## Best Practices

1. **Say which value of a Multi-Valued attribute you mean**<br /> Use `Where()`, `Min()`, `Max()` or `Join()`, so an object gaining a second value is handled rather than reported as an error.

2. **Handle missing values**<br /> Use `Coalesce()` to provide a fallback, or `IsNullOrEmpty()` to check before using a value. This prevents an expression clearing the target or writing malformed data when an attribute has not been populated yet; see [Nulls, Missing Inputs, and Whitespace](#nulls-missing-inputs-and-whitespace).

3. **Always use `EscapeDN()` in distinguished names**<br /> This prevents special characters in names from breaking LDAP paths.

4. **Test before saving**<br /> Use the expression test feature in the UI to verify your expressions with sample data.

5. **Keep expressions simple**<br /> If an expression is getting complex, consider splitting the logic across multiple attribute mappings.

6. **Document complex expressions**<br /> Add a note in the Synchronisation Rule's description explaining what complex expressions do, so the next person can understand them.
