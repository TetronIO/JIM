# Source Code Reference

> Detailed coding conventions, architecture rules, and development tasks for `src/`. See root `CLAUDE.md` for behavioural rules and guardrails.

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

**DateTime Handling (IMPORTANT):**
- Always use `DateTime` type (not `DateTimeOffset`) in models
- Always use `DateTime.UtcNow` for current time - NEVER use `DateTime.Now`
- PostgreSQL stores DateTime as `timestamp with time zone` (internally UTC)
- **Runtime quirk**: Npgsql returns `DateTimeOffset` when reading from database, even though model properties are `DateTime`
- Code that processes DateTime values from the database must handle BOTH `DateTime` and `DateTimeOffset` types
- See `DynamicExpressoEvaluator.ToFileTime()` for an example of handling both types
- This design choice maintains database portability (MySQL, SQL Server, etc. handle DateTimeOffset differently)

**Raw SQL Nullable Parameters (CRITICAL):**
- NEVER use `(object?)value ?? DBNull.Value` as a parameter to `ExecuteSqlRawAsync` or `ExecuteSqlInterpolatedAsync`
- EF Core cannot infer the PostgreSQL type from bare `DBNull.Value`, causing: `InvalidOperationException: The current provider doesn't have a store type mapping for properties of type 'DBNull'`
- ALWAYS wrap nullable parameters with a typed `NpgsqlParameter`: `NullableParam(value, NpgsqlTypes.NpgsqlDbType.Text)` (see helper method in `ConnectedSystemRepository`, `ActivitiesRepository`, `MetaverseRepository`)
- This applies to ALL nullable columns in raw SQL INSERT/UPDATE statements: string, int, Guid, DateTime, bool, etc.

**Exception Handling:**
- NEVER use generic `catch` or `catch (Exception)` clauses; always catch a specific exception type. This applies even in diagnostic/telemetry code that "should never break callers" - enumerate the concrete failure modes for the operation instead.
- For file-open code paths (`FileStream`, `Directory.CreateDirectory`, `Path.*`), the expected set is `UnauthorizedAccessException`, `IOException`, `ArgumentException`, `NotSupportedException`, `System.Security.SecurityException`.
- When several catches share identical fallback behaviour, extract a small private helper (e.g. `FailOpen(path, ex)`) and call it from each typed catch - keeps the catches specific without duplicating the handler body.
- For JS interop retry patterns in `OnAfterRenderAsync` (e.g. loading user preferences), catch `InvalidOperationException` specifically; this is the exception Blazor throws when JS interop is invoked before the runtime is ready

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

CodeQL runs on every PR via the github-code-quality bot and comments on rule violations. Write code that avoids its common triggers up front rather than fixing after review:

- **Unused loop variables**: do not write `foreach (var x in collection)` when `x` is never read. CodeQL flags this as "Useless assignment to local variable" (`cs/useless-assignment-to-local`). Use `for (var i = 0; i < collection.Count; i++)` when you only need iteration count, or refactor to actually use the variable.
- **Redundant null-conditional**: do not use `?.` on a variable that has already been null-checked with an early return. The `?.` is redundant once control flow guarantees non-null, and CodeQL flags it.
- **Implicit filter in `foreach`**: do not write `foreach (var x in xs) { if (predicate) ... }` - CodeQL flags this as "Missed opportunity to use Where". Push the predicate into the iterator: `foreach (var x in xs.Where(x => predicate)) ...`. This applies whenever the body's first (or only) statement is an `if` whose single branch acts on `x`; the guard should move into the sequence so the loop iterates only matching elements.

**Nullable Dereference in Razor:**
- When accessing a nullable `.Value` property in Razor markup (e.g. `context.LastUpdated.Value`), capture it into a local variable inside the `@if (x.HasValue)` block: `var lastUpdated = context.LastUpdated.Value;` then use the local variable in markup expressions. This avoids repeated nullable dereference warnings from code analysis.

**File Organisation:**
- One class per file - each class should have its own `.cs` file named after the class
- Exception: Enums are grouped into a single file per area/folder (e.g., `ConnectedSystemEnums.cs`, `PendingExportEnums.cs`)
- File names must match the class/interface name exactly (e.g., `MetaverseObject.cs` for `class MetaverseObject`)
- **Model placement**: All model/POCO/result classes MUST live in `JIM.Models/`; NEVER define them inline in service or server files in `JIM.Application` or other projects
  - Exceptions: UI-specific models may live in `JIM.Web/Models/`, and API DTOs in `JIM.Web/Models/Api/`
  - If a service method needs a result type, create it as its own file in the appropriate `JIM.Models/` subdirectory

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

**Razor Comments:**
- **Section headers**: Use box-drawing delimiters: `@* ─── Section Title ─── *@` (U+2500 horizontal box-drawing character). One line, standing alone between markup blocks, to visually separate major page sections.
- **Inline comments**: Use plain comments: `@* Explanation of what follows *@`. Brief, contextual, placed immediately above or beside the relevant markup.
- Do NOT use multi-line banner comments (`===`, `amamam`, or similar filler characters). One line is enough.

**Tabs:**
- Use `<NavigableMudTabs>` instead of `<MudTabs>` for all top-level page tabs; it syncs the active tab with a `?t=slug` query string, enabling browser back/forward navigation
- Use plain `<MudTabs>` only for tabs inside dialogs or nested sub-tabs where URL navigation is not needed

**Alerts:**
- ALWAYS use `Variant="Variant.Outlined"` on all `<MudAlert>` components
- This ensures a consistent outlined style across the entire UI

**Panel Spacing (target: uniform `mt-6` visual gaps between all block-level sections):**
- Use `Class="pa-4 mt-6"` on `<MudPaper Outlined="true">` panels to ensure consistent vertical spacing between sections
- Exception: the **first** panel on a page should omit `mt-6` (use just `Class="pa-4"`) so there is no unnecessary top margin
- **After intro text**: `MudText` with `Typo.subtitle1` renders as a `<p>` with its own bottom margin (~16px). The first panel after intro text should use `mt-4` (not `mt-6`) so the combined gap matches `mt-6` visually
- **Tab content spacing**: Use `TabPanelsClass="pa-0 mt-6"` on `NavigableMudTabs` / `MudTabs` so the gap between tab headers and tab panel content matches the surrounding spacing
- **Tabs margin**: `NavigableMudTabs` may not honour `Class` for outer margin; wrap in `<div class="mt-6">` to guarantee spacing above the tab bar

**Tooltips:**
- ALWAYS use `Arrow="true" Placement="Placement.Top"` on all `<MudTooltip>` components
- This ensures tooltips appear above the element with a downward-pointing arrow, consistent across the entire UI

**UI Element Sizing:**
- ALWAYS use normal/default sizes for ALL UI elements when adding new components
- Text: Use `Typo.body1` (default readable size)
- Chips: Use `Size.Medium` or omit Size parameter entirely (defaults to Medium)
- Buttons: Use `Size.Medium` or omit Size parameter entirely (defaults to Medium)
- Icons: Use `Size.Medium` or omit Size parameter entirely (defaults to Medium)
- Other MudBlazor components: Omit Size parameter to use default sizing
- Only use smaller sizes (`Typo.body2`, `Size.Small`, etc.) when explicitly requested by the user
- Users prefer readable, appropriately-sized UI elements by default

**MudTable Row Density:**
- All new data tables should include a density toggle allowing users to switch between normal and compact row spacing
- Use `Dense="@_dense"` and `Class="@(_dense ? "dense-body-only" : "")"` on the `MudTable`; the `dense-body-only` CSS class keeps header rows at normal height while body rows are compact
- Add a `MudButton` with `StartIcon` (not `MudIconButton`, which renders circular) in the `ToolBarContent` to toggle density, using `Icons.Material.Filled.DensitySmall` / `DensityMedium`
- Persist the preference via `IUserPreferenceService.GetTableDenseAsync()` / `SetTableDenseAsync()` (stored in browser localStorage under a single shared key, so the setting applies globally across all pages)
- Default to normal spacing (`_dense = false`); load the saved preference in `OnAfterRenderAsync` with `catch (InvalidOperationException)` for JS interop retry
- See `Pages/Types/Index.razor` for the reference implementation

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

JIM follows strict n-tier architecture. Each layer may ONLY call the layer directly below it:

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

**Adding API Endpoint:**
1. Add method to controller in `JIM.Web/Controllers/Api/`
2. Use DTOs for request/response (in `JIM.Web/Models/Api/`)
3. Add XML comments for OpenAPI documentation
4. Test via the Scalar API reference at `/api/reference`

**Modifying Database Schema:**
1. Update entity in `JIM.Models/`
2. Create migration: `dotnet ef migrations add [Name] --project src/JIM.PostgresData`
3. Review generated migration
4. Test: `dotnet ef database update --project src/JIM.PostgresData`
5. Commit migration files

**CRITICAL: NEVER flatten, squash, delete, or reset EF Core migrations.** Migrations are append-only. Deployed instances track applied migrations by name in `__EFMigrationsHistory`; removing old migrations and replacing them with a combined migration will break every existing deployment.

**Updating Architecture Diagrams:**

When making architectural changes (new containers, components, connectors, or significant restructuring):
1. Update `docs/diagrams/structurizr/workspace.dsl` to reflect the change
2. Regenerate SVGs: `jim-diagrams` (requires Docker)
3. Commit both the DSL changes and regenerated SVG files together

> **DSL syntax and diagram details:** See `docs/diagrams/structurizr/README.md`
