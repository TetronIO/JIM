# Configuration Change History: Full Coverage of Configuration Object Types

- **Status:** Done
- **Created:** 2026-07-05
- **Author:** JayVDZ
- **Issue:** [#14](https://github.com/TetronIO/JIM/issues/14) *(tracked as a sub-task of the parent change-history issue)*

## Problem Statement

The Configuration Change History capability (issue #14, [`PRD_CONFIGURATION_CHANGE_HISTORY.md`](PRD_CONFIGURATION_CHANGE_HISTORY.md)) delivered versioned, redaction-aware change snapshots for three configuration object types: Connected System, Synchronisation Rule, and Schedule (each including their child objects: Run Profiles, object types and attribute selections, partitions and containers, connector settings, Attribute Flows, Object Matching Rules, scoping criteria, and Schedule steps). That PRD explicitly directed a generic build with the remaining types enabled "incrementally", but no increment was ever scoped.

The result is a two-tier audit posture that undermines the feature's compliance story:

- **Some configuration types record a plain Activity but no versioned snapshot**, so an auditor can see *that* something changed but not *what*: Service Settings (including security-sensitive values such as retention periods), Metaverse Attributes, Metaverse Object Types (whose update path records no Activity at all), and Trusted Certificates.
- **Some configuration types record nothing whatsoever**: API Keys (whose UI mutations also bypass the application layer entirely, so there is no server hook to attach an Activity to), Role assignments, Predefined Searches and their criteria, Connector Definitions, and Example Data Templates and Sets. For API Keys and Role assignments this is a security-grade gap: credential and privilege changes leave no audit trail.

JIM is deployed in high-trust environments (healthcare, finance, government). "Who changed this setting, when, and what was it before" must be answerable for **every** configuration object an administrator can change, not just the three types delivered so far.

## Goals

- Every admin-mutable configuration object type records an Activity on create, update, and delete, attributed to the initiating principal; verified by exercising each mutation path and finding its Activity.
- Every admin-mutable configuration object type records a versioned configuration snapshot with its Activity, retrievable and diffable through the same UI, REST API, and PowerShell surfaces as the existing three types; verified per type via `Get-JIMConfigurationChangeHistory`.
- Change history for the newly covered types is discoverable in the admin portal from the place each object is managed, consistent with the existing Changes tab and Schedule History tab experience.
- Security-sensitive values in the newly covered types (encrypted Service Setting values, API key secrets) are never stored in, or rendered from, the history, using the established keyed-hash redaction pattern.
- The optional "Reason for change" capture (UI prompt, `-ChangeReason`, REST field) works for the newly covered types wherever they are mutated.
- API Key mutations are moved out of the Blazor page into the application layer, restoring the N-tier rule that JIM.Web only calls `JimApplication`.

## Non-Goals

- Rollback / restore of a prior configuration version: remains the Phase 8 fast-follow of the original PRD.
- Connected System deletion tombstone capture: out of scope for this increment (has since been delivered separately; all three delete paths now record a tombstone).
- Business/identity data (CSO and MVO) change history: delivered under #269; not touched.
- Changes to the storage model, diff engine, retention behaviour, or Activities-list filters: the existing infrastructure is type-agnostic and is reused as-is.
- History for runtime/operational state (worker tasks, run results, import watermarks, seeded reference data such as built-in Roles' definitions): configuration only.
- Making the change reason mandatory (remains an open question on the original PRD).

## User Stories

1. As an auditor in a regulated deployment, I want every configuration change in JIM to carry a versioned before/after record, so that no administrative action is exempt from review.
2. As an administrator, I want to see who changed a Service Setting (for example a retention period) and what its previous value was, so that I can diagnose behaviour changes and undo mistakes confidently.
3. As a security officer, I want API key and role assignment changes recorded with who made them and when, so that privilege and credential changes are traceable.
4. As an administrator, I want schema-shaping changes (Metaverse Object Types and Attributes) versioned, so that I can trace when an attribute was added, renamed, or removed and by whom.
5. As an automation engineer, I want the same `Get-JIMConfigurationChangeHistory` cmdlet and REST endpoints to work for every configuration type, so that my audit tooling does not need per-type special cases.

## Requirements

### Functional Requirements

**Tier 1: types that already record an Activity; add versioned snapshots**

1. Service Setting updates and reverts-to-default record a versioned snapshot. Encrypted setting values are redacted with the established keyed-hash approach (changed/unchanged is provable; the value is never stored). The setting's effective value semantics (override vs default) must be representable in the diff.
2. Metaverse Attribute create, update, and delete record a versioned snapshot; delete records a tombstone entry consistent with Synchronisation Rule delete.
3. Metaverse Object Type create, update, and delete record a versioned snapshot. The update path's missing Activity is fixed as part of this work (it currently records no Activity at all). The snapshot includes the object type's attribute associations and deletion-rule/grace-period configuration.
4. Trusted Certificate add, update, and delete record a versioned snapshot; certificate metadata (subject, thumbprint, expiry, notes) is captured, never key material.

**Tier 2: types that record no Activity today; add Activity plumbing, then snapshots**

5. API Key create, update, and delete record an Activity and a versioned snapshot. The API key secret is never stored in any form (not even hashed) in the snapshot; role assignments and metadata are captured. Prerequisite: API key mutations move from the Blazor page into a proper application-layer server (the page currently calls the repository directly).
6. Role definitions and Role assignments are both versioned: changes to a Role's configuration record an Activity and a versioned snapshot (in anticipation of the wider RBAC roll-out under #612, where roles become admin-editable; the capture path must exist even while built-in Roles are seed-only), and adding or removing an object's membership of a Role (however initiated, including the automatic first-admin assignment) records an Activity and a versioned snapshot of the Role's membership-relevant configuration.
7. Predefined Search create, update, and delete record an Activity and a versioned snapshot; criteria and criteria groups roll up into the owning Predefined Search's snapshot, mirroring how scoping criteria roll into a Synchronisation Rule.
8. Connector Definition create, update, and delete (including connector file changes) record an Activity and a versioned snapshot of definition metadata (never connector binary content; file changes are recorded as name/size/hash).
9. Example Data Template and Example Data Set create, update, and delete record an Activity and a versioned snapshot.

**Cross-cutting**

10. Every Schedule step mutation path records exactly one versioned snapshot of the owning Schedule per save, and the application layer must not offer an unaudited step mutation surface. *(Corrected 2026-07-05 during Phase 1: the draft presumed API-driven step changes bypassed capture, but there are no step-level REST endpoints; both step mutation surfaces reconcile steps and then perform the audited whole-Schedule update, which captures them. The residual risk is the bare application-layer step methods, which rely on callers following that pattern; the implementation plan proposes consolidating step reconciliation into the audited Schedule update as the durable fix.)*
11. Each newly covered type's history is retrievable via `GetConfigurationChangeHistoryAsync` (correct key shape per type), the per-type REST `change-history` endpoints, and `Get-JIMConfigurationChangeHistory -Type <TypeName>`, with single-version diff and compare-two-versions parity.
12. Each newly covered type's history is viewable in the admin portal from where the object is managed: a Changes tab where a detail page exists (API Keys have one), and a per-row history affordance opening the standard version list/diff view on list-plus-dialog pages (Service Settings, Certificates, Predefined Searches where no detail page exists), following the Schedule editor's History tab precedent.
13. The optional reason is capturable for every newly covered mutation: the shared "Reason for change" prompt on UI save paths, `-ChangeReason` on the write cmdlets, and the optional reason field on REST write DTOs. Where a write cmdlet or REST write endpoint does not yet exist for a type, adding it is in scope only insofar as needed for reason parity; broader endpoint coverage remains tracked elsewhere.
14. All new capture paths honour the existing behaviours: the `ChangeTracking.ConfigurationChanges.Enabled` toggle, the semantic no-change dedupe guard (a save that changes nothing consumes no version), best-effort capture (a capture failure never rolls back the mutation), and the configuration-change retention period.

### Non-Functional Requirements

- These are low-volume administrative operations; no bulk-path performance work is expected. Snapshot capture must not measurably slow the admin UI save interactions.
- No new NuGet packages.

## Examples and Scenarios

### Scenario 1: Service Setting change is diffable

**Given**: an administrator changed `History.RetentionPeriod` from 90 days to 30 days yesterday.
**When**: an auditor opens the setting's history (UI or `Get-JIMConfigurationChangeHistory -Type ServiceSetting ...`).
**Then**: they see a v(n) entry attributed to the administrator with the diff `90.00:00:00 → 30.00:00:00`, plus any reason recorded at save time.

### Scenario 2: API key lifecycle is audited

**Given**: an administrator creates an API key with the Administrator role, later narrows it to a lesser role, and eventually deletes it.
**When**: a security officer reviews the Activity list filtered to Configuration, or the key's history.
**Then**: three versioned entries exist (create, update showing the role change old → new, delete tombstone), each attributed and timestamped; the key's secret appears in none of them.

### Scenario 3: Metaverse Object Type update is no longer silent

**Given**: an administrator changes a Metaverse Object Type's deletion grace period.
**When**: they save.
**Then**: an Activity is recorded (fixing today's silent update) carrying a versioned snapshot whose diff shows the grace-period change; the optional reason prompt was offered at save.

### Scenario 4: No-change saves stay quiet for new types

**Given**: an administrator opens a Predefined Search and clicks save without changing anything.
**When**: the save completes.
**Then**: no new version is recorded, consistent with the semantic dedupe guard on the existing types.

## Constraints

- Self-contained and air-gap deployable; no new external dependencies.
- Follow the existing capture architecture (snapshot on the Activity, `ConfigurationSnapshotService` builder per type, type-agnostic diff engine); do not introduce a parallel mechanism.
- Respect N-tier layering; the API key work must remove the existing violation, not extend it.
- British English throughout; JIM entity names Title Case in all user-facing text.

## Affected Areas

| Area | Impact |
|------|--------|
| Models | Possible new `ActivityTargetType` handling (values largely exist already); no storage model changes expected |
| Application | New snapshot builders per type; capture calls in `ServiceSettingsServer`, `MetaverseServer`, `CertificateServer`, `SearchServer`, `SchedulerServer` (steps), `ExampleDataServer`, connector-definition paths; new API key server methods; Activity plumbing for Tier 2 servers; `ChangeHistoryServer` retrieval routing for new target types |
| API | Per-type `change-history` retrieval endpoints; optional reason field on write DTOs for newly covered types |
| PowerShell | `Get-JIMConfigurationChangeHistory -Type` values for the new types; `-ChangeReason` on the relevant write cmdlets |
| UI | Changes/history surfaces on Service Settings, Metaverse Object Type / Attribute pages, Certificates, API Keys, Predefined Searches, Example Data pages; reason prompt wiring on their save paths; API key page refactored onto the application layer |
| Tests | TDD coverage per capture path, mirroring `ConfigurationChangeCaptureCoverageTests` / `ScheduleConfigurationChangeCaptureTests` patterns |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/configuration/activities.md` | Update the "Coverage" note: configuration change history covers all configuration object types; enumerate them |
| `docs/configuration/service-settings.md`, `docs/configuration/api-keys.md`, `docs/configuration/certificates.md`, `docs/configuration/predefined-searches.md`, `docs/configuration/metaverse.md` | Note the per-object change history surface where each type is managed |
| `engineering/DEVELOPER_GUIDE.md` | Update the configuration change capture architecture section if server plumbing patterns change |

## Dependencies

- Builds directly on the delivered #14 infrastructure (snapshot service, diff engine, retention, Activities filters, reason capture). No external dependencies.
- Overlaps with #377 (admin CRUD for custom Metaverse Attributes): whichever lands second must respect the other's capture/mutation paths.

## Decisions (2026-07-05)

Resolved from the draft's open questions:

1. **Role coverage depth**: Role definitions are versioned as well as assignments, in anticipation of the wider RBAC roll-out (#612).
2. **History surface**: a Changes tab where a detail page exists (API Keys included); a per-row history affordance for list-plus-dialog pages.
3. **Example Data**: full parity. A customer-facing UI for CRUDing templates and data sets is anticipated, so the audit trail must be in place.
4. **Connector files**: recording name/size/hash (never content) is sufficient.

## Acceptance Criteria

- [x] Every mutation path for Service Setting, Metaverse Attribute, Metaverse Object Type, and Trusted Certificate records an Activity carrying a versioned snapshot; the Metaverse Object Type update path's missing Activity is fixed. *(Phases 2-4)*
- [x] Every mutation path for API Key, Role (definition and assignment), Predefined Search (including criteria and groups), Connector Definition, and Example Data Template/Set records an Activity carrying a versioned snapshot. *(Phases 5-9)*
- [x] API Key mutations no longer bypass the application layer; the Blazor page calls new server methods. *(N-tier enforcement interim slice + Phase 5)*
- [x] Encrypted Service Setting values and API key secrets never appear in stored or rendered history; redaction is covered by tests. *(the API key secret is excluded in every form, not even a hash; Trusted Certificate key material, Connector Definition file binaries, and Example Data Set values are likewise excluded)*
- [x] No Schedule step mutation path bypasses capture: every step change records exactly one versioned snapshot of the owning Schedule per save. *(Phase 1: verified every step path reconciles then calls the audited whole-Schedule update; the presumed gap was disproved)*
- [x] Each newly covered type's history is retrievable via the REST API and `Get-JIMConfigurationChangeHistory` with diff and compare parity, and viewable in the admin portal where the object is managed.
- [x] The reason prompt, `-ChangeReason`, and REST reason field work for the newly covered types' mutations.
- [x] The enable toggle, semantic no-change dedupe, best-effort capture, and configuration-change retention apply to all new capture paths, covered by tests per path.
- [x] `docs/configuration/activities.md` coverage note updated to enumerate full coverage.

## Additional Context

- Original PRD and plan: [`PRD_CONFIGURATION_CHANGE_HISTORY.md`](PRD_CONFIGURATION_CHANGE_HISTORY.md), [`engineering/plans/doing/CONFIGURATION_CHANGE_HISTORY.md`](../plans/doing/CONFIGURATION_CHANGE_HISTORY.md). The original PRD's Additional Context explicitly directed: "build capture and storage generically across `IAuditable` configuration objects... then enable the remaining configuration types incrementally". This PRD is that increment.
- Coverage audit (2026-07-05) that motivated this PRD: 3 of ~13 admin-mutable configuration types have versioned history. Tier 1 gaps (Activity exists, no snapshot): Service Setting, Metaverse Attribute, Metaverse Object Type (update also missing its Activity), Trusted Certificate. Tier 2 gaps (no Activity at all): API Key (UI also bypasses the application layer), Role assignments, Predefined Search, Connector Definition, Example Data Template/Set.
- Suggested phasing for the implementation plan: Tier 1 first (highest audit value, least plumbing), then the security-critical Tier 2 items (API Keys including the N-tier fix, Role assignments), then the remainder (Predefined Search, Connector Definition, Example Data).
- Related: #612 (entitlement management may introduce custom roles), #377 (Metaverse Attribute CRUD), #827 (configuration change preview references the same coverage map).
