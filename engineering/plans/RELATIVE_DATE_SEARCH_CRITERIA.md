# Relative Date/Time Search and Scoping Criteria: Implementation Plan

- **Status:** Planned
- **Issue:** [#85](https://github.com/TetronIO/JIM/issues/85) (sub-tasks [#849](https://github.com/TetronIO/JIM/issues/849), [#850](https://github.com/TetronIO/JIM/issues/850))
- **PRD:** [`engineering/prd/PRD_RELATIVE_DATE_SEARCH_CRITERIA.md`](../prd/PRD_RELATIVE_DATE_SEARCH_CRITERIA.md)

## Overview

Add relative ("now ± N units") date criteria to both sync rule scope filters and object/predefined searches, alongside the existing literal-date support. Because object/predefined searches currently support no date comparison at all (and no group logic), the work is delivered in three strictly sequential phases, each building and testing green before the next begins:

1. **Phase 1 (#849)** Predefined-search typed (non-text) comparison support, including the criteria CRUD API, PowerShell cmdlets, and an edit UI. Literal only.
2. **Phase 2 (#850)** Predefined-search `All`/`Any` and nested-group query semantics.
3. **Phase 3 (#85)** Relative date/time criteria across scoping and search: shared resolution, model fields, evaluator and query-translator changes, API and PowerShell relative parameters, the criterion in-place-edit (PATCH) retrofit for scoping, and the relative UI control.

Sequencing rationale and resolved decisions (whole-day rounding with an Hours exception, calendar-correct month/year arithmetic, on-demand evaluation, single-unit offsets only) live in the PRD and are not re-argued here.

## Current State (verified)

- **Scoping evaluation** (`src/JIM.Application/Servers/ScopingEvaluationServer.cs`): already does full recursive `All`/`Any`/nested-group evaluation and per-type comparison, including `EvaluateDateTimeComparison` against a literal `DateTimeValue`. This is the reference pattern for Phase 2 and the insertion point for Phase 3 relative resolution.
- **Scoping criterion model** (`src/JIM.Models/Logic/SyncRuleScopingCriteria.cs`): carries typed value carriers (`StringValue`, `IntValue`, `LongValue`, `DateTimeValue`, `BoolValue`, `GuidValue`) and `CaseSensitive`.
- **Predefined-search criterion model** (`src/JIM.Models/Search/PredefinedSearchCriteria.cs`): carries **only** `StringValue` (non-nullable). Needs typed carriers.
- **Predefined-search group model** (`src/JIM.Models/Search/PredefinedSearchCriteriaGroup.cs`): already has `Type` (All/Any), `Criteria`, `Position`, `ChildGroups`, `ParentGroup`. Nesting is modelled; only the query translator ignores it.
- **Query translator** (`src/JIM.PostgresData/Repositories/MetaverseRepository.cs`, `GetMetaverseObjectsOfTypeAsync`, around lines 886-961): handles text equality/prefix/suffix only; throws `NotSupportedException` for ordering operators; has stubbed `// err?` group-type handling and a `// todo: handle group nesting` comment, so groups are effectively flattened and ANDed.
- **Predefined-search API** (`src/JIM.Web/Controllers/Api/PredefinedSearchesController.cs`): GET (all / by id / by uri) and PATCH (update, `isEnabled` only). No criteria/group write endpoints.
- **Scoping API** (`src/JIM.Web/Controllers/Api/SynchronisationController.cs`): criteria-group CRUD (`Create`/`Update`/`Delete` group, plus child-group create) exists; criteria have **POST and DELETE only**, no update path.
- **Predefined-search UI** (`src/JIM.Web/Pages/Admin/PredefinedSearchDetail.razor`): read-only display of criteria, showing `StringValue` only.
- **Scoping UI** (`src/JIM.Web/Pages/Admin/SyncRuleDetailScopingCriteriaGroup.razor`): full add/remove criteria + nested groups; `MudDatePicker` for DateTime.
- **PowerShell** (`src/JIM.PowerShell/Public/`): `ScopingCriteria/` has `Get`/`New`(group, criterion)/`Set`(group)/`Remove`(group, criterion); no criterion `Set`. `Search/` has only `Get-JIMPredefinedSearch` and `Set-JIMPredefinedSearch`.

## Shared design decisions (apply across phases)

- **New enums** added to `src/JIM.Models/Search/SearchEnums.cs` (enums are grouped per area): `DateCriteriaValueMode { Absolute = 0, Relative = 1 }`, `RelativeDateUnit { Hours, Days, Weeks, Months, Years }`, `RelativeDateDirection { Ago, FromNow }`.
- **Shared resolution helper** `RelativeDateResolver` in `src/JIM.Models/Search/` (JIM.Models is referenced by both JIM.Application and JIM.PostgresData, so both consume the one implementation). Signature roughly: `static DateTime Resolve(int count, RelativeDateUnit unit, RelativeDateDirection direction, DateTime nowUtc)`. Rules: `FromNow` adds, `Ago` subtracts; `Hours` -> `AddHours`; `Days` -> `AddDays`; `Weeks` -> `AddDays(±count*7)`; `Months` -> `AddMonths`; `Years` -> `AddYears`; then for every unit except `Hours`, truncate to midnight UTC (`.Date`). Pure function, no `DateTime.UtcNow` inside (caller passes `nowUtc`) so it is unit-testable deterministically.
- **Relative fields** added to both `SyncRuleScopingCriteria` and `PredefinedSearchCriteria`: `DateCriteriaValueMode ValueMode` (default `Absolute`), `int? RelativeCount`, `RelativeDateUnit? RelativeUnit`, `RelativeDateDirection? RelativeDirection`.
- **Friendly date operator labels** in the UI: for `DateTime` attributes the editor shows "before / on or before / after / on or after / equals / does not equal" mapped to the existing `SearchComparisonType` values, mirroring the prior-art wording; no new enum values.
- **No new NuGet packages.** Phase 2 expression composition is hand-rolled (see Risks).

---

## Phase 1: Predefined-search typed comparison support (#849)

**Goal:** object/predefined searches can filter on `DateTime`, `Number`, `LongNumber`, `Boolean`, and `Guid` attributes (literal values), configured and edited through API, PowerShell, and UI. No relative dates yet, no group semantics beyond what exists.

### Model and persistence
- `PredefinedSearchCriteria`: make `StringValue` nullable; add `int? IntValue`, `long? LongValue`, `DateTime? DateTimeValue`, `bool? BoolValue`, `Guid? GuidValue`, `bool CaseSensitive = true`, to mirror `SyncRuleScopingCriteria`.
- EF migration (append-only) adding the new nullable columns and relaxing `StringValue` nullability. Review generated migration; confirm against `JimDbContextModelSnapshot.cs`.

### Data / query translation
- In `MetaverseRepository.GetMetaverseObjectsOfTypeAsync`, replace the `NotSupportedException` switch with typed predicates that select the correct `MetaverseObjectAttributeValue` column based on the criterion attribute's `AttributeDataType`, supporting `Equals`/`NotEquals`/`LessThan`/`LessThanOrEquals`/`GreaterThan`/`GreaterThanOrEquals` for ordered types and `Equals`/`NotEquals` for `Boolean`/`Guid`. Reuse the existing `AttributeValues.Any(av => av.Attribute.Id == ... && <typed comparison>)` shape so the existing `IX_MetaverseObjectAttributeValues_DateTimeValue` (and sibling) indexes remain usable.
- Group composition stays as-is for this phase (single-group behaviour preserved); Phase 2 fixes multi-group/nesting.

### Application / repository layer
- Add criteria-group and criterion CRUD to the predefined-search server and repository interfaces (mirroring the scoping-criteria methods on `SynchronisationServer`): create/get/update/delete group, create/update/delete criterion. IDs are `int` per the identifier rules.

### API (`PredefinedSearchesController`)
- New endpoints mirroring the scoping shape:
  - `GET /predefined-searches/{id}/criteria-groups`
  - `POST /predefined-searches/{id}/criteria-groups`, `POST .../{groupId}/child-groups`
  - `PUT /predefined-searches/{id}/criteria-groups/{groupId}` (type, position)
  - `DELETE /predefined-searches/{id}/criteria-groups/{groupId}`
  - `POST /predefined-searches/{id}/criteria-groups/{groupId}/criteria`
  - `PUT /predefined-searches/{id}/criteria-groups/{groupId}/criteria/{criterionId}` (full criterion update)
  - `DELETE .../criteria/{criterionId}`
- DTOs in `src/JIM.Web/Models/Api/` carrying the typed values; XML docs for Scalar. Validation: operator-not-applicable to data type -> `400`; value carrier mismatching the attribute type -> `400`.

### PowerShell (`Public/Search/`)
- New: `New-/Get-/Set-/Remove-JIMPredefinedSearchCriteriaGroup`, `New-/Set-/Remove-JIMPredefinedSearchCriterion`, following the `ScopingCriteria/` cmdlet conventions (parameter sets, attribute-by-id-or-name, `SupportsShouldProcess`, comment-based help, `ValidateSet` for `ComparisonType`). Typed value parameters (`-StringValue`/`-IntValue`/`-LongValue`/`-DateTimeValue`/`-BoolValue`/`-GuidValue`/`-CaseSensitive`).

### UI (`PredefinedSearchDetail.razor`)
- Replace the read-only criteria display with an editor. Reuse the "Add Criteria" dialog pattern from `SyncRuleDetailScopingCriteriaGroup.razor` (which is the candidate to extract into a shared component, since two pages will now use it; see Risks). Per-type value inputs as in the scoping editor. Friendly operator labels for `DateTime`.

#### UI mock: predefined-search criteria editor (was read-only)

The Criteria panel gains the same add/remove affordances the scoping editor already has. Today it only lists `StringValue`; after Phase 1 it edits typed criteria.

```
Predefined Search: "Distribution groups"            [ Run ]
------------------------------------------------------------------
 Object type: Group

 CRITERIA GROUP. LOGIC TYPE: ALL                          [ 🗑 ]

   (MV) GroupType    [ Equals ]          [ Text: Distribution ]  🗑
   (MV) MemberCount  [ Greater Than ]    [ Number: 0 ]           🗑

            [ + Add Criteria Group ]   [ + Add Criteria ]
------------------------------------------------------------------
 Results (live):  37 groups
```

#### UI mock: "Add Criteria" dialog, literal typed value (Number shown)

```
+------------------------------------------------------------+
|  ⚖  Add Criteria                                           |
+------------------------------------------------------------+
|  Metaverse Attribute            [ MemberCount        ▼ ]   |
|  Comparison Type                [ Greater Than       ▼ ]   |
|  Number Value                   [ 0                    ]   |
+------------------------------------------------------------+
|                                   [ Cancel ]  [ Add Criteria ]
+------------------------------------------------------------+
```

For a `DateTime` attribute in this phase the value control is the existing `MudDatePicker`, and the Comparison Type dropdown shows the friendly labels ("before", "on or before", "after", "on or after", "equals", "does not equal") mapping to the `SearchComparisonType` values.

### Tests
- `JIM.Models.Tests`: criterion typed-value round-trip / mapping.
- `JIM.Web.Api.Tests`: each new endpoint, plus validation (`400`) cases.
- Repository/integration tests (not EF in-memory, per the `.Include` masking caveat in CLAUDE.md): typed predicates return the correct objects for each data type and operator against PostgreSQL.

**Phase 1 done when:** literal typed criteria can be created/edited/deleted via API, PowerShell, and UI, and predefined searches return correct results for each data type and ordered operator; build and full test suite green.

---

## Phase 2: Predefined-search group semantics (#850)

**Goal:** the query translator honours `All` (AND), `Any` (OR), and nested groups for all criteria types, matching the semantics the scoping evaluator already implements in memory.

### Data / query translation
- Replace the flattened per-group loop in `GetMetaverseObjectsOfTypeAsync` with a recursive builder that turns each `PredefinedSearchCriteriaGroup` into an `Expression<Func<MetaverseObject, bool>>`: each criterion becomes `mo => mo.AttributeValues.Any(av => av.Attribute.Id == X && <typed comparison>)`; a group combines its criteria and child groups with `Expression.AndAlso` (All) or `Expression.OrElse` (Any); the top-level groups combine per existing top-level semantics. Apply the final composed expression with a single `.Where(...)`.
- Use a small hand-rolled parameter-rebinding helper (an `ExpressionVisitor` that replaces the lambda parameter) to combine expressions, so EF Core can translate the tree. **Do not** use `Expression.Invoke` (EF cannot translate it) and **do not** add LINQKit (no new NuGet).

### Tests
- Repository/integration tests for: single `All` group (AND), single `Any` group (OR), and at least one nested `(A OR B) AND C` construction, across text and at least one numeric/date type. Confirm Phase 1 single-group behaviour is unchanged.

### UI
- Ensure the Phase 1 predefined-search editor supports adding nested groups and choosing group type (the scoping editor already does; the shared component should cover both).

#### UI mock: nested groups in the predefined-search editor

A child group renders indented inside its parent with its own logic type, mirroring the scoping editor. This expresses `(A OR B) AND C`.

```
 CRITERIA GROUP. LOGIC TYPE: ALL                          [ 🗑 ]

   (MV) IsActive    [ Equals ]            [ Boolean: true ]      🗑

   ┌── CRITERIA GROUP. LOGIC TYPE: ANY                    [ 🗑 ] ┐
   │                                                            │
   │   (MV) Department [ Equals ]   [ Text: Finance ]       🗑  │
   │   (MV) Department [ Equals ]   [ Text: Sales ]         🗑  │
   │                                                            │
   │        [ + Add Criteria Group ]   [ + Add Criteria ]       │
   └────────────────────────────────────────────────────────────┘

            [ + Add Criteria Group ]   [ + Add Criteria ]
```

**Phase 2 done when:** multi-criteria and nested predefined searches return correct results; build and full test suite green.

---

## Phase 3: Relative date/time criteria (#85)

**Goal:** relative dates work end-to-end on both surfaces, plus the criterion in-place-edit retrofit.

### Model and persistence
- Add the shared enums and `RelativeDateResolver` (see Shared design decisions).
- Add `ValueMode` + relative fields to `SyncRuleScopingCriteria` and `PredefinedSearchCriteria`.
- EF migration (append-only) adding the four columns to both tables; existing rows default to `Absolute` with null relative fields.
- Update `SyncRuleScopingCriteria.ToString()` (and equivalent) to render relative criteria in plain language ("30 days ago"), since the UI chips use it.

### Evaluation (scoping) and query translation (search)
- `ScopingEvaluationServer`: where a criterion is `DateTime` and `ValueMode == Relative`, resolve the boundary via `RelativeDateResolver.Resolve(count, unit, direction, DateTime.UtcNow)` once and feed it into the existing `EvaluateDateTimeComparison`. Compute per evaluation pass, not per object.
- `MetaverseRepository`: where a date criterion is relative, resolve the boundary to a literal `DateTime` **before** building the predicate (so the SQL sees a constant and the index is used). One resolution per query execution.

### API
- Extend the scoping DTOs/requests (`SyncRuleScopingCriteriaDto`, `CreateScopingCriterionRequest`) and the Phase 1 predefined-search criterion DTOs with `valueMode` + relative fields; XML docs.
- **Retrofit** `PUT /sync-rules/{id}/scoping-criteria/{groupId}/criteria/{criterionId}` (`UpdateScopingCriterion`) on `SynchronisationController`, with the backing application/repository `UpdateScopingCriterionAsync(int id, ...)`; this is net-new public API for scoping and gets its own reviewable change set + tests.
- Validation across both surfaces: relative-on-non-date, missing unit/direction, negative count, both-modes-set -> `400` structured error.

### PowerShell
- Extend `New-JIMScopingCriterion` and `New-JIMPredefinedSearchCriterion` with `-ValueMode`/`-RelativeCount`/`-RelativeUnit`/`-RelativeDirection` in a dedicated parameter set (mutually exclusive with `-DateTimeValue`); add `Set-JIMScopingCriterion` (backed by the retrofitted PATCH) and ensure `Set-JIMPredefinedSearchCriterion` carries the relative surface. Client-side validation of relative combinations.

### UI
- Scoping and predefined-search editors: when the selected attribute is `DateTime`, show a `ValueMode` toggle (Absolute/Relative). Absolute keeps `MudDatePicker`; Relative shows count (`MudNumericField`, min 0) + unit (`MudSelect`) + direction (`MudSelect`) with a live plain-language preview. Support editing an existing criterion in place. Relative criterion chips render plain language. (See PRD UI Mocks.)

#### UI mock: "Add/Edit Criteria" dialog, DateTime attribute, Relative mode

The `Value mode` toggle and the Relative sub-form appear only for `DateTime` attributes; all other types are unchanged from Phase 1. In Edit mode the dialog opens pre-populated with the existing criterion's values (in-place edit, requirement 24).

```
+------------------------------------------------------------+
|  ⚖  Edit Criteria                                          |
+------------------------------------------------------------+
|  Metaverse Attribute            [ AccountExpiry      ▼ ]   |
|  Comparison Type                [ On or before       ▼ ]   |
|                                                            |
|  Value mode      ( ○ Absolute )  ( • Relative )            |
|                                                            |
|   Count            Unit                Direction           |
|  [   7   ]        [ Days        ▼ ]   [ From now    ▼ ]    |
|                    Hours / Days / Weeks / Months / Years   |
|                    Ago / From now                          |
|                                                            |
|  ┌──────────────────────────────────────────────────┐     |
|  │ ℹ Matches when AccountExpiry is on or before      │     |
|  │   7 days from now (re-evaluated each run).         │     |
|  └──────────────────────────────────────────────────┘     |
+------------------------------------------------------------+
|                                   [ Cancel ]  [ Save ]      |
+------------------------------------------------------------+
```

#### UI mock: relative criterion chips (both editors)

Saved relative criteria read as plain language, never a resolved literal date, and each row gains an Edit affordance (the in-place edit path).

```
 CRITERIA GROUP. LOGIC TYPE: ALL                          [ 🗑 ]

   (MV) AccountExpiry  [ On or before ]  [ 7 days from now ]  ✎  🗑
   (MV) Department     [ Equals ]        [ Text: Finance ]    ✎  🗑
```

Notes: `Value mode` is a `MudRadioGroup`; the Relative inputs are `MudNumericField` (min 0) + two `MudSelect`s. The preview restates the resolved meaning and that it re-evaluates each run. The `Hours` unit gives instant precision; `Days` and coarser round to midnight UTC (Resolved Decision 1).

### Docs
- `engineering/SYNC_RULE_SCOPING.md`: add relative-date examples (two worked examples). Update changelog under `[Unreleased]` for the user-facing capability.

### Tests
- `JIM.Models.Tests`: `RelativeDateResolver` per unit/direction, month/year calendar edge cases (e.g. 31 Mar minus 1 month), whole-day truncation vs Hours instant, zero offset.
- `JIM.Worker.Tests` (`ScopingEvaluationTests`): relative DateTime scoping including null-attribute handling and "set shifts as `now` advances" (inject `nowUtc`).
- `JIM.Web.Api.Tests`: relative round-trip on both surfaces; the new scoping criterion PATCH; all `400` validation cases.
- Integration: predefined search with a relative date criterion returns the correct objects.

**Phase 3 done when:** all PRD acceptance criteria are met; `dotnet build JIM.sln` and `dotnet test JIM.sln` are green.

---

## Success Criteria

Maps to the PRD acceptance criteria. In brief: relative criteria configurable and editable in place on both surfaces via UI, API, and PowerShell; calendar-correct, whole-day-rounded (Hours excepted) resolution computed fresh each evaluation; predefined searches gain typed date comparison and correct `All`/`Any`/nesting; existing literal behaviour and tests unchanged; append-only migrations; full build/test green.

## Risks and Mitigations

- **EF expression composition (Phase 2).** Combining predicates into an EF-translatable tree is the main technical risk. Mitigation: hand-rolled parameter-rebinding `ExpressionVisitor`, no `Expression.Invoke`, no new dependency; cover with integration tests against real PostgreSQL (EF in-memory would mask translation failures).
- **Shared "Add Criteria" component.** Two editors will need the same dialog. Mitigation: extract the scoping dialog into a shared component in `JIM.Web/Shared/` during Phase 1 rather than copy-pasting (per the JIM.Web conventions on componentising repeated UI).
- **Scoping criterion PATCH is new public API.** Slightly beyond issue #85's literal wording. Mitigation: isolate as its own change set with dedicated tests; called out explicitly here and in the PRD.
- **`StringValue` nullability change (Phase 1).** Relaxing a non-null column is a schema change. Mitigation: append-only migration; verify no code assumes non-null `StringValue` on predefined-search criteria.
- **Index usage for relative queries.** Resolving the boundary to a constant before building the predicate keeps the `DateTimeValue` index usable; verified by inspecting the generated SQL / query plan in integration tests.

## Dependencies

- Strictly sequential: Phase 1 (#849) -> Phase 2 (#850) -> Phase 3 (#85). Each phase builds and tests green before the next starts.
- No new NuGet packages, PowerShell modules, or connectors. Air-gapped friendly ("now" is the host UTC clock).
