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
- The relative anchor supports at least the units administrators actually use: **days, weeks, months, years**, in both directions (ago / from now). Verifiable by configuring one criterion per unit/direction and confirming the computed boundary.
- Object/predefined searches gain **typed date comparison** (`LessThan`, `LessThanOrEquals`, `GreaterThan`, `GreaterThanOrEquals`, `Equals`, `NotEquals`) for `DateTime` attributes, both literal and relative, closing the gap with sync rule scoping. Verifiable by a predefined search returning the correct objects for a literal-date and a relative-date criterion.
- Existing literal-date scope filters and searches continue to work unchanged (backward compatible). Verifiable by the existing scoping integration/unit tests passing without modification.
- The relative-vs-literal choice is a first-class, discoverable option in the criteria editor UI, not a hidden mode. Verifiable by an administrator building a relative criterion end to end in the UI with no documentation.

## Non-Goals

- **Arbitrary date arithmetic / expression language.** No "now minus 2 months plus 3 days", no cron-style expressions, no calendar-aware business-day maths. The relative anchor is a single signed offset of one unit (count + unit + direction). Compound offsets are out of scope.
- **Relative anchors for non-date types.** This feature is `DateTime`-attribute only. Relative numeric ranges ("salary within 10% of X") are not in scope.
- **Time-of-day / sub-day precision semantics beyond what the attribute already stores.** The feature computes boundaries from `DateTime.UtcNow`; it does not introduce business-hours, time-zone-per-user, or "start of day" rounding as a configurable behaviour in v1 (see Open Questions for the day-boundary rounding decision).
- **Changing the comparison-operator set.** No new `SearchComparisonType` values; relative dates reuse the existing `LessThan` / `GreaterThan` / etc. operators. A relative criterion changes how the *comparison value* is produced, not how the comparison is performed.
- **A general fix for unimplemented predefined-search group logic (`All` vs `Any`, nesting) beyond what date search requires.** The `All`/`Any` and nesting TODOs in `MetaverseRepository` are pre-existing. This PRD must not *regress* them and should implement at least the group semantics its own date criteria need, but a complete predefined-search query-engine overhaul is a separate effort (flag in Open Questions).
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
2. A Relative value must be expressed as **(offset count: integer, unit: enum, direction: enum)**, e.g. `count=7, unit=Days, direction=FromNow`. The unit enum must include at least `Days`, `Weeks`, `Months`, `Years`. The direction enum must include `Ago` (subtract from now) and `FromNow` (add to now). A zero offset is permitted and resolves to "now".
3. At evaluation time the Relative value must resolve to a concrete UTC `DateTime` as `DateTime.UtcNow (±) offset`, using calendar-correct arithmetic for `Months`/`Years` (`AddMonths`/`AddYears`, not fixed 30/365-day multiplication). The resolved value then feeds the **existing** comparison operator unchanged (`LessThan`, `GreaterThan`, `Equals`, etc.).
4. Relative resolution must be **computed fresh on every evaluation** (every sync run, every search execution). The resolved boundary must never be persisted back onto the criterion as a literal.
5. Relative values apply only to attributes of type `DateTime`. The API and UI must reject a Relative value on any other attribute data type.
6. The resolution helper must be a single shared function used by **both** the sync rule scoping evaluator and the object-search query path, so the two cannot diverge. (Scoping evaluates in memory; search must translate to a query predicate against a computed boundary value, computed once per search execution before the query is built.)

#### Sync rule scoping

7. `ScopingEvaluationServer`'s DateTime comparison path must resolve a Relative criterion to its boundary `DateTime` and then apply the existing operator logic. Null-attribute handling must match the existing literal behaviour (a null attribute value does not satisfy ordering comparisons).
8. A scope filter using a Relative criterion must produce a different in-scope set as wall-clock time advances, with no edit to the rule, because resolution uses `DateTime.UtcNow` at each evaluation.

#### Object / predefined searches

9. Object/predefined searches must support `DateTime` attribute comparison for `Equals`, `NotEquals`, `LessThan`, `LessThanOrEquals`, `GreaterThan`, `GreaterThanOrEquals`, in both Absolute and Relative modes. This requires the predefined-search criteria model to carry a typed `DateTimeValue` (and the Relative fields), not only the current `StringValue`.
10. The query translator (`MetaverseRepository.GetMetaverseObjectsOfTypeAsync`) must translate a date criterion into an EF predicate over the relevant attribute-value column (`DateTimeValue`), replacing the current `NotSupportedException` for ordering operators on date attributes. Relative criteria resolve their boundary to a literal `DateTime` **before** the predicate is built (so the database sees a constant, and the existing `IX_MetaverseObjectAttributeValues_DateTimeValue` index is usable).
11. The date-criteria predicates must respect the criterion's containing group semantics (`All` = AND, `Any` = OR) for the group(s) the date criteria live in. Implementing this for date criteria must not break the existing text-criteria behaviour.

#### API

12. The REST DTOs and request models that carry scoping criteria (`SyncRuleScopingCriteriaDto`, `CreateScopingCriterionRequest`, and the predefined-search criteria equivalents) must carry the value-mode and the three relative fields (count, unit, direction) alongside the existing `DateTimeValue`. Field names and JSON shape to be settled in the implementation plan.
13. The API must validate, and reject with `400 Bad Request` and a structured error: a Relative value on a non-`DateTime` attribute; a Relative value missing a unit or direction; a negative offset count (direction encodes sign, so the count is non-negative); and a criterion that sets both a literal `DateTimeValue` and Relative fields (mode is exclusive).
14. Round-trip persistence must be lossless: a criterion POSTed/PATCHed with Relative fields and fetched back must return identical mode, count, unit, direction, and comparison type.

#### UI

15. The scope-criteria editor (`SyncRuleDetailScopingCriteriaGroup.razor`) must, when the selected attribute is `DateTime`, let the administrator choose Absolute or Relative. Absolute shows the existing `MudDatePicker`. Relative shows a numeric count input, a unit selector (Days/Weeks/Months/Years), and a direction selector (Ago/From now).
16. The predefined-search criteria editor must offer the same Absolute/Relative date control. (Note: the current `PredefinedSearchDetail.razor` is read-only for criteria; the edit affordance it needs is a dependency, see Open Questions.)
17. The relative control must render a plain-language preview of what it means, e.g. "Matches dates more than 7 days from now", so the administrator can confirm intent before saving.
18. Existing literal-date criteria must continue to display and edit exactly as today when their mode is Absolute.

#### Persistence

19. The new fields (value mode, relative count, relative unit, relative direction) must be added to the `SyncRuleScopingCriteria` table and the predefined-search criteria table via an **append-only** EF Core migration. Existing rows default to Absolute mode with null relative fields, preserving current behaviour. Migrations must never be flattened or edited per the repository's migration policy.

### Non-Functional Requirements

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

### Scenario 4: Calendar-correct month arithmetic

**Given** a criterion `(now - 1 Months, Relative)` evaluated on 2026-03-31
**When** the boundary is resolved
**Then** it resolves to 2026-02-28 (via `AddMonths(-1)`), not "31 days earlier".

### Scenario 5: API rejects a relative value on a non-date attribute

**Given** a POST that sets Relative fields on a criterion targeting a `Number` attribute
**When** the request is processed
**Then** the API returns `400 Bad Request` with a structured error stating relative values apply only to `DateTime` attributes.

### Scenario 6: API rejects a criterion with both modes set

**Given** a POST that sets both `DateTimeValue` and Relative fields
**When** the request is processed
**Then** the API returns `400 Bad Request`; value mode is exclusive.

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
| API | `JIM.Web/Models/Api` scoping and predefined-search DTOs/request models carry the new fields; validation in the controllers (`SynchronisationController` and the predefined-search controller). |
| UI | `SyncRuleDetailScopingCriteriaGroup.razor` Absolute/Relative date control with preview; predefined-search criteria editor gains the same control (and an edit affordance if none exists). |
| Docs | `engineering/SYNC_RULE_SCOPING.md` gains relative-date examples; user docs for searches. |
| Tests | New unit tests for relative resolution (calendar arithmetic, direction, null handling) and the search predicate path; integration coverage for a relative scope filter shifting over time. |

## Dependencies

- **Predefined-search criteria editing UI.** `PredefinedSearchDetail.razor` currently displays criteria read-only and `PredefinedSearchCriteria` carries only `StringValue`. The object-search half of this feature depends on (a) a typed value carrier on the criteria model and (b) an edit affordance. If these are larger than expected, the implementation plan may split delivery: sync-rule scoping (relative dates on an already-working literal path) first, object-search relative dates second.
- The pre-existing `All`/`Any`/nesting group-logic gap in `MetaverseRepository` is adjacent; this feature must implement at least what its date criteria need and must not regress existing text-criteria behaviour.

## Open Questions

1. **Day-boundary rounding.** Should "now minus 30 days" mean *exactly* 30×24h before the current instant, or the **start of the day** 30 days ago? Administrators usually mean whole-day boundaries ("hired in the last 30 days" should not depend on the time the search runs). Recommend: resolve to whole-day boundaries (truncate to midnight UTC) for relative criteria, and state it explicitly. Needs a decision because it affects edge-of-window matches.
2. **Value-mode representation.** Explicit `enum ValueMode { Absolute, Relative }` field, versus inferring Relative from "relative fields are populated". Recommend an explicit enum for clarity and to make the exclusive-mode validation unambiguous.
3. **Scope of the predefined-search query-engine work.** Do we implement only the group semantics our date criteria require, or take the opportunity to close the broader `All`/`Any`/nesting TODO? Recommend scoping tightly to date criteria here and filing the general overhaul separately, to keep this feature shippable.
4. **Unit set.** Are Days/Weeks/Months/Years sufficient, or is `Hours` needed for any scope filter? Recommend Days/Weeks/Months/Years for v1; add finer units only on a concrete request.

## Acceptance Criteria

- [ ] A date criterion can be saved as Absolute (literal) or Relative (count + unit + direction) in both sync rule scoping and object/predefined searches.
- [ ] A Relative scope criterion re-resolves against `DateTime.UtcNow` on every evaluation; the in-scope set shifts as time advances with no edit to the rule.
- [ ] Relative units cover Days/Weeks/Months/Years in both Ago and FromNow directions; Months/Years use calendar-correct `AddMonths`/`AddYears`.
- [ ] Object/predefined searches support `Equals`/`NotEquals`/`LessThan`/`LessThanOrEquals`/`GreaterThan`/`GreaterThanOrEquals` on `DateTime` attributes (literal and relative); the previous `NotSupportedException` path for date ordering operators is gone.
- [ ] Existing literal-date scope filters and any existing searches behave identically to before (backward compatible); existing scoping tests pass unmodified.
- [ ] The API carries the new fields, round-trips them losslessly, and returns `400` for: relative-on-non-date, missing unit/direction, negative count, both-modes-set.
- [ ] The criteria editor UI exposes the Absolute/Relative choice with a plain-language preview; Absolute uses the existing date picker.
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

### Alignment note (issue #85 vs current code, June 2026)

The issue states "at the moment you can perform greater than and less than date searches". As of this PRD that is true **only for sync rule scoping**; **object/predefined searches support no date comparison at all** (`PredefinedSearchCriteria` is `StringValue`-only and the query translator throws `NotSupportedException` for ordering operators). The feature is therefore larger on the search side than the issue implies: literal date search must be built before relative date search can sit on it. The sync-rule-scoping side is genuinely incremental. This is reflected in the Dependencies and Open Questions above; the implementation plan should consider delivering scoping first, search second.
