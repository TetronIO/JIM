# Metaverse Object Connection Explanations

- **Status:** Planned
- **Issue:** [#348](https://github.com/TetronIO/JIM/issues/348)
- **PRD:** [`../prd/PRD_METAVERSE_OBJECT_CONNECTION_EXPLANATIONS.md`](../prd/PRD_METAVERSE_OBJECT_CONNECTION_EXPLANATIONS.md)
- **UI mockups:** [MVO Connections Mocks](https://claude.ai/artifact/4Tj5DYpEMR7c8g9pSoAqD9) (board 1B chosen)
- **Related:** [#1519](https://github.com/TetronIO/JIM/issues/1519) Connections tab and Sync Preview, [#399](https://github.com/TetronIO/JIM/issues/399) value provenance, [#204](https://github.com/TetronIO/JIM/issues/204) scope management enhancements, [#1463](https://github.com/TetronIO/JIM/issues/1463) group-based scoping, [`../prd/PRD_SCOPING_CRITERIA_EVALUATION_MATRIX.md`](../prd/PRD_SCOPING_CRITERIA_EVALUATION_MATRIX.md)
- **Last Updated:** 2026-10-05 (decisions: join record stored durably (D4 option B); interim Inspect / Changes restriction dropped, deferred with Activity page access to RBAC; multi-valued semantics filed as [#1923](https://github.com/TetronIO/JIM/issues/1923)); 2026-10-05 (initial plan)

## Overview

The Connections tab gains two answers it cannot give today: **why** each joined connection exists (the recorded join, plus whether the person is still in scope of the relevant Synchronisation Rules), and **why not** for every Connected System that has an enabled export rule for the person's type but no joined object. Both rest on a new capability underneath: scoping evaluation that explains itself, criterion by criterion, through the same code path synchronisation uses. Hints, "To come into scope" bullets and a copyable plain-text summary are generated server-side so the portal, REST API and a new `Get-JIMMetaverseObjectConnection` cmdlet say exactly the same thing. Connected System Objects start recording the Synchronisation Rule that joined them, so "how it joined" stops depending on Activity history.

The PRD settles *what*; this plan settles *how*, and records four research findings that change parts of the PRD (all reflected there under Open Questions).

## Business Value

- Answers the top helpdesk escalation ("why hasn't Jane been provisioned to X?") from one page, in seconds, without reading rules by hand.
- Distinguishes data problems (a missing Cost Centre) from rule problems (a wrong comparison), which today needs an expert.
- A summary an administrator can paste into a ticket, identical across portal, API and PowerShell.
- Bulk reporting through PowerShell ("everyone out of scope of the Finance App rule, and why").
- A durable record of which Synchronisation Rule created each join, useful well beyond this page.
- The shared evaluator makes scoping behaviour testable in a far more direct way, which also benefits synchronisation.

## Technical Architecture

### Current state

- **Scoping evaluation** lives in `ScopingEvaluationServer` (`src/JIM.Application/Servers/ScopingEvaluationServer.cs`), a pure, dependency-free class with two near-duplicate paths: `IsMvoInScopeForExportRule` and `IsCsoInScopeForImportRule`, each with a recursive group evaluator and a per-criterion evaluator. Semantics worth preserving exactly:
  - Top-level groups are ORed; within a group, `All`/`Any` apply; an empty group is true; a rule with no groups is in scope.
  - Every child is evaluated (no short-circuit), so an invalid criterion anywhere throws.
  - A missing value (absent row, or an asserted-null row on the Metaverse side, #91) fails every comparison except `Equals` against an all-null absolute criterion; so `NotEquals` on a missing value is **false**.
  - Only the first value of an attribute is compared (`FirstOrDefault`).
  - Relative date criteria resolve against one `nowUtc` per evaluation.
  - `EnsureOperatorValidForType` throws `InvalidOperationException` for an operator invalid for the type (defence in depth behind the write path).
  - Every caller (worker, export evaluation, scope reconciliation, the preview adapters, deprovisioning, recall) goes through these two methods; there is no second implementation, in C# or SQL.
- **Rule loading for synchronisation** (`ConnectedSystemRepository.GetSyncRulesAsync`, around line 6017) loads every rule with about twenty split-query includes, and scoping groups only two levels deep (groups and their direct child groups, with criteria). The editor (`SyncRuleDetailScopingCriteriaGroup.razor`) offers "add group" inside every group, at any depth. See finding F2.
- **Connections** come from `MetaverseServer.GetMetaverseObjectConnectionsAsync` (`MetaverseServer.cs:1424-1492`): joined CSOs (core load, external ids only), Metaverse Object header, `GetSyncRuleHeadersAsync(typeId)` for IsSource/IsTarget (no scope evaluation), and one batched Pending Export lookup for State. DTO: `MetaverseObjectConnection` (`src/JIM.Models/Staging/DTOs/MetaverseObjectConnection.cs`). The portal loads it lazily on first activation of the `connections` slug (`View.razor:883-902`, `:1000-1009`); REST embeds it in `GET objects/{id}` (`MetaverseController.cs:1571-1593`) as `MetaverseObjectDto.ConnectedSystemObjects`; there is no connections endpoint and no connections cmdlet.
- **Join history** (finding F3): a CSO carries `JoinType` and `DateJoined` and nothing about the rule. Recoverable today:
  - **Projected**: the RPEI with `ConnectedSystemObjectId = cso && ObjectChangeType = Projected` (indexed column) gives the Activity; its root sync outcome carries `SyncRuleId`/`SyncRuleName`.
  - **Provisioned**: `PendingExport.ProvisioningSyncRuleId` while the Create is pending; afterwards the export RPEI for the CSO and its `ExportCreateStaged` causal edge carry the rule.
  - **Joined (inbound matching)**: the RPEI gives the Activity; the rule is recorded nowhere ("first rule to match wins", `SyncTaskProcessorBase.cs:5440-5444`).
  - **Joined (export matching)** (`ExportEvaluationServer.cs:2449-2452`): no Joined RPEI and no rule.
  - All of it ages out with Activity retention, and outcomes are not written at the None tracking level.
- **Export decisions** (finding F1, F4): provisioning is decided by `SyncEngine.DecideOutboundStaging` (`SyncEngine.ExportStaging.cs:48-130`). `ProvisionToConnectedSystem` is `bool?` and every check is `== true`, so null means "does not provision". Provisioning creates a joined CSO in `PendingProvisioning` immediately, so "provisioning under way" always has a joined CSO. An Object Type conflict (`DetectExportObjectTypeConflict`, `SyncEngine.ExportStaging.cs:169-191`) also requires a joined CSO of another type in the same Connected System, the single slot being enforced by `IX_ConnectedSystemObjects_ConnectedSystemId_MetaverseObjectId_Unique`. Export evaluation in the worker is queued only when inbound Attribute Flow changed the Metaverse Object (`SyncTaskProcessorBase.cs:2024-2027`).
- **Outbound preview** (`SyncPreviewServer.PreviewSyncForMvoAsync` → `ExportEvaluationServer.EvaluateOutboundPreviewForMaterialisedMvosAsync`, `:3152-3225`) drops out-of-scope, unjoined rules at `:3187` and quietly skips Object Type conflicts at `:3189`. It builds a full `ExportEvaluationCache` (every rule, deep includes) per request.
- **Page structure** (`View.razor`): page is `Roles = "User"`; the Connections and Password panels sit inside `AuthorizeView Roles="Administrator"` and load lazily by slug (`:1000-1009`). Deep links use `?t=<slug>` (`NavigableMudTabs.razor:224-229`). Change history, connections and their loaded flags are **not** reset when navigating from one Metaverse Object to another (`:657-682`); only provenance and Password state are.
- **API** controller is Administrator-only throughout; `GetObjectAsync` does not load Created By / Last Updated By (the portal gets them from `GetMetaverseObjectDetailAsync` on the `CappedMva` path only, `MetaverseRepository.cs:1443-1479`).
- **PowerShell**: `Get-JIMMetaverseObjectProvenance.ps1` is the template (comment help with `.OUTPUTS`, `-Id` with `ValueFromPipelineByPropertyName`, connection guard, `Invoke-JIMApi`). Tests in `src/JIM.PowerShell/Tests/Metaverse.Tests.ps1`; manifest exports in `JIM.psd1` (`# Metaverse` group). No format files or type names exist in the module.
- **Clipboard**: `wwwroot/js/interop.js` has `copyToClipboard`, gated on `window.isSecureContext`.
- **Tests**: `test/JIM.Web.Api.Tests/MetaverseObjectConnectionsTests.cs`, `MetaverseControllerObjectsTests.cs`, `MetaverseControllerProvenanceTests.cs`; `test/JIM.Web.Tests/MetaverseObjectConnectionsTableTests.cs`, `TypesViewAdministratorGateTests.cs` (source-text gate tests); `test/JIM.Worker.Tests/OutboundSync/ExportEvaluationTests.cs`, `SyncEngineTests/SyncEngineOutboundStagingTests.cs`, `SyncPreview/SyncPreviewServerTests.cs`.

### Research findings that change the PRD

| # | Finding | Effect |
|---|---|---|
| F1 | Export evaluation runs only when an object's attribute values change. A new export rule, provisioning switched on, or widened criteria do not provision already-in-scope objects until their data changes. | The PRD's "Provisions at next sync" reason was a false promise; renamed **Not yet provisioned**, worded truthfully. Whether the engine behaviour is a defect goes to its own issue once a test confirms it (Phase 0). |
| F2 | The synchronisation rule loader includes scoping groups two levels deep; the editor allows any depth. A third-level group would be missing at evaluation, and its parent could evaluate as an empty (met) group. | Probable synchronisation integrity defect. Must be confirmed (Phase 0) and, if real, fixed first in its own stack layer, because the explanation must load exactly the tree synchronisation loads. |
| F3 | No join rule is recorded on the CSO; history is partial and ages out. | Decided: record it durably on the CSO (D4), with derivation as the fallback for existing objects. |
| F4 | "Provisioning under way" and "Object Type conflict" always involve a joined CSO. | Both move to the joined rows' expansion; Not connected keeps three scope reasons plus Rule misconfigured. |
| F5 | Activity pages show per-run CSO and Metaverse Object changes to every signed-in user. | Decided: the planned interim Inspect / Changes restriction is dropped; all three are re-evaluated together in the RBAC work. |

### Proposed solution

```
                       ┌───────────────────────────── ScopingEvaluationServer ─────────────────────────────┐
 synchronisation ────► │ IsMvoInScopeForExportRule / IsCsoInScopeForImportRule  (bool, unchanged signatures) │
 (worker, previews,    │                    │                                                               │
  reconciler, ...)     │                    ▼                                                               │
                       │        ScopingEvaluator.Evaluate<TSource>(groups, source, nowUtc, trace: null)     │
                       │                    ▲                                                               │
 explanations ───────► │ ExplainMvoForExportRule / ExplainCsoForImportRule  (trace: ScopingTraceBuilder)    │
                       └───────────────────────────────────────┬───────────────────────────────────────────┘
                                                               │ ScopingExplanation (tree)
                                                               ▼
 MetaverseServer.GetMetaverseObjectConnectionExplanationsAsync(mvoId, includeNotConnected)
   1. joined CSOs + state (existing)          4. CSO values for import-rule criteria attributes (one query)
   2. scoping rules for the type (new, full   5. join records (recorded columns, else derived from history)
      depth, enabled, both directions)        6. explain per joined row; conflicts; not-connected reasons
   3. Metaverse Object values for criteria    7. ScopingExplanationSummariser: hint, bullets, plain text
      attributes, uncapped (one query)
                                                               │ MetaverseObjectConnectionExplanations
                     ┌─────────────────────────────────────────┼──────────────────────────────┐
                     ▼                                         ▼                              ▼
     View.razor Connections tab               GET objects/{id}/connections        Get-JIMMetaverseObjectConnection
     (row expansion, Not connected,           ?includeNotConnected=true            -Id -ConnectedSystemName
      Copy summary)                                                                 -IncludeNotConnected
```

### Key decisions

**D1. One evaluator, an optional trace.** `ScopingEvaluator` (internal, in `JIM.Application/Servers/Scoping/`) holds the only implementation of group and criterion evaluation. It is generic over a `struct` value source (`MvoScopingValueSource`, `CsoScopingValueSource`, both implementing `IScopingValueSource`: resolve the criterion's attribute, return the first evaluable value and the count of values), so there is no boxing or interface dispatch on the hot path. An optional `ScopingTraceBuilder` records each node's outcome; when it is null (every synchronisation call) nothing extra is allocated. The boolean methods keep their signatures and become thin wrappers; the explain methods pass a trace. Agreement is structural (same function), and tested anyway.

**D2. Invalid criteria.** The validity check becomes a `bool` helper both modes call. In boolean mode an invalid criterion still logs and throws exactly as today. In explain mode it is recorded as `Invalid`, evaluation continues so the rest of the tree is shown, and the rule outcome is `Undetermined`. Synchronisation behaviour is unchanged.

**D3. Rule and value loading for explanations.** A new repository method loads only what explanations need: enabled rules (both directions) for one Metaverse Object Type, with Connected System name and status, CSO type id and name, `ProvisionToConnectedSystem`, and the scoping tree **at full depth** with criterion attributes. It does not reuse `GetSyncRulesAsync` (far too heavy for a page load) but must build the tree with the **same** tree-loading helper synchronisation uses once F2 is resolved, so the two cannot diverge. Values: the Metaverse Object's values for the criteria attribute ids are loaded uncapped (the page's `CappedMva` object is not reused, since a capped multi-valued attribute could change what "first value" means), and joined CSOs' values for import-rule criteria attribute ids in one query.

**D4. Recording how a join came about (decided: option B, record durably).**
- *Option A, derive only:* new read-only lookups over RPEIs (by CSO id and change type), sync outcomes, `PendingExport.ProvisioningSyncRuleId` and `ExportCreateStaged` causal edges. No schema change; the joining rule shows "not recorded" for every inbound or export-matching join and for anything past retention.
- *Option B, record durably (chosen):* add `JoinSyncRuleId` (nullable FK, `ON DELETE SET NULL`) and `JoinSyncRuleName` (snapshot) to `ConnectedSystemObjects`, set at the four join sites (inbound match `SyncTaskProcessorBase.cs:~5440`, projection `~5505`, export match `ExportEvaluationServer.cs:~2449`, provisioning `~2817`) and cleared wherever a join is broken. Adding nullable columns is a metadata-only change in PostgreSQL, so the migration is cheap on large tables. Option A's derivation remains the fallback for existing rows, and supplies the Activity link in both options.
- Why B was chosen: "not recorded" on every joined account would make the feature look broken for the most common join type, and anything derived silently degrades with retention. The cost is one migration, four assignments, and making sure every raw-SQL CSO bulk insert and update carries the two columns (the main risk; see Risks).

**D5. Not-connected reasons.** For each enabled export rule of the type whose Connected System is not being deleted and holds no CSO joined to this Metaverse Object: evaluate; `Undetermined` gives **Rule misconfigured**; out of scope gives **Not in scope**; in scope with `ProvisionToConnectedSystem == true` gives **Not yet provisioned**; otherwise **Rule doesn't provision**. A disabled Connected System adds a qualifier to the hint. One entry per rule (two rules on one system are two entries), ordered by Connected System then rule name.

**D6. Joined-row conflicts.** For each joined CSO, any enabled export rule on the same Connected System for a different CSO type yields an Object Type conflict note on that row, using the existing static `DetectExportObjectTypeConflict` so the wording matches what synchronisation reports.

**D7. Summaries.** `ScopingExplanationSummariser` (pure) turns an explanation into: a hint (failing attribute names in tree order, `;` between `All` branches, `or` within an `Any` branch); structured bullets (segments typed as text, attribute, expected value, current value or no value, so the portal can style them while the plain text stays identical); and the plain-text summary. Operator wording is a single table (equals / must equal, before / must be before for dates, less than / must be less than for numbers, and so on). Values are formatted culture-invariantly in en-GB style; the evaluation time is rendered in UTC with a "UTC" suffix so the text is identical on every surface. Credential attribute values render as "(hidden)".

**D8. API shape.** `GET api/v1/metaverse/objects/{id}/connections?includeNotConnected=false` returns `MetaverseObjectConnectionExplanationsDto` (`metaverseObjectId`, `displayName`, `evaluatedAt`, `connections[]`, `notConnected[]` or null). Each explanation carries both the tree (`groups`) and a flat `criteria[]` with `path`, `met`, `outcome`, so clients need not walk the tree. `not-connected` is opt-in to match the cmdlet. 404 when the Metaverse Object does not exist (header check first, as `change-history` does).

**D9. Cmdlet shape.** `Get-JIMMetaverseObjectConnection -Id <Guid> [-ConnectedSystemName <string>] [-IncludeNotConnected]`. `-Id` is the Metaverse Object id with `ValueFromPipelineByPropertyName`, following `Get-JIMMetaverseObjectProvenance`; the cmdlet is read-only, so the child-noun alias hazard in `src/JIM.PowerShell/CLAUDE.md` does not bite. It emits one object per connection or not-connected entry with a uniform top level (`ConnectedSystem`, `Object`, `Role`, `Join`, `State`, where State is the reason for a not-connected entry), plus `Scoping`, `Hint`, `Bullets`, `Summary`. `-ConnectedSystemName` filters client-side, case-insensitively, and errors when nothing matches. The module has no format files, so docs use `Format-Table` explicitly.

**D10. Portal structure.** `MetaverseObjectConnectionsTable` gains MudTable child-row expansion (`ChildRowContent`). New shared components: `ScopingExplanationView` (the tree, used by joined and not-connected expansions), `ScopingBulletList`, `MetaverseObjectNotConnectedSection`. Joined explanations load with the connections (one call); the Not connected section loads on first expansion. Copy summary uses `interop.copyToClipboard`; where the clipboard is unavailable (plain HTTP, no secure context) it opens a dialog with the text pre-selected instead of failing silently, and confirms either way with a snackbar.

### Data model

New types in `JIM.Models` (`Logic/Scoping/` for explanations, `Staging/DTOs/` for connection results):

```csharp
public enum ScopingCriterionOutcome { Met, NotMet, NoValue, AttributeMissing, Invalid }
public enum ScopingRuleOutcome { InScope, OutOfScope, Undetermined }

public sealed class ScopingExplanation
{
    public int SyncRuleId; public string SyncRuleName; public SyncRuleDirection Direction;
    public ScopingRuleOutcome Outcome; public bool HasCriteria; public DateTime EvaluatedAt;
    public List<ScopingGroupExplanation> Groups;          // top-level groups, ORed
}
public sealed class ScopingGroupExplanation
{
    public string Path; public SearchGroupType Type; public bool? Met;   // null when undetermined
    public int MetCount; public int ChildCount;
    public List<ScopingCriterionExplanation> Criteria; public List<ScopingGroupExplanation> ChildGroups;
}
public sealed class ScopingCriterionExplanation
{
    public string Path; public int? AttributeId; public string? AttributeName; public AttributeDataType? AttributeType;
    public SearchComparisonType ComparisonType; public bool CaseSensitive;
    public string? ExpectedDisplay; public DateCriteriaValueMode ValueMode; public string? RelativeDisplay; public DateTime? ResolvedDate;
    public string? ActualDisplay; public bool Masked; public int AdditionalValuesNotEvaluated;
    public ScopingCriterionOutcome Outcome;
}

public enum NotConnectedReason { NotInScope, ProvisioningDisabled, RuleMisconfigured, NotYetProvisioned }
public enum JoinRecordSource { Recorded, Derived, NotRecorded }

public sealed class JoinRecord { /* JoinType, DateJoined, SyncRuleId?, SyncRuleName?, ActivityId?, RpeiId?, Source */ }
public sealed class ExplanationBullet { public List<ExplanationSegment> Segments; public string PlainText; }

public sealed class MetaverseObjectConnectionExplanation   // extends today's MetaverseObjectConnection fields
{ /* + DateJoined, JoinRecord, List<ScopingExplanation> Scoping, List<ExportObjectTypeConflict> Conflicts */ }
public sealed class NotConnectedEntry
{ /* ConnectedSystemId, ConnectedSystemName, ConnectedSystemStatus, SyncRuleId, SyncRuleName, ObjectTypeName,
     NotConnectedReason Reason, string Hint, List<ExplanationBullet> Bullets, string Summary, ScopingExplanation Scoping */ }
public sealed class MetaverseObjectConnectionExplanations
{ /* MetaverseObjectId, DisplayName, EvaluatedAt, Connections, NotConnected (null unless requested) */ }
```

Paths are one-based and dot-separated: top-level group, then child position, counting criteria before child groups (the evaluator's order).

Durable join record (D4): `ConnectedSystemObject.JoinSyncRuleId` (`int?`, FK to `SyncRules`, `SetNull`) and `JoinSyncRuleName` (`string?`), one migration, plus the in-memory repository mirror.

## Implementation Phases

Work lands as a stack (see `/stack-pr`), bottom-up. Phase 0 decides whether layer 1 exists.

### Phase 0: Verify the findings (tests only)

1. **F2, nested depth:** a `RequiresPostgres` database test that saves an export rule with a three-level scoping tree through the real repository, loads it with `GetSyncRulesAsync`, and asserts the tree is complete; plus a worker-level test showing the scope outcome a missing third level produces. Red confirms the defect.
2. **F1, provisioning trigger:** a worker test that creates an export rule with provisioning on for an existing, unchanged, in-scope Metaverse Object, runs a full synchronisation, and asserts whether a Create Pending Export is staged. This pins current behaviour either way; if it is not staged, file a separate issue (search first) and link it from the PRD, and word Not yet provisioned accordingly.
3. **Multi-valued first-value behaviour:** a unit test that pins today's semantics, so the explanation's "further values not evaluated" flag has a fixed reference. The semantics themselves are decided and fixed under [#1923](https://github.com/TetronIO/JIM/issues/1923).

### Layer 1 (conditional on Phase 0.1): Load scoping trees at full depth

A synchronisation integrity fix, in its own PR beneath the feature. Introduce one tree-loading helper (load all groups for a set of rules level by level, or with a recursive CTE, then assemble in memory), use it from `GetSyncRulesAsync` and the other rule loaders that include scoping groups (`ConnectedSystemRepository.cs:~6072`, `~6805`), and mirror it in `JIM.InMemoryData`. Phase 0.1's test goes green; synchronisation suites pass. Changelog: 🐛 fix entry.

### Layer 2: Reset per-object state when navigating between Metaverse Objects

A pre-existing defect the Connections work would inherit: following a reference from one person to another keeps the first person's connections and change history. Reset `_connections`, `_connectionsLoaded`, change history paging and (from Phase 5) explanation state alongside the provenance reset in `OnParametersSetAsync`. bUnit or source-text test first.

### Phase 1: Shared evaluator and explanation model

1. Explanation model types (above) in `JIM.Models`.
2. `ScopingEvaluator` with struct value sources and `ScopingTraceBuilder`; `ScopingEvaluationServer` boolean methods delegate to it; add `ExplainMvoForExportRule` and `ExplainCsoForImportRule`.
3. Tests first (`test/JIM.Worker.Tests/Scoping/` or alongside existing scoping tests):
   - Agreement: for every operator × applicable type × (value, missing value, asserted null) × case sensitivity × absolute/relative date, explanation outcome equals boolean result.
   - Shape: nested `All`/`Any` trees to depth 4, multiple top-level groups, empty groups, no groups; paths correct; `MetCount` correct.
   - Seeded randomised tree generator comparing both outputs over a few thousand cases.
   - Invalid criterion: boolean throws (unchanged); explanation returns Undetermined with the Invalid node; attribute missing; credential masking; additional values count.
4. Existing synchronisation suites (`JIM.Worker.Tests`, preview tests) pass unchanged; an allocation check (or BenchmarkDotNet-free micro-measure in a test) shows the boolean path allocates no more than before.

### Phase 2: Summariser

1. `ScopingExplanationSummariser` with the operator wording table, hint, bullets, plain text and UTC timestamp formatting.
2. Tests: one per PRD rule (All branches, Any branch, multiple top-level groups, missing value on positive and negated operators, relative dates, booleans, decimals, masked values), plus golden-text tests for the PRD's Scenario 1 summary.

### Phase 3: Connection explanations on the server

1. Repository: scoping rules for a Metaverse Object Type (D3), Metaverse Object values for attribute ids, CSO values for CSO ids and attribute ids; in-memory implementations.
2. Join records: the migration (`JoinSyncRuleId`, `JoinSyncRuleName`), the four write sites, the clear sites, every raw-SQL CSO insert and update column list, the in-memory repository mirror, and the derived lookups used as the fallback for existing objects and for the Activity link.
3. `MetaverseServer.GetMetaverseObjectConnectionExplanationsAsync(mvoId, includeNotConnected)` composing joined rows (state as today, join record, scoping per relevant rule, conflicts) and not-connected entries (D5), with summaries (D7).
4. Tests (`test/JIM.Web.Api.Tests/` alongside `MetaverseObjectConnectionsTests.cs`): each reason, disabled and deleting Connected Systems, two rules on one system, conflicts, provisioning-under-way rows excluded from Not connected, join records by type and by source (Recorded, Derived, NotRecorded), bounded query count. Worker tests that each join site records the rule and that breaking a join clears it; a database test that round-trips both columns through every bulk path; a test that deleting the rule nulls the reference and keeps the name.

### Phase 4: REST and PowerShell

1. `GET objects/{id}/connections` and DTOs (`src/JIM.Web/Models/Api/MetaverseObjectConnectionExplanationDtos.cs`); controller tests for 200, 404, `includeNotConnected` both ways, flat criteria paths; an `[Authorize]` attribute assertion in the style of `SystemControllerTests.cs:470-482`.
2. Created By and Last Updated By: a lightweight initiator lookup reused by `GetObjectAsync`, added to `MetaverseObjectDto`; DTO and controller tests.
3. `Get-JIMMetaverseObjectConnection.ps1`, manifest export, Pester tests (parameter sets, validation, requires connection, request binding including `-IncludeNotConnected`, pipeline from `Get-JIMMetaverseObject`, `-ConnectedSystemName` match and no-match, help documentation), module export test.

### Phase 5: Portal

1. `ScopingExplanationView`, `ScopingBulletList`, child-row expansion in `MetaverseObjectConnectionsTable`, `MetaverseObjectNotConnectedSection`, Copy summary with clipboard fallback dialog; wire into `View.razor` via the new server method.
2. bUnit tests (`test/JIM.Web.Tests/`): expansion renders join record and scoping; Not connected collapsed by default with count; each reason's chip and hint; bullets only in the expansion; empty state; Copy summary invokes interop with the server's text; fallback dialog when unavailable. Assert behaviour and logic-produced text, not presentation (per the cosmetic-change rule).
3. A Claude artefact screenshot of the built tab for review (UI-change rule).

### Phase 6: Documentation, changelog, verification

1. Docs per the PRD's Documentation Impact table, including `engineering/SYNC_RULE_SCOPING.md` (shared evaluator, explanation path, the missing-value and top-level-OR semantics stated plainly).
2. Changelog: ✨ Connections tab explanations and Not connected (#348); ✨ `Get-JIMMetaverseObjectConnection` and the connections endpoint, if not folded into the first; 🐛 for layer 1 and layer 2 in their own PRs.
3. Runtime verification on the stack (`pwsh ./scripts/Start-SandboxStack.ps1` in a sandbox, the devcontainer otherwise): seed data, create the Scenario 1 rules, run synchronisations and confirm new joins record their rule in the database, open a Metaverse Object's Connections tab, exercise the endpoint and the cmdlet, and confirm query counts in the logs. Consider an integration scenario step asserting a Not in scope explanation end to end, if Scenario 010 can host it cheaply.
4. Move PRD and plan to `done/` with Status Done in the PR that closes #348.

## Success Criteria

- Every PRD acceptance criterion ticked.
- Agreement tests and the randomised comparison pass; synchronisation suites pass unchanged; no measurable change in synchronisation duration on a Medium template run.
- Opening the Connections tab issues a fixed number of queries independent of rule and criterion counts.
- A helpdesk question of the PRD's Scenario 1 shape is answered from the page in under ten seconds, including copying the summary.

## Benefits

- **Operational:** the most common identity question answered without expert rule reading; summaries that travel.
- **Correctness:** one evaluator for synchronisation and explanation; Phase 0 and layer 1 close a probable silent scope-widening defect.
- **Architecture:** the explanation primitive is reusable by #204 (scope validation), the scoping evaluation matrix scenario (a no-commit evaluation path), and future criterion kinds (#1463).
- **Auditability:** every new join records the Synchronisation Rule responsible.

## Dependencies

- No new packages or services.
- One EF Core migration (D4).
- Phase 0's database test needs Docker (`RequiresPostgres`); in a cloud sandbox, start the daemon by hand if the session hook's warning is real.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Refactoring the evaluator changes a synchronisation outcome | Boolean signatures unchanged; agreement and randomised tests; every synchronisation suite must pass unchanged; struct sources and null trace keep the hot path allocation-neutral |
| Explanation loads a different tree or different values from synchronisation | One tree-loading helper for both (layer 1); uncapped value load for criteria attributes; Phase 0 pins first-value semantics |
| D4 join columns silently dropped by raw-SQL bulk CSO writes | Enumerate every raw-SQL insert and update of `ConnectedSystemObjects` before writing code; database tests that round-trip the columns through each bulk path |
| Summary wording misleads (negated operators, missing values, nested alternatives) | Wording table in one place, golden-text tests per PRD rule, review of real summaries during runtime verification |
| Credential or sensitive values leak through explanations or copied text | Masking at explanation-build time (not in the UI); tests for explanation, summary, API and cmdlet output |
| Clipboard unavailable on HTTP deployments | Fallback dialog with pre-selected text |
| Large estates: many export rules per type | Evaluation is in memory over a bounded load; Not connected loads on demand |
| The Not yet provisioned wording goes stale if the engine behaviour (F1) is changed later | The wording is generated server-side from one place, linked to the F1 issue, so the fix updates it in the same PR |
