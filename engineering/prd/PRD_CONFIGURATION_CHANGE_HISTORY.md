# Configuration Change History

- **Status:** Doing
- **Created:** 2026-06-25
- **Author:** JayVDZ
- **Issue:** [#14](https://github.com/TetronIO/JIM/issues/14)

## Problem Statement

JIM records *that* configuration objects change, but not *what* changed. Every configuration object (Connected System, Synchronisation Rule, Object Matching Rule, Metaverse Attribute, Metaverse Object Type, Service Setting, and so on) is `IAuditable` and produces an Activity capturing who acted, when, and the operation type. None of them capture the before and after of the change.

As a result, administrators and auditors cannot answer everyday governance questions: who changed this Synchronisation Rule's scope last week and what did they alter; what did this Connected System's configuration look like before the last edit; was this misconfiguration a recent change. Configuration changes are among the highest-impact actions in JIM (a single Synchronisation Rule edit can reshape thousands of identities) yet are currently the least traceable.

The equivalent capability for business and identity data (Connected System Objects and Metaverse Objects) was delivered under #269: per-object change timelines via the shared `ChangeHistoryTimeline`, per-type retention, and worker housekeeping cleanup. This PRD closes the remaining gap by extending that change-history capability to configuration objects, with full coverage across the UI, the REST API, and the PowerShell module.

## Goals

- Administrators can view a complete, version-ordered timeline of changes on any supported configuration object's detail page, showing what changed, when, and who changed it (user, API key, or system).
- Each change is captured as a complete point-in-time snapshot, so any two versions can be compared and (in a later phase) a prior version restored.
- Configuration changes are easy to find in the existing Activities list view through better filtering, with no new central change-history page introduced.
- Sensitive configuration values (credentials, secrets) are never stored in or rendered from the change history.
- Configuration change history is retained independently of high-volume identity-data history, and can be disabled via a Service Setting (enabled by default).
- An administrator can optionally record a reason (a "commit message") when saving a configuration change, shown in the history.
- Everything an administrator can do for configuration change history in the UI is equally available via the REST API and the PowerShell module: recording a reason on a change, retrieving change history (summary and full detail), and (in a later phase) rollback.

## Non-Goals

- Business and identity data (CSO and MVO) change history: already delivered under #269; not changed here.
- Rollback / restore of a prior configuration version: explicitly a fast-follow after this PRD's first release. The snapshot model is designed to make it cheap to add later; v1 captures and renders changes, it does not write a prior version back. When delivered, rollback must be available via the UI, the REST API, and PowerShell.
- Updating or deleting individual change-history entries via any surface: change history is immutable Activity data; only the existing housekeeping / retention process removes it.
- A new central change-history page: rejected. The existing Activities list view remains the single go-to, enhanced with filters.
- Consolidating the Activities list view and the Operations/History view: a known concern, but out of scope here and tracked separately.
- Exporting change history to an external system, or downloadable change logs: future enhancements noted on the issue, not in this release.

## User Stories

1. As an administrator, I want to see what changed on a Synchronisation Rule and who changed it, so that I can understand why synchronisation behaviour changed.
2. As an auditor, I want to filter the Activities list to configuration changes within a date range and by who made them, so that I can review configuration governance without inspecting every object individually.
3. As an administrator, I want to compare two versions of a Connected System's configuration, so that I can see exactly what differs between them.
4. As an administrator making a sensitive change, I want to record a short reason, so that future reviewers understand the intent.
5. As a security-conscious operator, I want secrets excluded from the change history, so that the history itself is not a credential-disclosure risk.
6. As an operator of a long-running instance, I want configuration history kept longer than identity-data history but still bounded, so that storage stays controlled while configuration change history is retained.
7. As an automation engineer, I want to record a reason when I change configuration from a script and retrieve a configuration object's change history programmatically, so that changes made through the API or PowerShell are exactly as traceable as changes made in the UI.

## Requirements

### Functional Requirements

**Capture**

1. When a supported configuration object is created, updated, or deleted, JIM records a change entry attributed to the initiator (user, API key, or system) with a UTC timestamp, linked to the originating Activity.
2. Each change entry captures a complete snapshot of the object's post-change state, including nested children such as Attribute Flows, scoping criteria, and Object Matching Rules, sufficient to render a diff against the prior version and to support a later restore.
3. Each change increments a per-object version number that is shown in the UI.
4. Capture is generic across configuration object types but enabled per type; the first release enables Synchronisation Rule and Connected System.
5. Sensitive field values (for example Connected System credentials and bind secrets) are redacted in the stored change entry: the entry records that the field changed without recording its old or new value.
6. On saving a configuration change via the UI, the administrator may enter an optional free-text reason, persisted with the change entry.

**Per-object Changes view**

7. Each supported configuration object's detail page has a "Changes" tab showing a version-ordered history (newest first) with version number, initiator, timestamp, relative time, optional reason, and a one-line summary of what changed.
8. Selecting a version shows a structured diff in the object's natural shape: additions, removals, and modifications (old value to new value) with friendly labels; unchanged branches collapsed; sensitive values shown as changed but hidden.
9. The user can compare any two versions of the object.
10. The Changes tab reuses the shared change-history timeline shell used for CSO and MVO where practical, with a configuration-specific tree/diff detail renderer.

**Activities list integration**

11. The Activities list view gains a coarse category quick-filter to isolate Configuration changes (mapping the relevant `ActivityTargetType` values), alongside Identity data, Sync runs, and System.
12. The Activities list view supports filtering by initiator type (user, API key, or system) and by date range.
13. Activities list filter state is reflected in the URL so a filtered view is shareable and bookmarkable.
14. A configuration-change row in the Activities list links through to that object's Changes tab at the relevant version, and/or renders the same diff on the Activity detail page (single renderer, two entry points).

**Retention and enablement**

15. Configuration change history is retained on a configurable schedule, independently of, and typically longer than, the high-volume sync and identity-data history. Because the change payload is stored with its Activity (see Additional Context), this is realised as target-type-aware Activity retention: configuration-change Activities have their own retention period, separate from sync and identity-data Activity retention. Proposed defaults are confirmed in the implementation plan.
16. Configuration change tracking can be enabled or disabled via a Service Setting, enabled by default, mirroring the existing `ChangeTracking.*.Enabled` pattern; disabling does not delete existing history.
17. Expired configuration change history is removed by the existing worker housekeeping cleanup and recorded via an Activity (count and date range), consistent with the existing history cleanup.

**PowerShell and REST API**

18. The feature has full parity across the REST API and the PowerShell module. The module wraps the API (`Invoke-JIMApi`, `/api/v1/...`) rather than adding separate capability, follows the existing `Verb-JIM<Entity>` naming, and respects the API endpoint identifier rules (retrieval by id, with a name-based overload where the configuration object has a stable name, as the existing cmdlets already provide).
19. Creating, updating, or deleting a configuration object via the API or PowerShell accepts an optional reason for change, attributed to the calling principal and persisted with the change entry. In the API this is an optional field on the write request DTOs (for example `UpdateSyncRuleRequest`, `UpdateConnectedSystemRequest`); in PowerShell it is a `-ChangeReason` parameter on the relevant write cmdlets (for example `Set-JIMSyncRule`, `New-JIMSyncRule`, `Set-JIMConnectedSystem`).
20. The full Activity record, including its configuration change payload, is retrievable via the existing Activities API and `Get-JIMActivity`. Storing change history with the Activity means no separate call is required to obtain everything about an Activity.
21. A dedicated, user-friendly retrieval is provided: a per-object change-history API endpoint (mirroring the existing CSO and MVO `.../change-history` endpoints) and a `Get-JIMConfigurationChangeHistory` style cmdlet, targeting an object by id or name like its siblings, supporting two modes:
    - Summary / outline: the change history for an object (for example a Synchronisation Rule) as a capped or paged list of entries (version, who, when, reason, one-line summary), without the full detail.
    - Single change, full detail: a specified change entry returned either as raw structured data (`-Raw`) or as a visualised git-style diff with git-style colour coding where the host supports it (`-AsDiff`).
22. Change-history retrieval is read-only across the API and PowerShell; there is no create, update, or delete of change-history entries (it is immutable Activity data; only housekeeping / retention removes it).
23. When rollback is implemented (later phase), it is exposed via the REST API and a PowerShell cmdlet (for example `Restore-JIMConfiguration...`) as well as the UI.

### Non-Functional Requirements

- Capture must not materially slow configuration save operations. Configuration writes are low-frequency and single-object, so snapshotting one object per save is acceptable; this must not regress save latency perceptibly.
- Redaction of sensitive values is a hard security requirement. JIM is deployed in healthcare, finance, and government environments; no secret may be persisted to, or rendered from, the change history (in any surface: UI, API, or PowerShell).
- The diff renderer must remain responsive for the largest realistic configuration objects (for example a Synchronisation Rule with many Attribute Flows).
- British English throughout; JIM domain entity names Title Cased.

## Examples and Scenarios

### Scenario 1: Viewing what changed on a Synchronisation Rule

**Given**: an administrator edited the scope of the "HR to AD" Synchronisation Rule.
**When**: another administrator opens that rule's detail page, selects the "Changes" tab, and opens the latest version.
**Then**: they see a tree diff showing the scoping criterion that was added (highlighted as an addition) and the Attribute Flow whose expression changed (old value to new value), with the editor's name, the timestamp, and any reason recorded.

### Scenario 2: Finding configuration changes in the Activities list

**Given**: a busy Activities list with activities of many types.
**When**: an auditor selects the "Configuration" category quick-filter, sets initiator to User, and a date range of the last 7 days.
**Then**: the Activities list shows only user-made configuration changes in that window, and the filtered view's URL can be copied and shared.

### Scenario 3: Sensitive values are protected

**Given**: an administrator updates a Connected System's bind password.
**When**: anyone views that change in the history, through any surface.
**Then**: the entry shows that the password attribute changed, but does not reveal the old or new value.

### Scenario 4: Comparing two versions

**Given**: a Connected System has been edited several times.
**When**: an administrator picks version 3 and version 6 in the Changes tab and chooses Compare.
**Then**: a single diff shows all differences between those two versions in the object's natural structure.

### Scenario 5: Retention keeps configuration history longer than identity data

**Given**: identity-data history retention is 90 days and configuration-change Activity retention is set to a longer period.
**When**: worker housekeeping runs.
**Then**: expired configuration change entries are removed only after the configuration retention period elapses, and the cleanup is recorded as an Activity.

### Scenario 6: Reviewing and changing configuration from PowerShell

**Given**: an administrator is automating configuration via the PowerShell module.
**When**: they run `Set-JIMSyncRule -Id 12 -Disable -ChangeReason "Pausing during HR cutover (CHG0098)"`, then later `Get-JIMConfigurationChangeHistory -SyncRule 12` for an outline, and `Get-JIMConfigurationChangeHistory -SyncRule 12 -ChangeId <id> -AsDiff` for one change.
**Then**: the disable is recorded with the reason and attributed to the calling principal; the summary lists versions with who, when, and reason; and the detail view renders a git-style, colour-coded diff of that change. Passing `-Raw` instead of `-AsDiff` returns the structured data for further processing.

## Constraints

- Must build on the existing change-history infrastructure from #269 (`ChangeHistoryTimeline`, `ChangeHistoryServer`, worker housekeeping cleanup, and the `ChangeTracking.*` Service Settings) rather than introducing a parallel system.
- Must not modify the CSO/MVO relational change model. Configuration change history is stored with its Activity (see Additional Context); this is a deliberate divergence from the CSO/MVO relational tables.
- Full REST API and PowerShell parity is required. The PowerShell module wraps the REST API (`Invoke-JIMApi`, `/api/v1/...`) rather than calling the application layer directly, follows the `Verb-JIM<Entity>` naming, and respects the API endpoint identifier rules.
- Self-contained and air-gap deployable: no external services; snapshots stored in PostgreSQL.
- No new third-party NuGet or PowerShell dependencies without the governance process (`System.Text.Json` is already available for serialisation; PowerShell 7 `$PSStyle` is available for colourised output).
- Must respect N-tier layering (UI / API to `JimApplication` to repository); UI and API must never call repositories directly.

## Affected Areas

| Area | Impact |
|------|--------|
| Database | Storage for configuration change snapshots (PostgreSQL `jsonb`) carried with the `Activity`; migration; index for per-object retrieval; target-type-aware Activity retention |
| Application | Capture on configuration create/update/delete in the relevant servers; sensitive-value redaction; carry the optional reason; new retrieval and retention methods (extend `ChangeHistoryServer`) |
| Worker | Extend housekeeping cleanup to be target-type-aware for configuration-change Activities |
| API | Optional reason field on the configuration write request DTOs (create/update/delete); per-object `.../change-history` retrieval endpoints (paged summary plus single-change detail) mirroring CSO/MVO; configuration change payload surfaced on the Activity detail DTO |
| PowerShell | `-ChangeReason` on configuration write cmdlets (for example `Set-JIMSyncRule`, `Set-JIMConnectedSystem`); new `Get-JIMConfigurationChangeHistory` (summary plus single-change `-Raw` / `-AsDiff` git-style coloured diff); change payload surfaced via existing `Get-JIMActivity`; Pester tests; future `Restore-JIM...` cmdlet |
| UI | "Changes" tab and tree/diff renderer on supported configuration detail pages; optional comment-on-save dialog; Activities list category, initiator, and date filters plus URL persistence; Service Settings for configuration retention and the enable toggle |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/...` (admin how-to) | New "Configuration change history" section: viewing changes on a configuration object, finding configuration changes in Activities, the retention setting |
| `docs/...` (PowerShell and API reference) | Document the `-ChangeReason` parameter on write cmdlets, the new `Get-JIMConfigurationChangeHistory` cmdlet (summary and diff modes), and the change-history API endpoints |
| `engineering/DEVELOPER_GUIDE.md` | Note the configuration change-history snapshot model carried on the Activity, and how it differs from the CSO/MVO relational model |

## Dependencies

- Builds on #269 (CSO/MVO change history), which is delivered.
- No external dependencies.

## Open Questions

1. Default retention period for configuration-change Activities. Proposal: notably longer than the 90-day identity-data default, possibly effectively indefinite by default given the low volume of configuration changes; confirm in the implementation plan.
2. Storage shape: full post-change snapshot per version, versus snapshot plus a precomputed diff; and the exact mechanism for carrying it on the Activity (a property on the Activity entity versus a child record keyed to the Activity). Recommendation is a full snapshot per version; finalised in the plan.
3. Diff rendering for deeply nested collections: the identity-matching strategy for child items (for example matching Attribute Flows across versions) so diffs are stable rather than noisy.
4. How should the reason be supplied on a DELETE over the REST API (query parameter versus request body), given HTTP DELETE-with-body is awkward for some clients?
5. Should the git-style diff be rendered entirely PowerShell-side (ANSI via `$PSStyle`) from structured data returned by the API, or should the API also offer a pre-rendered unified-diff text representation for non-PowerShell clients?
6. Should the optional change reason ever be made mandatory (for example via a Service Setting that requires it)? Default: optional.
7. Which configuration object types follow Synchronisation Rule and Connected System, and in what order?

## Acceptance Criteria

> **Implementation status (2026-07-03):** the backend (capture, redaction, diff engine, retrieval), REST API, PowerShell, the per-object Changes tab (Synchronisation Rule, Connected System, and Schedule), and the Activity-detail diff rendering are delivered. Capture integrity has been hardened beyond the original plan: coverage across all Connected System mutation paths, a semantic no-change dedupe guard, Simple Mode Object Matching Rule capture, per-rule attribute-priority capture, and exclusion of runtime state (status, import watermark) from both snapshots and Activities. Type-aware retention is delivered: configuration-change Activities are retained for their own, far longer period (default ~10 years) and the general history cleanup no longer touches them. The Activities-list filters (category quick-filter, initiator type, date range, URL-persisted state) are delivered, as are Schedule write-side capture and the comment-on-save reason prompt (the shared `ChangeReasonDialog` on the Synchronisation Rule, Connected System, and Schedule save paths). Remaining: rollback (future). See the [implementation plan](../plans/doing/CONFIGURATION_CHANGE_HISTORY.md) for phase status.

- [x] Creating, updating, or deleting a supported configuration object records a change entry with initiator, UTC timestamp, version number, and a complete post-change snapshot, carried with its Activity. *(Create and update for both types, plus Synchronisation Rule delete, are captured; Connected System hard-delete capture is deferred to a follow-up. Schedules are delivered in full: retrieval, API, PowerShell, UI, and write-side capture.)*
- [x] Synchronisation Rule and Connected System detail pages each have a "Changes" tab showing version history with version number, initiator, time, optional reason, and a summary. *(Delivered as the shared `ConfigurationChangesTab`, also hosted as a History tab in the Schedule editor.)*
- [x] Selecting a version renders a structured tree diff (additions, removals, modifications with old-to-new values, friendly labels, unchanged branches collapsed).
- [x] Any two versions of a supported object can be compared. *(Diff engine, compare endpoint and cmdlet, and in-portal compare in the Changes tab.)*
- [x] Sensitive configuration values are never stored in, or rendered from, the change history in any surface.
- [x] An optional reason can be entered on save (UI) and is shown in the history. *(The shared `ChangeReasonDialog` prompts on the Synchronisation Rule, Connected System create/details/settings, and Schedule editor save paths; cancelling aborts the save. API and PowerShell already captured the reason.)*
- [x] The Activities list view has a Configuration category quick-filter, initiator-type and date-range filters, and URL-persisted filter state.
- [x] A configuration-change activity links through to the relevant object and version diff. *(The Activity detail page renders the same diff inline, loading by the change's object rather than the Activity's target type, links to the changed object, including Object Matching Rules to their owning object's Matching tab, and explains why no snapshot exists when one was legitimately not captured.)*
- [x] Configuration change history is retained independently of identity-data history (target-type-aware Activity retention). *(Configuration-change Activities are governed by the `History.ConfigurationChangeRetentionPeriod` Service Setting, default ~10 years; the general Activity cleanup spares them.)*
- [x] Configuration change tracking can be disabled via a Service Setting (default enabled); disabling retains existing history.
- [x] Expired configuration change history is cleaned up by worker housekeeping and recorded via an Activity.
- [x] The REST API and PowerShell module have full parity for: recording an optional reason on configuration create/update/delete; retrieving change history (summary and single-change detail); and (when delivered) rollback.
- [x] A write cmdlet (for example `Set-JIMSyncRule`) accepts `-ChangeReason`, and the reason is persisted and attributed to the calling principal.
- [x] `Get-JIMConfigurationChangeHistory` returns a capped / paged summary for an object, and for a single change returns either raw data (`-Raw`) or a git-style colour-coded diff (`-AsDiff`).
- [x] The full Activity record, including its change payload, is retrievable via `Get-JIMActivity` and the Activities API.
- [x] No API or cmdlet permits updating or deleting individual change-history entries.
- [x] Pester tests cover the new and modified cmdlets.
- [x] Rollback / restore is explicitly not delivered in this release (captured as a fast-follow, with UI, API, and PowerShell coverage when it lands).

## Additional Context

**Relationship to #269 and the recommended storage approach** (direction for the implementation plan, not final design):

- Change history for configuration objects is stored with its `Activity` (the preferred direction): a complete, versioned, redaction-aware structured snapshot per change, serialised as PostgreSQL `jsonb`, rather than the relational per-attribute change model used for CSO and MVO. The exact mechanism (a property on the `Activity` entity versus a child record keyed to the Activity) is finalised in the implementation plan. Rationale: configuration objects are nested, heterogeneous aggregates (a Synchronisation Rule has Attribute Flows, scoping criteria, and matching rules) and are low-volume; this is the opposite profile to the flat, homogeneous, high-volume CSO/MVO data the relational model was optimised for. A document model renders the object in its natural tree (the strongest UX for diffs) and makes version compare and later restore straightforward. The two change-history families are therefore split by volume profile, a deliberate and documented decision.
- Storing the payload with the Activity has two welcome consequences: retrieving "all Activity information" (via the Activities API and `Get-JIMActivity`) automatically includes the change history, and the retention requirement becomes target-type-aware Activity retention (configuration-change Activities kept longer than sync and identity-data Activities), which matches the framing in issue #14's comments about separate retention policies for configuration versus CSO/MVO history.
- The `Activity` model already carries the configuration target types (`ConnectedSystem`, `SyncRule`, `ObjectMatchingRule`, `MetaverseAttribute`, `ServiceSetting`, and others), the operations (`Create`, `Update`, `Delete`, and notably `Revert`), and the initiator triad, so the Activity envelope already exists; this feature adds the change payload. A `// todo` comment in `Activity.cs` already earmarked a "json blob that contains object changes" and flagged sensitive-value access control. This PRD adopts that direction, but as a structured, versioned, redaction-aware document rather than an opaque blob, because the UX (a stable, friendly tree diff) and the security (redaction) live in that structure.
- **Phasing**: build capture and storage generically across `IAuditable` configuration objects, but enable and polish the Changes tab and redaction for Synchronisation Rule and Connected System first (the hardest cases: nested-collection diffing and secret redaction), then enable the remaining configuration types incrementally. The Activities list filters and the API/PowerShell retrieval apply to all configuration types immediately, since they only need the `Activity` envelope that already exists.
- Rollback / restore is a fast-follow; `ActivityTargetOperationType.Revert` already exists as a foothold, and rollback must ship with UI, API, and PowerShell coverage when delivered.

**PowerShell and API grounding** (existing conventions this feature follows):

- Cmdlets are PowerShell functions under `src/JIM.PowerShell/`, named `Verb-JIM<Entity>`, calling the REST API through the private `Invoke-JIMApi` helper. Direct precedents already exist: `Get-JIMConnectedSystemObjectChangeHistory`, `Get-JIMMetaverseObjectChangeHistory`, and `Get-JIMActivity`. The new `Get-JIMConfigurationChangeHistory` follows the same shape and paging conventions (`PaginatedResponse<T>`, `-Page` / `-PageSize`, an `-All` streaming option).
- There is no existing reason / comment / justification field anywhere (only an isolated `Notes` field on certificates), and no formatted-output precedent (no `Format.ps1xml`, no ANSI colour). The optional reason and the git-style coloured diff are therefore new patterns; PowerShell 7 `$PSStyle` provides the colour support.

**Prior art:** #269 and `engineering/plans/done/CSO_MVO_CHANGE_OBJECTS.md`; the shared component `src/JIM.Web/Shared/ChangeHistoryTimeline.razor`; the existing change-history cmdlets and `/api/v1/.../change-history` endpoints.
