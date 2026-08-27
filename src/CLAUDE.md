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
Every new source file MUST include a copyright header as the very first content.

| File type | Header |
|-----------|--------|
| `.cs` | `// Copyright (c) Tetron Limited. All rights reserved.`<br>`// Licensed under the Tetron Commercial License. See LICENSE file in the project root.` |
| `.razor` | `@* Copyright (c) Tetron Limited. All rights reserved. *@`<br>`@* Licensed under the Tetron Commercial License. See LICENSE file in the project root. *@` |
| `.ps1`, `.psm1`, `.psd1` | `# Copyright (c) Tetron Limited. All rights reserved.`<br>`# Licensed under the Tetron Commercial License. See LICENSE file in the project root.` |
| `.sh` | `# Copyright (c) Tetron Limited. All rights reserved.`<br>`# Licensed under the Tetron Commercial License. See LICENSE file in the project root.` |

- For `.cs` files: place the header at line 1, followed by a blank line, then the file content
- For `.ps1`/`.psm1`/`.psd1` files: place the header at line 1, or immediately below a `#Requires` directive where one is present. In a `.psd1` manifest the comment header goes above the opening `@{`; it is **in addition to** the manifest's own `Copyright` key, which is what `Get-Module` surfaces and which neither states the licence nor satisfies this rule
- For `.sh` files: place the header **after** the shebang line (`#!/bin/bash` or similar), no blank line between shebang and header
- For `.razor` files: place the header **after** all `@` directives (`@page`, `@using`, `@inject`, etc.), followed by a blank line before the markup; where a component declares no directives, line 1. This is house style, not a compiler constraint (Razor is happy with the notice on line 1), but it is the enforced position. `_Imports.razor` is included like any other component
- Do NOT add headers to auto-generated files (EF migrations, `.Designer.cs`, `.g.cs`, `.AssemblyInfo.cs`)

**Enforcement:** `scripts/Lint-CopyrightHeaders.ps1` checks every file of the types above and fails the `build-and-test` CI job on anything missing or misplaced. **Position is enforced, not just presence** - 12 `JIM.Web` components had the notice injected between an `@if` condition and its opening brace, which a presence check would have passed. Blank-line counts are not policed; only that nothing of substance precedes the notice.

```bash
pwsh -File ./scripts/Lint-CopyrightHeaders.ps1        # report
pwsh -File ./scripts/Lint-CopyrightHeaders.ps1 -Fix   # insert or relocate, per file type
```

`.editorconfig` enforces the same rule for `.cs` via `file_header_template` + `IDE0073`. Because `Directory.Build.props` sets `EnforceCodeStyleInBuild` and `TreatWarningsAsErrors`, a `.cs` file without the header is a **build error**, not an IDE hint. The generated-code exclusions above are duplicated there as `severity = none` sections; **if you change the exclusions in one place, change them in the other**, or `dotnet build` and the lint script will disagree.

**DateTime Handling (IMPORTANT):**
- Always use `DateTime` type (not `DateTimeOffset`) in models
- Always use `DateTime.UtcNow` for current time - NEVER use `DateTime.Now`
- PostgreSQL stores DateTime as `timestamp with time zone` (internally UTC)
- **Runtime quirk**: Npgsql returns `DateTimeOffset` when reading from database, even though model properties are `DateTime`
- Code that processes DateTime values from the database must handle BOTH `DateTime` and `DateTimeOffset` types
- See `DynamicExpressoEvaluator.ToFileTime()` for an example of handling both types
- This design choice maintains database portability (MySQL, SQL Server, etc. handle DateTimeOffset differently)
- **The mechanism, and its one visible cost:** `PostgresDataRepository`'s constructor sets the `Npgsql.EnableLegacyTimestampBehavior` AppContext switch, under which `DateTime` maps to `timestamp without time zone`. The EF tooling never constructs that repository, so migrations and `JimDbContextModelSnapshot.cs` are scaffolded with the switch off and declare `timestamp with time zone`, which is what the database actually holds. Runtime and migrations therefore disagree by exactly the schema's 99 DateTime columns, permanently, which is why `PendingModelChangesWarning` is suppressed in all four EF configuration sites; without it `MigrateAsync()` throws on first boot. The consequence is that a *genuine* model change is invisible at runtime, so the guards that still work are the design-time ones (`dotnet ef migrations has-pending-model-changes`, and `MigrationDesignerChainTests`), both of which run with the switch off. Retiring the switch would need `DateTime.Kind` normalised to `Utc` at every write; the schema needs no change either way.

**SQL Parameterisation (security):**
- ALWAYS parameterise SQL. EF Core does this by default. Raw Npgsql is fine on worker hot paths (see "Worker Hot Path - Raw SQL Over EF Projection" below) but must use `NpgsqlParameter` or the `NullableParam` helper.
- NEVER concatenate or interpolate user-controlled values into a SQL string; always pass them as parameters.

**Raw SQL Nullable Parameters (CRITICAL):**
- NEVER use `(object?)value ?? DBNull.Value` as a parameter to `ExecuteSqlRawAsync` or `ExecuteSqlInterpolatedAsync`
- EF Core cannot infer the PostgreSQL type from bare `DBNull.Value`, causing: `InvalidOperationException: The current provider doesn't have a store type mapping for properties of type 'DBNull'`
- ALWAYS wrap nullable parameters with a typed `NpgsqlParameter`: `NullableParam(value, NpgsqlTypes.NpgsqlDbType.Text)` (see helper method in `ConnectedSystemRepository`, `ActivitiesRepository`, `MetaverseRepository`)
- This applies to ALL nullable columns in raw SQL INSERT/UPDATE statements: string, int, Guid, DateTime, bool, etc.

**Raw SQL Column Lists (MANDATORY guard):**

Raw SQL bypasses the EF model, so a hand-typed column list silently diverges from it the moment a migration adds a column: writes default the new column as NULL for every bulk-written row, with no error anywhere. This is a proven, recurring failure class (attribute-value provenance #91; Decimal audit values, CSO PartitionId and RPEI ErrorStackTrace found in one 2026-07 sweep). Three rules prevent it:

1. **Never hand-type a column list in a raw SQL statement.** Every COPY / INSERT / UPDATE column list must come from the table's `*BulkColumns` constants class in `JIM.PostgresData/Repositories/` (`MvoBulkInsertColumns`, `MvoChangeBulkColumns`, `CsoBulkColumns`, `CsoChangeBulkColumns`, `RpeiBulkColumns`, `PendingExportBulkColumns`), interpolated via `BulkSqlHelpers.ToQuotedList` so the constant IS the statement's column list. The writer beside it must write values in exactly list order (comment `// ... order below MUST match ...` at the writer). Partial updates get their own named update list plus a documented exclusion list explaining why each excluded column is not written. When adding raw SQL for a table with no constants class yet, create one following the existing pattern; do not add a seventh inline list.
2. **Every constant is guarded by `BulkInsertColumnCompletenessTests`** (JIM.Worker.Tests): insert lists are asserted against the EF model's mapped columns, and update lists plus exclusions must cover the insert list exactly. These run in every unit pass and in `build-and-test` on every PR, so a migration that adds a column fails the test run with a message naming the column and the writers to extend. A new constants class needs its tests added in the same change.
3. **Every raw write path needs a `RequiresPostgres` round-trip test** that persists a fully populated entity through the public repository method and asserts every field on read-back (pattern: `MvoChangeValuePersistenceDatabaseTests`, `CsoPartitionIdPersistenceDatabaseTests`). The completeness test cannot catch a writer writing values in the wrong order, a wrong `NpgsqlDbType`, or a temp-table shape drifting from its UPDATE; the round-trip test catches all three, and the in-memory provider structurally cannot.

Exempt from rule 1: deliberately narrow, single-purpose statements that set call-site-computed values (single-column FK fix-ups, status-mark updates, scope flags) and read-side SELECT projections, which are deliberate subsets. If in doubt whether a statement is "the write path for the entity" or a targeted mutation, treat it as the write path.

**Exception Handling:**
- NEVER use generic `catch` or `catch (Exception)` clauses; always catch a specific exception type. This applies even in diagnostic/telemetry code that "should never break callers" - enumerate the concrete failure modes for the operation instead.
- **Sanctioned exception: worker-task / Activity execution boundaries.** The `Worker.cs` task-dispatch cases (and any equivalent top-level boundary whose contract is "any failure must be recorded on the Activity via `FailActivityWithErrorAsync`, never escape silently") MUST catch all exceptions; enumerating types there would leave an unanticipated failure with a permanently in-flight Activity, violating the Synchronisation Integrity rules (`src/JIM.Application/CLAUDE.md`), which take precedence. When the github-code-quality bot flags one of these as a "Generic catch clause", reply to the thread with this rationale and resolve it; do not narrow the catch to appease the linter (precedent: PR #911, `Worker.cs` temporal-reconciliation case).
- **Fallback-dispatcher catches need an exception filter, not a bare `catch (Exception)`.** A broad catch whose job is to divert to a degraded/fallback path (bulk operation fails -> per-object fallback; fast path fails -> slow path) is NOT the sanctioned Activity-boundary case above and will be flagged as "Generic catch clause". Write it as `catch (Exception ex) when (ex is not OperationCanceledException)`: the filter satisfies CodeQL, and excluding cancellation is semantically correct - an aborting run must propagate, not grind on through the fallback (precedent: PR #996, `FlushPendingMvoDeletionsAsync` bulk-to-per-MVO fallback). Do not copy CodeQL's suggested long exclusion list (`OutOfMemoryException`, `StackOverflowException`, ...): those are uncatchable or process-fatal anyway, and the list is noise.
- For file-open code paths (`FileStream`, `Directory.CreateDirectory`, `Path.*`), the expected set is `UnauthorizedAccessException`, `IOException`, `ArgumentException`, `NotSupportedException`, `System.Security.SecurityException`.
- When several catches share identical fallback behaviour, extract a small private helper (e.g. `FailOpen(path, ex)`) and call it from each typed catch - keeps the catches specific without duplicating the handler body.
- For JS interop retry patterns in `OnAfterRenderAsync` (e.g. loading user preferences), catch `InvalidOperationException` specifically; this is the exception Blazor throws when JS interop is invoked before the runtime is ready
- For JS interop in code paths that can run during component or circuit teardown (`OnAfterRenderAsync`, `Dispose`/`DisposeAsync`, timer and polling callbacks), also catch `JSDisconnectedException`; this is the disposal-side sibling, thrown when the client has already disconnected. Note that `NavigationManager.NavigateTo` performs its JS interop in a fire-and-forget continuation, so a local try/catch cannot observe the failure; teardown-reachable code must not call `NavigateTo` at all (see `NavigableMudTabs` for the guard pattern)

**Logging Security (CWE-117 - log injection):**
- ALWAYS wrap user-controlled `string?` values with `LogSanitiser.Sanitise()` (from `JIM.Utilities`) before passing them as arguments to any `ILogger` or Serilog log call
- Integers, GUIDs, enums, and DateTimes are safe and do not need wrapping
- NEVER log secrets, tokens, or personal data, sanitised or otherwise

**Worker Hot Path - Raw SQL Over EF Projection:**
- For queries on the synchronisation hot path (per-page flushes, cross-page resolution, export evaluation, change-record persistence), default to raw Npgsql (`NpgsqlCommand` + `DbDataReader`, or `BeginBinaryImportAsync` for COPY) rather than EF Core - even `AsNoTracking()` projection.
- Measured on a cross-page MvoChange-id lookup (113 RPEIs): EF projection 7 ms vs raw SQL 2 ms (~3.5x faster). The gap widens with row count because EF materialisation cost scales harder than the query itself.
- EF projection is still appropriate for UI reads and infrequent operations. For **bulk worker paths**, mirror the existing `BulkInsertRpeisRawAsync` / `BulkUpdateRpeiFieldsRawAsync` / `BulkInsertMvoChangesRawAsync` patterns - they exist for a reason.
- When adding a new **Summary**-tier method (see Entity Retrieval Naming Taxonomy below), implement as raw SQL into a DTO, not EF projection into an anonymous type.

**Raw SQL Writes Must Fix Up or Detach Tracked Instances:**
- The worker's DbContext lives for the whole run profile execution, so anything a tracked query loaded earlier in the run is still in the identity map when a raw SQL write changes or removes its row. Raw SQL bypasses the change tracker; the code issuing it owns re-synchronising the tracker.
- **Close what you open.** EF Core only auto-closes connections it opened itself; a connection opened by repository code (`GetDbConnection()` + `OpenAsync()`) stays checked out of the Npgsql pool until the DbContext is disposed. Wrap every manual open in `await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(conn);` (JIM.PostgresData) - it opens only if closed and closes only what it opened, so it is transaction-safe. On per-batch contexts an unclosed connection pins one pooled connection per batch and exhausts the pool (Max Pool Size 30): the Scale200k10kGroups parallel export failed from batch 29 onwards (2026-07-13). Sibling rule: any factory-created per-unit-of-work context must be deterministically disposed (`ISyncRepositoryScope`), never left to the GC.
- Raw **UPDATE**: set the same properties on the tracked instances to match the new database state (pattern: the tracked-CSO fix-up loops in `SyncRepository.DeleteMetaverseObjectsAsync`), or the next `SaveChangesAsync` writes the stale values back.
- Raw **DELETE**: detach the tracked instances AND their tracked children (pattern: `DetachTrackedEntities` / `DetachTrackedChildEntities` in `SyncRepository.CsOperations.cs`). This is required even when your code never touches the tracked instance again, because EF acts on it for you: deleting a principal applies client-side cascade fix-up to every tracked dependent (e.g. `PendingExport.SourceMetaverseObjectId` is SetNull-on-delete, so deleting a Metaverse Object marks any tracked Pending Export sourced from it Modified), and `SaveChangesAsync` then issues an UPDATE against the raw-deleted row, affects 0 rows, and throws `DbUpdateConcurrencyException` - which poisons every later `SaveChangesAsync` on that context, including error-handling paths (Scenario4-DeletionRules failure on PR #996).
- **Tracker surgery must not trigger DetectChanges.** `ChangeTracker.Entries()` / `Entries<T>()` runs `DetectChanges()` first, which attaches any undetected untracked graphs hanging off tracked navigations - and mid-sync those graphs routinely carry duplicate-key instances (cross-page reference resolution builds them), so the attach throws "another instance with the same key value is already being tracked". Wrap detach/fix-up enumeration in an `AutoDetectChangesEnabled = false` try/finally (see `DetachTrackedEntities` in `SyncRepository.CsOperations.cs`; found via Scenario8 after the Scenario4 fix above).
- The in-memory provider performs no affected-row-count checks, so the entire unit suite passes with this bug present. Any change adding a raw SQL write reachable from the sync path needs a `RequiresPostgres` regression test that runs the write on a context holding tracked instances of the affected rows (pattern: `MvoDeletionPendingExportReplaceDatabaseTests`).

**`DbSet.Add` Walks the Graph; `Entry()` Does Not:**
- `DbSet.Add(entity)` (and `Update()`) traverses every navigation and marks each **untracked** entity it reaches for insertion, including up through parent navigations. `DbContext.Entry(entity)` tracks the one entity and traverses nothing. Adding a child whose parent navigation is set therefore inserts the parent too, and when that parent is detached but already persisted (the UI loads an entity in one scope and saves it in another; the global tracking behaviour is NoTracking, so *every* loaded entity is detached), the save fails on a duplicate key somewhere up the chain - typically at the far end, naming a table the code never touched.
- Two ways to stop it, both used in `ConnectedSystemRepository`: track the parent first (traversal stops at an already-tracked entity, so each `Add` is confined to what is genuinely new; see `MarkConnectedSystemGraphForUpdate`), or null the parent navigation and set the scalar FK before adding (see `ReconcileObjectTypesAsync`). Prefer the first when the parent is right there; the second when the child is added in isolation.
- **Ordering is load-bearing where the first approach is used.** Keep the parent's `UpdateDetachedSafe` above the loop that adds its children, and say why in a comment; moving it back down silently reintroduces the bug.
- Cover with a `RequiresPostgres` test that loads in one context and saves in another (pattern: `ConnectedSystemHierarchyPersistenceDatabaseTests`). The in-memory provider enforces no key constraints, so the whole unit suite passes with this bug present: the portal's "Retrieve Hierarchy" was broken in every build for exactly that reason.

**Mutating Repository Methods Must Assert They Got a Tracked Entity:**

Three of JIM's four hosts run the DbContext `NoTracking`: JIM.Web (`Program.cs`), JIM.Scheduler (`Program.cs`), and `JimDbContext`'s own default. Only JIM.Worker tracks by default. A repository method that loads an entity, mutates it and calls `SaveChangesAsync` therefore works perfectly from the sync engine and does **nothing at all** from the portal, while reporting success: no exception, no log, no row written. The same happens when a caller hands over an entity it loaded on an earlier, now-disposed context (every Blazor page that wraps its load in `using var jim = JimFactory.Create()`).

This class has now been paid for three times: a Synchronisation Rule's `Enabled` toggle silently reverting (`ConnectedSystemRepository.UpdateSyncRuleAsync`), Predefined Search criteria (`SearchRepository`), and a Metaverse Object Type's Deletion Rules (`MetaverseRepository.UpdateMetaverseObjectTypeAsync`).

- **Load with an explicit `AsTracking()`**, or accept an entity the caller loaded tracked; then **assert it** with `context.RequireTracked(entity, nameof(Method), remedy)` (`JIM.PostgresData/TrackedEntityGuard.cs`). The remedy string must name the fix (which retrieval method tracks, or which query needs the `AsTracking`), not merely restate that the entity is detached.
- `Update()`, `Add()`, `Attach()` and `Entry(x).State = ...` attach explicitly and are unaffected by query tracking behaviour. A *query* result is what goes detached. Swapping an `Update()` for a load-then-`SetValues()` without `AsTracking()` converts a working method into a silent no-op; that is exactly how the Metaverse Object Type fix shipped broken the first time.
- **The traversal rule above has a sharper edge across a many-to-many skip navigation.** Children reached through a one-to-many carry real keys, so `Update()` marks them Modified. Join rows are not entities at all, so EF cannot tell an existing one from a new one and inserts it, failing on the join table's primary key. The model has exactly three of these: `MetaverseObject.Roles` ↔ `Role.StaticMembers`, `ApiKey.Roles`, and `MetaverseObjectType.Attributes` ↔ `MetaverseAttribute.MetaverseObjectTypes`. Never pass a detached entity of those types with the skip navigation loaded; write the entity's own columns instead. That is what broke the Deletion Rules panel: `Update()` on a detached object type re-inserted a binding per attribute.
- **This is a rule about *mutating* paths only, and tracking stays opt-in.** Read paths must NOT acquire an `AsTracking()` because of this section: untracked reads are the default deliberately (the identity map and snapshotting cost real time and memory at scale, which is why the worker hot path goes further and bypasses EF entirely, above). Equally, `NoTracking` is a default and not a blanket: some reads genuinely require `AsTracking()` for correctness, not just for saving. `MetaverseRepository.GetMetaverseObjectByTypeAndAttributeAsync` is the worked example: its `Include` path cycles back to the same entity, and EF Core forbids that in a no-tracking query because there is no identity resolution to break the cycle. Turning tracking off universally breaks the application, as the integration suite demonstrates. Opt in where the operation needs it; leave everything else alone.
- **The unit suite cannot catch any of this.** The in-memory provider tracks by default and enforces no unique constraint on join tables, so both faults pass silently. Cover a new mutating repository method with a `RequiresPostgres` test whose context is configured `NoTracking`, matching JIM.Web (pattern: `MetaverseObjectTypeUpdateDatabaseTests`). A fixture built on a tracking context proves nothing about the portal.

**Check DB Constraints Before Proposing Model-Touching Fixes:**
- Before designing a fix that changes how rows are inserted, merged, or de-duplicated in a table, read the relevant `CreateIndex` / `HasIndex` declarations in `src/JIM.PostgresData/Migrations/` (the initial migration or the latest one affecting the table) - or the corresponding section in `JimDbContextModelSnapshot.cs`.
- Unique indexes and FK cascades are opinions baked into the schema. A fix that violates them fails at INSERT time, not at review time, and the shape of the fix usually needs to change as a result.
- Example: `IX_MetaverseObjectChanges_ActivityRunProfileExecutionItemId` is `unique: true`. That meant "merge cross-page reference flow into the existing RPEI" required routing the *new* attribute rows under the *existing* MvoChange parent, not creating a second parent row under the same RPEI FK.

**Prefer FK Scalars Over Navigation Checks Under AsNoTracking:**
- When testing whether a related entity exists, prefer the FK scalar property (`parent.ChildId.HasValue`) over the navigation property (`parent.Child != null`).
- FK columns are always populated from the row data; navigation properties require the query to have `.Include(...)`-d them. If a future optimisation switches a query to `AsNoTracking()` without the right `ThenInclude(...)`, the navigation silently becomes null and every `!= null` check flips to a false negative - bugs that are invisible in unit tests that use the full entity graph.
- Applies especially in `src/JIM.Worker/` where queries routinely use `AsNoTracking()` and selective `.Include` for performance. Example: use `o.ParentSyncOutcomeId.HasValue` in `SyncTaskProcessorBase` rather than `o.ParentSyncOutcome != null`.

**Code Quality (github-code-quality / CodeQL):**

CodeQL runs on every PR via the github-code-quality bot and comments on rule violations. Write code that avoids its common triggers up front rather than fixing after review. **The bot reviews the whole PR diff at PR-open time**, so on a long-lived branch, writing-time lapses accumulate invisibly and land as one wave of findings that block the merge (PR #911: eight findings, five of them shapes already documented below). Before opening a PR from a multi-commit branch, sweep the branch's new/changed C# **and Razor** (`git diff origin/main... -- '*.cs' '*.razor'`) for the shapes in this section. Razor is NOT exempt: `@code` blocks and markup expressions compile to C# and CodeQL analyses them identically; a `.cs`-only sweep let two guarded nullable `.Value` dereferences in `.razor` markup through on PR #1013.

- **Unused loop variables**: do not write `foreach (var x in collection)` when `x` is never read. CodeQL flags this as "Useless assignment to local variable" (`cs/useless-assignment-to-local`). Use `for (var i = 0; i < collection.Count; i++)` when you only need iteration count, or refactor to actually use the variable.
- **Redundant / constant conditions**: do not re-test a value whose null-state an earlier early-return guard already established. CodeQL flags two shapes: a `?.` on a variable proven non-null ("redundant null-conditional"), and a `!= null` / `== null` operand that is therefore always true/false inside a later `if` ("Constant condition" / `cs/constant-condition`). Example: after `if (ctx == null) return;`, a subsequent `if (ctx != null && ...)` has a constant first operand; drop it, keeping the rest of the condition exactly (seen on PR #870). The general rule: once control flow guarantees a value's null-state, stop restating it.
- **Implicit filter in `foreach`**: do not write `foreach (var x in xs) { if (predicate) ... }` - CodeQL flags this as "Missed opportunity to use Where". Push the predicate into the iterator: `foreach (var x in xs.Where(x => predicate)) ...`. This applies whenever the body's first (or only) statement is an `if` whose single branch acts on `x`; the guard should move into the sequence so the loop iterates only matching elements.
  - **Early-`continue` guards are the same shape, including dictionary probes.** `foreach (var k in keys) { if (!dict.TryGetValue(k, out var v)) continue; ... }` (and `if (!Contains) continue;`) is flagged identically, even though the guard is a lookup rather than a predicate on `x` (four findings at once on PR #996). Preferred fixes, in order: (1) when the dictionary was just built from the same keys and the body only needs the key and value, iterate the dictionary directly (`foreach (var (k, v) in dict)`) - no filter needed at all; (2) `foreach (var k in keys.Where(dict.ContainsKey)) { var v = dict[k]; ... }` (fine off the hot path; it double-looks-up); (3) the `TryGetValue` Select/Where pipeline below when you need single-lookup performance. Do not defer this shape to "fix if the bot flags it" - it always flags it, and the writing-time fix is cheaper than the review round trip.
  - **Mind the nullable-flow loss when the guard was a null check.** Moving `if (x == null) continue;` (or `if (!x.HasValue) continue;`) into `.Where(x => x != null)` (or `.Where(x => x.HasValue)`) removes the compiler's flow analysis inside the loop body, because null-state does not flow through `Where`. The build is zero-warning, so body dereferences then need the null-forgiving `!`: `x!.Member` (else CS8602), and a nullable *value* type's `.Value` after `.Where(v => v.HasValue)` trips CS8629 and likewise needs `!` (`x.Field!.Value`). Add those `!`s in the same change so you don't trade a CodeQL note for a build warning, and rebuild before pushing (seen on PR #870).
- **Map-only `foreach`**: do not write a `foreach` whose first action is to map the iteration variable into another value (e.g. `foreach (Match m in matches) { var name = m.Groups[1].Value; ... }`). CodeQL flags this as "Missed opportunity to use Select" (`cs/...-use-select`). Project with `.Select(...)` and iterate the projected sequence: `foreach (var name in matches.Select(m => m.Groups[1].Value)) ...`.
  - **Do not half-convert.** If the loop *both* maps the variable *and* guards its body with an `if`, converting only the map leaves a guarded body that immediately trips the sibling "use Where" rule above (a real back-and-forth seen on PR #866). Convert the whole loop to one pipeline and drain it, e.g. `target.AddRange(src.Select(...).Where(...))`. A `TryGetValue` guard composes as `.Select(k => dict.TryGetValue(k, out var v) ? v : null).Where(v => v != null).Select(v => v!)`.
- **Allocating a reusable object inside a loop**: do not write `foreach (...) { var sb = new StringBuilder(); ... }` - github-code-quality flags this as "StringBuilder creation in loop". Hoist the instance above the loop and `Clear()` it at the start of each iteration, reusing one instance per method call. Applies to any per-iteration allocation of a resettable builder/buffer; most common in chunked bulk-SQL builders (`BulkUpdateMvoRowsViaEfAsync`, the `BulkInsert*` paths in `SyncRepository.MvoOperations`) (seen on PR #955).
- **If/else assigning the same target**: when both branches of an `if`/`else` do nothing but assign the *same* variable or property, collapse to one conditional assignment: `target = cond ? a : b;`. CodeQL flags the two-branch form as "Missed ternary opportunity".
- **Integer arithmetic feeding a wider parameter**: when an `int` product/sum feeds a `double` or `long` parameter (e.g. `AddDays(count * 7)`), CodeQL flags "Possible loss of precision" because the `int` multiplication can overflow before the widening conversion. Promote an operand at the source: `AddDays(count * 7d)` (seen on PR #911, `RelativeDateResolver`).
- **`HasValue`-guarded `.Value` inside a lambda**: nullable flow analysis does not cross lambda or expression-tree boundaries, so `if (id.HasValue) { ... query.SingleOrDefaultAsync(g => g.Id == id.Value) }` is flagged "Dereferenced variable may be null" even though the guard is airtight. Hoist the value into a local before the lambda (`var idValue = id.Value;`) and use the local in the lambda and any interpolated messages (seen on PR #911, `SearchRepository`). This is the C# sibling of the "Nullable dereference in Razor" rule in `JIM.Web/CLAUDE.md`.
- **Guarded nullable `.Value` in Razor markup**: the Razor-side sibling of the two rules above; capture a local inside the `@if` guard block and use the local in markup (full rule: "Nullable dereference in Razor" in `JIM.Web/CLAUDE.md`). Pattern guards (`is > 0`, `is not null`) do not satisfy CodeQL any more than `HasValue` does, and the rule applies to every nullable value type (`int?` as much as `DateTime?`): on PR #1013 the same panel captured locals for three `DateTime?` fields but dereferenced the `int?` beside them, and both list and detail pages were flagged.
- **"Missing Dispose call on local IDisposable" where ownership genuinely transfers is a false positive; reply and resolve, do not restructure.** CodeQL cannot see ownership transfer: an `HttpClient` created inline and handed to a constructor that stores and later disposes it (`ScimHttpClient.Dispose()` disposes the `HttpClient` it was given) is flagged as never disposed, even when every call site deterministically disposes the owning object (`using var client = ...`). Do not "fix" it with double-dispose tuples or by having both sides dispose; state the ownership chain in a reply and resolve the thread (precedent: PR #1177, three `HttpClient` findings across the SCIM connector tests). Two genuine variants to check before replying: the owner really does dispose the transferred object in its own `Dispose`, and every path that creates the owner disposes it. Where the flagged allocation does NOT escape (a bare `new HttpResponseMessage(...)` built inline in a stub handler's ternary), route it through the file's existing response-construction helper (`Json(...)`) instead; returned-from-helper allocations are not flagged, and the style matches the rest of the file (same PR, three findings).
- **Grouped assertions: always `using (Assert.EnterMultipleScope())`, never `Assert.Multiple(...)`.** The suite was migrated wholesale in PR #1262 (694 call sites, 136 files); do not reintroduce the delegate form in new tests. The migration was not a bug fix: `Assert.Multiple(() => { ... })` binds to the non-obsolete `Multiple(Action)` overload, so the recurring "Call to obsolete method `Multiple`" finding really was a false positive (github-code-quality resolves it to `Multiple(TestDelegate)` instead, and the compiler disagrees: `dotnet build -warnaserror` is clean, and a genuine obsolete call would fail it with CS0618). We migrated because arguing that per-PR was costing more than the change did (2 threads on #1151, 4 on #1241, 8 on #1257), and because the scope form is genuinely better: `Assert.Multiple(async () => ...)` is an async void lambda whose post-`await` assertions can escape the scope, whereas `await` inside a `using` block cannot. Watch one thing when writing new grouped assertions: `return` inside the old lambda exited only the lambda, but inside a `using` block it exits the whole test method.
- **Asserting that nothing throws: always `Assert.That(..., Throws.Nothing)`, never `Assert.DoesNotThrow`/`Assert.DoesNotThrowAsync`.** The suite was migrated wholesale in PR #1354 (131 call sites, 44 files), for the same reason as the grouped-assertion migration above: the "Call to obsolete method `DoesNotThrow`" finding recurs on every PR that touches a test file, and arguing it each time costs more than the change did. The constraint form carries an assertion message as a third argument exactly as the old overload did, so a message-carrying call converts without losing anything. Both delegate shapes survive the conversion intact: `async () => { ... await ... }` and a plain `() => SomethingAsync()` each bind to NUnit's `AsyncTestDelegate` rather than to the void-returning `TestDelegate`, so the work is still awaited and an exception thrown after an `await` still fails the assertion. That binding was proved by probe before the migration landed, because the failure mode if it had gone the other way is silent (an `async void` assertion passes regardless of what the delegate does).
- **Bare dereference after an NUnit `Is.Not.Null` assertion (tests)**: `Assert.That(x, Is.Not.Null)` is not annotated to establish non-null null-state, so CodeQL flags any later bare `x.Member` as "Dereferenced variable may be null" (`cs/dereferenced-value-may-be-null`) even though the assertion is airtight. The C# compiler, by contrast, goes quiet after the *first* `x!` you write (the null-forgiving operator sets the tracked null-state to non-null for the rest of the block), so the build stays zero-warning and masks the accumulating CodeQL debt. Apply `!` to **every** dereference after the assertion, not just the first (`x!.A`, `x!.B`, ...), or bind a non-null local once (`var xnn = x!;`) and use it thereafter. This is the test-side sibling of the two rules above; whole waves land at once on long-lived branches (PR #933: eight identical findings across the configuration change-capture test files, one per shared `_completedActivity` field asserted then dereferenced field-by-field).
- **"Clear text storage of sensitive information" on a value that is merely *named* after a secret is a false positive; reply, resolve, and have the alert dismissed.** `cs/cleartext-storage-of-sensitive-information` decides a value is sensitive from the identifier, so any member or method whose name contains "password" taints whatever it returns, whatever its type. On PR #1328 it reported an `int` row count as stored in clear text, end to end: `DeleteTerminalInitialPasswordsAsync` returned an affected-row count, into an `int` on a result object, into an `int` on a response DTO beside five identical counts that had shipped years earlier without complaint. Do **not** rename to dodge it where the name is honest; every faithful name for initial-password work contains the word, and a vaguer one costs readability for no security gain. State the type of the flow and what is genuinely reachable (here: `PendingInitialPassword` stores no password value at all), resolve the review thread, and ask for the code-scanning alert to be dismissed as a false positive, which needs Security-tab access the MCP tools do not expose. Note this is an *alert*, not one of the nine required checks, so a live one does not block the merge; the aggregate `CodeQL` check reports failure while `Analyze (csharp)` passes.

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
5. **Declare the Connector's phases** via `IConnectorPhases` if it performs internal work an administrator would otherwise wait through blind (a file load, a paged fetch), and enter them through the `IConnectorProgress` it is handed. Derive the Connector's tests from `ConnectorPhaseConformanceTests` (JIM.Worker.Tests), which enforces the declaration rules. Declaring nothing is a valid answer where per-item counts already say more; say so where the interface is implemented. Author guidance: `docs/developer/connectors.md` > Declaring the steps of your work; design: `engineering/notes/RUN_PROFILE_PHASES.md`

**Adding a built-in (seeded) object or audited configuration entity:**

Two invariants here are enforced only by remembering them, and both have been missed before (the built-in example data template in #866, then the built-in Temporal Scope Reconciliation schedule):

1. **Seed idempotently via `SeedingServer`, and make it converge**: check-then-create against the persisted state, safe to run on every startup, because it *does* run on every startup. `SeedAsync` has no "already seeded" short-circuit any more (#916): the whole pipeline is `SeedingServer.ApplyBuiltInConfigurationAsync`, which both `JimApplication.InitialiseDatabaseAsync` and the factory reset call. A pass that creates only on a virgin database is a built-in that never reaches an existing deployment. Where a type has more than one built-in instance, declare them in a catalogue (`BuiltInMetaverseSchema`, `SeedingServer.BuiltInConnectors/BuiltInExampleDataSets/BuiltInSchedules/BuiltInRoleNames`) and loop it; a hardcoded "does the one I know about exist?" check suppresses every later addition, which is exactly how the Schedule and Role passes were wrong.
   **`SeedingServer.BuiltInConvergencePaths` must name every entity type carrying a `BuiltIn` flag**, and `BuiltInConfigurationConvergenceTests` fails the build if a new one appears without an entry. That test is the guard; adding the entry is how you record which pass keeps the new type converged.
   **The pipeline's loads must be change-tracked** (`withChangeTracking: true`). It mutates and saves what it loads, and three of JIM's four hosts run the DbContext `NoTracking` (the factory reset runs in JIM.Web). Untracked instances of already-persisted rows get walked into by `AddRange` and re-inserted, and two untracked instances of the same row collide on `TrackGraph`; both were live faults until #916, invisible from JIM.Worker because it tracks by default.
2. **Audited entities must be created through the audited path, even by the seeder.** If the entity carries configuration change history and Activities (Connected Systems, Synchronisation Rules, Schedules; anything in the configuration `ActivityTargetType` set), seed it via its owning server's create method with `ActivityInitiatorType.System` (see `SeedBuiltInSchedulesAsync`), never `Repository.*` directly. A repository-direct seed records no Create Activity and no version-1 snapshot, so the object's change history begins with whichever principal touches it next, misattributing its origin. **This is not optional for batch-seeded types.** When a type is seeded through a shared cross-referencing batch (Predefined Searches, Metaverse Object Types/Attributes) where re-routing each object through an individual audited create would break reference resolution, record the baseline *after* the batch persists instead: a System-attributed Create Activity plus version-1 snapshot, grouped under the seeding parent, over the "created this pass" list (see `SearchServer.RecordSeededPredefinedSearchBaselineAsync`). Either way the built-in object MUST end up with a Create Activity and v1 baseline as a child of the "System Initialisation" Activity (`SeedingServer.GetOrCreateSeedingActivityAsync`); "it is batch-seeded" is not an exemption. If seeding a config type without a baseline is a deliberate choice, say so where the seed happens.
3. **Factory reset restores it for free, provided rule 1 holds.** `SystemServer.ResetSystemAsync` re-runs the whole pipeline after the wipe (#916), so a built-in that converges properly needs nothing added here; the bespoke per-object repairs that preceded it are gone. The one thing the pipeline cannot do is restore the *provenance* of built-ins the wipe preserved, because the ordinary passes correctly no-op for them: that is `RebaselineBuiltInConfigurationAsync`, which the reset calls as well. Extend the reset tests to prove your object comes back.
   **A child table you add under a configuration entity must cascade from its owner.** The wipe removes custom configuration with `DELETE ... WHERE "BuiltIn" = false` and relies entirely on the database to clear what those rows own; a `NO ACTION` foreign key fails the delete with `23503`, and since the whole wipe is one transaction, the reset rolls back and removes nothing (#1477). EF's convention for an *optional* foreign key is `ClientSetNull`, i.e. `NO ACTION`, so a containment relationship left to convention is wrong by default: configure `OnDelete(DeleteBehavior.Cascade)` explicitly in `JimDbContext` (see the "Configuration ownership" block). `DeletePathForeignKeyCoverageTests` asserts this across the whole schema against real PostgreSQL, for the factory reset and for deleting a Connected System alike, so the guard is automatic; if a foreign key genuinely must not cascade (`ServiceSettings` is the one such case, since cascading would delete the preserved singleton), add it to the relevant surface's `SeveredForeignKeys` with its reason and give the sequence a compensating step.
4. Cover with red-first unit tests (`BuiltInConfigurationConvergenceTests` and `SeedingTestHarness` are the mock pattern; the harness is a small in-memory fake of every repository the pipeline touches, shared by the seeding and reset fixtures). Real-PostgreSQL coverage belongs in `SystemResetDatabaseTests` / `SeedingIdempotencyDatabaseTests`; the in-memory provider tracks by default and enforces no join-table key, so it cannot see either of the tracking faults in rule 1. Update docs and the changelog if user-facing.

**Adding a table that hangs off a Connected System:**

`ConnectedSystemRepository.DeleteConnectedSystemAsync` is a hand-ordered sequence of raw SQL statements in one transaction, so the database's cascades do not save you here the way they do elsewhere: the sequence deletes the Connected System *last*, and every table above it in the ordering is deleted by a statement of its own. A new child table therefore needs one of three things, and the wrong choice is a Connected System nobody can delete (the portal reports a save failure and the whole delete rolls back, having removed nothing):

1. Its rows are removed by a statement in the sequence, placed before whatever it references (`ConnectedSystemPasswordSynchronisations`, whose reference to its target Object Type is `Restrict`, has to precede the Object Type delete; #1119).
2. Its rows are retained for audit and the sequence nulls the reference instead (`Activities`, `MetaverseObjectChanges`).
3. It cascades from something already deleted, and that thing is deleted before what the table references.

`DeletePathForeignKeyCoverageTests` asserts this across the whole schema against real PostgreSQL, alongside the same property for the factory reset, so add the table to the surface's `RemovedTables` or the foreign key to its `SeveredForeignKeys` and the guard stays honest. It cannot see a statement whose `WHERE` covers only part of a table (that is #1477's descendant case), so a table with a self-reference or two ownership paths also wants a behavioural test in `ConnectedSystemDeletionDatabaseTests`.

**Adding API Endpoint:**
1. Add method to controller in `JIM.Web/Controllers/Api/`
2. Use DTOs for request/response (in `JIM.Web/Models/Api/`)
3. Add XML comments for OpenAPI documentation
4. Test via the Scalar API reference at `/api/reference`

**Route templates are validated at runtime, not by the compiler (boot the app to catch route bugs):**
- Route parameter names must be unique across the *combined* template (the controller-level `[Route]` plus the action's `[HttpGet]`/`[HttpPost]` template). Every controller route is `api/v{version:apiVersion}/[controller]`, so **`version` is already a route parameter on every action** - never reuse it. An action template like `change-history/{version:int}` yields two `version` parameters in the merged route, which ASP.NET rejects at **startup** with an `ArgumentException` ("An item with the same key has already been added"), crashing the app (and the `openapi-gen` Docker build stage) before the route table is built. Use a distinct name such as `{changeVersion:int}`. Likewise avoid colliding with `[controller]` or other ambient tokens.
- This whole class of bug (duplicate route params, ambiguous templates, bad constraints) is a **runtime route-binding failure, not a compile error**: `dotnet build` stays clean, and unit tests that call controller action methods directly bypass routing, so they pass too. It only surfaces when the app actually boots. **After adding or renaming any API route, validate by starting the app** (`jim-build-light` / `jim-stack`) or running an integration test or the OpenAPI generation; do not rely on `dotnet build` + method-level unit tests alone.

**Adding a navigation property to an entity? Generate the OpenAPI document before you push.**

```bash
./scripts/Generate-OpenApiDoc.ps1 -NoBuild -OutputPath /tmp/openapi-v1.json
```

(`jim-openapi-generate` in the devcontainer. About 90 seconds against an already-built solution.)

The generator walks the property graph of every response type. Where a cycle runs through a type that does not get its own entry in `components/schemas`, the type is inlined and re-expanded rather than referenced, until the walk hits System.Text.Json's 256-level depth limit and fails the whole document. No document means no `jim.web` image and no release, and nothing else catches it: `dotnet build` is clean, every unit test passes, and the only failing check is `openapi-document`, which takes about five minutes in CI. This has now shipped twice, both times through a new child entity holding a reference back to its parent: #1238 via `ConnectedSystemObjectTypeTag`, then #1277 via `ConnectedSystemObjectTypeExtension`.

- **A back-reference on a child entity gets `[JsonIgnore]`.** If a type is only ever reached as an element of its parent's collection, the navigation pointing back at that parent (or at any other type carrying the same collection) is never serialised; callers have the foreign key. Both of the above were fixed exactly this way.
- **Do not reason about it from "does this introduce a cycle".** Plenty of cycles are fine: `ConnectedSystem` and `ConnectedSystemObjectType` point at each other and always have, because both are registered as components and so resolve to a `$ref`. Whether a given cycle survives depends on which types the generator decides to register, which is its own internal policy.
- **There is no unit test that substitutes for this, and it is not for want of trying.** Calling `JsonSchemaExporter` directly over the same types does not reproduce the generator's behaviour: it inlines everything, so it throws for `ConnectedSystem` at every `MaxDepth` from 64 to 2048 while the real document generates that type without complaint. A guard built on it would fail on models that are perfectly fine. Run the generator; it is the only faithful check short of CI.

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
5. Run the unit tests: `BulkInsertColumnCompletenessTests` will fail for any table whose raw SQL writers the new column must be added to (see "Raw SQL Column Lists" above). Extend the named `*BulkColumns` constant AND the corresponding writers (values in list order), place the column consciously in the update or exclusion list, and extend the table's `RequiresPostgres` round-trip test to assert the new column persists. A schema change is not complete until these pass.
6. Commit migration files

**If `main` gains migrations while yours is unmerged, regenerate yours.** A migration's `.Designer.cs` is a snapshot of the *whole* model at that point, and `dotnet ef migrations remove` restores `JimDbContextModelSnapshot.cs` from the **previous** migration's Designer. A Designer scaffolded before someone else's migration landed is missing their columns, so a later routine add-then-remove silently deletes those columns from the snapshot, and the next migration generated from it tries to re-add a column the database already has. Nothing fails while this happens. After merging `main` into your branch, `dotnet ef migrations remove` and re-add your migration so its Designer snapshots the merged model. `MigrationDesignerChainTests` (JIM.Worker.Tests) fails the build if you forget, naming the columns; see issue #1379.

**CRITICAL: NEVER flatten, squash, delete, or reset EF Core migrations.** Migrations are append-only. Deployed instances track applied migrations by name in `__EFMigrationsHistory`; removing old migrations and replacing them with a combined migration will break every existing deployment.

**Updating Architecture Diagrams:**

When making architectural changes (new containers, components, connectors, or significant restructuring):
1. Update the affected hand-authored SVGs under `docs/assets/diagrams/` (System Context, Containers, and the component views for the Application Layer, Web Application, Worker, Connectors and Scheduler)
2. If `system-context.svg` or `containers.svg` changed (or the diagram tokens in `custom.css` did), regenerate the README exports: `pwsh ./scripts/Export-ReadmeDiagrams.ps1`
3. Commit the diagram changes together with the code change

> **Authoring conventions:** `docs/CLAUDE.md` > Concept Diagrams; **visual language:** `engineering/DESIGN.md` > Diagrams
