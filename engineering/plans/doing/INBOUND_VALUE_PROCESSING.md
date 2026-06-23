# Per-mapping Inbound Value Processing

- **Status:** Done
- **Issue:** [#843](https://github.com/TetronIO/JIM/issues/843)
- **Parent:** [#91](https://github.com/TetronIO/JIM/issues/91) (MV attribute priority)

## Overview

Inbound synchronisation can now clean and normalise imported **text** values before they flow to the metaverse, configured **per attribute-flow mapping** (`SyncRuleMapping`). The first and default-on transform treats whitespace-only and empty values as no value; the others trim, collapse internal whitespace, and normalise case.

This was raised as #843, originally framed as a single connected-system `TreatWhitespaceAsNull` toggle. During design both the grain and the scope changed (see Decision).

## Problem

Historically the inbound value-handling path treated only a strict `null` result as "no value". A whitespace-only or empty string (imported directly, or produced by an expression) was stored as a literal value. This is inconsistent (`null` clears, `""`/`"   "` persists), surprising in the UI (an attribute that looks blank may actually hold spaces), and downstream systems handle `""`/whitespace inconsistently. More generally, identity engineers routinely need to normalise dirty source text (trim stray padding, fold case for usernames/email) at the point a value enters the metaverse.

## Decision

- **Grain: the attribute mapping, not the connected system.** Whitespace (and the need to trim/case-fold) is driven by how humans populated a *particular field*, and varies attribute-by-attribute *within* one source; the system type (CSV vs directory) does not predict it. The control therefore lives on the inbound `SyncRuleMapping`, alongside #91's forthcoming `NullIsValue`/`Priority`. Normalisation (this feature) runs *before* resolution (#91): a value processed to "no value" feeds the same `ConnectedNoValue` contribution state as an expression-null or a direct-absent value.
- **Scope: a small value-processing family, not just whitespace.** Whitespace-as-no-value is the first member; trim, collapse-internal-whitespace and case normalisation ship alongside it.
- **Model: a `[Flags]` enum for the independent binary transforms + a separate enum for the multi-way case choice.** Two `int` columns; compact, typed, queryable, and extensible (a new binary transform is a new bit; case is mutually exclusive so it cannot be a flag).
- **`NullIsValue` is deliberately not part of this set.** It is a *resolution* concern (does a no-value contribution assert/clear vs fall through), owned by #91. Value processing is *normalisation*. They compose; they are kept distinct in the UI.

## Model and schema

`JIM.Models/Logic/SyncRuleEnums.cs`:

```csharp
[Flags]
public enum InboundValueProcessing
{
    None                       = 0,
    TreatWhitespaceAsNoValue   = 1 << 0,   // default on
    TrimWhitespace             = 1 << 1,
    CollapseInternalWhitespace = 1 << 2
}

public enum InboundCaseNormalisation { None = 0, Upper, Lower, Title }
```

`JIM.Models/Logic/SyncRuleMapping.cs` gains:

```csharp
public InboundValueProcessing InboundValueProcessing { get; set; } = InboundValueProcessing.TreatWhitespaceAsNoValue;
public InboundCaseNormalisation CaseNormalisation { get; set; } = InboundCaseNormalisation.None;
```

The store default for `InboundValueProcessing` is configured via `HasDefaultValue(TreatWhitespaceAsNoValue)` in `JimDbContext.OnModelCreating`, so the migration `AddInboundValueProcessingToSyncRuleMapping` **backfills existing rows** to whitespace-as-no-value (the opinionated default applies to mappings created before this feature shipped). These columns are only meaningful for **import** mappings targeting **text** attributes; they are ignored for export mappings and non-text types.

## Engine semantics

All inbound text flow goes through `SyncEngine.AttributeFlow.cs`. The flags/enum ride on the `syncRuleMapping` object already passed to `ProcessMapping`, so no engine-entrypoint signature changed.

The pure helper `ApplyInboundTextProcessing(string? value, InboundValueProcessing, InboundCaseNormalisation)` implements the **fixed canonical order** and returns the transformed value, or `null` when it collapses to no value:

1. **Trim** leading/trailing whitespace (if `TrimWhitespace`).
2. **Collapse** runs of internal whitespace to a single space (if `CollapseInternalWhitespace`).
3. **Case** normalise (Upper / Lower / Title; Title lower-cases first so all-caps words are title-cased not left as-is).
4. **Whitespace-as-no-value**: if `TreatWhitespaceAsNoValue` and the result is null/whitespace, return `null`.

The flag *bits* record which transforms are enabled; the engine always applies them in this order regardless of bit order.

Applied at **inbound flow time** (not import time, because the consuming mapping is unknown at import):

- **Direct text flow** (`ProcessMapping` text case): each source `StringValue` is run through the helper; nulls are dropped and the set de-duplicated, then diffed against the MVO. An emptied set clears the attribute. Multi-valued attributes therefore drop whitespace-only entries and keep the rest. The CSO retains the raw source value; only the metaverse receives the processed value.
- **Expression scalar** (`ProcessExpressionMapping`, text target): the result string is processed; a `null` result routes into the existing "expression returned null" clear path. This runs *after* the #842 typed-catch, so it does not affect expression-failure handling.
- **Expression array**: each entry is processed and whitespace-collapsed entries dropped before the existing array-diff (an empty array clears all values).

Non-text attribute types are never processed (a hard type guard).

## Surfaces

- **UI (mapping editor):** `SyncRuleDetail.razor` shows a "Value processing" group inside the Attribute Flow dialog, only for **import** rules whose **target is a text attribute**: three checkboxes (treat whitespace as no value, trim, collapse internal whitespace) and a case-normalisation select, with helper text. Persisted by the existing full-graph `CreateOrUpdateSyncRuleAsync` save.
- **REST API:** `CreateSyncRuleMappingRequest` (request) and `SyncRuleMappingDto` (response, via `FromEntity`) carry `InboundValueProcessing?` / `InboundCaseNormalisation?`. The global `JsonStringEnumConverter` serialises the `[Flags]` value as a comma-separated name string. `CreateSyncRuleMappingAsync` sets them on the new mapping (import branch only). Readable via `GET /sync-rules/{id}/mappings`.
- **PowerShell:** `New-JIMSyncRuleMapping` gains import-only parameters `-TrimWhitespace`, `-CollapseInternalWhitespace`, `-PreserveWhitespace` (inverts the default) and `-CaseNormalisation` (`None`/`Upper`/`Lower`/`Title`), composed into the POST body. `Get-JIMSyncRuleMapping` surfaces the fields automatically (it returns the DTO).

## Whitespace visibility (when the toggle is off)

When an administrator turns whitespace-as-no-value **off**, whitespace can flow and be stored. To stop such a value rendering as a visually blank cell (indistinguishable from no value), two shared components were added:

- `WhitespaceValue.razor`: a low-lighted "(whitespace)" indicator with a tooltip visualising the characters (the `<EmptyValue />` sibling).
- `TextValueDisplay.razor`: a dispatcher that renders `<EmptyValue />` for null/empty, `<WhitespaceValue />` for whitespace-only, or the value otherwise.

`TextValueDisplay` is wired into every attribute-value display surface: `MvoDetailsTable`, `CsoAttributeValue` (used by the CSO detail and activity-item pages), `MvoMvaDialog`, `PendingExportMvaDialog`, `AttributeChangeTable`, `ChangeHistoryTimeline`, and the activity-item and pending-export value columns. Because it operates on the final display string, wrapping a string-returning value helper is safe (non-text values are never whitespace and render unchanged).

## Worked examples

| Source value | Processing | Case | Result |
|--------------|-----------|------|--------|
| `"   "` | `TreatWhitespaceAsNoValue` (default) | None | no value (clears the MVO attribute) |
| `"   "` | `None` | None | `"   "` (literal, flagged "(whitespace)" in the UI) |
| `" John "` | `TreatWhitespaceAsNoValue \| TrimWhitespace` | None | `"John"` |
| `"John   Smith"` | `CollapseInternalWhitespace` | None | `"John Smith"` |
| `"  aLICE   SMITH  "` | `Trim \| CollapseInternalWhitespace` | Title | `"Alice Smith"` |
| `"ALICE@x.com"` | `None` | Lower | `"alice@x.com"` |
| MVA `[" a ", "   ", "b"]` | `TreatWhitespaceAsNoValue \| TrimWhitespace` | None | `["a", "b"]` |

## Relationship to #91 (attribute priority)

This lands the **first** per-attribute-flow property on `SyncRuleMapping`, ahead of #91's `NullIsValue`/`Priority` (additive, no conflict). The pipeline is: normalise (this feature) → contribution state (`ConnectedNoValue` / `ConnectedWithValue`) → resolution (`NullIsValue` + `Priority`, #91). A value processed to "no value" is exactly the `ConnectedNoValue` signal #91 resolves, so the two align cleanly.

## Test coverage

- **Engine** (`test/JIM.Worker.Tests/SyncEngineTests/SyncEngineValueProcessingTests.cs`): the pure helper across all transforms and combinations in canonical order; through `FlowInboundAttributes` for direct SVA/MVA, expression scalar and array paths; whitespace on (clears/drops) vs off (preserves); non-text unaffected; real values unchanged.
- **API** (`test/JIM.Web.Api.Tests/SynchronisationControllerMappingTests.cs`): `FromEntity` round-trip and `CreateSyncRuleMappingAsync` setting the fields (and defaulting when omitted).
- **PowerShell** (`src/JIM.PowerShell/Tests/SyncRuleMappings.Tests.ps1`): parameter existence and import-only parameter sets, the `CaseNormalisation` validate-set, and request-body composition (default, combined switches, `-PreserveWhitespace`).

## Out of scope / future

- Outbound (export) value processing.
- User-orderable transform pipeline (the order is fixed and canonical).
- Per-source (vs per-mapping) processing.
- `NullIsValue` and priority resolution (owned by #91).
