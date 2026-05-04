# MVO Detail Page Performance: Eager Change History Load

> Investigation of the slow load on the metaverse object detail page (e.g. `/t/groups/v/{id}`) and a proposed remediation. Pre-decision; presented for review before any implementation work begins.

## Symptom

The metaverse object detail page used to load quickly for groups of this size and is now noticeably slow. Reproduction case: `/t/groups/v/430aa262-a4b7-4a5f-9665-60caa877fe44`.

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

## Open questions for the reviewer

1. Scope: do all four pieces in one PR, or split (UI + DTO first, API/PowerShell as a follow-up)?
2. Page size: default 50 reasonable, or different given typical change-row size?
3. Sort: should the API expose `sortDirection` from day one, or always DESC and add it later if asked?
4. Should the Changes tab render a count badge before the lazy-load fires? (We already have `MetaverseObject.Changes.Count` from the eager-load today; the DTO approach loses that unless we surface a separate `totalCount` from the paginated call, which is in the proposed signature already.)

## Branch state at the time of writing

- Branch: `feature/perf-instrumentation-mvo-detail`
- Commit `e232cb64`: instrumentation only (spans + listener wiring in `JIM.Web`). No behaviour change.
- Nothing from the recommendation above is implemented yet.
