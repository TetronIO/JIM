# Relative Date/Time Search and Scoping Criteria

- **Status:** Planned
- **Created:** 2026-06-21
- **Author:** Jay Van der Zant
- **Issue:** [#85](https://github.com/TetronIO/JIM/issues/85)

## Problem Statement

JIM lets administrators filter objects by date in two places: sync rule **scope filters** (which objects a sync rule applies to) and **object searches** (predefined searches over metaverse objects). In both, a date criterion today can only compare an attribute against a **fixed, literal date**: "`AccountExpiry` LessThan `2026-09-01`". The literal is captured once, in a date picker, and frozen.

Real identity-management policy is almost never expressed against a fixed calendar date; it is expressed **relative to now**:

- "Accounts that **expire within the next 7 days**" (deprovisioning warning sweep).
- "Joiners **hired in the last 30 days**" (onboarding scope).
- "Contracts that **ended more than 90 days ago**" (stale-account cleanup).
- "Certifications **due in the next quarter**" (review scope).

With only literal dates, an administrator has to either recompute and re-enter the date every day (so a saved search or a scoping rule rots the moment it is saved), or build brittle external automation that PATCHes the date on a schedule. A scope filter that says "expiring within 7 days" must mean *7 days from the moment the rule is evaluated*, every sync run, forever, with no human intervention. That is the gap.

There is also an **inconsistency** that compounds the problem: literal date comparisons already work for sync rule scoping (`ScopingEvaluationServer.EvaluateDateTimeComparison`), but object/predefined searches do not support **any** date comparison at all. `PredefinedSearchCriteria` carries only a `StringValue`, and the query translator in `MetaverseRepository.GetMetaverseObjectsOfTypeAsync` throws `NotSupportedException` for `LessThan` / `GreaterThan` / `LessThanOrEquals` / `GreaterThanOrEquals` (and never implements `All` vs `Any` group logic). So for object searches, the issue's premise ("at the moment you can perform greater than and less than date searches") does not currently hold; literal date search has to be built before relative date search can sit on top of it.

## Goals

- Administrators can express a date criterion as **relative to evaluation time** ("now minus 30 days", "now plus 7 days") instead of, or in addition to, a fixed literal date, in **both** sync rule scope filters and object/predefined searches.
- A relative criterion is **re-evaluated against the current time every time the rule or search runs**; a saved relative scope filter or search does not need editing to stay correct as time passes. Verifiable by: configure "expires within 7 days", advance the clock / wait, confirm the in-scope set shifts without any edit to the rule.
- The relative anchor supports the units administrators actually use: **hours, days, weeks, months, years**, in both directions (ago / from now). Verifiable by configuring one criterion per unit/direction and confirming the computed boundary.
- Object/predefined searches gain **typed date comparison** (`LessThan`, `LessThanOrEquals`, `GreaterThan`, `GreaterThanOrEquals`, `Equals`, `NotEquals`) for `DateTime` attributes, both literal and relative, closing the gap with sync rule scoping. Verifiable by a predefined search returning the correct objects for a literal-date and a relative-date criterion.
- Existing literal-date scope filters and searches continue to work unchanged (backward compatible). Verifiable by the existing scoping integration/unit tests passing without modification.
- The relative-vs-literal choice is a first-class, discoverable option in the criteria editor UI, not a hidden mode. Verifiable by an administrator building a relative criterion end to end in the UI with no documentation.

## Non-Goals

- **Arbitrary date arithmetic / expression language.** No "now minus 2 months plus 3 days", no cron-style expressions, no calendar-aware business-day maths. The relative anchor is a single signed offset of one unit (count + unit + direction). Compound / nested offsets are out of scope. (Note: some traditional ILM criteria engines technically allow nesting duration functions to build compound offsets; we deliberately exclude that to keep the editor and validation surface simple, and revisit only on a concrete request.)
- **Relative anchors for non-date types.** This feature is `DateTime`-attribute only. Relative numeric ranges ("salary within 10% of X") are not in scope.
- **Business-hours / per-user time-zone semantics.** The feature computes boundaries from the host's UTC clock. It does not introduce business-day arithmetic or a per-user display time zone for relative resolution in v1. (Whole-day rounding for day-and-coarser units *is* in scope; see Resolved Decisions.)
- **Changing the comparison-operator set.** No new `SearchComparisonType` values; relative dates reuse the existing `LessThan` / `GreaterThan` / etc. operators. A relative criterion changes how the *comparison value* is produced, not how the comparison is performed.
- **Migration of existing literal scope filters into relative ones.** Existing data stays literal; administrators opt into relative explicitly.

## User Stories

1. As an identity administrator, I want a sync rule scope filter that means "accounts expiring within the next 7 days", so that the deprovisioning-warning rule keeps targeting the right accounts every sync run without me editing it daily.
2. As an identity administrator, I want an object search for "joiners hired in the last 30 days", so that I can save it once and reuse it as a live onboarding worklist rather than re-typing the date each morning.
3. As an identity administrator, I want to choose between a fixed date and a relative period when I add a date criterion, so that I can express both "before our 2026 cut-over date" (literal) and "ended more than 90 days ago" (relative) using the same editor.
4. As a developer maintaining the scoping evaluator, I want relative-date resolution to live in one place and feed the existing comparison logic, so that scoping and search share one definition of "now minus N days" and cannot drift apart.

## Requirements

### Functional Requirements

#### Model and semantics

1. A date criterion (in both `SyncRuleScopingCriteria` and the predefined-search criteria model) must support two **value modes**: **Absolute** (the existing literal `DateTimeValue`) and **Relative** (a computed anchor). The mode is explicit, not inferred.
2. A Relative value must be expressed as **(offset count: integer, unit: enum, direction: enum)**, e.g. `count=7, unit=Days, direction=FromNow`. The unit enum must include `Hours`, `Days`, `Weeks`, `Months`, `Years`. The direction enum must include `Ago` (subtract from now) and `FromNow` (add to now). A zero offset is permitted and resolves to "now".
3. At evaluation time the Relative value must resolve to a concrete UTC `DateTime` as `DateTime.UtcNow (±) offset`, using calendar-correct arithmetic for `Months`/`Years` (`AddMonths`/`AddYears`, not fixed 30/365-day multiplication). The resolved value then feeds the **existing** comparison operator unchanged (`LessThan`, `GreaterThan`, `Equals`, etc.).
3a. **Whole-day rounding.** For units of `Days` and coarser (`Days`, `Weeks`, `Months`, `Years`), the resolved boundary must be truncated to **midnight UTC** (start of day) so that a saved relative criterion does not shift its matched set depending on the time of day it runs. For the `Hours` unit, resolution is to the **exact instant** (`UtcNow ± N hours`, no rounding), because sub-day precision is the entire point of that unit.
4. Relative resolution must be **computed fresh on every evaluation** (every sync run, every search execution). The resolved boundary must never be persisted back onto the criterion as a literal.
5. Relative values apply only to attributes of type `DateTime`. The API and UI must reject a Relative value on any other attribute data type.
6. The resolution helper must be a single shared function used by **both** the sync rule scoping evaluator and the object-search query path, so the two cannot diverge. (Scoping evaluates in memory; search must translate to a query predicate against a computed boundary value, computed once per search execution before the query is built.)

#### Sync rule scoping

7. `ScopingEvaluationServer`'s DateTime comparison path must resolve a Relative criterion to its boundary `DateTime` and then apply the existing operator logic. Null-attribute handling must match the existing literal behaviour (a null attribute value does not satisfy ordering comparisons).
8. A scope filter using a Relative criterion must produce a different in-scope set as wall-clock time advances, with no edit to the rule, because resolution uses `DateTime.UtcNow` at each evaluation.

#### Object / predefined searches

9. Object/predefined searches must support `DateTime` attribute comparison for `Equals`, `NotEquals`, `LessThan`, `LessThanOrEquals`, `GreaterThan`, `GreaterThanOrEquals`, in both Absolute and Relative modes. This requires the predefined-search criteria model to carry a typed `DateTimeValue` (and the Relative fields), not only the current `StringValue`.
10. The query translator (`MetaverseRepository.GetMetaverseObjectsOfTypeAsync`) must translate a date criterion into an EF predicate over the relevant attribute-value column (`DateTimeValue`), replacing the current `NotSupportedException` for ordering operators on date attributes. Relative criteria resolve their boundary to a literal `DateTime` **before** the predicate is built (so the database sees a constant, and the existing `IX_MetaverseObjectAttributeValues_DateTimeValue` index is usable).
11. The predefined-search query translator must implement full group semantics: `All` = AND, `Any` = OR, and nested groups, for **all** criteria types (not only the new date criteria). This closes the pre-existing `All`/`Any`/nesting TODOs in `MetaverseRepository`, which are currently unimplemented stubs. The existing text-criteria behaviour must continue to work and gain correct group composition. This broader query-engine work is tracked as a sub-task of #85 (see Dependencies) and is a prerequisite for date search returning correct results inside mixed groups.

#### REST API (first-class support)

The Web API is a first-class delivery surface for this feature, not an afterthought: everything an administrator can do in the portal must be doable via the API, and the OpenAPI/Scalar reference must document it.

12. The scoping-criteria DTOs and request models (`SyncRuleScopingCriteriaDto`, `CreateScopingCriterionRequest`) must carry the value-mode and the three relative fields (count, unit, direction) alongside the existing `DateTimeValue`. Field names and JSON shape to be settled in the implementation plan; the relative fields must be documented in the XML comments that drive the Scalar reference.
13. The predefined-search API must gain **first-class criteria management** endpoints, which do not exist today (the only predefined-search write endpoint currently toggles `isEnabled`). Full CRUD: create/list/**update**/delete predefined-search **criteria groups** (group type All/Any, position) and **criteria** (attribute, operator, typed/relative value). The route shape mirrors the existing sync-rule scoping endpoints (`.../criteria-groups`, `.../criteria-groups/{groupId}/criteria`, with `PUT`/`PATCH` on the `{groupId}` and `{criterionId}` paths). These endpoints carry the typed values (from #849), group semantics (from #850), and the relative-date fields.
14. **Criterion in-place update on both surfaces.** A single criterion must be editable in place via `PATCH .../criteria/{criterionId}` (resolved value or relative fields, operator, attribute, case-sensitivity), preserving the criterion's ID and position. This is **new for sync rule scoping too**: the scoping API today exposes only POST and DELETE for criteria (`CreateScopingCriterion`, `DeleteScopingCriterion`), with no update path, so this feature **retrofits** an `UpdateScopingCriterion` (`PATCH .../scoping-criteria/{groupId}/criteria/{criterionId}`) endpoint alongside the new predefined-search equivalent. Editing a relative value (e.g. 7 days to 14 days) must not require delete-and-recreate.
15. All criteria write endpoints must validate and reject with `400 Bad Request` and a structured error: a Relative value on a non-`DateTime` attribute; a Relative value missing a unit or direction; a negative offset count (direction encodes sign, so the count is non-negative); an operator not applicable to the attribute's data type; and a criterion that sets both a literal `DateTimeValue` and Relative fields (mode is exclusive).
16. Round-trip persistence must be lossless across both the scoping and predefined-search criteria APIs: a criterion POSTed/PATCHed with Relative fields and fetched back must return identical mode, count, unit, direction, comparison type, and (for text) case-sensitivity.
17. ID-based identifier rules apply (per `src/CLAUDE.md`): GET exposes ID and, where applicable, name/URI overloads; PATCH/PUT/DELETE are ID-only.

#### PowerShell module (first-class support)

The `JIM` PowerShell module is a first-class delivery surface: a script author must be able to build, edit, and inspect relative-date criteria and predefined-search criteria entirely from the module, with `ValidateSet` constraints, comment-based help, and examples consistent with the existing cmdlets.

18. `New-JIMScopingCriterion` must gain relative-date parameters: `-ValueMode` (`Absolute`/`Relative`), `-RelativeCount` (non-negative int), `-RelativeUnit` (`ValidateSet` of `Hours`,`Days`,`Weeks`,`Months`,`Years`), and `-RelativeDirection` (`ValidateSet` of `Ago`,`FromNow`). The relative parameters live in their own parameter set so they cannot be combined with `-DateTimeValue`. Comment-based help and at least one `.EXAMPLE` per direction must be added.
19. A new `Set-JIMScopingCriterion` cmdlet must be added (backed by the retrofitted criterion `PATCH` from requirement 14), so a scoping criterion (including its relative value) can be edited in place. It carries the same typed-value and relative-date parameter surface as `New-JIMScopingCriterion`, with only the explicitly-bound fields sent (the partial-update idiom already used by `Set-JIMPredefinedSearch` and `Set-JIMScopingCriteriaGroup`).
20. The module must gain **first-class predefined-search criteria cmdlets**, which do not exist today (the `Search/` area currently has only `Get-JIMPredefinedSearch` and `Set-JIMPredefinedSearch`, the latter limited to `isEnabled`). Full set, following the same conventions as the existing `ScopingCriteria/` cmdlets (verb-noun, parameter sets, attribute-by-id-or-name, `SupportsShouldProcess`):
    - `New-/Get-/Set-/Remove-JIMPredefinedSearchCriteriaGroup` (the `Set-` covers the group-update parity gap that scoping already has via `Set-JIMScopingCriteriaGroup`).
    - `New-/Set-/Remove-JIMPredefinedSearchCriterion`, where `New-` and `Set-` carry the full typed-value and relative-date parameter surface.
21. `ComparisonType` `ValidateSet`s in the cmdlets stay in lockstep with `SearchComparisonType`; relative parameters are validated client-side before the call so an obviously-invalid combination fails fast with a clear PowerShell error rather than a raw `400`.

#### UI

See the **UI Mocks** section below for the concrete layouts these requirements describe.

22. The scope-criteria editor (`SyncRuleDetailScopingCriteriaGroup.razor`) must, when the selected attribute is `DateTime`, let the administrator choose Absolute or Relative. Absolute shows the existing `MudDatePicker`. Relative shows a numeric count input, a unit selector (Hours/Days/Weeks/Months/Years), and a direction selector (Ago/From now).
23. The predefined-search criteria editor must offer the same Absolute/Relative date control. The current `PredefinedSearchDetail.razor` is read-only for criteria; the edit affordance it needs is delivered as part of #849 (see Dependencies).
24. Both editors must support editing an existing criterion in place (not only add/remove), backed by the criterion `PATCH` from requirement 14, so a relative value can be adjusted without deleting and recreating the criterion.
25. The relative control must render a plain-language preview of what it means, e.g. "Matches dates more than 7 days from now", so the administrator can confirm intent before saving.
26. The criterion summary chip (currently rendered via `SyncRuleScopingCriteria.ToString()`) must render relative criteria in plain language (e.g. "30 days ago", not a resolved literal date), so a saved relative criterion reads as relative wherever it is displayed.
27. Existing literal-date criteria must continue to display and edit exactly as today when their mode is Absolute.

#### Persistence

28. The new fields (value mode, relative count, relative unit, relative direction) must be added to the `SyncRuleScopingCriteria` table and the predefined-search criteria table via an **append-only** EF Core migration. Existing rows default to Absolute mode with null relative fields, preserving current behaviour. Migrations must never be flattened or edited per the repository's migration policy.

### Non-Functional Requirements

- **Resolved on demand, never materialised.** Relative criteria resolve at the point of use: scope criteria at each sync run, searches at query time. The resolved boundary must never be cached or persisted onto the criterion. A consequence to set expectations for: a relative scope criterion's effective freshness equals the rule's **sync schedule** (a nightly rule re-evaluates "expires within 7 days" nightly, not continuously); searches are always fresh because they resolve per execution. If JIM ever introduces a materialised-membership ("set") concept, relative criteria must carry a recomputation trigger rather than a stored boundary, to avoid the stale-membership window that scheduled-recalculation designs suffer.
- Relative resolution must add no measurable per-object overhead in the scoping hot path: the boundary is computed once per evaluation pass, not once per object.
- Object-search date predicates must be index-friendly: the resolved boundary is a constant in the SQL, so the existing `DateTimeValue` index on attribute values is usable; no per-row function evaluation.
- All UTC, consistent with JIM's DateTime policy (store/compare UTC; `DateTime.UtcNow`, never `DateTime.Now`).
- British English throughout; no em dashes.
- No new NuGet packages.

## Examples and Scenarios

### Scenario 1: Scope filter, "expires within the next 7 days"

**Given** a sync rule whose scope criterion is `AccountExpiry LessThanOrEquals (now + 7 days, Relative)`
**When** the rule is evaluated on 2026-06-21
**Then** objects whose `AccountExpiry` is on or before 2026-06-28 are in scope; objects with a null `AccountExpiry` are out of scope
**And** when the same rule is evaluated unchanged on 2026-06-22, the boundary has moved to 2026-06-29 with no edit to the rule.

### Scenario 2: Object search, "hired in the last 30 days"

**Given** a predefined search over `User` with criterion `HireDate GreaterThanOrEquals (now - 30 days, Relative)`
**When** the search runs on 2026-06-21
**Then** the result set is exactly the users whose `HireDate` is on or after 2026-05-22; users with a null `HireDate` are excluded.

### Scenario 3: Absolute date still works

**Given** a scope criterion `ContractEnd LessThan 2026-01-01 (Absolute)`
**When** the rule is evaluated
**Then** behaviour is identical to today; no relative resolution occurs.

### Scenario 4: Calendar-correct month arithmetic with whole-day rounding

**Given** a criterion `(now - 1 Months, Relative)` evaluated at 2026-03-31T14:05Z
**When** the boundary is resolved
**Then** it resolves to 2026-02-28T00:00Z (via `AddMonths(-1)` then truncated to midnight UTC), not "31 days earlier" and not carrying the 14:05 time-of-day.

### Scenario 4a: Hours unit keeps sub-day precision

**Given** a criterion `(now - 6 Hours, Relative)` evaluated at 2026-06-21T14:05Z
**When** the boundary is resolved
**Then** it resolves to 2026-06-21T08:05Z exactly (no day rounding), because the `Hours` unit is exempt from whole-day truncation.

### Scenario 5: API rejects a relative value on a non-date attribute

**Given** a POST that sets Relative fields on a criterion targeting a `Number` attribute
**When** the request is processed
**Then** the API returns `400 Bad Request` with a structured error stating relative values apply only to `DateTime` attributes.

### Scenario 6: API rejects a criterion with both modes set

**Given** a POST that sets both `DateTimeValue` and Relative fields
**When** the request is processed
**Then** the API returns `400 Bad Request`; value mode is exclusive.

## UI Mocks

These are low-fidelity layouts, not pixel specs. They show the controls and their relationships; final styling follows the existing MudBlazor conventions (outlined variants, `mt-5` spacing, etc.). The editor reused for both sync rule scoping and predefined searches is the same "Add Criteria" dialog shape that exists today; the new part is the **Value mode** toggle and the **Relative** sub-form that appear only for `DateTime` attributes.

### Mock 1: "Add Criteria" dialog, DateTime attribute, Absolute mode (unchanged from today)

```
+------------------------------------------------------------+
|  ⚖  Add Criteria                                           |
+------------------------------------------------------------+
|                                                            |
|  Metaverse Attribute            [ AccountExpiry      ▼ ]   |
|                                                            |
|  Comparison Type                [ Less Than Or Equals ▼]   |
|                                                            |
|  Value mode      ( • Absolute )  ( ○ Relative )            |
|                                                            |
|  Date Value                     [ 2026-09-01        📅 ]   |
|                                                            |
+------------------------------------------------------------+
|                              [ Cancel ]  [ Add Criteria ]  |
+------------------------------------------------------------+
```

### Mock 2: same dialog, Relative mode selected (the new sub-form)

```
+------------------------------------------------------------+
|  ⚖  Add Criteria                                           |
+------------------------------------------------------------+
|                                                            |
|  Metaverse Attribute            [ AccountExpiry      ▼ ]   |
|                                                            |
|  Comparison Type                [ Less Than Or Equals ▼]   |
|                                                            |
|  Value mode      ( ○ Absolute )  ( • Relative )            |
|                                                            |
|   Count            Unit                Direction           |
|  [   7   ]        [ Days        ▼ ]   [ From now    ▼ ]    |
|                     Hours               Ago                |
|                     Days                From now           |
|                     Weeks                                  |
|                     Months                                 |
|                     Years                                  |
|                                                            |
|  ┌──────────────────────────────────────────────────┐      |
|  │ ℹ  Matches when AccountExpiry is on or before     │      |
|  │    7 days from now (re-evaluated each run).      │      |
|  └──────────────────────────────────────────────────┘      |
|                                                            |
+------------------------------------------------------------+
|                               [ Cancel ]  [ Add Criteria ] |
+------------------------------------------------------------+
```

Notes:
- **Value mode** is a `MudRadioGroup` (or segmented toggle). It renders only when the chosen attribute is `DateTime`; for all other types the dialog is exactly as today.
- The three Relative inputs are a `MudNumericField` (Count, min 0) + two `MudSelect`s (Unit, Direction). The **Hours** unit is present per the resolved decision.
- The blue info box is the live plain-language **preview** (requirement 22). It updates as the operator, count, unit, and direction change, and states explicitly that the value is re-evaluated each run, so an administrator understands it is not a frozen date.

### Mock 3: saved criterion chips (list view), showing a relative criterion in plain language

```
CRITERIA GROUP. LOGIC TYPE: ALL

 (MV) AccountExpiry   [ Less Than Or Equals ]   [ 7 days from now ]   🗑
 (MV) Department      [ Equals ]                [ Text: Finance   ]   🗑

           [ + Add Criteria Group ]   [ + Add Criteria ]
```

The relative chip reads "7 days from now", not a resolved literal date (requirement 23). The literal `Department` criterion is unchanged.

### Mock 4: predefined-search criteria editor (new edit affordance, delivered in #849)

`PredefinedSearchDetail.razor` is read-only today. #849 adds an editor that reuses the same "Add Criteria" dialog shape as Mocks 1 and 2, so an administrator builds object-search date filters (literal or relative) with an identical experience to sync rule scoping.

```
Predefined Search: "Joiners (last 30 days)"          [ Edit ] [ Run ]
------------------------------------------------------------------
 Object type: User

 CRITERIA GROUP. LOGIC TYPE: ALL
   (MV) HireDate    [ Greater Than Or Equals ]  [ 30 days ago ]   🗑

            [ + Add Criteria Group ]   [ + Add Criteria ]
------------------------------------------------------------------
 Results (live):  142 users
```

## API and PowerShell Examples

These illustrate the first-class API and module surfaces (requirements 12 to 19). Exact field names and routes are settled in the implementation plan; shapes mirror the existing scoping-criteria endpoint and the existing `New-JIMScopingCriterion` cmdlet.

### REST: add a relative scope criterion ("expires within 7 days")

```
POST /api/v1/synchronisation/sync-rules/5/scoping-criteria/10/criteria
Content-Type: application/json

{
  "metaverseAttributeName": "AccountExpiry",
  "comparisonType": "LessThanOrEquals",
  "valueMode": "Relative",
  "relativeCount": 7,
  "relativeUnit": "Days",
  "relativeDirection": "FromNow"
}
```

### REST: add a relative criterion to a predefined search (new endpoint, #849/#850)

```
POST /api/v1/predefined-searches/3/criteria-groups/8/criteria
Content-Type: application/json

{
  "metaverseAttributeName": "HireDate",
  "comparisonType": "GreaterThanOrEquals",
  "valueMode": "Relative",
  "relativeCount": 30,
  "relativeUnit": "Days",
  "relativeDirection": "Ago"
}
```

### PowerShell: relative scope criterion

```powershell
New-JIMScopingCriterion -SyncRuleId 5 -GroupId 10 `
    -MetaverseAttributeName 'AccountExpiry' `
    -ComparisonType LessThanOrEquals `
    -ValueMode Relative -RelativeCount 7 -RelativeUnit Days -RelativeDirection FromNow
```

### PowerShell: relative predefined-search criterion (new cmdlets, #849/#850)

```powershell
$group = New-JIMPredefinedSearchCriteriaGroup -PredefinedSearchId 3 -GroupType All -PassThru

New-JIMPredefinedSearchCriterion -PredefinedSearchId 3 -GroupId $group.Id `
    -MetaverseAttributeName 'HireDate' `
    -ComparisonType GreaterThanOrEquals `
    -ValueMode Relative -RelativeCount 30 -RelativeUnit Days -RelativeDirection Ago
```

### PowerShell: edit a relative value in place (no delete-and-recreate)

```powershell
# Widen the scope window from 7 to 14 days; the criterion keeps its ID and position.
Set-JIMScopingCriterion -SyncRuleId 5 -GroupId 10 -CriterionId 42 -RelativeCount 14
```

## Constraints

- Must respect JIM's append-only EF migration policy; new columns only, no schema rewrite.
- Must reuse the existing `SearchComparisonType` operators; no new operators.
- Must work in air-gapped deployments; no external time/date service. "Now" is the host's UTC clock.
- Must follow the strict n-tier architecture: UI/API call `JimApplication`, never repositories directly.
- British English; no em dashes; Tetron copyright headers on new files.

## Affected Areas

| Area | Impact |
|------|--------|
| Database | New columns on `SyncRuleScopingCriteria` and the predefined-search criteria table (value mode, relative count, relative unit, relative direction); append-only migration. No change to attribute-value tables. |
| Models | `JIM.Models/Logic/SyncRuleScopingCriteria.cs` and `JIM.Models/Search/PredefinedSearchCriteria.cs` gain mode + relative fields; new enums (relative unit, direction, value mode) in `JIM.Models/Search/`. A shared relative-date resolution helper. |
| Application | `ScopingEvaluationServer` resolves Relative criteria before comparison; shared resolution helper invoked here and by the search path. |
| Data | `MetaverseRepository.GetMetaverseObjectsOfTypeAsync` implements typed `DateTime` predicates (literal + resolved-relative) and the group semantics those date criteria require, replacing the current `NotSupportedException`. |
| API | `JIM.Web/Models/Api` scoping and predefined-search DTOs/request models carry the new fields; validation in `SynchronisationController`; **new first-class predefined-search criteria-group and criteria endpoints** (full CRUD) on the predefined-search controller, which has no criteria-write endpoints today; **retrofit `UpdateScopingCriterion` (`PATCH .../criteria/{criterionId}`)** which scoping lacks today; OpenAPI/Scalar XML docs for all of the above. |
| PowerShell | `New-JIMScopingCriterion` gains relative-date parameters in a new parameter set; **new `Set-JIMScopingCriterion`** for in-place edit; **new `Search/` cmdlets** (`New-/Get-/Set-/Remove-JIMPredefinedSearchCriteriaGroup`, `New-/Set-/Remove-JIMPredefinedSearchCriterion`) carrying typed-value + relative-date parameters with `ValidateSet`s and comment-based help, mirroring the existing `ScopingCriteria/` cmdlets. |
| UI | `SyncRuleDetailScopingCriteriaGroup.razor` Absolute/Relative date control with live preview; relative-aware criterion chip text; predefined-search criteria editor (new edit affordance, #849) reuses the same control. See UI Mocks. |
| Docs | `engineering/SYNC_RULE_SCOPING.md` gains relative-date examples; user/API docs for searches; PowerShell cmdlet help. |
| Tests | New unit tests for relative resolution (calendar arithmetic, hours-vs-day rounding, direction, null handling) and the search predicate path; API tests for the new predefined-search criteria endpoints and validation; integration coverage for a relative scope filter shifting over time. |

## Dependencies

This feature is decomposed into the following sub-tasks of #85. The two predefined-search enabling gaps are tracked as their own sub-issues so they can be sized, reviewed, and (if needed) merged independently:

- **Sub-task: Predefined-search typed comparison support** (sub-issue, see Additional Context). `PredefinedSearchCriteria` carries only `StringValue` today and the query translator throws `NotSupportedException` for ordering operators. This sub-task adds typed value carriers (including `DateTime`) and implements the ordering predicates. Prerequisite for the object-search half of this feature.
- **Sub-task: Predefined-search group semantics (`All`/`Any` + nesting)** (sub-issue, see Additional Context). Closes the pre-existing unimplemented group-logic TODOs in `MetaverseRepository` (requirement 11). Prerequisite for any multi-criteria predefined search returning correct results.
- **Predefined-search criteria editing UI.** `PredefinedSearchDetail.razor` currently displays criteria read-only; the object-search half needs an edit affordance for the criteria, including the Absolute/Relative date control (requirement 21). Delivered within #849.

The implementation is **strictly sequential** (a single implementer is delivering all of it here, so phases land one after another, each building and testing green before the next starts):

1. **#849 (predefined-search typed comparison support)** first: typed value carriers + ordering predicates, so date search has a literal foundation.
2. **#850 (predefined-search `All`/`Any` + nesting group semantics)** next: correct multi-criteria composition on top of #849.
3. **Relative date/time criteria** last, layered onto the now-working literal paths for *both* sync rule scoping and object search, including the shared resolution helper, API, PowerShell, and UI.

Sync rule scoping relative dates are part of phase 3, not parallelised ahead of it; keeping a single linear order avoids merge churn and lets each phase's tests gate the next.

## Resolved Decisions

These were open during drafting and have been settled. The functional requirements above are written as firm requirements reflecting them.

1. **Whole-day rounding (with an Hours exception).** Relative boundaries for `Days`/`Weeks`/`Months`/`Years` truncate to midnight UTC, so a saved criterion does not drift by time of day. The `Hours` unit resolves to the exact instant. See requirement 3a. This is a deliberate divergence from traditional ILM criteria engines, which resolve relative dates to the exact instant (`now` to the second); the `Hours` unit recovers that instant-level precision where it is genuinely needed.
2. **Hours included.** The unit set is `Hours`, `Days`, `Weeks`, `Months`, `Years`. See requirement 2.
3. **Close the broader predefined-search gaps, tracked as sub-tasks.** Typed comparison support and full `All`/`Any`/nesting group semantics are in scope for this feature and tracked as the two sub-issues listed under Dependencies, rather than deferred to a separate unscheduled effort.
4. **Explicit value-mode enum.** Value mode is an explicit `enum ValueMode { Absolute, Relative }` field, not inferred from whether relative fields are populated, so the exclusive-mode validation (requirement 13) is unambiguous.

## Acceptance Criteria

- [ ] A date criterion can be saved as Absolute (literal) or Relative (count + unit + direction) in both sync rule scoping and object/predefined searches.
- [ ] A Relative scope criterion re-resolves against `DateTime.UtcNow` on every evaluation; the in-scope set shifts as time advances with no edit to the rule.
- [ ] Relative units cover Hours/Days/Weeks/Months/Years in both Ago and FromNow directions; Months/Years use calendar-correct `AddMonths`/`AddYears`.
- [ ] Day-and-coarser units truncate the resolved boundary to midnight UTC; the Hours unit resolves to the exact instant.
- [ ] Predefined-search group semantics (`All`/`Any` + nesting) are implemented for all criteria types (closing the prior TODO stubs), delivered via the tracked sub-issue.
- [ ] Object/predefined searches support `Equals`/`NotEquals`/`LessThan`/`LessThanOrEquals`/`GreaterThan`/`GreaterThanOrEquals` on `DateTime` attributes (literal and relative); the previous `NotSupportedException` path for date ordering operators is gone.
- [ ] Existing literal-date scope filters and any existing searches behave identically to before (backward compatible); existing scoping tests pass unmodified.
- [ ] The API carries the new fields, round-trips them losslessly, and returns `400` for: relative-on-non-date, missing unit/direction, negative count, operator-not-applicable, both-modes-set.
- [ ] The predefined-search API exposes first-class criteria-group and criteria endpoints (full CRUD), documented in the Scalar reference; these did not exist before this feature.
- [ ] A single criterion is editable in place via `PATCH .../criteria/{criterionId}` on both surfaces (preserving ID and position); the scoping criterion-update endpoint, absent today, is retrofitted.
- [ ] The PowerShell module supports the full feature first-class: `New-/Set-JIMScopingCriterion` (relative-date parameters; in-place edit) and new `New-/Get-/Set-/Remove-JIMPredefinedSearchCriteria(Group)` / `...Criterion` cmdlets exist with `ValidateSet`s, comment-based help, and worked examples.
- [ ] The criteria editor UI exposes the Absolute/Relative choice with a live plain-language preview; Absolute uses the existing date picker; saved relative criteria render as plain language (e.g. "30 days ago") in the criterion chips.
- [ ] New EF migration is append-only; existing rows default to Absolute with null relative fields.
- [ ] Unit tests cover relative resolution (each unit, each direction, month/year edge cases, null attribute), and the search predicate path; integration coverage demonstrates a relative scope filter's set shifting with time.
- [ ] `engineering/SYNC_RULE_SCOPING.md` documents relative-date criteria with at least two worked examples.

## Additional Context

- Scoping criteria model: [`src/JIM.Models/Logic/SyncRuleScopingCriteria.cs`](../../src/JIM.Models/Logic/SyncRuleScopingCriteria.cs)
- Operator enum: [`src/JIM.Models/Search/SearchEnums.cs`](../../src/JIM.Models/Search/SearchEnums.cs)
- Scoping evaluator (literal DateTime comparison today): `src/JIM.Application/Servers/ScopingEvaluationServer.cs` (`EvaluateDateTimeComparison`)
- Predefined-search criteria (StringValue-only today): [`src/JIM.Models/Search/PredefinedSearchCriteria.cs`](../../src/JIM.Models/Search/PredefinedSearchCriteria.cs)
- Search query translator (throws on date ordering today): `src/JIM.PostgresData/Repositories/MetaverseRepository.cs` (`GetMetaverseObjectsOfTypeAsync`)
- Scope-criteria editor UI: `src/JIM.Web/Pages/Admin/SyncRuleDetailScopingCriteriaGroup.razor`
- Scoping doc: [`engineering/SYNC_RULE_SCOPING.md`](../SYNC_RULE_SCOPING.md)
- Sub-task: predefined-search typed comparison support: [#849](https://github.com/TetronIO/JIM/issues/849)
- Sub-task: predefined-search `All`/`Any` + nested group semantics: [#850](https://github.com/TetronIO/JIM/issues/850)

### Prior art and alignment

Relative date criteria are well-trodden ground in traditional ILM platforms, whose set/criteria filter engines (a subset of XPath 2.0) express relative dates as **`now ± an ISO-8601 duration`** compared with the ordinary relational operators, for example "objects whose expiry is at or before now + 7 days" or "objects modified at or after now − 10 days".

**Crucially, that capability lived in the filter-expression layer, not in the UI.** In those platforms the visual criteria builder for a DateTime attribute offered only the plain comparison operators (`=`, `!=`, `<`, `<=`, `>`, `>=`) against a **literal** date; relative behaviour required either hand-authoring the underlying filter expression in an "advanced" view, or relying on scheduled background recalculation of "temporal" membership. There was no first-class relative-date control. **This PRD's central UX improvement over that prior art is to make the relative anchor a first-class UI control** (count + unit + direction, with a live preview; see UI Mocks), so an administrator never has to drop to a filter-expression language to say "expires within 7 days".

Two aspects of that prior art directly validate decisions in this PRD:

- **Operators are not date-specific.** The legacy engines reuse the standard relational/equality operators (`<`, `<=`, `>`, `>=`, `=`, `!=`) against a computed `now ± offset`, and restrict date attributes to those operators (no `contains` / `starts-with` on dates). This matches our "change the comparison value, not the operator" model (requirement 3) and our operator-applicability validation (requirement 15).
- **Day/time vs year/month split for calendar correctness.** Those engines separate day-time durations (days, hours, etc.) from year-month durations (months, years) precisely because months and years require calendar arithmetic. This is the same reason we mandate `AddMonths` / `AddYears` rather than fixed multipliers (requirement 3). Our (count + unit + direction) tuple is a UI-friendly encoding of the same split: Hours/Days/Weeks resolve via day-time arithmetic, Months/Years via calendar arithmetic.

Deliberate divergences from that prior art, recorded so they are conscious choices rather than omissions:

1. **Whole-day rounding** (Resolved Decision 1): the legacy engines resolve to the exact instant; we truncate day-and-coarser units to midnight UTC for predictable membership, with the `Hours` unit recovering instant precision.
2. **No compound / nested offsets** (Non-Goals): the legacy engines can nest duration functions to build `now − 1 year − 3 days`; we support a single unit per criterion.
3. **On-demand evaluation rather than scheduled recalculation** (Non-Functional Requirements): legacy "temporal sets" recalculate membership on a background schedule and can be stale between runs; JIM resolves relative criteria at the point of use (sync run / query), so they are fresh when used, at the cost of freshness being tied to the sync schedule for scope criteria.

### Alignment note (issue #85 vs current code, June 2026)

The issue states "at the moment you can perform greater than and less than date searches". As of this PRD that is true **only for sync rule scoping**; **object/predefined searches support no date comparison at all** (`PredefinedSearchCriteria` is `StringValue`-only and the query translator throws `NotSupportedException` for ordering operators). The feature is therefore larger on the search side than the issue implies: literal date search (and the predefined-search group-logic gap) must be built before relative date search can sit on it. The sync-rule-scoping side is genuinely incremental. This is reflected in the Dependencies and Resolved Decisions above; delivery is strictly sequential, enabling sub-tasks #849 then #850 first, then relative date/time criteria across scoping and search.
