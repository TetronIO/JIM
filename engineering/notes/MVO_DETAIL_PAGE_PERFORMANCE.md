# MVO Detail Page Performance: Eager Change History Load

- **Status:** Done
- **Branch:** `feature/perf-instrumentation-mvo-detail`
- **Implementation commit:** `09e6806a`

> Investigation of the slow load on the Metaverse Object detail page (e.g. `/t/groups/v/{id}`), the chosen remediation, and the validation results.

## Symptom

The Metaverse Object detail page used to load quickly for groups of this size and is now noticeably slow. Reproduction case: `/t/groups/v/430aa262-a4b7-4a5f-9665-60caa877fe44`.

## Quantification

Diagnostic spans were added on a feature branch (`feature/perf-instrumentation-mvo-detail`, commit `e232cb64`) covering `MetaverseServer.GetMetaverseObjectDetailAsync` and the five distinct DB round-trips inside the repository's CappedMva path. The `DiagnosticListener` had to be wired into `JIM.Web` startup (it was previously only enabled in `JIM.Worker`); no spans from the web app reached the logs prior to that fix.

Observed timings, two consecutive loads of the same page (`JIM_LOG_LEVEL=Debug`):

| Span | Load 1 | Load 2 |
|---|---:|---:|
| `Mvo.LoadShellWithChanges` | **1539 ms** | **1225 ms** |
| `Mvo.LoadSvaValues` | 248 ms | 169 ms |
| `Mvo.LoadCappedMvaValues` (1 attr) | 48 ms | 4 ms |
| `Mvo.LoadAttributeCounts` | 16 ms | 6 ms |
| `Mvo.LoadAttributePluralities` | 9 ms | 5 ms |
| `Mvo.GetTypeByPluralName` | 30 ms | 1 ms |
| **`Mvo.GetDetail` total** | **1863 ms** | **1409 ms** |

The dominant cost is the shell-with-changes query at [`MetaverseRepository.cs:377-399`](../../src/JIM.PostgresData/Repositories/MetaverseRepository.cs#L377-L399); it accounts for ~80% of the total. Even on the warm second load it is 1.2s, so this is not a cold-cache effect; it is the work itself. Attribute-value loads (SVA + capped MVA) are fast.

The slow query loads the MVO together with its full change history graph, eagerly including:

- `Changes` → `AttributeChanges` → `Attribute`
- `Changes` → `AttributeChanges` → `ValueChanges` → `ReferenceValue` → `Type`
- `Changes` → `AttributeChanges` → `ValueChanges` → `ReferenceValue` → `AttributeValues` (filtered to `DisplayName`) → `Attribute`
- `Changes` → `SyncRule`
- `Changes` → `ActivityRunProfileExecutionItem` → `Activity`

…using `AsSplitQuery` and `AsTracking` (the latter required only because of the cycle introduced by the DisplayName filter-Include). Every change row materialises a deep entity graph; every value-change reference materialises a target MVO entity plus its DisplayName attribute value plus its attribute definition.

## Consumer audit

Only one consumer reads bulk MVO change history today:

| Consumer | Path | Today |
|---|---|---|
| **UI: `Pages/Types/View.razor`** | `GetMetaverseObjectDetailAsync` (CappedMva) | Loads ALL changes eagerly via the Include chain above. The slow path. |
| **API: `GET /api/v1/metaverse/objects/{id}`** | `GetMetaverseObjectAsync` → `MetaverseObjectDto` | Does not expose change history at all. No Includes, no DTO field. |
| **PowerShell: `Get-JIMMetaverseObject`** | calls the API endpoint above | No access to change history. |
| `Admin/DeletedObjects.razor` | separate `GetDeletedMvoChangeHistoryAsync` path | Unaffected — different code path. |
| `ActivityRunProfileExecutionItemDetail.razor` | accesses one `MetaverseObjectChange` from a single RPEI | Unaffected — different direction (RPEI → change), single row. |

The slow page is the only consumer of bulk MVO change history. The API/PowerShell gap is a real product gap (no audit-trail access for automation/compliance), not a parity break the proposed fix would introduce.

## Field usage

`View.razor` projects each change into `ChangeHistoryTimeline.ChangeGroup` ([`View.razor:259-309`](../../src/JIM.Web/Pages/Types/View.razor#L259-L309)). The fields actually consumed are scalars and a flat list of value-change scalars:

- Change row: `ChangeType`, `ChangeTime`, `InitiatedByType`, `InitiatedByName`, `InitiatedById`, `ChangeInitiatorType`, `SyncRuleId`, `SyncRuleName`, `ActivityRunProfileExecutionItemId`
- From `ActivityRunProfileExecutionItem.Activity`: `TargetName`, `ConnectedSystemId`, `TargetContext`, `ConnectedSystemRunType` (4 scalars; full `Activity` not needed)
- From `ActivityRunProfileExecutionItem`: `ConnectedSystemObjectId`, `ExternalIdSnapshot`
- Per attribute change: `AttributeName`, `AttributeType`, `Attribute?.AttributePlurality` (just the enum, not the full attribute entity)
- Per value change: `ValueChangeType`, primitive values (`StringValue`, `IntValue`, etc.), and IF reference: `ReferenceValueId`, reference `DisplayName`, reference type `Name` and `PluralName` (for href construction)

Expensive Includes that are loaded today and not required: the full `MetaverseAttribute` entity, the full `Activity` entity, the full reference-target `MetaverseObject` plus its `AttributeValues` filtered to `DisplayName`, plus the cycle-tracking that comes with `AsTracking`. All of that materialises hundreds of EF entities just to read a handful of strings off each.

## Recommendation

A coordinated four-piece change. Each piece is small; together they fix the symptom, eliminate a latent time-bomb (change history grows monotonically with sync runs), and close the API/PowerShell audit-trail gap.

### 1. New change-history DTO + repository projection

`MvoChangeHistoryDto` with nested `MvoAttributeChangeDto` and `MvoValueChangeDto`, filed under `JIM.Models/Core/DTOs/`. Implemented as a single EF `Select(...)` projection (or raw SQL if EF generates poor SQL — measure first). No `AsTracking`, no Include chains, no DisplayName filter-Include. Returns only the fields the timeline actually consumes.

Matches the existing **Detail/Header** retrieval taxonomy in [`src/CLAUDE.md`](../../src/CLAUDE.md): a flat DTO projection sized to the consumer.

### 2. Paginate it

New `GetMvoChangeHistoryAsync(Guid mvoId, int page, int pageSize)` returning `(List<MvoChangeHistoryDto>, int totalCount)`.

The change history table grows linearly with sync runs. Even if today's MVOs have 200 changes, a long-lived deployment will see 5,000+ on heavily-synced groups. Loading them all on page open is a perf time-bomb. Default page size 50, ordered by `ChangeTime DESC`.

### 3. Lazy-load on tab activation

`View.razor` stops asking for changes in `OnParametersSetAsync`. The Changes tab loads page 1 on first activation; "Load more" or a pager appends additional pages. The Details and Properties tabs render instantly.

### 4. Surface it via API + PowerShell

For parity, and as a useful new capability:

- `GET /api/v1/metaverse/objects/{id}/change-history?page=N&pageSize=M&sortDirection=desc` → `PaginatedResponse<MvoChangeHistoryDto>`
- New cmdlet `Get-JIMMetaverseObjectChangeHistory -Id <guid> [-All | -Page N -PageSize M]` (matches the existing `Get-JIMMetaverseObject` shape)

## Expected outcomes

- Detail page: ~1.5s → likely 50–150ms (eliminates the dominant span; remaining attribute-value loads are already fast)
- Changes tab: bounded to one page (~50 rows) regardless of MVO chattiness; loads in tens of ms
- API/PowerShell: gain a first-class change-history capability instead of "we don't expose that"
- Architecture: matches the existing Detail/Header DTO taxonomy. No layer violations.

## Cost

Roughly half a day. Files touched:

- 1 new DTO file in `JIM.Models/Core/DTOs/`
- 1 new repository method (+ interface entry in `IMetaverseRepository`)
- 1 new server method on `MetaverseServer`
- 1 new API controller action on `MetaverseController`
- 1 new PowerShell cmdlet under `JIM.PowerShell/Public/Metaverse/`
- 1 modified Razor page (`Pages/Types/View.razor`)
- Tests for the API endpoint and the projection

## Considered alternatives

**A. Drop change-history Includes from the existing detail call and add a separate `GetMvoChangeHistoryAsync` (no DTO, return entities).** Smaller change; still removes the eager-load. Rejected: keeps the entity-materialisation overhead, doesn't address the time-bomb, and skips API/PowerShell parity.

**B. DTO projection only, no pagination, no lazy-load.** Smaller change; still removes the bulk of the cost via projection. Rejected: still loads ALL changes upfront, so the page degrades again as change count grows. Half-measure.

**C. Combined (recommended above).** Aligns with all three quality axes (UX, architecture, performance) per [`src/CLAUDE.md`](../../src/CLAUDE.md) and surfaces a useful new API capability.

## Decisions taken at implementation time

The four open questions resolved as follows:

1. **Scope:** all four pieces shipped in a single PR (commit `09e6806a`). Splitting would have left the API/PowerShell gap open and introduced cross-PR coupling for the DTO contract.
2. **Page size:** default 50, clamped to [1, 100] at the server. Matches the shape of the existing `Get-JIMMetaverseObject` cmdlet pagination.
3. **Sort:** always DESC by `ChangeTime`. No `sortDirection` query parameter; can be added later if asked.
4. **Count badge:** the badge needs no lazy-load. `MvoDetailResult` gained a `ChangeCount` field (single `COUNT(*)` against the same FK index used by the page query) plus `EarliestChangeInitiator` / `LatestChangeInitiator` summaries so the Properties tab still shows "Created By" / "Last Updated By" without round-tripping the change rows.

## Validation (post-implementation)

After `09e6806a`, five page loads of the same group MVO (`JIM_LOG_LEVEL=Debug`):

| Span | Before | After (cold, 1st hit) | After (warm, range) |
|---|---:|---:|---:|
| `Mvo.LoadShell` (formerly `LoadShellWithChanges`) | 1225–1539 ms | **1.0 ms** | 0.7–1.5 ms |
| `Mvo.LoadChangeCountAndInitiators` (new) | n/a | 6.4 ms | 2.1–5.4 ms |
| `Mvo.LoadAttributeCounts` | 6–16 ms | 60.2 ms | 7.3–8.8 ms |
| `Mvo.LoadAttributePluralities` | 5–9 ms | 5.1 ms | 3.2–6.8 ms |
| `Mvo.LoadSvaValues` | 169–248 ms | **1032.8 ms** [SLOW] | 23.3–278.9 ms |
| `Mvo.LoadCappedMvaValues` (1 attr) | 4–48 ms | 52.8 ms | 2.8–8.3 ms |
| **`Mvo.GetDetail` total** | **1409–1863 ms** | **1159 ms** | **47.7–305.4 ms** |
| `Mvo.GetChangeHistory` (lazy, only when Changes tab opened) | n/a | 217 ms | 94–217 ms |

Headline: `Mvo.LoadShell` went from ~1.2–1.5 s to **0.7–1.5 ms (~1000× faster)**. This is the dominant gain and confirms the change-history Include chain was the entire structural problem.

Warm steady-state `Mvo.GetDetail` is now **47–305 ms**, a 5–25× improvement over the previous 1.4–1.9 s. When the user does not click the Changes tab (run 2 of the captured set), no change-history work runs at all, so the worst-case cost only applies when the user actually wants the data. Worst-case path (cold first hit + immediate Changes tab click) is 1159 + 217 = ~1376 ms, which is still no worse than the previous unconditional path.

The `[SLOW]` log entries that remain after this change come from a different span (`Mvo.LoadSvaValues`), not the change-history path that this work targeted.

## Follow-up: cold-cache spike on `Mvo.LoadSvaValues`

The first page load after a container restart shows `Mvo.LoadSvaValues` at ~1033 ms, with subsequent warm hits dropping to 23–279 ms. The original investigation captured the same query at 169–248 ms (warm). The cold-vs-warm pattern looks like an Npgsql connection-pool / EF compiled-model warmup effect rather than a structural regression.

It is outside the scope of this branch and does not negate the change-history fix. If it becomes a user-visible problem on production cold starts, candidate next steps are:

- Extend `Warming up EF Core model` in `JIM.Web/Program.cs` to also pre-execute the SVA shape against a representative MVO.
- Profile the SVA query at cold start (cross-reference `Mvo.LoadSvaValues` span tags) to see whether the cost is in plan generation, statistics population, or the query itself.
- Consider whether the SVA query needs its own raw-SQL path (per the worker hot-path guidance in `src/CLAUDE.md`); for a UI read this is probably overkill, but worth measuring before deciding.

This is a separate engineering task; no action taken on this branch.
