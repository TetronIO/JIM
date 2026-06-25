# Configuration Change History

- **Status:** Planned
- **Created:** 2026-06-25
- **Author:** JayVDZ
- **Issue:** [#14](https://github.com/TetronIO/JIM/issues/14)

## Problem Statement

JIM records *that* configuration objects change, but not *what* changed. Every configuration object (Connected System, Synchronisation Rule, Object Matching Rule, Metaverse Attribute, Metaverse Object Type, Service Setting, and so on) is `IAuditable` and produces an Activity capturing who acted, when, and the operation type. None of them capture the before and after of the change.

As a result, administrators and auditors cannot answer everyday governance questions: who changed this Synchronisation Rule's scope last week and what did they alter; what did this Connected System's configuration look like before the last edit; was this misconfiguration a recent change. Configuration changes are among the highest-impact actions in JIM (a single Synchronisation Rule edit can reshape thousands of identities) yet are currently the least auditable.

The equivalent capability for business and identity data (Connected System Objects and Metaverse Objects) was delivered under #269: per-object change timelines via the shared `ChangeHistoryTimeline`, per-type retention, and worker housekeeping cleanup. This PRD closes the remaining gap by extending that audit capability to configuration objects.

## Goals

- Administrators can view a complete, version-ordered timeline of changes on any supported configuration object's detail page, showing what changed, when, and who changed it (user, API key, or system).
- Each change is captured as a complete point-in-time snapshot, so any two versions can be compared and (in a later phase) a prior version restored.
- Configuration changes are easy to find in the existing Activities list view through better filtering, with no new central audit page introduced.
- Sensitive configuration values (credentials, secrets) are never stored in or rendered from the change history.
- Configuration change history has its own retention policy, independent of high-volume identity-data retention, and can be disabled via a Service Setting (enabled by default).
- An administrator can optionally record a reason (a "commit message") when saving a configuration change, shown in the history.

## Non-Goals

- Business and identity data (CSO and MVO) change history: already delivered under #269; not changed here.
- Rollback / restore of a prior configuration version: explicitly a fast-follow after this PRD's first release. The snapshot model is designed to make it cheap to add later; v1 captures and renders changes, it does not write a prior version back.
- A new central audit or change-history page: rejected. The existing Activities list view remains the single go-to, enhanced with filters.
- Consolidating the Activities list view and the Operations/History view: a known concern, but out of scope here and tracked separately.
- Exporting change history to an external system, or downloadable change logs: future enhancements noted on the issue, not in this release.

## User Stories

1. As an administrator, I want to see what changed on a Synchronisation Rule and who changed it, so that I can understand why synchronisation behaviour changed.
2. As an auditor, I want to filter the Activities list to configuration changes within a date range and by who made them, so that I can review configuration governance without inspecting every object individually.
3. As an administrator, I want to compare two versions of a Connected System's configuration, so that I can see exactly what differs between them.
4. As an administrator making a sensitive change, I want to record a short reason, so that future reviewers understand the intent.
5. As a security-conscious operator, I want secrets excluded from the audit log, so that the history itself is not a credential-disclosure risk.
6. As an operator of a long-running instance, I want configuration history kept longer than identity-data history but still bounded, so that storage stays controlled while configuration audit is retained.

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

15. Configuration change history has a configurable retention period, independent of CSO, MVO, and Activity retention, defaulting to a period appropriate to low-volume configuration data (proposed default confirmed in the implementation plan).
16. Configuration change tracking can be enabled or disabled via a Service Setting, enabled by default, mirroring the existing `ChangeTracking.*.Enabled` pattern; disabling does not delete existing history.
17. Expired configuration change history is removed by the existing worker housekeeping cleanup and audited via an Activity (count and date range), consistent with the existing history cleanup.

### Non-Functional Requirements

- Capture must not materially slow configuration save operations. Configuration writes are low-frequency and single-object, so snapshotting one object per save is acceptable; this must not regress save latency perceptibly.
- Redaction of sensitive values is a hard security requirement. JIM is deployed in healthcare, finance, and government environments; no secret may be persisted to, or rendered from, the change history.
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
**When**: anyone views that change in the history.
**Then**: the entry shows that the password attribute changed, but does not reveal the old or new value.

### Scenario 4: Comparing two versions

**Given**: a Connected System has been edited several times.
**When**: an administrator picks version 3 and version 6 in the Changes tab and chooses Compare.
**Then**: a single diff shows all differences between those two versions in the object's natural structure.

### Scenario 5: Retention keeps configuration history longer than identity data

**Given**: identity-data history retention is 90 days and configuration history retention is set to a longer period.
**When**: worker housekeeping runs.
**Then**: expired configuration change entries are removed only after the configuration retention period elapses, and the cleanup is recorded as an Activity.

## Constraints

- Must build on the existing change-history infrastructure from #269 (`ChangeHistoryTimeline`, `ChangeHistoryServer`, worker housekeeping cleanup, and the `ChangeTracking.*` / `History.RetentionPeriod.*` Service Settings) rather than introducing a parallel system.
- Must not modify the CSO/MVO relational change model. Configuration uses a snapshot/document model; this is a deliberate divergence (see Additional Context).
- Self-contained and air-gap deployable: no external services; snapshots stored in PostgreSQL.
- No new third-party NuGet dependencies without the governance process (`System.Text.Json` is already available for serialisation).
- Must respect N-tier layering (UI to `JimApplication` to repository); UI must never call repositories directly.

## Affected Areas

| Area | Impact |
|------|--------|
| Database | New storage for configuration change snapshots (PostgreSQL `jsonb`), linked to `Activity`; migration; index for per-object retrieval |
| Application | Capture on configuration create/update/delete in the relevant servers; sensitive-value redaction; new retrieval and retention methods (extend `ChangeHistoryServer`) |
| Worker | Extend housekeeping cleanup to configuration change history |
| API | Endpoint(s) to retrieve a configuration object's change history / versions, mirroring the CSO/MVO change endpoints |
| UI | "Changes" tab and tree/diff renderer on supported configuration detail pages; optional comment-on-save dialog; Activities list category, initiator, and date filters plus URL persistence; Service Settings for configuration retention and the enable toggle |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/...` | New "Configuration change history" section (admin how-to: viewing changes on a configuration object, finding configuration changes in Activities, the retention setting) |
| `engineering/DEVELOPER_GUIDE.md` | Note the configuration change-history snapshot model and how it differs from the CSO/MVO relational model |

## Dependencies

- Builds on #269 (CSO/MVO change history), which is delivered.
- No external dependencies.

## Open Questions

1. Default retention period for configuration change history. Proposal: notably longer than the 90-day identity-data default, possibly effectively indefinite by default given the low volume of configuration changes; confirm in the implementation plan.
2. Storage shape: full post-change snapshot per version, versus snapshot plus a precomputed diff. Recommendation is a full snapshot per version (enables compare and restore, and can be re-diffed later); to be finalised in the plan.
3. Diff rendering for deeply nested collections: the identity-matching strategy for child items (for example matching Attribute Flows across versions) so diffs are stable rather than noisy.
4. Should the optional change reason ever be made mandatory (for example via a Service Setting that requires it)? Default: optional.
5. Which configuration object types follow Synchronisation Rule and Connected System, and in what order?

## Acceptance Criteria

- [ ] Creating, updating, or deleting a supported configuration object records a change entry with initiator, UTC timestamp, version number, and a complete post-change snapshot, linked to its Activity.
- [ ] Synchronisation Rule and Connected System detail pages each have a "Changes" tab showing version history with version number, initiator, time, optional reason, and a summary.
- [ ] Selecting a version renders a structured tree diff (additions, removals, modifications with old-to-new values, friendly labels, unchanged branches collapsed).
- [ ] Any two versions of a supported object can be compared.
- [ ] Sensitive configuration values are never stored in, or rendered from, the change history.
- [ ] An optional reason can be entered on save and is shown in the history.
- [ ] The Activities list view has a Configuration category quick-filter, initiator-type and date-range filters, and URL-persisted filter state.
- [ ] A configuration-change activity links through to the relevant object and version diff.
- [ ] Configuration change history has its own retention period, independent of identity-data retention.
- [ ] Configuration change tracking can be disabled via a Service Setting (default enabled); disabling retains existing history.
- [ ] Expired configuration change history is cleaned up by worker housekeeping and audited via an Activity.
- [ ] Rollback / restore is explicitly not delivered in this release (captured as a fast-follow).

## Additional Context

**Relationship to #269 and the recommended storage approach** (direction for the implementation plan, not final design):

- This feature deliberately uses a snapshot/document model: a complete, versioned, redaction-aware structured snapshot per change, stored as PostgreSQL `jsonb` and linked to the existing `Activity` record, rather than the relational per-attribute change model used for CSO and MVO. Rationale: configuration objects are nested, heterogeneous aggregates (a Synchronisation Rule has Attribute Flows, scoping criteria, and matching rules) and are low-volume; this is the opposite profile to the flat, homogeneous, high-volume CSO/MVO data the relational model was optimised for. A document model renders the object in its natural tree (the strongest UX for diffs) and makes version compare and later restore straightforward. The two change-history families are therefore split by volume profile, a deliberate and documented decision.
- The `Activity` model already carries the configuration target types (`ConnectedSystem`, `SyncRule`, `ObjectMatchingRule`, `MetaverseAttribute`, `ServiceSetting`, and others), the operations (`Create`, `Update`, `Delete`, and notably `Revert`), and the initiator triad, so the audit envelope already exists; this feature adds the change payload. A `// todo` comment in `Activity.cs` already earmarked a "json blob that contains object changes" and flagged sensitive-value access control. This PRD adopts that direction, but as a structured, versioned, redaction-aware document rather than an opaque blob, because the UX (a stable, friendly tree diff) and the security (redaction) live in that structure.
- **Phasing**: build capture and storage generically across `IAuditable` configuration objects, but enable and polish the Changes tab and redaction for Synchronisation Rule and Connected System first (the hardest cases: nested-collection diffing and secret redaction), then enable the remaining configuration types incrementally. The Activities list filters apply to all configuration types immediately, since they only need the `Activity` envelope that already exists.
- Rollback / restore is a fast-follow; `ActivityTargetOperationType.Revert` already exists as a foothold.

**Prior art:** #269 and `engineering/plans/done/CSO_MVO_CHANGE_OBJECTS.md`; the shared component `src/JIM.Web/Shared/ChangeHistoryTimeline.razor`.
