# Scoping Criteria Evaluation Matrix (Scenario 11)

- **Status:** Planned
- **Created:** 2026-05-22
- **Author:** Jay Van der Zant
- **Issue:** #[number] *(to be filed after PRD review)*

## Problem Statement

Sync Rule scoping in JIM is driven by `SyncRuleScopingCriteria`, which composes attribute comparisons via `SearchComparisonType` operators inside `SearchGroupType` groups (`All`/`Any`, optionally nested). Together this is a moderately large evaluation matrix: 12 operators (Equals, NotEquals, StartsWith, NotStartsWith, EndsWith, NotEndsWith, Contains, NotContains, LessThan, LessThanOrEquals, GreaterThan, GreaterThanOrEquals) × 6 typed value carriers (Text, Number, LongNumber, DateTime, Boolean, Guid) × 2 group structures × `CaseSensitive` flag for text comparisons.

Scenario 10 (`Invoke-Scenario10-SyncRuleScoping.ps1`) deliberately covers only the **common ILM shape**: text `Equals` / `StartsWith` / `Contains` in a single `All` group, exercised against the full action lifecycle (inbound enter / in-scope-update / exit-disconnect / exit-remain-joined; outbound enter / exit-disconnect / exit-delete; cross-system inline cascade). It is fast (around 2 minutes 41 seconds on Nano) precisely because it does not enumerate the evaluation matrix.

The result is a coverage gap: most of the operator / attribute-type / group-structure combinations have **no integration coverage at all**. A regression in, say, `LessThanOrEquals` on a `DateTime` attribute, or `Any` (OR) group short-circuiting, would not be caught by any of the eleven existing scenarios. Unit tests cover individual operator evaluation in isolation but do not cross the API, persistence, and worker-evaluation boundaries end-to-end, and they do not cover the round-trip persistence of typed values via the public REST API.

This PRD scopes a dedicated integration scenario whose purpose is **evaluation correctness across the full matrix**, complementing rather than duplicating Scenario 10. It is a parameterised sweep: each "cell" is one operator + value-type + group-structure combination, asserted by checking which subset of a known seed population is in scope after a single inbound sync.

## Goals

- Cover the full operator × value-type × group-structure matrix that `SyncRuleScopingCriteria` exposes, in a single repeatable integration scenario.
- Cover `CaseSensitive` true/false behaviour for every text-based operator.
- Cover null / missing attribute handling for every operator (e.g. `EmployeeId IS NULL` MVOs must not match `EmployeeId Equals "1234"`).
- Cover `All` (AND) groups, `Any` (OR) groups, and at least one nested-group construction.
- Cover round-trip persistence of typed values (Text, Number, LongNumber, DateTime, Boolean, Guid) via the public REST API: configure a rule, fetch it back, confirm value carriers survived intact.
- Run reliably at the Nano template; total wall-clock for the matrix should be meaningfully faster than running the equivalent permutations as separate scenarios.
- Produce a per-cell pass/fail breakdown in the standard scenario report, so an operator-level regression points to the exact failing cell rather than a generic "scoping matrix failed".

## Non-Goals

- Cascade and lifecycle assertions (RPEI shapes, PendingExport queue contents, OutOfScope action selection, MVO obsoletion). Those live in Scenario 10 and must not be duplicated here.
- Multi-connector / cross-system cascade. The matrix targets a single inbound rule against a single Connected System; outbound cascade is Scenario 10's territory.
- Performance / scale assertions. The matrix runs at Nano and asserts correctness, not throughput. Performance baselines remain Scenario 14's responsibility.
- New operator types, new value carriers, or new group semantics. This scenario tests what the system already supports; it is not a vehicle for extending the scoping engine.
- UI coverage. The matrix is driven via the REST API; the scoping rule editor in the Blazor UI is out of scope.
- `Reference` attribute scoping. The scoping criteria model only carries `GuidValue` (object ID) and does not currently target reference attributes as such; if and when reference scoping is added, it gets its own coverage.

## User Stories

1. As a developer modifying the scoping evaluator (`SyncRuleScopingEvaluator` or any of its callers), I want a single integration scenario that exercises every operator and value-type combination, so that a regression in `NotContains` on a `LongNumber` attribute is caught at PR time rather than in production.
2. As a release engineer running the pre-release suite, I want operator-level evaluation correctness to be part of regression, so that I can ship with confidence that no scoping-engine change quietly broke a specific combination.
3. As a developer triaging a failed CI run, I want each matrix cell to be a named, individually-reported assertion, so that the failure surface points at the exact operator / type / group combination rather than at the whole scenario.

## Requirements

### Functional Requirements

#### Seed dataset

1. The scenario must seed a small (`<= 20`), fully deterministic HR-style dataset via the file connector, with attribute values chosen so that every operator/type combination has a non-empty AND a non-empty-complement match set. Concretely the seed must include:
   - **Text values** spanning case, prefix, suffix, and substring distinctions (e.g. `Department` values like `Finance`, `finance`, `FinancePartners`, `CorporateFinance`, `Sales`, `IT`, plus at least one MVO with `Department` null).
   - **Number values** spanning negative, zero, small, and large (e.g. `EmployeeNumber` from `-10` to `10000`, plus one null).
   - **LongNumber values** with at least one value beyond `Int32.MaxValue` so the LongNumber-vs-Number distinction is real, plus one null.
   - **DateTime values** spanning at least two calendar years and including the literal epoch `1970-01-01T00:00:00Z`, plus one null.
   - **Boolean values** with `true`, `false`, and one null.
   - **Guid values** with at least three distinct GUIDs and one null.
2. The seed dataset must be generated by a deterministic helper (fixed inputs, no `Get-Date`) so that the cache machinery in `Get-OrGenerate-TestCSV.ps1` can serve it byte-identically across runs.
3. The seed dataset must be loaded exactly once at scenario start; matrix cells reuse it rather than re-importing per cell.

#### Template handling (locked to bespoke seed)

The matrix is testing operator evaluation correctness, not scale behaviour, and every cell's expected match-set is hand-derived from a specific record population. Varying that population by `-Template` would either invalidate the expected sets (if the generator's output replaced the seed) or make the parameter a lie (if the seed were used regardless). Neither is acceptable. The scenario is therefore locked to its bespoke deterministic seed for the actual matrix work.

4. The scenario **must accept** the standard `-Template` parameter for runner-API consistency (so `Run-IntegrationTests.ps1 -Scenario All -Template Small` does not have to special-case Scenario 11), but the parameter is **informational only** for this scenario. It does not change the seed population, the cell list, or the expected match-sets.
5. The scenario's docstring must explicitly state that template is informational, mirroring Scenario 10's existing wording: "Scoping evaluation correctness is template-independent; Nano is sufficient and is the default."
6. Scale-related concerns (query planner behaviour on large CSO tables, predicate pushdown at high row counts, evaluator memory footprint) are explicitly the responsibility of Scenario 14 (Performance Baselines) and must not be retrofitted onto this scenario.

#### Matrix manifest

Cell definitions live in a checked-in **manifest** under `test/integration/scenarios/data/`, not inline in the scenario script. Diffing the manifest is the canonical way to see what changed when cells are added, modified, or removed; the script reads the manifest and executes it.

7. The matrix must be defined declaratively in a manifest file at `test/integration/scenarios/data/scoping-criteria-matrix.*` (extension chosen during implementation; JSON or PSD1). Each entry carries a stable cell name, the operator, the targeted attribute, the value carrier, the group structure, the `CaseSensitive` flag (where applicable), the coverage tier(s) the cell belongs to (see requirement 13), and the expected matching `EmployeeId` set.
8. The scenario script must validate the manifest at load time and fail fast with a clear error if any cell is malformed (unknown operator, mismatched value carrier, unknown tier, expected set referencing an `EmployeeId` not in the seed).

#### Cell shape

9. Each matrix cell is a tuple of `(operator, value-carrier-type, group-structure, case-sensitivity, expected-matching-MVO-set)`. The expected matching set is the literal set of `EmployeeId`s the rule should accept given the seed.
10. The scenario must drive each cell via the **public REST API** (Sync Rule create/update + inbound sync trigger), not via direct repository writes. This is what makes the test meaningful as an end-to-end check.
11. Per cell, the test must:
    1. Configure a sandbox import Sync Rule against the seeded Connected System with the cell's scoping criteria.
    2. Trigger an inbound sync.
    3. Read back the set of MVOs the rule projected, identified by `EmployeeId`.
    4. Assert the projected set matches the cell's expected set exactly.
    5. Tear down the cell's projections cleanly so the next cell starts from a known state (see "Cell isolation" below).
12. Each cell is reported as its own pass/fail line in the scenario summary, with a stable cell name like `Operator=NotContains, Type=Text, Group=All, CaseSensitive=false`.

#### Coverage tiers

The matrix runs at one of three coverage tiers, selectable via parameter or interactive menu (see requirement 31 for the parameter surface). Each tier is a strict superset of the one below it.

13. The matrix must support three coverage tiers, with the following content and wall-clock targets at Nano (targets assume the batched-sync cell-isolation strategy in requirement 22 option 1):

    | Tier | Selector | Cells (approx) | Target | Purpose |
    |------|----------|----------------|--------|---------|
    | Quick | `-Quick` | ~12 | < 90 s | Fast PR feedback. One cell per operator. No group nesting, no null-handling cells, no `CaseSensitive=false` variations. |
    | Default (Full) | *(no flag)* | ~120 | < 5 min | Every applicable `(operator × value-type)` pair (per requirement 14), null-handling cells per operator/type, both `CaseSensitive` settings for text, and **at least one** cell of each group structure (`All`, `Any`, nested). Default for both interactive runs and automated regression. |
    | Exhaustive | `-Exhaustive` | ~360 | < 10 min | Full Cartesian over `(operator × value-type × group-structure)`: every operator is tested as a single-criterion rule **and** in an `All` group with a second criterion **and** in an `Any` group with a second criterion **and** in a nested `(A OR B) AND C` construction. Default tier's null-handling and `CaseSensitive` coverage carries forward. Reserved for pre-release runs and post-evaluator-refactor verification. |

14. The Default (Full) tier must cover every `(operator, applicable-value-type)` pair, where "applicable" means the operator makes semantic sense for the type:
    - All 12 operators × **Text**, with both `CaseSensitive=true` and `CaseSensitive=false` for each.
    - All 12 operators × **Number**, **LongNumber**, **DateTime**.
    - **Boolean** restricted to `Equals` and `NotEquals` (other operators are not semantically meaningful and should be rejected by the API; see requirement 25).
    - **Guid** restricted to `Equals` and `NotEquals`.
15. The Default tier must include at least:
    - One `All` (AND) group with two criteria from different attributes (e.g. `Department Equals Finance` AND `IsActive Equals true`).
    - One `Any` (OR) group with two criteria (e.g. `Department Equals Finance` OR `Department Equals Sales`).
    - One nested group construction representing `(A OR B) AND C`.
16. The Exhaustive tier must include, for every applicable `(operator, value-type)` pair from requirement 14, **all four** group-structure variations: single-criterion, two-criterion `All`, two-criterion `Any`, and nested `(A OR B) AND C` with the operator placed in position `A`. The second criterion in multi-criteria cells must use a different attribute so that the group's logic is genuinely exercised (not just degenerate `X AND X` / `X OR X`).
17. The Default and Exhaustive tiers must both include explicit null-handling cells: for each operator on each type, at least one cell asserts that MVOs whose target attribute is null are correctly excluded (or, where the operator's documented semantics say otherwise, correctly included). The Quick tier does not include null-handling cells.
18. `-Quick` and `-Exhaustive` are mutually exclusive; supplying both must fail fast with a clear error.
19. The Exhaustive tier's wall-clock target is **contingent on the batched-sync cell-isolation strategy** (requirement 22 option 1). If the implementation plan adopts per-cell-sync (requirement 22 option 3), Exhaustive becomes structurally infeasible; the plan must either (a) restrict Exhaustive to a subset that fits the wall-clock budget, or (b) defer Exhaustive to a follow-up scenario. The plan must not silently ship a tier that exceeds 15 minutes.

#### Round-trip persistence

20. The scenario must include a round-trip persistence sub-test that, for each value-carrier type, configures a Sync Rule with a non-trivial value, fetches the rule back via the API, and asserts the value carrier (`StringValue` / `IntValue` / `LongValue` / `DateTimeValue` / `BoolValue` / `GuidValue`), `ComparisonType`, and `CaseSensitive` flag survived persistence intact.
21. The round-trip sub-test must run before the evaluation matrix; if a value carrier is silently dropped on persistence, the matrix results would be meaningless and we want to know that first. The round-trip sub-test runs in all three tiers.

#### Cell isolation

JIM does **not** currently expose a sync-preview path that evaluates scoping criteria without committing projections. The implementation must therefore use one of the strategies below. The strategy is settled in the implementation plan, not here, because the wall-clock impact depends on measurements taken during the spike.

22. Cells must be isolated from each other without paying the cost of a full `Reset-JIMSystem` per cell. The implementation plan must pick one of the following, with justification grounded in a measured wall-clock spike at Nano against the canonical seed:
    1. **Batched sync, one rule per cell, distinct projected object types** (recommended starting point): create N import Sync Rules in one go, each with its cell's scoping criteria and its own Metaverse Object Type. A single inbound sync run evaluates all rules; per-cell assertions read back per-object-type. Amortises sync-run overhead across cells and is the only strategy that makes the Exhaustive tier (requirement 13) feasible inside its wall-clock budget.
    2. **Single Sync Rule, mutated in place**: keep one sandbox rule, PATCH its scoping criteria between cells, full-sync each cell, expect deprovisioning to clean up the previous cell's projections. Re-exercises the deprovisioning lifecycle that Scenario 10 already covers, so cell assertions are coupled to lifecycle correctness; rejected unless the batched path is shown to be unviable.
    3. **One rule per cell, sync per cell**: simplest to reason about, but pays the sync-run overhead per cell and is the slowest of the three. Last resort only; under this strategy the Exhaustive tier must be restricted or deferred per requirement 19.
23. If none of the three options above can keep the Default tier under its 5-minute Nano wall-clock target, the implementation plan must explicitly raise this in a follow-up issue before adopting an option that exceeds the budget. The implementation plan must not silently relax tier wall-clock targets.
24. The scenario must complete with the JIM instance returned to a known-empty state (no sandbox rules, no leftover MVOs, no orphaned PendingExports, no leftover sandbox Metaverse Object Types), achieved by a single `Reset-JIMSystem -Force` at scenario end. The scenario must not require any manual cleanup to leave the host re-runnable.

#### API behaviour negative-tests

25. Where an operator is not semantically applicable to a value type (per requirement 14 above; e.g. `Contains` on `Boolean`, `GreaterThan` on `Guid`), the matrix must include at least one negative cell that POSTs the combination via the API and asserts the API rejects it cleanly (`400 Bad Request` with a structured error), rather than accepting it and silently misbehaving.
26. Where an operator requires a value carrier that contradicts the attribute's `AttributeDataType` (e.g. supplying `IntValue` on a text comparison), the API must reject the combination. The matrix must include at least one such cell.

#### Reporting

27. The scenario must emit a structured per-cell result that integrates with the existing scenario report shape, so the standard runner picks it up without code changes.
28. The total cell count, pass count, fail count, individual failed-cell names, and the active coverage tier must appear in the scenario summary line printed by `Run-IntegrationTests.ps1`.
29. On any cell failure, the scenario must continue and run the remaining cells before failing the scenario overall, so a single broken operator does not hide the state of the rest of the matrix.

#### Test runner integration (parameters AND interactive menu)

Every configuration knob exposed by the scenario script must be selectable in **both** ways: as a parameter on `Run-IntegrationTests.ps1` (for scripted / CI use) and via the interactive menu (for ad-hoc developer use). The two surfaces must accept the same value sets and produce identical scenario invocations.

30. `Run-IntegrationTests.ps1` must register Scenario 11 in the auto-detected scenario list with a human-readable description in the `switch` block that maps scenario filenames to descriptions (currently at [test/integration/Run-IntegrationTests.ps1:480-490](../../test/integration/Run-IntegrationTests.ps1#L480-L490)). Proposed: "Sync Rule scoping criteria evaluation matrix".
31. The scenario must surface its scenario-specific options as **named parameters** on the scenario script (`-Quick`, `-Exhaustive`, `-OperatorFilter`, `-IncludeNegativeCells`), with `ValidateSet` constraints where the value set is bounded. Parameters must be discoverable via `Get-Help` on the scenario script and from `Run-IntegrationTests.ps1 -?`.
32. `Run-IntegrationTests.ps1` must accept and pass through the scenario-specific parameters introduced in requirement 31. The pass-through must not require changes to other scenarios; the runner must continue to work for any scenario that does not define those parameters.
33. When `Run-IntegrationTests.ps1` is launched **without** the scenario-specific parameters AND the user selects Scenario 11 in the interactive menu, the runner must prompt for the same options the parameters expose, in this order:
    1. **Coverage tier**: `Default (Full)` (default), `Quick`, or `Exhaustive`. Single-select; the three values are mutually exclusive.
    2. **Operator filter**: `All` (default) or a single `SearchComparisonType` value from a `ValidateSet` populated from the enum (so the menu options stay in lockstep with the enum without manual maintenance).
    3. **Include negative cells**: yes (default) or no.
    4. Any further scenario-specific options the implementation introduces.
34. Menu prompts must use the same selection idiom as the existing scenario / template menus (arrow keys, Enter to select, Esc to cancel), not free-form text input. Defaults must be pre-highlighted so an experienced developer can hold Enter through the prompts and get the documented default behaviour.
35. When scenario-specific parameters are supplied explicitly on the command line, the corresponding menu prompts must be **skipped silently** (matching the existing behaviour for `-Template`, `-DirectoryType`, `-LogLevel`, `-DisableChangeTracking`). The `-Step` parameter behaviour is unchanged.
36. The `-Step` parameter must be honoured for **operator-level** cell filtering: `-Step All` (default) runs the full set, `-Step <OperatorName>` runs only cells with that operator, and `-Step <FullyQualifiedCellName>` runs a single cell. The validation set for `-Step` is the union of `All`, the `SearchComparisonType` enum values, and the cell names; invalid values fail fast with a clear error listing the legal values. `-Step` composes with the coverage tier: a tier selects the candidate cell set, then `-Step` filters within it.
37. The pre-run banner printed by `Run-IntegrationTests.ps1` (currently around [Run-IntegrationTests.ps1:1694](../../test/integration/Run-IntegrationTests.ps1#L1694)) must include the resolved values for the scenario-specific options (coverage tier, operator filter, negative cells), so a developer reading the log knows exactly which matrix shape ran without re-reading the prompts they answered.
38. The end-of-run "re-run this scenario" hint that the runner emits (currently around [Run-IntegrationTests.ps1:1612](../../test/integration/Run-IntegrationTests.ps1#L1612)) must include the scenario-specific parameters so copy-paste reproduction works without further interactive input.

### Non-Functional Requirements

- Per-tier wall-clock targets at Nano: Quick < 90 s, Default < 5 min, Exhaustive < 10 min (Exhaustive contingent on requirement 22 option 1 per requirement 19). If the design cannot hit a target with full tier coverage, the implementation plan must propose a credible reduction (e.g. moving from per-cell sync runs to the batched-sync strategy in requirement 22 option 1) before locking the cell shape.
- Cell-level idempotency: re-running the scenario back-to-back at any tier must produce identical pass/fail per cell.
- British English throughout per CLAUDE.md.
- No new NuGet packages, no new PowerShell modules, no new connectors.

## Examples and Scenarios

### Scenario 1: Text NotContains, case-insensitive, All group, single criterion

**Given** seed dataset with MVOs whose `Department` values are `Finance`, `finance`, `FinancePartners`, `CorporateFinance`, `Sales`, `IT`, plus one null
**When** the cell's sandbox rule scopes to `Department NotContains "fin" AND CaseSensitive=false`
**Then** the projected MVO set is exactly `Sales` and `IT` (the null-Department MVO is excluded; the four `fin`-containing MVOs are excluded)

### Scenario 2: DateTime GreaterThanOrEquals, single criterion

**Given** seed dataset with MVOs whose `HireDate` values are `2020-01-01`, `2022-06-15`, `2024-03-01`, `2026-01-01`, plus one null
**When** the cell's sandbox rule scopes to `HireDate GreaterThanOrEquals 2024-03-01`
**Then** the projected MVO set is exactly the `2024-03-01` and `2026-01-01` MVOs; the null-HireDate MVO is excluded.

### Scenario 3: Any (OR) group, two text criteria

**Given** seed dataset with `Department` values `Finance`, `Sales`, `IT`, `HR`
**When** the cell's sandbox rule scopes to `Department Equals "Finance" OR Department Equals "HR"` in an `Any` group
**Then** the projected MVO set is exactly the Finance and HR MVOs.

### Scenario 4: Nested group `(A OR B) AND C`

**Given** the seed dataset
**When** the cell's sandbox rule scopes to `(Department Equals "Finance" OR Department Equals "Sales") AND IsActive Equals true`
**Then** the projected MVO set is the Finance + Sales MVOs whose `IsActive` is `true` (excludes inactive Finance/Sales MVOs and excludes all IT/HR MVOs regardless of `IsActive`)

### Scenario 5: Round-trip persistence of a DateTime criterion

**Given** the API accepts a rule with `ComparisonType=LessThan`, `DateTimeValue=2025-01-01T00:00:00Z`
**When** the test PATCHes the rule and immediately GETs it back
**Then** the returned rule has identical `ComparisonType`, identical `DateTimeValue` (no time-zone drift, no precision loss), and identical `CaseSensitive` flag.

### Scenario 6: Negative cell, semantically invalid combination

**Given** the API receives a request to scope by `Boolean Contains true`
**When** the request is POSTed
**Then** the API returns `400 Bad Request` with a structured error indicating `Contains` is not applicable to `Boolean`. The scenario records this as a passed negative cell.

### Scenario 7: Interactive menu drives the same configuration as parameters (Exhaustive run)

**Given** a developer runs `./Run-IntegrationTests.ps1` with no parameters and arrow-keys down to Scenario 11
**When** they hit Enter, then accept the default Template and DirectoryType, then arrow down to `Exhaustive` at the coverage-tier prompt, then accept the default `All` at the operator-filter prompt, then accept the default `Yes` at the include-negative-cells prompt
**Then** the resulting scenario invocation is identical to `./Run-IntegrationTests.ps1 -Scenario Scenario11-ScopingCriteriaMatrix -Exhaustive` would have produced. The pre-run banner shows `Coverage tier: Exhaustive`, `Operator filter: All`, `Include negative cells: Yes`. The end-of-run re-run hint prints the exact parameterised command.

### Scenario 8: Parameters skip menu prompts

**Given** a developer runs `./Run-IntegrationTests.ps1 -Scenario Scenario11-ScopingCriteriaMatrix -Quick`
**When** the runner detects `-Quick` was provided
**Then** the coverage-tier prompt is skipped (no flicker, no "press Enter to accept default" line) and the runner proceeds straight to the next unspecified prompt (operator filter), exactly mirroring how `-Template Nano` already skips the template menu.

### Scenario 9: Mutually exclusive tier selectors

**Given** a developer runs `./Run-IntegrationTests.ps1 -Scenario Scenario11-ScopingCriteriaMatrix -Quick -Exhaustive`
**When** the runner parses parameters
**Then** the runner fails fast with a clear error stating that `-Quick` and `-Exhaustive` are mutually exclusive, before any environment setup begins.

## Constraints

- Must remain cross-platform PowerShell. No bash scripts.
- Must work in air-gapped environments. No new cloud dependencies.
- Must not modify the scoping criteria model or the public API contract; this is purely a test scenario.
- Must use the existing `Run-IntegrationTests.ps1` runner. No new top-level entry points.
- Must follow the existing scenario template at [test/integration/scenarios/](../../test/integration/scenarios/).
- British English throughout; no em dashes.

## Affected Areas

| Area | Impact |
|------|--------|
| Integration tests | New `test/integration/scenarios/Invoke-Scenario11-ScopingCriteriaMatrix.ps1`; new declarative manifest at `test/integration/scenarios/data/scoping-criteria-matrix.*`; new scenario-local data helper for the deterministic seed; new helpers under `test/integration/utils/` for manifest validation and matrix-cell execution if reusable |
| Integration runner | `Run-IntegrationTests.ps1`: register Scenario 11 in the auto-detected list with a description in the filename-to-description `switch`; add named parameters for the scenario-specific options (`-Quick`, `-Exhaustive`, `-OperatorFilter`, `-IncludeNegativeCells`) with pass-through to the scenario script; add interactive menu prompts for coverage tier and the other options that mirror the existing template / directory-type menu idiom; extend the pre-run banner and end-of-run re-run hint to include the resolved scenario-specific values |
| Documentation | `engineering/INTEGRATION_TESTING.md`: add Scenario 11 to the Available Scenarios table, Quick Start command list (including separate examples for Default, `-Quick`, and `-Exhaustive` invocations), step example, detail section, and Phase 1 status table; renumber the existing Phase 2 placeholders (Multi-Source Aggregation, Database Source/Target, Performance Baselines) from 11 / 12 / 13 to 12 / 13 / 14 in all locations they appear |
| Application / API | None expected; if the matrix exposes a real bug in `SyncRuleScopingEvaluator`, validation, or persistence, that gets its own follow-up |
| Database | None |

## Dependencies

- Scenario 10 already merged. This PRD assumes Scenario 10 covers the lifecycle behaviour and that this scenario can focus purely on evaluation correctness.
- `Reset-JIMSystem` cmdlet (delivered in `feature/scenario-sync-rule-scoping` branch). The matrix relies on it for the single end-of-scenario cleanup.

## Resolved Decisions

These were open during PRD drafting and have been settled in conversation. They are listed here for traceability; the corresponding functional requirements above are written as firm requirements, not contingent on these decisions.

1. **Sync-preview path is unavailable.** JIM does not currently expose a way to evaluate scoping criteria without committing projections. The implementation plan picks from the three strategies in requirement 22 (preferred: batched sync with one rule per cell and distinct projected object types; required if Exhaustive is in scope).
2. **Matrix tabulation: declarative manifest.** Cells are defined in a checked-in manifest file under `test/integration/scenarios/data/`, not inline in the scenario script. See requirements 7 and 8.
3. **`-Step` granularity: operator-level.** `-Step <OperatorName>` runs every cell that uses that operator; `-Step <FullyQualifiedCellName>` runs a single cell; `-Step All` (default) runs the lot. `-Step` composes with the coverage tier (tier selects the candidate set, `-Step` filters within it). See requirement 36.
4. **Three coverage tiers.** The scenario offers Quick, Default (Full), and Exhaustive tiers with wall-clock targets of < 90 s, < 5 min, and < 10 min respectively at Nano (Exhaustive contingent on the batched-sync isolation strategy). Each tier is a strict superset of the one below. See requirement 13 for the tier matrix and requirement 31 for the parameter surface.
5. **Scenario numbering: insert at 11 and renumber.** The scoping evaluation matrix occupies Scenario 11, adjacent to its lifecycle complement Scenario 10. The existing Phase 2 placeholders move down by one: Multi-Source Aggregation becomes Scenario 12, Database Source/Target becomes Scenario 13, Performance Baselines becomes Scenario 14. The renumber is part of this scenario's `engineering/INTEGRATION_TESTING.md` documentation update (Affected Areas table).

## Acceptance Criteria

- [ ] `test/integration/scenarios/Invoke-Scenario11-ScopingCriteriaMatrix.ps1` exists and is invoked via `./test/integration/Run-IntegrationTests.ps1 -Scenario Scenario11-ScopingCriteriaMatrix`.
- [ ] Three coverage tiers (Quick, Default, Exhaustive) are implemented per requirement 13; `-Quick` and `-Exhaustive` are mutually exclusive and the runner fails fast when both are supplied.
- [ ] The Default tier covers every applicable `(operator, value-type)` pair (per requirement 14), `CaseSensitive` true/false for text, at least one `All` group, at least one `Any` group, at least one nested group, and at least one null-handling cell per operator/type.
- [ ] The Exhaustive tier additionally covers, for every applicable `(operator, value-type)` pair, all four group-structure variations (single, two-criterion `All`, two-criterion `Any`, nested `(A OR B) AND C`).
- [ ] Cell definitions live in a checked-in declarative manifest under `test/integration/scenarios/data/`; the scenario validates the manifest at load time and fails fast on malformed cells, including unknown tier values.
- [ ] The scenario includes a round-trip persistence sub-test for every value carrier type, executed before the evaluation matrix in all three tiers.
- [ ] The scenario includes negative-cell API checks for at least three semantically-invalid combinations (requirements 25 / 26).
- [ ] Each cell appears as a named pass/fail in the scenario report; the summary line shows total / pass / fail counts and the active tier.
- [ ] Cell failures do not halt the scenario; the matrix completes regardless and the scenario fails overall only if any cell failed.
- [ ] Scenario wall-clock at Nano: Quick under 90 s, Default under 5 min, Exhaustive under 10 min (on the standard devcontainer host, assuming batched-sync cell isolation).
- [ ] Scenario tears down cleanly with no orphaned sandbox rules, MVOs, PendingExports, or sandbox Metaverse Object Types; back-to-back runs at any tier produce identical results.
- [ ] `engineering/INTEGRATION_TESTING.md` is updated with Scenario 11 in all the places where the existing scenarios are listed (Available Scenarios table, Quick Start commands including separate Default / `-Quick` / `-Exhaustive` examples, step example, detail section, Phase 1 status table), and the existing Phase 2 placeholders 11 / 12 / 13 are renumbered to 12 / 13 / 14 everywhere they appear.
- [ ] `Run-IntegrationTests.ps1` registers Scenario 11 in the auto-detected scenario list with a human-readable description, exposes the scenario-specific options as named parameters that pass through to the scenario script, prompts for the same options (coverage tier, operator filter, negative cells) in the interactive menu when those parameters are not supplied, skips the prompts silently when they are, and prints the resolved values in both the pre-run banner and the end-of-run re-run hint.
- [ ] Every scenario-specific option can be set in both ways (parameter and menu) and the two paths produce identical scenario invocations for the same selections.
- [ ] No production code changes ship as part of this scenario; any bugs the matrix uncovers are filed as separate issues.

## Additional Context

- Scoping criteria model: [src/JIM.Models/Logic/SyncRuleScopingCriteria.cs](../../src/JIM.Models/Logic/SyncRuleScopingCriteria.cs)
- Operator enum: [src/JIM.Models/Search/SearchEnums.cs](../../src/JIM.Models/Search/SearchEnums.cs)
- Attribute data type enum: [src/JIM.Models/Core/CoreEnums.cs](../../src/JIM.Models/Core/CoreEnums.cs)
- Scenario 10 (the lifecycle complement): [test/integration/scenarios/Invoke-Scenario10-SyncRuleScoping.ps1](../../test/integration/scenarios/Invoke-Scenario10-SyncRuleScoping.ps1)
- Scoping evaluator: [engineering/SYNC_RULE_SCOPING.md](../SYNC_RULE_SCOPING.md)
- Reset cmdlet (cell-isolation backstop): `Reset-JIMSystem -Force` (delivered on `feature/scenario-sync-rule-scoping`)
- Phase 2 placement: existing placeholders renumbered to 12 / 13 / 14 in [engineering/INTEGRATION_TESTING.md](../INTEGRATION_TESTING.md) as part of this scenario's documentation deliverable.
