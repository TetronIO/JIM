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
| API | If an editor is in scope: create/update endpoints for templates/attributes plus a "test expression" endpoint (a sync-rule equivalent already exists and can be mirrored). If deferred: no API change beyond returning the new field on GET. |
| UI | If an editor is in scope: template-attribute authoring with a Pattern/Expression choice and a test/preview affordance (reuse the Synchronisation Rule expression-editor patterns in `SyncRuleAttributeFlowTab.razor`). If deferred: `ExampleDataTemplateDetail.razor` read-only display gains an "Expression" chip alongside the existing "Pattern" chip. |
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

1. **Editor scope (decision required before implementation).** There is no template-attribute editing surface today; templates are seeded in code. Options:
   - **(a) Build a minimal editor** as part of this work (UI + API + application/repository CRUD for template attributes, with a Pattern/Expression choice and a test/preview affordance reused from the Synchronisation Rule editor). Largest scope, but expression support is actually usable by administrators on delivery.
   - **(b) Ship engine-only in v1**: expression support is wired end-to-end and exercised by the bundled template + tests, configurable via seeding/code only; the editor is sequenced as a follow-up issue. Smaller, lands value for the bundled demo data immediately, but administrators cannot author their own expressions yet.
   Recommendation: **(b) first, then (a)** as a fast follow, unless authoring by administrators is the actual near-term driver, in which case do **(a)**. This keeps each PR coherent and avoids a half-built editor.

2. **Uniqueness mechanism.** Keep the `[UniqueInt]` token (and make expressions emit it, which composes awkwardly) versus promote uniqueness to a first-class per-attribute flag that post-processes both Pattern and Expression output. Recommendation: the **flag** - it is cleaner, works for both mechanisms, and removes a token-parsing edge case. The existing `[UniqueInt]` token stays supported for back-compat on patterns.

3. **Value-construction model (alternative design).** Rather than a separate Expression *mode*, allow inline function calls inside pattern braces, e.g. `{Lower(Replace(Company, " ", ""))}.io`. Single mental model, composes naturally with `[UniqueInt]`, nicer for the simple case. Downside: needs a mini-parser inside braces (or DynamicExpresso invoked per-brace), and it diverges from the `mv["..."]` syntax administrators already know from Synchronisation Rules. Recommendation: **prefer the separate `Expression` field** for cross-product consistency; record this alternative as considered-and-declined unless review prefers the single-model approach.

## Acceptance Criteria

- [ ] `ExampleDataTemplateAttribute` has a nullable `Expression` property with an EF migration; existing data and seeded templates load unchanged.
- [ ] `Validate()` rejects `Expression` + `Pattern` together, rejects `Expression` on non-text attributes, and reports expression cycles, each with a specific message.
- [ ] Setting `Expression` causes the generator to evaluate it via `IExpressionEvaluator` with an `mv`-populated context, producing the attribute value.
- [ ] Attributes are evaluated in dependency order via a topological sort over expression references; declaration order does not affect output (Scenario 2).
- [ ] Generation-time expression failures fail the execution with an attributed error via the Activity/RPEI surface; no silent blank values.
- [ ] Expression-produced values can be made unique, preserving `[UniqueInt]` behaviour for existing patterns (Scenario 4).
- [ ] The bundled User template generates a company-derived email domain (Scenario 1) with uniqueness preserved.
- [ ] Pattern-only templates generate equivalent data to before (Scenario 5).
- [ ] Public docs under `docs/` describe the new capability; `CHANGELOG.md` has a `✨` entry.
- [ ] `dotnet build JIM.sln` and `dotnet test JIM.sln` pass with zero errors and warnings; new behaviour is covered by tests written red-first.

## Additional Context

- Engine: `src/JIM.Application/Expressions/DynamicExpressoEvaluator.cs`, interface `src/JIM.Models/Interfaces/IExpressionEvaluator.cs`, context `src/JIM.Models/Expressions/ExpressionContext.cs` + `AttributeAccessor.cs`.
- Current generation logic: `src/JIM.Application/Servers/ExampleDataServer.cs` (`GenerateMetaverseStringValue`, `ReplaceAttributeVariables`, `ReplaceSystemVariables`).
- Bundled template construction: `src/JIM.Application/Servers/SeedingServer.cs` (email at the `{First Name}.{Last Name}[UniqueInt]@panoply.local` line).
- Read-only template view: `src/JIM.Web/Pages/Admin/ExampleDataTemplateDetail.razor`; API surface: `src/JIM.Web/Controllers/Api/ExampleDataController.cs` (list/get/execute only).
- Sync-rule expression UI to reuse: `src/JIM.Web/Pages/Admin/Components/SyncRuleAttributeFlowTab.razor`.
- Administrator-facing expression docs: `docs/concepts/expressions.md`.
