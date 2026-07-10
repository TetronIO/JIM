# Source Code Reference

> Detailed coding conventions, architecture rules, and development tasks for `src/`. See root `CLAUDE.md` for behavioural rules and guardrails.

## Solution Quality Principles (READ FIRST)

When proposing changes, always aim for the best option across all three of these axes simultaneously, not a compromise on any of them:

1. **Best user experience.** What does the person using JIM actually feel when this ships? Pick the option that removes friction, surfaces the right information, and "just works".
2. **Best architecture.** What does a staff engineer reviewing this in six months want to inherit? Pick the option that respects layers, models, and existing patterns rather than the option that's quicker to land.
3. **Best performance.** What does this look like at customer scale (10K, 100K, 1M objects), not just on the developer's laptop? Pick the option that uses the right tool (raw SQL / COPY for bulk worker paths, EF Core for UI reads) rather than the option that's familiar.

**Do not propose half-measures.** Offering a smaller intermediate fix because the proper fix "feels too big" is a false economy: it ships latent issues, causes emotional stress when those issues surface, and almost always takes more total time once you factor in the rework, re-testing, and re-explanation. If the right answer is the bigger change, propose the bigger change. Surface the cost honestly, but recommend the option that's correct on all three axes, not the one that's smallest. The user explicitly prefers the well-reasoned bigger fix over a chain of smaller pivots.

This rule overrides any instinct to "minimise change" or "stay tightly scoped" when the smaller scope would be wrong on UX, architecture, or performance.

## Key Project Locations

**Where to add:**
- API endpoints: `JIM.Web/Controllers/Api/`
- API models/DTOs: `JIM.Web/Models/Api/`
- API extensions: `JIM.Web/Extensions/Api/`
- API middleware: `JIM.Web/Middleware/Api/`
- UI pages: `JIM.Web/Pages/`
- Blazor components: `JIM.Web/Shared/`
- Business logic: `JIM.Application/Servers/`
- Performance diagnostics: `JIM.Application/Diagnostics/`
- Domain models: `JIM.Models/` (see subdirectories: `Core/`, `Staging/`, `Transactional/`, `Utility/`)
- Database repositories: `JIM.PostgresData/`
- Connectors: `JIM.Connectors/` or new connector project
- Tests: `../test/JIM.Web.Api.Tests/`, `../test/JIM.Models.Tests/`, `../test/JIM.Worker.Tests/`

## Code Style & Conventions

**IMPORTANT Rules:**
- YOU MUST use async/await for all I/O operations (method suffix: `Async`)
- YOU MUST use constructor injection for all dependencies
- YOU MUST test method signature: `[Test] public async Task TestNameAsync()`
- **CRITICAL: Use British English (en-GB) for ALL text:**
  - Code: "authorisation" not "authorization", "synchronisation" not "synchronization", "colour" not "color"
  - Comments: "behaviour" not "behavior", "centre" not "center", "licence" not "license" (noun)
  - Documentation: "organise" not "organize", "analyse" not "analyze", "programme" not "program" (unless referring to computer programs)
  - UI text: "minimise" not "minimize", "optimise" not "optimize", "cancelled" not "canceled"
  - Units: Metric only (metres, litres, kilograms, kilometres) - never use imperial units
  - Date/Time: Always use UTC for storage and internal operations; display in user's local time zone where appropriate
  - Exceptions: Technical terms, proper nouns, third-party library names, URLs
- **NEVER use em dashes (`—`)** in documentation, comments, or UI text. Use traditional separators instead:
  - In sentences: semicolons, commas, or colons (e.g. "JIM takes a different approach; it deploys..." not "JIM takes a different approach — it deploys...")
  - In bullet points: colons to separate a label from its description (e.g. "Attribute Writeback: Keep HR systems current" not "Attribute Writeback — Keep HR systems current")
  - In parenthetical asides: commas or parentheses
- **JIM domain entity names are proper nouns - Title Case them in user-facing text, documentation, and comments:**
  - "Synchronisation Rule", "Connected System", "Connected System Object", "Metaverse Object", "Metaverse Object Type", "Connected System Object Type", "Run Profile", "Attribute Flow", "Object Matching Rule", "Pending Export", "Activity" and similar named entities are capitalised **even mid-sentence**
  - **Never abbreviate "Synchronisation Rule" to "Sync Rule"** in user-facing text, documentation, or comments; always write it in full. (The `SyncRule` code identifier / type name is unaffected; this rule is about prose and UI text only.)
  - Applies to UI labels, headings, `MudText` prose, snackbar/dialog/validation messages, and Markdown docs. Example: "shared by every Synchronisation Rule that targets this system", not "...every synchronisation rule..."
  - Lowercase only when referring to a generic concept rather than the named entity (e.g. "object matching" as an activity), or in code identifiers and variable names

**Copyright Headers (MANDATORY on all new files):**
Every new source file MUST include a copyright header as the very first content. The `.editorconfig` enforces this for `.cs` files via `IDE0073`.

| File type | Header |
|-----------|--------|
| `.cs` | `// Copyright (c) Tetron Limited. All rights reserved.`<br>`// Licensed under the Tetron Commercial License. See LICENSE file in the project root.` |
| `.razor` | `@* Copyright (c) Tetron Limited. All rights reserved. *@`<br>`@* Licensed under the Tetron Commercial License. See LICENSE file in the project root. *@` |
| `.ps1` | `# Copyright (c) Tetron Limited. All rights reserved.`<br>`# Licensed under the Tetron Commercial License. See LICENSE file in the project root.` |
| `.sh` | `# Copyright (c) Tetron Limited. All rights reserved.`<br>`# Licensed under the Tetron Commercial License. See LICENSE file in the project root.` |

- For `.cs` and `.ps1` files: place the header at line 1, followed by a blank line, then the file content
- For `.sh` files: place the header **after** the shebang line (`#!/bin/bash` or similar), no blank line between shebang and header
- For `.razor` files: place the header **after** all `@` directives (`@page`, `@using`, `@inject`, etc.), followed by a blank line before the markup. Razor requires directives at the start of the file. Do NOT add headers to `_Imports.razor`.
- Do NOT add headers to auto-generated files (EF migrations, `.Designer.cs`, `.g.cs`, `.AssemblyInfo.cs`)

**DateTime Handling (IMPORTANT):**
- Always use `DateTime` type (not `DateTimeOffset`) in models
- Always use `DateTime.UtcNow` for current time - NEVER use `DateTime.Now`
- PostgreSQL stores DateTime as `timestamp with time zone` (internally UTC)
- **Runtime quirk**: Npgsql returns `DateTimeOffset` when reading from database, even though model properties are `DateTime`
- Code that processes DateTime values from the database must handle BOTH `DateTime` and `DateTimeOffset` types
- See `DynamicExpressoEvaluator.ToFileTime()` for an example of handling both types
- This design choice maintains database portability (MySQL, SQL Server, etc. handle DateTimeOffset differently)

**SQL Parameterisation (security):**
- ALWAYS parameterise SQL. EF Core does this by default. Raw Npgsql is fine on worker hot paths (see "Worker Hot Path - Raw SQL Over EF Projection" below) but must use `NpgsqlParameter` or the `NullableParam` helper.
- NEVER concatenate or interpolate user-controlled values into a SQL string; always pass them as parameters.

**Raw SQL Nullable Parameters (CRITICAL):**
- NEVER use `(object?)value ?? DBNull.Value` as a parameter to `ExecuteSqlRawAsync` or `ExecuteSqlInterpolatedAsync`
- EF Core cannot infer the PostgreSQL type from bare `DBNull.Value`, causing: `InvalidOperationException: The current provider doesn't have a store type mapping for properties of type 'DBNull'`
- ALWAYS wrap nullable parameters with a typed `NpgsqlParameter`: `NullableParam(value, NpgsqlTypes.NpgsqlDbType.Text)` (see helper method in `ConnectedSystemRepository`, `ActivitiesRepository`, `MetaverseRepository`)
- This applies to ALL nullable columns in raw SQL INSERT/UPDATE statements: string, int, Guid, DateTime, bool, etc.

**Exception Handling:**
- NEVER use generic `catch` or `catch (Exception)` clauses; always catch a specific exception type. This applies even in diagnostic/telemetry code that "should never break callers" - enumerate the concrete failure modes for the operation instead.
- **Sanctioned exception: worker-task / Activity execution boundaries.** The `Worker.cs` task-dispatch cases (and any equivalent top-level boundary whose contract is "any failure must be recorded on the Activity via `FailActivityWithErrorAsync`, never escape silently") MUST catch all exceptions; enumerating types there would leave an unanticipated failure with a permanently in-flight Activity, violating the Synchronisation Integrity rules (`src/JIM.Application/CLAUDE.md`), which take precedence. When the github-code-quality bot flags one of these as a "Generic catch clause", reply to the thread with this rationale and resolve it; do not narrow the catch to appease the linter (precedent: PR #911, `Worker.cs` temporal-reconciliation case).
- For file-open code paths (`FileStream`, `Directory.CreateDirectory`, `Path.*`), the expected set is `UnauthorizedAccessException`, `IOException`, `ArgumentException`, `NotSupportedException`, `System.Security.SecurityException`.
- When several catches share identical fallback behaviour, extract a small private helper (e.g. `FailOpen(path, ex)`) and call it from each typed catch - keeps the catches specific without duplicating the handler body.
- For JS interop retry patterns in `OnAfterRenderAsync` (e.g. loading user preferences), catch `InvalidOperationException` specifically; this is the exception Blazor throws when JS interop is invoked before the runtime is ready

**Logging Security (CWE-117 - log injection):**
- ALWAYS wrap user-controlled `string?` values with `LogSanitiser.Sanitise()` (from `JIM.Utilities`) before passing them as arguments to any `ILogger` or Serilog log call
- Integers, GUIDs, enums, and DateTimes are safe and do not need wrapping
- NEVER log secrets, tokens, or personal data, sanitised or otherwise

**Worker Hot Path - Raw SQL Over EF Projection:**
- For queries on the synchronisation hot path (per-page flushes, cross-page resolution, export evaluation, change-record persistence), default to raw Npgsql (`NpgsqlCommand` + `DbDataReader`, or `BeginBinaryImportAsync` for COPY) rather than EF Core - even `AsNoTracking()` projection.
- Measured on a cross-page MvoChange-id lookup (113 RPEIs): EF projection 7 ms vs raw SQL 2 ms (~3.5x faster). The gap widens with row count because EF materialisation cost scales harder than the query itself.
- EF projection is still appropriate for UI reads and infrequent operations. For **bulk worker paths**, mirror the existing `BulkInsertRpeisRawAsync` / `BulkUpdateRpeiFieldsRawAsync` / `BulkInsertMvoChangesRawAsync` patterns - they exist for a reason.
- When adding a new **Summary**-tier method (see Entity Retrieval Naming Taxonomy below), implement as raw SQL into a DTO, not EF projection into an anonymous type.

**Check DB Constraints Before Proposing Model-Touching Fixes:**
- Before designing a fix that changes how rows are inserted, merged, or de-duplicated in a table, read the relevant `CreateIndex` / `HasIndex` declarations in `src/JIM.PostgresData/Migrations/` (the initial migration or the latest one affecting the table) - or the corresponding section in `JimDbContextModelSnapshot.cs`.
- Unique indexes and FK cascades are opinions baked into the schema. A fix that violates them fails at INSERT time, not at review time, and the shape of the fix usually needs to change as a result.
- Example: `IX_MetaverseObjectChanges_ActivityRunProfileExecutionItemId` is `unique: true`. That meant "merge cross-page reference flow into the existing RPEI" required routing the *new* attribute rows under the *existing* MvoChange parent, not creating a second parent row under the same RPEI FK.

**Prefer FK Scalars Over Navigation Checks Under AsNoTracking:**
- When testing whether a related entity exists, prefer the FK scalar property (`parent.ChildId.HasValue`) over the navigation property (`parent.Child != null`).
- FK columns are always populated from the row data; navigation properties require the query to have `.Include(...)`-d them. If a future optimisation switches a query to `AsNoTracking()` without the right `ThenInclude(...)`, the navigation silently becomes null and every `!= null` check flips to a false negative - bugs that are invisible in unit tests that use the full entity graph.
- Applies especially in `src/JIM.Worker/` where queries routinely use `AsNoTracking()` and selective `.Include` for performance. Example: use `o.ParentSyncOutcomeId.HasValue` in `SyncTaskProcessorBase` rather than `o.ParentSyncOutcome != null`.

**Code Quality (github-code-quality / CodeQL):**

CodeQL runs on every PR via the github-code-quality bot and comments on rule violations. Write code that avoids its common triggers up front rather than fixing after review. **The bot reviews the whole PR diff at PR-open time**, so on a long-lived branch, writing-time lapses accumulate invisibly and land as one wave of findings that block the merge (PR #911: eight findings, five of them shapes already documented below). Before opening a PR from a multi-commit branch, sweep the branch's new/changed C# (`git diff origin/main... -- '*.cs'`) for the shapes in this section:

- **Unused loop variables**: do not write `foreach (var x in collection)` when `x` is never read. CodeQL flags this as "Useless assignment to local variable" (`cs/useless-assignment-to-local`). Use `for (var i = 0; i < collection.Count; i++)` when you only need iteration count, or refactor to actually use the variable.
- **Redundant / constant conditions**: do not re-test a value whose null-state an earlier early-return guard already established. CodeQL flags two shapes: a `?.` on a variable proven non-null ("redundant null-conditional"), and a `!= null` / `== null` operand that is therefore always true/false inside a later `if` ("Constant condition" / `cs/constant-condition`). Example: after `if (ctx == null) return;`, a subsequent `if (ctx != null && ...)` has a constant first operand; drop it, keeping the rest of the condition exactly (seen on PR #870). The general rule: once control flow guarantees a value's null-state, stop restating it.
- **Implicit filter in `foreach`**: do not write `foreach (var x in xs) { if (predicate) ... }` - CodeQL flags this as "Missed opportunity to use Where". Push the predicate into the iterator: `foreach (var x in xs.Where(x => predicate)) ...`. This applies whenever the body's first (or only) statement is an `if` whose single branch acts on `x`; the guard should move into the sequence so the loop iterates only matching elements.
  - **Mind the nullable-flow loss when the guard was a null check.** Moving `if (x == null) continue;` (or `if (!x.HasValue) continue;`) into `.Where(x => x != null)` (or `.Where(x => x.HasValue)`) removes the compiler's flow analysis inside the loop body, because null-state does not flow through `Where`. The build is zero-warning, so body dereferences then need the null-forgiving `!`: `x!.Member` (else CS8602), and a nullable *value* type's `.Value` after `.Where(v => v.HasValue)` trips CS8629 and likewise needs `!` (`x.Field!.Value`). Add those `!`s in the same change so you don't trade a CodeQL note for a build warning, and rebuild before pushing (seen on PR #870).
- **Map-only `foreach`**: do not write a `foreach` whose first action is to map the iteration variable into another value (e.g. `foreach (Match m in matches) { var name = m.Groups[1].Value; ... }`). CodeQL flags this as "Missed opportunity to use Select" (`cs/...-use-select`). Project with `.Select(...)` and iterate the projected sequence: `foreach (var name in matches.Select(m => m.Groups[1].Value)) ...`.
  - **Do not half-convert.** If the loop *both* maps the variable *and* guards its body with an `if`, converting only the map leaves a guarded body that immediately trips the sibling "use Where" rule above (a real back-and-forth seen on PR #866). Convert the whole loop to one pipeline and drain it, e.g. `target.AddRange(src.Select(...).Where(...))`. A `TryGetValue` guard composes as `.Select(k => dict.TryGetValue(k, out var v) ? v : null).Where(v => v != null).Select(v => v!)`.
- **Allocating a reusable object inside a loop**: do not write `foreach (...) { var sb = new StringBuilder(); ... }` - github-code-quality flags this as "StringBuilder creation in loop". Hoist the instance above the loop and `Clear()` it at the start of each iteration, reusing one instance per method call. Applies to any per-iteration allocation of a resettable builder/buffer; most common in chunked bulk-SQL builders (`BulkUpdateMvoRowsViaEfAsync`, the `BulkInsert*` paths in `SyncRepository.MvoOperations`) (seen on PR #955).
- **If/else assigning the same target**: when both branches of an `if`/`else` do nothing but assign the *same* variable or property, collapse to one conditional assignment: `target = cond ? a : b;`. CodeQL flags the two-branch form as "Missed ternary opportunity".
- **Integer arithmetic feeding a wider parameter**: when an `int` product/sum feeds a `double` or `long` parameter (e.g. `AddDays(count * 7)`), CodeQL flags "Possible loss of precision" because the `int` multiplication can overflow before the widening conversion. Promote an operand at the source: `AddDays(count * 7d)` (seen on PR #911, `RelativeDateResolver`).
- **`HasValue`-guarded `.Value` inside a lambda**: nullable flow analysis does not cross lambda or expression-tree boundaries, so `if (id.HasValue) { ... query.SingleOrDefaultAsync(g => g.Id == id.Value) }` is flagged "Dereferenced variable may be null" even though the guard is airtight. Hoist the value into a local before the lambda (`var idValue = id.Value;`) and use the local in the lambda and any interpolated messages (seen on PR #911, `SearchRepository`). This is the C# sibling of the "Nullable dereference in Razor" rule in `JIM.Web/CLAUDE.md`.
- **Bare dereference after an NUnit `Is.Not.Null` assertion (tests)**: `Assert.That(x, Is.Not.Null)` is not annotated to establish non-null null-state, so CodeQL flags any later bare `x.Member` as "Dereferenced variable may be null" (`cs/dereferenced-value-may-be-null`) even though the assertion is airtight. The C# compiler, by contrast, goes quiet after the *first* `x!` you write (the null-forgiving operator sets the tracked null-state to non-null for the rest of the block), so the build stays zero-warning and masks the accumulating CodeQL debt. Apply `!` to **every** dereference after the assertion, not just the first (`x!.A`, `x!.B`, ...), or bind a non-null local once (`var xnn = x!;`) and use it thereafter. This is the test-side sibling of the two rules above; whole waves land at once on long-lived branches (PR #933: eight identical findings across the configuration change-capture test files, one per shared `_completedActivity` field asserted then dereferenced field-by-field).

**File Organisation:**
- One class per file - each class should have its own `.cs` file named after the class
- Exception: Enums are grouped into a single file per area/folder (e.g., `ConnectedSystemEnums.cs`, `PendingExportEnums.cs`)
- File names must match the class/interface name exactly (e.g., `MetaverseObject.cs` for `class MetaverseObject`)
- **Model placement**: All model/POCO/result classes MUST live in `JIM.Models/`; NEVER define them inline in service or server files in `JIM.Application` or other projects
  - Exceptions: UI-specific models may live in `JIM.Web/Models/`, and API DTOs in `JIM.Web/Models/Api/`
  - If a service method needs a result type, create it as its own file in the appropriate `JIM.Models/` subdirectory

**Method Spacing:**
- Every method must have a blank line above it, or above its XML doc comment block if one is present. The only exception is the first method in a class.
- This applies to interfaces, abstract classes, and concrete classes alike; XML doc comments attach to the method below, so the blank line goes *above* the comment, not between the comment and the method.

**Naming Patterns:**
- Methods: `GetObjectAsync`, `CreateMetaverseObjectAsync`
- Classes: Full descriptive names (avoid abbreviations)
- Properties: PascalCase with nullable reference types enabled

**Entity Retrieval Naming Taxonomy:**

Repository and server methods that load a single entity follow a weight-based taxonomy. Pick the lightest variant that satisfies the caller's needs so that expensive object graphs are only materialised when genuinely required.

| Level       | Suffix                  | Returns                                                                                | Example                             | Use case                                                                 |
|-------------|-------------------------|----------------------------------------------------------------------------------------|-------------------------------------|--------------------------------------------------------------------------|
| **Summary** | `GetXxxSummaryAsync`    | Minimal scalar projection (a DTO of a handful of fields). No entity materialisation.   | `PendingExportSummary`              | High-scale filtering and reconciliation (100K+ objects).                 |
| **Header**  | `GetXxxHeaderAsync`     | Lightweight DTO with denormalised FK names and aggregated counts.                      | `ConnectedSystemHeader`, `SyncRuleHeader` | List views, grids, dropdowns.                                     |
| **Core**    | `GetXxxCoreAsync`       | Materialised entity with essential first-level navigation properties only.            | `GetConnectedSystemCoreAsync`       | API validation, write-path lookups, worker bootstrap, existence checks. |
| **Detail**  | `GetXxxDetailAsync`     | Full entity wrapped in a result object with metadata (for example, capped MVA totals). | `CsoDetailResult`, `PendingExportDetailResult` | Detail pages that may need paging metadata alongside the entity.   |
| **Full**    | `GetXxxAsync` (no suffix) | Complete entity graph with all relevant Includes and navigation properties.          | `GetConnectedSystemAsync`           | Sync engine, schema import, and other operations that genuinely need everything. |

**Rules for picking a variant:**

1. **Summary** (lightest): SQL projection into a flat DTO. No entity materialisation. Use when operating at extreme scale.
2. **Header**: SQL projection into a DTO with denormalised names and aggregated counts. For list and grid display.
3. **Core**: Materialised entity with first-level navigation properties only (no deep collection loading, no matching rules, no container trees). Use for operations that need the entity but not its full graph, such as null checks in API controllers before performing a dependent query.
4. **Detail**: Full entity wrapped in a result object with metadata (for example, total attribute counts when capped). For UI detail pages.
5. **Full** (no suffix, just `GetXxxAsync`): Complete entity graph with all Includes. Reserve for operations that genuinely need everything.

**When adding a new retrieval method, start from the lightest variant that works**; only promote to a heavier one if the caller actually needs the additional data.

**Blazor / MudBlazor UI conventions live in `JIM.Web/CLAUDE.md`** (loads automatically when working under `JIM.Web`): row density, empty values, tooltips, alerts, panel spacing, element sizing, tabs, Razor comments, and nullable dereference in Razor. Shared components: `<TableDensityToggle>` (the compact-row toggle) and `<EmptyValue>` (the low-lighted empty-cell hyphen). Prefer these over hand-rolled markup.

## Architecture Quick Reference

**Metaverse Pattern:**
- MetaverseObject = Central identity entity
- ConnectedSystemObject = External system identity
- SyncRule = Bidirectional mapping between systems
- All operations flow through the metaverse (never direct system-to-system)

**Layer Dependencies (top to bottom):**
1. JIM.Web (Presentation - includes both Blazor UI and REST API at `/api/`)
2. JIM.Application (Business Logic)
3. JIM.Models (Domain)
4. JIM.Data, JIM.PostgresData (Data Access)

**CRITICAL: Respect N-Tier Architecture - NEVER Bypass Layers:**

JIM follows strict n-tier architecture. Each layer may ONLY call the layer directly below it. This is compile-time enforced: `JimApplication.Repository` is `internal`, so a `Jim.Repository.*` call from JIM.Web, JIM.Worker, or JIM.Scheduler is a build error. If a caller needs data the facade does not expose, add a method to the owning server; do not widen the accessor. The single sanctioned exception is `JimApplication.SyncRepository` (`IRepository.Sync`), the sync engine's raw-SQL hot-path repository, usable only from sync task processing:

```
+------------------+
|     JIM.Web      |  Blazor pages, API controllers
+--------+---------+
         | ONLY calls JimApplication (never Repository directly)
         v
+------------------+
| JIM.Application  |  Business logic, orchestration (Servers/)
+--------+---------+
         | ONLY calls Repository interfaces
         v
+------------------+
|    JIM.Data      |  Repository interfaces
+------------------+
         |
         v
+------------------+
| JIM.PostgresData |  EF Core implementations
+------------------+
```

**Rules:**
- **JIM.Web** (UI/API) must ONLY access data through `JimApplication` facade (e.g., `Jim.Metaverse`, `Jim.Scheduler`, `Jim.ConnectedSystems`)
- **NEVER** call `Jim.Repository.*` directly from Blazor pages or API controllers
- If a method doesn't exist on the Application layer, ADD IT there - don't bypass to the repository
- This separation ensures business logic stays in one place and can be tested independently

**Bad - Bypassing layers:**
```csharp
// In a Blazor page - WRONG!
var schedule = await Jim.Repository.Scheduling.GetScheduleAsync(id);
```

**Good - Respecting layers:**
```csharp
// In a Blazor page - CORRECT!
var schedule = await Jim.Scheduler.GetScheduleAsync(id);
```

## Common Development Tasks

**Adding a Connector:**
1. Implement `IConnector` and capability interfaces
2. Add to `JIM.Connectors/` or create new project
3. Register in DI container
4. Add tests

**Adding a built-in (seeded) object or audited configuration entity:**

Two invariants here are enforced only by remembering them, and both have been missed before (the built-in example data template in #866, then the built-in Temporal Scope Reconciliation schedule):

1. **Seed idempotently via `SeedingServer`**: check-then-create, safe to run on every startup.
2. **Audited entities must be created through the audited path, even by the seeder.** If the entity carries configuration change history and Activities (Connected Systems, Synchronisation Rules, Schedules; anything in the configuration `ActivityTargetType` set), seed it via its owning server's create method with `ActivityInitiatorType.System` (see `SeedBuiltInSchedulesAsync`), never `Repository.*` directly. A repository-direct seed records no Create Activity and no version-1 snapshot, so the object's change history begins with whichever principal touches it next, misattributing its origin. **This is not optional for batch-seeded types.** When a type is seeded through a shared cross-referencing batch (Predefined Searches, Metaverse Object Types/Attributes) where re-routing each object through an individual audited create would break reference resolution, record the baseline *after* the batch persists instead: a System-attributed Create Activity plus version-1 snapshot, grouped under the seeding parent, over the "created this pass" list (see `SearchServer.RecordSeededPredefinedSearchBaselineAsync`). Either way the built-in object MUST end up with a Create Activity and v1 baseline as a child of the "System Initialisation" Activity (`SeedingServer.GetOrCreateSeedingActivityAsync`); "it is batch-seeded" is not an exemption. If seeding a config type without a baseline is a deliberate choice, say so where the seed happens.
3. **Factory reset must restore it.** The wipe truncates most tables and built-in data is contractually preserved; ensure `SystemServer.ResetSystemAsync` re-seeds the object after the wipe, and extend the reset tests to prove it. (#916 proposes replacing the bespoke post-reset repairs with a re-run of the whole seeding pipeline; once that lands, seeding through the pipeline covers this step automatically.)
4. Cover with red-first unit tests (`BuiltInScheduleSeedingTests` / `SystemResetBuiltInScheduleTests` show the mock pattern), and update docs and the changelog if user-facing.

**Adding API Endpoint:**
1. Add method to controller in `JIM.Web/Controllers/Api/`
2. Use DTOs for request/response (in `JIM.Web/Models/Api/`)
3. Add XML comments for OpenAPI documentation
4. Test via the Scalar API reference at `/api/reference`

**Route templates are validated at runtime, not by the compiler (boot the app to catch route bugs):**
- Route parameter names must be unique across the *combined* template (the controller-level `[Route]` plus the action's `[HttpGet]`/`[HttpPost]` template). Every controller route is `api/v{version:apiVersion}/[controller]`, so **`version` is already a route parameter on every action** - never reuse it. An action template like `change-history/{version:int}` yields two `version` parameters in the merged route, which ASP.NET rejects at **startup** with an `ArgumentException` ("An item with the same key has already been added"), crashing the app (and the `openapi-gen` Docker build stage) before the route table is built. Use a distinct name such as `{changeVersion:int}`. Likewise avoid colliding with `[controller]` or other ambient tokens.
- This whole class of bug (duplicate route params, ambiguous templates, bad constraints) is a **runtime route-binding failure, not a compile error**: `dotnet build` stays clean, and unit tests that call controller action methods directly bypass routing, so they pass too. It only surfaces when the app actually boots. **After adding or renaming any API route, validate by starting the app** (`jim-build-light` / `jim-stack`) or running an integration test or the OpenAPI generation; do not rely on `dotnet build` + method-level unit tests alone.

**API Endpoint Identifier Rules (MUST follow):**

These rules apply across the REST API (`JIM.Web/Controllers/Api/`), the application and repository layers that back it, and any PowerShell cmdlet that wraps an endpoint.

- **GET (single-entity retrieval) MUST expose an ID-based signature.** The canonical route is `GET /resource/{id}` (or `{id:int}` / `{id:guid}` as appropriate). The ID is the only identifier guaranteed to be immutable and globally unique across the lifetime of the object.
- **GET SHOULD also expose a name-based overload** for discoverability, where "name" is whichever human-readable immutable-ish slug the resource uses: `Name` for most objects, `Uri` for `PredefinedSearch`, `Key` for `ServiceSetting`, etc. Route the overload under a distinct path (e.g. `GET /resource/by-uri/{uri}`) so ASP.NET Core routing can disambiguate, or use a different type constraint on `{id}` that prevents the name from matching.
- **PATCH / PUT / DELETE MUST use the ID-based signature only.** Name-based overloads for write operations are **not allowed**, because the "name" field is itself mutable — a PATCH that renames the resource via a URI-keyed route would invalidate the very key used to locate it, and a DELETE keyed by name is racy against concurrent renames. Clients that only know the name must resolve it to an ID via a GET first.
- **List endpoints** (`GET /resource`) SHOULD return headers that include the ID, so that automation and PowerShell callers can discover IDs for subsequent PATCH/DELETE calls.
- These rules apply to the server (`JIM.Application/Servers/`) and repository (`JIM.Data/Repositories/`) layers too: `UpdateXxxAsync` / `DeleteXxxAsync` methods take `int`/`Guid` IDs, never name strings.

**Clearing optional values on partial updates (REST + PowerShell):**

Optional, clearable fields (e.g. `Description`) follow one convention across both surfaces:

- **REST update DTOs**: a nullable field that is omitted (or JSON `null`) means "leave unchanged"; an empty or whitespace-only string clears the stored value to `null` (normalise on the server). State these semantics in the DTO property's XML doc. Precedent: `UpdateConnectedSystemRequest.Description`, `UpdateSyncRuleRequest.Description`.
- **PowerShell cmdlets**: both `$null` and `''` clear the value; lead documentation and examples with the more idiomatic `$null` (`Set-JIMSyncRule -Id 5 -Description $null`) and mention `''` as equivalent. The binder coerces `$null` to `''` for `[string]` parameters, so the two are indistinguishable inside the cmdlet; never add `[ValidateNotNullOrEmpty]` to a clearable field (it would reject both). Implement with a plain `[string]` parameter guarded by `$PSBoundParameters.ContainsKey('...')`, so the bound (empty) value is sent and the API clears the field. Cover both the `$null` and `''` paths with Pester tests.

**Modifying Database Schema:**
1. Update entity in `JIM.Models/`
2. Create migration: `dotnet ef migrations add [Name] --project src/JIM.PostgresData`
3. Review generated migration
4. Test: `dotnet ef database update --project src/JIM.PostgresData`
5. Commit migration files

**CRITICAL: NEVER flatten, squash, delete, or reset EF Core migrations.** Migrations are append-only. Deployed instances track applied migrations by name in `__EFMigrationsHistory`; removing old migrations and replacing them with a combined migration will break every existing deployment.

**Updating Architecture Diagrams:**

When making architectural changes (new containers, components, connectors, or significant restructuring):
1. Update the affected hand-authored SVGs under `docs/assets/diagrams/` (System Context, Containers, Worker components) and the Mermaid component diagrams in `docs/developer/architecture.md`
2. If `system-context.svg` or `containers.svg` changed (or the diagram tokens in `custom.css` did), regenerate the README exports: `pwsh ./scripts/Export-ReadmeDiagrams.ps1`
3. Commit the diagram changes together with the code change

> **Authoring conventions:** `docs/CLAUDE.md` > Concept Diagrams; **visual language:** `engineering/DESIGN.md` > Diagrams
