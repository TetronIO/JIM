---
title: Nulls and Missing Inputs
description: "What an expression does with an attribute the object does not have: when a target value is cleared, kept or malformed, and how to have JIM refuse."
type: concept
audience: administrator
---

# Nulls and Missing Inputs

Read this before you write an [expression](expressions.md) that references an attribute which might be absent. How JIM treats a missing input decides whether a target value is **cleared**, **kept**, or **silently filled with malformed data**. Get this wrong and an expression can quietly clear good data, or write a broken value that passes straight through to the target system.

```jim-diagram
archetype: comparison
title: Where a missing input ends up, by how the expression uses it
desc: "An attribute the object does not have reads as null. Passed to a string function such as Trim, the null travels through unchanged, the expression returns null, and the target is cleared. Joined with + in a concatenation, the null reads as an empty string, so the expression returns a real but malformed value, such as jane.@company.com, which is stored as it is. Guarded with Coalesce, the expression returns the fallback, and the fallback is stored. The effect on the target is for an attribute with one contributing Synchronisation Rule. Missing Input Behaviour can have JIM refuse instead: contribute no value, fail the mapping or fail the object."
columns:
- {label: A string function, sub: '`Trim(cs["middleName"])`'}
- {label: Concatenation, sub: '`a + "." + b`'}
- {label: A guard, sub: '`Coalesce(a, b)`'}
rows:
- label: The missing input reads as
  cells:
  - {text: "null"}
  - {text: an empty string}
  - {text: "null, then the fallback"}
- label: The expression returns
  cells:
  - {text: "null", sub: travels through unchanged}
  - {text: a real value, sub: '`jane.@company.com`'}
  - {text: the fallback's value}
- label: The target attribute
  cells:
  - {text: cleared, sub: with one contributor}
  - {text: a malformed value stored, sub: passes straight through}
  - {text: the fallback stored}
footer: "Missing Input Behaviour can refuse instead: contribute no value, fail the mapping, or fail the object"
```

When you reference an attribute the object does not have (for example `cs["middleName"]` on a person with no middle name), the expression sees **null**. There are four behaviours to understand, and a per-mapping setting (point 5) that lets you have JIM refuse the outcome rather than guess at it.

## :material-null: 1. A null result means "no value", never "leave it alone"

When an expression evaluates to null, JIM treats that as a **deliberate assertion that the mapping supplies no value**, not as "no opinion, leave it alone". This is intentionally different from the behaviour of some traditional ILM tools, where a function call that returned null meant "do nothing".

What happens to the target depends on how many Synchronisation Rules contribute to the attribute:

- **A single contributor (the common case).**<br /> A null result clears the target: any existing value on the Metaverse attribute is removed.
- **Multiple contributors.**<br /> [Attribute Priority](attribute-priority.md) resolves the outcome. A null from the winning rule hands the attribute to the next-priority contributor within the same synchronisation run, so a lower-priority source's value takes over rather than the attribute going blank; the target is only cleared when no contributor supplies a value. The exception is a rule with **"Null is a value"** set: its null is authoritative, the target is cleared, and lower-priority contributors are not consulted. A null from a lower-priority rule never disturbs the winning value.

The authoring takeaway is the same either way: **a null result is never "keep the current value".** If you do not want a missing input to change the target, you must guard the expression (see point 3).

## :material-function-variant: 2. Built-in string functions are null-safe and propagate null

The string functions (`Trim`, `Upper`, `Lower`, `Capitalise`, `Left`, `Right`, `Substring`, `Replace`, `EscapeDN`, and similar) are null-safe: given a null input, they return null rather than erroring. That is convenient, but it means **null travels through them unchanged**.

So a bare `Trim(cs["middleName"])` on a person with no middle name evaluates to null, which (per point 1) **clears** the target attribute:

```csharp
// HAZARD -- if middleName is absent, this evaluates to null and CLEARS the target
Trim(cs["middleName"])

// SAFE -- fall back to the existing target value, or to an empty result you control
Coalesce(Trim(cs["middleName"]), mv["Middle Name"])
```

To avoid an accidental clear, guard the expression so it never resolves to a bare null for a value you want to preserve: use `Coalesce()` to supply a fallback, or `IIF()` with an `IsNullOrEmpty()` / `IsNullOrWhitespace()` check (see point 3).

## :material-link-variant: 3. String concatenation does NOT yield null; it yields a malformed value

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

## :material-format-text-wrapping-wrap: 4. Whitespace-only results become no value (by default)

A result that is only whitespace (spaces, tabs, newlines) is, by default, collapsed to **no value** and clears the target, exactly like null. This is governed per import mapping by the **Treat whitespace as no value** control in [inbound value processing](../configuration/attribute-mappings.md#value-processing-inbound), which is **on by default**. Switch it off for a mapping where whitespace is genuinely meaningful, and a whitespace-only result is stored as a literal value instead (the portal flags it with a `(whitespace)` indicator so it is not mistaken for an empty cell).

## :material-alert-octagon-outline: 5. Missing Input Behaviour: have JIM refuse rather than guess

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

## :material-clipboard-list-outline: Summary

| Expression evaluates to | Stored as | Effect on target |
|-------------------------|-----------|------------------|
| `null` (for example `Trim()` of an absent attribute) | No value | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } Clears any existing value |
| Whitespace only, **Treat whitespace as no value** on (default) | No value | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } Clears any existing value |
| Whitespace only, **Treat whitespace as no value** off | Literal whitespace | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } Stored (flagged `(whitespace)` in the portal) |
| Concatenation with a missing operand (for example `a + "." + null`) | A real, possibly malformed value | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } Stored as-is, bypassing the null/clear path; the malformed value reaches the target |
| A non-empty value | That value, in the attribute's type | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } Stored |
| A value the attribute cannot hold (for example text, for a Number attribute) | Nothing | Kept as it was; the object is reported with an expression error |
| More than one value, for a single-valued attribute | Nothing | Kept as it was; the object is reported with a `Multi-Valued to Single-Valued` error |

The "Effect on target" column describes an attribute with a single contributing Synchronisation Rule. Where several rules contribute, a "no value" outcome feeds [Attribute Priority](attribute-priority.md) resolution instead of clearing outright (see point 1).

The rule of thumb: **reference a possibly-absent attribute and you risk clearing the target; concatenate one and you risk writing malformed data.** Guard with `Coalesce()`, `IIF()`, and the `IsNullOrEmpty` / `IsNullOrWhitespace` checks, set a **Missing Input Behaviour** (point 5) where there is no safe fallback, and test with sample data (including the missing-input case) before you deploy.
