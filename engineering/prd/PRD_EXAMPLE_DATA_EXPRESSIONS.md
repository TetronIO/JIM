# Expression-Based Attribute Generation in Example Data Templates

- **Status:** Planned
- **Created:** 2026-06-23
- **Author:** JayVDZ (premise reviewed and PRD drafted via Claude Code)
- **Issue:** [#549](https://github.com/TetronIO/JIM/issues/549)

## Problem Statement

The example data generation engine lets template authors construct a string attribute from a **pattern**: literal text plus `{Attribute Name}` placeholders that interpolate other generated attributes, plus `[UniqueInt]` for uniqueness. The bundled User template already uses this to build email addresses: `{First Name}.{Last Name}[UniqueInt]@panoply.local`.

What patterns **cannot** do is transform the values they interpolate. There is no way to lower-case a referenced value, strip its spaces, take a substring, or otherwise reshape it. The practical consequence: an email domain that should be derived from the assigned `Company` attribute (lower-case it, remove spaces, append a TLD) has to be hard-coded into the pattern instead. Anyone wanting realistic, internally-consistent demo data, where the email domain matches the company, the UPN follows a derived convention, or a sAMAccountName is a truncated lower-cased name, hits a wall.

This is the same class of need that Synchronisation Rule Attribute Flows already solve with the DynamicExpresso expression engine (`mv["..."]` accessors plus a string-function library). Example data generation is the one value-construction surface in JIM that has not yet been given that engine.

> **Premise note.** Issue #549 originally framed the gap as "cannot derive one attribute's value from another". That is inaccurate: pattern interpolation already references sibling attributes. The accurate gap is **transformation**, and this PRD is scoped accordingly. The issue body has been corrected.

## Goals

- A template author can populate a text attribute from an **expression** that reads and transforms other already-generated attributes on the same object, using the same `mv["Attribute Name"]` syntax and function library as Synchronisation Rule Attribute Flows.
- The bundled User template can generate a company-derived email domain without hard-coding it; verifiable by generating data and confirming the email domain matches a transformed `Company` value.
- Expressions that reference other attributes are evaluated **after** their dependencies, with a clear, deterministic ordering; verifiable by a multi-attribute chain (e.g. email derived from first/last/company) producing correct output every run.
- Invalid expressions are caught at template-design time (not silently at generation time); verifiable via `IExpressionEvaluator.Validate()` surfacing an error before a template can be saved or executed.
- Value uniqueness works for expression-produced values, not just patterns; verifiable by generating a population large enough to force a collision and confirming uniqueness is preserved.
- Existing `Pattern`-based templates continue to behave identically; verifiable by the current bundled templates generating byte-for-byte equivalent data.
- The capability is identical across UI, REST API, and PowerShell because all three configure the same canonical expression string; verifiable by an expression authored in the UI builder being readable/writable byte-for-byte via the API and PowerShell. A guided builder lets non-expert administrators assemble common recipes without hand-writing expressions, while emitting a plain expression underneath.

## Non-Goals

- **No new expression engine or functions.** This reuses `IExpressionEvaluator`/`DynamicExpressoEvaluator` as-is. Adding new functions is out of scope unless a gap is found during implementation.
- **No Connected System Object (`cs[...]`) access.** During example data generation there is no CSO; only `mv[...]` (the object being built) is meaningful. The `cs` accessor will be present but empty.
- **No expression support for non-text attributes** in v1 (numbers, dates, booleans, references). Text attributes are where the demand is.
- **No change to the manager-assignment, reference-assignment, or numeric/date generators.**
- **Not a general template-authoring UI overhaul.** Whether a template-attribute editor is built here is an explicit open decision (see Open Questions); this PRD does not assume the broader template CRUD surface beyond what expression authoring requires.

## User Stories

1. As an administrator setting up a demo or proof-of-concept, I want generated email addresses whose domain is derived from each identity's company, so that the demo data looks internally consistent and believable.
2. As an administrator, I want to write a transform once using the same expression language I already use for Synchronisation Rules, so that I do not have to learn a second value-construction syntax.
3. As an administrator, I want an invalid expression to be rejected when I author it, with a clear error, so that I do not discover the mistake only after a long generation run.
4. As an administrator generating a large population, I want expression-derived values that must be unique (such as email or UPN) to stay unique, so that downstream uniqueness constraints are not violated.
5. As an administrator who is not comfortable writing expressions by hand, I want a guided UI that assembles common value-generation recipes for me (full name, company-derived email, initial-plus-surname, truncation), so that I can configure realistic generation without learning the expression syntax, while the result is still a plain expression that an API or PowerShell user could have written directly.

## Requirements

### Functional Requirements

1. `ExampleDataTemplateAttribute` gains an optional `Expression` string property, applicable to text (`AttributeDataType.Text`) attributes.
2. `Expression` and `Pattern` are mutually exclusive on a single attribute. `Validate()` rejects an attribute that sets both, and rejects `Expression` on a non-text attribute, with a specific `ExampleDataTemplateAttributeException` message.
3. When `Expression` is set, the generator evaluates it through `IExpressionEvaluator`, supplying an `ExpressionContext` whose `mv` accessor exposes the object's already-generated attribute values keyed by Metaverse Attribute name. The evaluated result (coerced to string) becomes the attribute value.
4. The generator orders attribute evaluation so that every attribute an expression references is generated before the expression runs. Ordering is a topological sort over the reference graph derived from each expression's `mv["..."]` references, with cycle detection reported as a validation error.
5. Expression evaluation errors at generation time are reported through the standard Activity/RPEI error surface (never silent), consistent with JIM's synchronisation-integrity rules. A single attribute's expression failure must fail the template execution with a clear, attributed error rather than producing a partial/blank value silently.
6. Value uniqueness is available to expression-produced values. Uniqueness becomes a first-class per-attribute option that applies to both `Pattern` and `Expression` outputs (see Open Question 2 for the token-vs-flag decision), preserving the current `[UniqueInt]` behaviour for existing patterns.
7. Expression validation is available at design time via the existing `IExpressionEvaluator.Validate()` and surfaced wherever a template attribute is authored or imported.
8. The bundled User template's email attribute is updated to demonstrate a company-derived domain via an expression (replacing the hard-coded `@panoply.local`), provided uniqueness is preserved.
9. The expression string is the **canonical, persisted representation** of expression-based generation. Any UI builder (see Authoring UX below) is sugar that compiles **to** an expression string; it never persists a separate structured representation. This guarantees the UI, REST API, and PowerShell all configure the identical capability through the same field, with the API/PowerShell surfaces simply supplying the string directly.

### Non-Functional Requirements

- Generation throughput must not regress materially for templates that use only patterns/data sets. Expression evaluation uses the shared compiled-expression cache, so per-object cost is an interpreter invocation, not a recompile.
- Expression evaluation must be thread-safe under the existing `Parallel.For` per-object generation. `DynamicExpressoEvaluator` is stateless apart from its concurrent static cache; the per-object `ExpressionContext` is not shared across threads.
- Must work fully air-gapped; no new external dependencies.
- British English throughout; Metaverse Object / Metaverse Attribute / Synchronisation Rule proper-noun casing in all UI text and docs.

## Examples and Scenarios

### Scenario 1: Company-derived email domain

**Given** a User template with `First Name`, `Last Name`, and `Company` attributes generated from data sets, and an `Email` attribute whose expression is:
```
Lower(mv["First Name"]) + "." + Lower(mv["Last Name"]) + "@" + Lower(Replace(mv["Company"], " ", "")) + ".io"
```
**When** the template is executed
**Then** an identity with First Name "Ada", Last Name "Lovelace", Company "Stark Industries" gets email `ada.lovelace@starkindustries.io`, and the domain tracks whatever company each identity is assigned.

### Scenario 2: Dependency ordering is automatic

**Given** the `Email` expression above references `First Name`, `Last Name`, and `Company`, declared in any order in the template
**When** the template is executed
**Then** the three referenced attributes are always generated before `Email` is evaluated, regardless of declaration order, and no "AttributeValue not found" error occurs.

### Scenario 3: Invalid expression rejected at design time

**Given** an author enters `Lower(mv["First Name"]` (missing closing parenthesis)
**When** the attribute is validated (on save, or before template execution)
**Then** validation fails with the DynamicExpresso parse error and position, and the template cannot be executed with the invalid expression.

### Scenario 4: Uniqueness preserved for expression output

**Given** an `Email` expression that could collide (e.g. two "John Smith" identities at the same company) and the attribute is marked unique
**When** a population large enough to force a collision is generated
**Then** the colliding values are disambiguated (e.g. an integer suffix) exactly as `[UniqueInt]` does for patterns today, and no duplicate email is produced.

### Scenario 5: Existing pattern templates unchanged

**Given** the bundled templates that use only `Pattern`/data sets
**When** they are executed after this change
**Then** they generate equivalent data to before; no behavioural change for pattern-only attributes.

## Authoring UX: a structured builder over canonical expressions

The slickest UX for non-expert administrators is a guided builder that assembles common generation recipes, the same instinct behind the per-mapping inbound value-processing controls (#843). But this PRD deliberately takes the **inverse architecture** to that feature, because the two problems have different shapes.

**Inbound value processing chose structured-as-canonical.** It persists typed enum columns (`InboundValueProcessing` flags + `InboundCaseNormalisation`) and does not lower to expressions. That works because its transform family is tiny and fixed (trim, collapse, case). The cost it accepted is mirroring every option by hand across three surfaces: checkboxes in the editor, fields on the DTO, and explicit PowerShell parameters (`-TrimWhitespace`, `-CollapseInternalWhitespace`, `-CaseNormalisation`, …).

**Value generation should choose expression-as-canonical, with the builder as UI-only sugar.** The reasons are exactly why the inbound choice does not transfer:

- Generation transforms are **open-ended and compositional** ("first.last@company-domain", "first-initial + surname", "truncate to 8 chars", "prefix + sequential number"), not a fixed family. Enumerating them as enum columns is a treadmill, and each new recipe would mean another round of UI + DTO + PowerShell plumbing.
- A canonical **expression string gives API and PowerShell parity for free**: automation users send the string and get the full capability immediately; only the UI needs the builder. This is precisely the "same functionality, just more verbose" outcome requested, without the per-option mirroring tax.

### Builder design (forward-only, raw expression is the source of truth)

The one genuine risk is round-tripping: reconstructing builder state from an arbitrary expression is not reliable (a hand-written API expression has no builder representation). Persisting both a structured "recipe" and the compiled expression would let them drift. The PRD therefore mandates **a single persisted form (the expression)** and a builder that is *assisted authoring*, not a bidirectional editor:

- The builder is a **palette of insertable fragments** (e.g. "lower-case an attribute" inserts `Lower(mv["…"])`, "strip spaces" wraps in `Replace(…, " ", "")`, "first initial" inserts `Left(mv["…"], 1)`) plus a few **one-click full recipes** (the company-derived email being the headline one).
- A **raw expression field is always present and is the source of truth.** The builder writes into it; editing it by hand is always allowed.
- A **live preview** uses the existing `IExpressionEvaluator.Test()` against sample attribute values, so the author sees a concrete result while building.
- On re-open, the raw expression is shown; the builder offers to regenerate/replace rather than claiming to reverse-parse arbitrary text.

This is net-new UX (Synchronisation Rule flows today expose only a raw expression textbox), so the builder built here is a candidate to retrofit onto sync-rule attribute flows later.

## Constraints

- Must reuse `IExpressionEvaluator`; no second expression dialect.
- Backward compatible with existing seeded templates and any persisted template data (the new column is nullable).
- EF Core migration is append-only; never edit or squash existing migrations.
- `ExampleDataServer` is hand-constructed by `JimApplication` (not DI-resolved), so the evaluator must be threaded through its constructor, mirroring how `SyncEngine` receives an optional `IExpressionEvaluator`.

## Affected Areas

| Area | Impact |
|------|--------|
| Database | New nullable `Expression` column on `ExampleDataTemplateAttribute`; possibly a `UniqueValue` flag if uniqueness is decoupled from the `[UniqueInt]` token. EF migration. |
| Models | `ExampleDataTemplateAttribute.Expression` property; updated `Validate()` (mutual exclusivity, non-text rejection, cycle detection input). |
| Application | `ExampleDataServer`: accept `IExpressionEvaluator`; evaluate expressions in `GenerateMetaverseStringValue()`; replace the weak `OrderBy(AttributeDependency)` with a topological sort over expression references; route uniqueness through a shared mechanism. `JimApplication` constructor wiring. `SeedingServer` bundled User template update. |
| API | Return the new `Expression` (and uniqueness) field on GET. If create/update is in scope: template-attribute write endpoints, plus a "test expression" endpoint (a sync-rule equivalent already exists and can be mirrored for the builder's live preview). The `Expression` is carried as a plain string, so no per-transform DTO fields are needed. |
| UI | Builder phase: template-attribute authoring offering Pattern vs Expression, with the structured **builder** (fragment palette + one-click recipes + live preview) writing into a raw expression field that is the source of truth (see Authoring UX). Reuse expression display/highlight patterns from `SyncRuleAttributeFlowTab.razor`. Engine-only phase: `ExampleDataTemplateDetail.razor` read-only display gains an "Expression" chip alongside the existing "Pattern" chip. |
| PowerShell | Template-attribute cmdlets (if/when create/update is exposed) take `Expression` as a string parameter; no per-transform switches needed, unlike the inbound value-processing cmdlets. |
| Tests | `JIM.Worker.Tests` (or the relevant example-data test home): expression evaluation, ordering/topological sort, cycle detection, uniqueness, validation, and a regression test that pattern-only templates are unchanged. TDD: red first. |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/configuration/` (example data / data generation page, or a new one) | Document the `Expression` option for template attributes, the `mv["..."]` accessor in the generation context, and the uniqueness option. |
| `docs/concepts/expressions.md` | Note that the expression language now also applies to example data generation (currently it describes Synchronisation Rule usage). |
| `docs/powershell/example-data.md` | If editor/API endpoints are added, document any new cmdlets/parameters. |
| `CHANGELOG.md` | `✨` entry under `[Unreleased]` for expression-based example data generation. |

## Dependencies

- None blocking. Builds entirely on the existing `IExpressionEvaluator` infrastructure.
- The editor decision (Open Question 1) may split this into two sequenced deliverables.

## Open Questions

1. **Delivery sequencing (decision required before implementation).** There is no template-attribute editing surface today; templates are seeded in code. The expression engine and the builder are separable. Options:
   - **(a) Engine-only first**: expression support is wired end-to-end and exercised by the bundled template + tests, configurable via seeding/code (and, if the create/update API is added, via REST/PowerShell as a raw string); the builder UI is sequenced as a fast follow. Lands the bundled-demo benefit and full automation parity quickly.
   - **(b) Engine + builder together**: also ship the guided builder described in Authoring UX. Largest scope, but non-expert administrators can author generation on delivery.
   Recommendation: **(a) then (b)**. The engine + canonical expression string is the load-bearing part and unlocks API/PowerShell immediately; the builder is pure UI sugar on top and is lower-risk to add once the canonical form is settled. This keeps each PR coherent and avoids a half-built editor. Note that (a) still requires a decision on whether template-attribute create/update is exposed via API/UI at all, or whether v1 is seeding-only.

2. **Uniqueness mechanism.** Keep the `[UniqueInt]` token (and make expressions emit it, which composes awkwardly) versus promote uniqueness to a first-class per-attribute flag that post-processes both Pattern and Expression output. Recommendation: the **flag** - it is cleaner, works for both mechanisms, and removes a token-parsing edge case. The existing `[UniqueInt]` token stays supported for back-compat on patterns.

3. **Builder scope (which recipes ship first).** The builder is only "slicker" if it nails the genuinely common generation cases. Proposed v1 vocabulary: concatenate attributes with literals; lower/upper/title-case; strip/replace spaces; first-initial (`Left(…, 1)`); truncate (`Left`/`Substring`); append a literal domain; derive a domain from an attribute. Plus one-click recipes: full display name, and company-derived email. Open: is this set right, and should the builder support conditional output (`IIF`) in v1 or defer it to raw-expression editing?

4. **Relationship to Pattern (considered, not blocking).** A lighter single-model alternative is to drop the separate `Expression` field and instead allow inline functions inside pattern braces, e.g. `{Lower(Replace(Company, " ", ""))}.io`. Recommendation: **keep Pattern and Expression as two plain-string fields** - Pattern stays the readable choice for pure interpolation, Expression is the power tier and the builder's compile target, and both are strings so API/PowerShell parity holds either way. The inline-functions-in-braces model is recorded as considered-and-declined (it needs a brace mini-parser and diverges from the `mv["..."]` syntax used in Synchronisation Rules) unless review prefers a single syntax.

## Acceptance Criteria

- [ ] `ExampleDataTemplateAttribute` has a nullable `Expression` property with an EF migration; existing data and seeded templates load unchanged.
- [ ] `Validate()` rejects `Expression` + `Pattern` together, rejects `Expression` on non-text attributes, and reports expression cycles, each with a specific message.
- [ ] Setting `Expression` causes the generator to evaluate it via `IExpressionEvaluator` with an `mv`-populated context, producing the attribute value.
- [ ] Attributes are evaluated in dependency order via a topological sort over expression references; declaration order does not affect output (Scenario 2).
- [ ] Generation-time expression failures fail the execution with an attributed error via the Activity/RPEI surface; no silent blank values.
- [ ] Expression-produced values can be made unique, preserving `[UniqueInt]` behaviour for existing patterns (Scenario 4).
- [ ] The bundled User template generates a company-derived email domain (Scenario 1) with uniqueness preserved.
- [ ] Pattern-only templates generate equivalent data to before (Scenario 5).
- [ ] The expression is persisted/transmitted as a single canonical string; an expression authored via the UI builder round-trips byte-for-byte through the API/PowerShell, and no separate structured representation is stored.
- [ ] (Builder phase) Non-expert administrators can assemble the company-derived email recipe via the guided builder with a live preview, without typing expression syntax.
- [ ] Public docs under `docs/` describe the new capability; `CHANGELOG.md` has a `✨` entry.
- [ ] `dotnet build JIM.sln` and `dotnet test JIM.sln` pass with zero errors and warnings; new behaviour is covered by tests written red-first.

## Additional Context

- Engine: `src/JIM.Application/Expressions/DynamicExpressoEvaluator.cs`, interface `src/JIM.Models/Interfaces/IExpressionEvaluator.cs`, context `src/JIM.Models/Expressions/ExpressionContext.cs` + `AttributeAccessor.cs`.
- Current generation logic: `src/JIM.Application/Servers/ExampleDataServer.cs` (`GenerateMetaverseStringValue`, `ReplaceAttributeVariables`, `ReplaceSystemVariables`).
- Bundled template construction: `src/JIM.Application/Servers/SeedingServer.cs` (email at the `{First Name}.{Last Name}[UniqueInt]@panoply.local` line).
- Read-only template view: `src/JIM.Web/Pages/Admin/ExampleDataTemplateDetail.razor`; API surface: `src/JIM.Web/Controllers/Api/ExampleDataController.cs` (list/get/execute only).
- Sync-rule expression UI to reuse: `src/JIM.Web/Pages/Admin/Components/SyncRuleAttributeFlowTab.razor` (raw expression textbox; no builder exists yet).
- Precedent for structured transform UX (and the architecture this PRD deliberately inverts): per-mapping inbound value processing, `engineering/plans/doing/INBOUND_VALUE_PROCESSING.md` (#843). That feature persists structured enum columns and mirrors each option across UI/API/PowerShell; this PRD instead makes the expression string canonical and treats the builder as UI-only sugar, because generation transforms are open-ended rather than a fixed family.
- Administrator-facing expression docs: `docs/concepts/expressions.md`.
