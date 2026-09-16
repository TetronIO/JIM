# Unique Value Generation and Collision Remediation

- **Status:** Planned
- **Created:** 2026-07-07
- **Updated:** 2026-09-16 (design revised after review: JIM-owned generated value assignments replace the confirming-import model; Collision Remediation added; Import and Export Attribute Flow modes; causality integration against #1087/#1495; Set Value deferred)
- **Author:** JayVDZ (PRD drafted and revised via Claude Code)
- **Issue:** [#242](https://github.com/TetronIO/JIM/issues/242)
- **Plan:** [UNIQUE_VALUE_GENERATION.md](../plans/UNIQUE_VALUE_GENERATION.md)
- **Related:** [#549](https://github.com/TetronIO/JIM/issues/549) example-data expressions (closed; interim tracker), [#223](https://github.com/TetronIO/JIM/issues/223) Initial Export Only (per-mapping flag precedent), [#1121](https://github.com/TetronIO/JIM/issues/1121) Initial Password Provisioning (parked-state and queue-and-follow precedents), [#1087](https://github.com/TetronIO/JIM/issues/1087) / [#1495](https://github.com/TetronIO/JIM/issues/1495) causality views, [#1079](https://github.com/TetronIO/JIM/issues/1079) optimistic export apply
- **UI mockups:** [Unique Value Generation: Design and Mockups](https://claude.ai/artifact/G9R6cK7WR7QwmPukctFpkb) (design explainers, diagrams and six screens, built against `engineering/DESIGN.md` tokens)

## Problem Statement

In real deployments the HR system is authoritative for identity data (name, employee ID, department, start date) but is **not** the source of the technical identifiers an IDAM team owns: the account name (sAMAccountName), the email address, and the User Principal Name (UPN). Today JIM has no way to generate those identifiers itself. The bundled demo data papers over this by putting IT-owned attributes into the HR CSV feed, which is unrealistic and undermines the demo's credibility.

Generating these identifiers is not just string construction; JIM already has an expression engine that can build `firstname.lastname` from source attributes. The missing capability is **uniqueness**: a generated account name must not collide with one already in use, and when the natural candidate is taken the value must be disambiguated deterministically (`joe.bloggs`, then `joe.bloggs1`, `joe.bloggs2`, ...). An expression alone cannot do this, for three concrete architectural reasons found in the current codebase:

1. **Expressions have no uniqueness state.** `DynamicExpressoEvaluator` is deliberately stateless and side-effect-free, with a process-wide thread-safe compiled-expression cache (`src/JIM.Application/Expressions/DynamicExpressoEvaluator.cs`). `Evaluate` is synchronous and performs no I/O. A uniqueness check needs database queries, connector calls and a cross-object reservation set; none belongs inside a pure, cached, synchronous evaluator.
2. **Attribute Flows recompute every synchronisation.** A generated value that is recomputed from its expression on every run cannot survive being corrected by a target (see below): the next run would recompute the original and fight the correction for ever. A generated value has to be remembered, not recomputed.
3. **Siblings within a batch are invisible to a database check.** Synchronisation processes Connected System Objects in pages and batch-persists at page boundaries (`src/JIM.Worker/Processors/SyncFullSyncTaskProcessor.cs`). Two new starters named "John Smith" in the same page would both find `john.smith` free and both take it.

There is a fourth reason, which the first draft of this PRD underestimated: **uniqueness only matters in the target system, and JIM's view of the target is a proxy.** A value can be unique across everything JIM knows and still collide in a directory, because the conflicting object sits in an OU outside the connector's import scope, or was created by someone else between JIM's check and JIM's write. No check, however thorough, closes that gap. Only the target can arbitrate, so the design has to treat an export-time rejection as an expected outcome to be handled, not an error to be reported and abandoned.

## Goals

- An administrator can configure an Attribute Flow so that **JIM generates** an attribute value from a base expression and guarantees it is not already in use, on an import Synchronisation Rule (the value becomes Metaverse data, reusable by every downstream export) or on an export Synchronisation Rule (the value belongs to one Connected System Object only).
- Generated values are checked for availability against everything JIM can see before they are proposed: in-run reservations, the Metaverse, the relevant connector spaces, and, where the connector supports it, the target directory itself.
- When a target nevertheless rejects a generated value as already in use, JIM **self-heals** by generating the next candidate and exporting again, without administrator involvement, unless doing so would rename an account another system has already provisioned; in that case JIM stops and gives the administrator a clear decision with remediation advice. This behaviour is called **Collision Remediation** and is controllable per Attribute Flow.
- Generated values are **sticky**: assigned once, remembered, never renumbered on a re-run, and never recomputed from the expression once committed.
- Everything JIM generates, corrects or declines to correct is **fully transparent**: visible on the identity's attribute history, on the Run Profile execution item, and in the causality views, with what happened, why, when and by which Activity.
- The generation and uniqueness capability is built once, as a service keyed on the object and attribute rather than on the caller, so that internally managed identities and workflow-driven generation can reuse it later without a second implementation.

## Non-Goals

- **No manual setting of a Metaverse attribute value by an administrator (Set Value).** This was designed and is deliberately deferred out of #242: it is the first non-import write of Identity data in JIM and belongs with the internally managed identities design. The analysis is preserved in the linked artefact; the service keeps the manual caller as a seam.
- **No immediate or on-demand export lane.** Generated and remediated values reach targets through ordinary synchronisation and export runs. JIM's password delivery lane (#1121) bypasses Pending Exports because a password is never Metaverse data; that precedent is explicitly not copied for values that are.
- **No connector calls from the web tier.** `JIM.Web` does not instantiate connectors today and does not start here. Live checks in the authoring dialog use JIM's own data only.
- **No automatic renaming of a value another target has already provisioned** (see the anchoring rule). JIM will not silently rename a live account.
- **No workflow engine, approval step, or human override of a generated value** beyond the actions defined for the Needs Decision state.
- **No general-purpose sequence or counter service.** Suffixing exists only to resolve collisions on a generated attribute.
- **No new expression dialect.** Base values are built with `DynamicExpressoEvaluator`; there is no `{n}` placeholder or similar token for administrators to learn.

## Key Concepts

**Generated value assignment.** A record that JIM owns: the object it belongs to (a Metaverse Object for an import flow, a Connected System Object for an export flow), the attribute, the value, its state and how it came to be. Because a generated value has no external source, JIM is its authority, and JIM revises its own assignment when a target rejects it. States: `Proposed` (generated and checked, not yet accepted by a target), `Committed` (accepted; sticky from here), `Remediated` (revised after a rejection; transitions to Committed when the revised value is accepted), `NeedsDecision` (remediation declined to act; awaiting an administrator).

**The boundary that keeps the pipeline clean.** JIM revises only values JIM itself generated. It never writes to a value a Connected System contributed. This is narrow and checkable in code.

**The four gates.** Every candidate passes, cheapest first: the in-run reservation set; Metaverse values; the connector space of each participating Connected System (which holds staged and optimistically applied values the target does not yet have, #1079); and, where the connector implements it, a live probe of the target that deliberately searches wider than the connector's import scope. All four are always on and are not administrator configuration. A local hit suppresses a probe, so the ordering reduces cost rather than adding it.

**Two connector capabilities.** *Probing* (can the connector check availability in the target before export?) and *rejection classification* (can the connector report that an export failed specifically because a value was already in use?). They are independent. Probing makes collisions rare; classification is what makes Collision Remediation possible at all.

**Collision Remediation.** On a classified uniqueness rejection, JIM generates the next candidate, updates the Pending Export and exports again, bounded by the attempt limit. A per-Attribute-Flow switch, on by default where any participating connector classifies rejections, disabled with an explanation where none does. Off means an ordinary export error is recorded.

**Anchoring.** In import mode a value is exported to several systems. Once any target has accepted it, the value is *anchored*: revising it would rename a live account. Remediation acts only while a value is unanchored; an anchored collision enters Needs Decision instead. Export-mode values are always unanchored by definition.

## User Stories

1. As an IDAM administrator, I want JIM to generate a unique account name from an identity's first and last name when HR does not supply one, so that I do not have to pollute the HR feed with IT-owned attributes.
2. As an IDAM administrator, I want the second "John Smith" to receive `john.smith1` automatically, and the third `john.smith2`, so that account names never collide.
3. As an IDAM administrator onboarding into an existing directory, I want JIM to check the directory itself, including OUs JIM does not import, so that a generated name does not collide with an account JIM has never seen.
4. As an IDAM administrator, when a directory rejects a generated value as already in use, I want JIM to pick the next value and try again without waking me, and to tell me afterwards what it did and why.
5. As an IDAM administrator, I do not want JIM to rename an account that already exists in production because a second system happened to have the same name; I want to be told and to decide.
6. As an IDAM administrator, I want to see on the identity exactly which values JIM generated, which it corrected, when, and because of which system, in the same views I use for everything else JIM does.
7. As an IDAM administrator, I want to be able to turn Collision Remediation off for a flow and have collisions recorded as plain export errors instead.
8. As an IDAM administrator, I want the same feature available on an export Synchronisation Rule for a value that only one target needs and that I do not want held as identity data.
9. As an IDAM administrator, I want a bulk import that forces many collisions to fail loudly if it cannot allocate a unique value within a bounded number of attempts, so that JIM never silently writes a duplicate or hangs.

## Requirements

### Functional Requirements

**Configuration**

1. "JIM generates it" is a **source type** on the Add/Edit Attribute Flow dialog, alongside "An attribute" and "An expression", available on import and export Synchronisation Rules for single-valued text target attributes.
2. The generation form captures: the base expression (the existing expression syntax and function library; no uniqueness token); the target attribute ("Assign the value to"); the collision strategy (add a number, or add a letter; start value; separator); the attempt limit; and the Collision Remediation switch.
3. Suffix position is determined by JIM: before the first `@` where the base value contains one, otherwise appended. The dialog shows a live preview of the first candidate and the collision sequence for sample inputs.
4. For an import flow, the dialog shows the **derived** list of Connected Systems the value is exported to (every Connected System with an export Attribute Flow targeting that Metaverse attribute), with each system's probe and rejection-classification capability stated, and a per-system exclusion from availability checks. The list is not editable otherwise and updates automatically as export Synchronisation Rules change. For an export flow the list is the one Connected System.
5. The Collision Remediation switch is enabled when at least one participating Connected System's connector classifies uniqueness rejections, and defaults to on. Where none does, it is rendered disabled with the reason. Where some do and some do not, it stays enabled and the system list states which systems will record an export error instead.

**Generation**

6. Generation is a contribution to the target attribute like any other mapping and participates in attribute priority (#91). It contributes only when every higher-priority contribution is silent, which delivers "only when the authoritative source supplies no value" without a bespoke flag. A source at higher priority later supplying a value supersedes the generated one, visibly.
7. Each candidate passes the four gates in order. A candidate is proposed only when all four report it free, or when the probe reports it cannot determine and the local three report it free.
8. The live probe is three-state: Found, NotFound, CouldNotDetermine. A directory applies access control to searches as a silent filter, so an empty result from an under-privileged account is indistinguishable from absence; the probe must not report NotFound in that case. (The same trap #1121 documented for the Fine-Grained Password Policy signal.) Probes are batched per candidate set where the protocol allows, so latency does not scale with attempt count.
9. A proposed value is recorded as a generated value assignment in state Proposed, keyed on the object and attribute, with the Synchronisation Rule mapping that produced it recorded as its origin.
10. A committed assignment is sticky: the value is never recomputed from the expression and never renumbered. Re-running any import or synchronisation produces the same value.
11. If no free candidate is found within the attempt limit, the object fails hard through the standard RPEI/Activity error surface, naming the attribute, the last candidate tried and the scope that rejected it. No value is written.
12. Uniqueness holds within a single run via the in-run reservation set, which persists across pages within the run and is released at run end.

**Collision Remediation**

13. When an export fails and the connector classifies the failure as a uniqueness rejection, and Collision Remediation is on for the flow, and the value is unanchored: JIM generates the next candidate, updates the assignment to Remediated, updates the Pending Export and retries within the same export run, bounded by the attempt limit. On acceptance the assignment becomes Committed with the accepted value.
14. When the value is anchored (any other target has already accepted it): JIM does not revise it. The assignment enters NeedsDecision, the export item records an error naming the rejecting system, the value, and the system that has provisioned it, and the identity, its Synchronisation Rule and the Connected System carry a needs-attention indicator.
15. When Collision Remediation is off, or the connector cannot classify the rejection: an ordinary export error is recorded and the assignment is unchanged.
16. NeedsDecision has three exits: **Allow the rename** (per identity; a confirmation names every system that will change and what it will receive; unanchors this identity so the next synchronisation and export runs carry the change, recorded as authorised by the administrator); **Retry** (releases NeedsDecision so the next export run tries the same value again, for when the conflict has been fixed at its source); **Leave it** (visible until resolved). Saving a change to the flow's configuration also releases NeedsDecision, as #1121 releases parked items. NeedsDecision never expires.
17. In import mode, a remediated value flows to every other target through ordinary synchronisation and export. Derived values (an email built from the account name) recompute and re-export the same way. Nothing special-cases the dependency.

**Transparency**

18. Every generation, remediation, NeedsDecision entry and administrator action is recorded against the assignment and surfaces through the existing causality model (#1087, #1495): a sync outcome `GeneratedValueRemediated` (tone Warning, plain label "Value corrected", technical "Collision Remediation", attribute row Set with the previous value) on the export item; an execution item error type `GeneratedValueCollisionUnresolved` (tone Error, "Needs a decision") for the anchored case; and, for the import-mode consequence that crosses execution items, a new append-only `CausalEdgeType.ExportRejectionCausedGeneratedValueRevision` with reason codes `GeneratedValueAlreadyInUse`, `GeneratedValueAnchoredElsewhere` and `GeneratedValueRenameAuthorised`. A retry within one export item is an outcome, not an edge, per the edge rules in `CausalEdgeEnums.cs`.
19. The identity's attribute history for a generated attribute is the causality Timeline scoped to that attribute, rendered by the same event model, with `Generated by JIM` and `Corrected` chips on the attribute row. No bespoke history widget.
20. The Run Profile execution summary reports counts of values generated, collisions remediated and items needing a decision via the existing stat-counter mechanism; the summary band sentence names the systems involved.

**Reuse**

21. Pattern evaluation, candidate sequencing, the four-gate oracle and assignment persistence live in `JIM.Application` as a service behind an interface, with data access threaded in from the worker as `IExpressionEvaluator` is today. The service knows nothing about which caller asked. The assignment is keyed on object and attribute so that a future workflow step or administrator action becomes another caller, not another implementation.

**Surface parity**

22. Every configuration element above (source type, generation settings, exclusions, Collision Remediation switch) is settable through the portal, the REST API (Synchronisation Rule and Attribute Flow DTOs) and PowerShell (`New-JIMSyncRuleMapping` / `Set-JIMSyncRuleMapping` for the flow; `Set-JIMSyncRule` where rule-level settings are involved) in the same PR, with tests and docs. The NeedsDecision list and its Allow-the-rename and Retry actions ship across all three surfaces likewise; REST answers `202 Accepted` for actions whose effect lands on a later run, following the #1121 pattern.

### Non-Functional Requirements

- **Performance at scale.** Local gates are single indexed lookups (the `(AttributeId, StringValue)` indexes on both Metaverse and Connected System Object attribute values already exist; the Metaverse-by-value repository method does not) or reservation-set hits. Probes are made only for candidates that clear the local gates and are batched. Target: no material throughput regression versus a non-generated text flow on a 100k-object import.
- **Batch safety.** The reservation set is thread-safe and run-scoped, generalised from `ExampleDataValueTrackerStore`.
- **Synchronisation Integrity.** Fail fast; every failure reported via RPEIs and Activities; never write a duplicate silently; never loop unbounded (`src/JIM.Application/CLAUDE.md`).
- **Air-gapped.** No external service; probes go to the Connected System the connector already talks to.
- **Append-only enums.** New `CausalEdgeType`, `CausalReasonCode`, sync outcome, RPEI error and export error members are appended, never inserted; ordinal tests updated.
- **British English throughout; proper-noun casing** for Metaverse Object, Connected System Object, Synchronisation Rule, Attribute Flow in all UI text, comments and docs.

## Examples and Scenarios

### Scenario 1: Account name generated when HR supplies none

**Given** an import Synchronisation Rule whose Account Name Attribute Flow uses "JIM generates it" with base `Lower(mv["First Name"]) + "." + Lower(mv["Last Name"])`, and an incoming identity Joe Bloggs with no account name in the feed
**When** the import runs and all four gates report `joe.bloggs` free
**Then** the assignment is created as Proposed with `joe.bloggs`, the Metaverse holds it, and the attribute shows `Generated by JIM`.

### Scenario 2: Collision caught by a local gate

**Given** a Metaverse Object already holds `joe.bloggs`
**When** a second identity generates the same base value
**Then** it receives `joe.bloggs1` without any probe being made; a third receives `joe.bloggs2`.

### Scenario 3: Intra-batch collision within one page

**Given** two new identities producing `john.smith` in the same page, neither yet persisted
**Then** the first gets `john.smith` and the second `john.smith1`; the page flush writes no duplicate.

### Scenario 4: Brownfield, caught by the probe

**Given** Corporate AD contains an account `joe.bloggs` in an OU outside the connector's import scope, and the LDAP connector implements probing
**When** generation proposes `joe.bloggs`
**Then** the probe reports Found and JIM proposes `joe.bloggs1` instead.

### Scenario 5: Collision Remediation at export

**Given** Scenario 4's account was created after the probe, or the probe reported CouldNotDetermine, so `joe.bloggs` was proposed and staged
**When** the export to Corporate AD fails with a classified uniqueness rejection and no other target has accepted the value
**Then** JIM generates `joe.bloggs1`, updates the Pending Export and the assignment (Remediated), retries in the same run, and on acceptance commits `joe.bloggs1`. The export item records `GeneratedValueRemediated`; the identity's history shows the correction with the reason; other targets receive `joe.bloggs1` on their next export.

### Scenario 6: Anchored collision enters Needs Decision

**Given** an import-mode Account Name `r.okafor` accepted by Corporate AD, and a later export to Contractor LDAP rejected as already in use
**Then** JIM does not revise the value. The assignment enters NeedsDecision; the export item records `GeneratedValueCollisionUnresolved` naming both systems; the identity, Synchronisation Rule and Connected System show needs-attention. The administrator can Allow the rename (JIM then remediates to `r.okafor1` everywhere on the next runs), Retry after fixing the conflict in Contractor LDAP, or leave it.

### Scenario 7: Allocation exhaustion fails hard

**Given** an attempt limit and a pathological population that exhausts it
**Then** the object fails with an attributed RPEI error describing the exhausted generation, and no duplicate is written.

### Scenario 8: Re-run is stable

**Given** Scenario 5 has committed `joe.bloggs1`
**When** the same import runs again, in full or delta
**Then** the identity keeps `joe.bloggs1`; nothing is recomputed or renumbered.

### Scenario 9: Export-mode generation

**Given** an export Synchronisation Rule to a ticketing system whose Attribute Flow for a login name uses "JIM generates it"
**Then** the assignment is keyed on the Connected System Object, uniqueness is checked against that one system, and Collision Remediation always applies because an export-mode value is never anchored. The Metaverse is untouched.

### Scenario 10: Collision Remediation off

**Given** the switch is off for the flow in Scenario 5
**Then** the export records an ordinary export error for `joe.bloggs`, the assignment stays Proposed, and the administrator resolves it.

## UI Mocks

Mockups for all six screens, plus the design explainers and diagrams (assignment lifecycle, the service and its callers, the three Set Value shapes), are linked in the document header. Screens extend existing surfaces; none is a new page. Dialog structure and row chips follow the shipped patterns from #843 (Value processing section) and #223 (`Initial Export Only` checkbox and chip); events follow the causality idiom from #1087 and #1495.

| # | Screen | Route | Shows |
|---|--------|-------|-------|
| 1 | Add Attribute Flow, source type | `/admin/sync-rules/{id}` | "JIM generates it" beside Attribute and Expression |
| 2 | Generation form | `/admin/sync-rules/{id}` | Base expression, "Assign the value to", collision strategy, derived system list with both capabilities, Collision Remediation on, live preview |
| 3 | Collision Remediation, unavailable and off | `/admin/sync-rules/{id}` | Disabled-with-reason and off-with-consequence states |
| 4 | Identity attribute history | `/t/{type}/v/{id}` | Causality Timeline scoped to the attribute; `Generated by JIM` and `Corrected` chips |
| 5 | Activity, Timeline view | `/activity/{id}` | Summary band and pills; remediated (Warning) and needs-a-decision (Error) events with the three exits |
| 6 | Allow the rename | `/t/{type}/v/{id}` | Confirmation naming every system that will change, and when |

## Integration Testing

Unit and database-tier tests cannot prove the parts of this feature that matter most, because those parts are interactions with a real directory: the probe's silent-ACL behaviour, an export rejected by the target, and a rename carried through a live account. Two integration requirements follow.

### A dedicated scenario

A new scenario, `Invoke-Scenario22-UniqueValueGeneration.ps1` (numbered after the current highest), exercises every positive and negative path and every configuration permutation against the standard Samba AD and OpenLDAP targets plus a CSV target that can neither probe nor classify. Setup, run and teardown follow the runner's conventions (`Run-IntegrationTests.ps1`; scenario scripts are never invoked directly). It must cover, at minimum:

| Area | Cases |
|------|-------|
| Modes | Import Attribute Flow (value in the Metaverse, exported to two directories); Export Attribute Flow (value on one Connected System Object only, Metaverse untouched) |
| Strategy | Number and letter suffixes; start value; separator; suffix placed before `@` for an email-shaped value and appended otherwise |
| Gates | Collision caught by the Metaverse; by a connector space holding an optimistically applied value the target does not yet have; intra-batch within one page; brownfield object in an OU outside the connector's import scope caught by the probe; probe reporting CouldNotDetermine (under-privileged bind) falling back to local gates without reporting NotFound |
| Collision Remediation | Post-probe collision remediated within the export run and committed; the accepted value reaching the second directory on its next export; remediation off recording an ordinary export error; a CSV target with no classification recording an ordinary export error while the LDAP targets remediate (mixed capability) |
| Anchoring | Value accepted by one directory then rejected by the other: Needs Decision entered, no rename performed, needs-attention indicators present; each exit exercised: Allow the rename (rename carried out on the next runs and recorded as authorised), Retry after removing the conflicting object, release on flow configuration change |
| Failure | Attempt limit exhausted: object failed via RPEI, nothing written |
| Stability | Full and delta re-runs leave committed values unchanged; a higher-priority source later supplying a value supersedes the generated one visibly |
| Transparency | Assertions against the Activity's outcomes and causal edges (`GeneratedValueRemediated`, `GeneratedValueCollisionUnresolved`, the revision edge and its reason codes) and against the identity's attribute history |
| Surface parity | The same flow configured through the REST API and through PowerShell produces identical behaviour; Needs Decision listing and actions exercised through both |

### Converting existing scenarios

Because JIM could not generate identifiers before this feature, the shared HR feed (`Generate-TestCSV.ps1`) carries IT-owned attributes as source data: `samAccountName`, `email` and `userPrincipalName`. Scenario 1 (HR to Identity Directory) imports `samAccountName` to Account Name from HR and exports it to the directory, which is the unrepresentative shape this feature exists to remove; Scenarios 2, 8, 10, 12, 13, 15, 17 and 18 reference the same columns. As part of this feature, every existing scenario is audited against one criterion: **an IT-owned attribute that flows from a source feed into the Metaverse is converted to generation; an attribute used as a join key or external identifier (the cross-domain scenarios) is kept.** Scenario 1 is the canonical conversion. The generator gains a switch so converted scenarios receive feeds without those columns while unconverted ones are unchanged. Converted scenarios must still pass with identical downstream expectations, which is itself a regression test of the feature under realistic load.

## Constraints

- Must reuse `IExpressionEvaluator`; the evaluator stays stateless and side-effect-free. Uniqueness state (reservations, lookups, probes, assignments) lives in the application service and is threaded into the sync engine the way the evaluator is.
- EF Core migrations are append-only. New tables and columns (assignments; mapping configuration) are new migrations. The value-lookup indexes already exist.
- Raw SQL over EF projection for any new worker hot-path lookup (`src/CLAUDE.md`).
- `JIM.Web` must not gain connector dependencies.
- Existing `CausalEdgeType` / `CausalReasonCode` / outcome / error enums are extended only by appending.

## Affected Areas

| Area | Impact |
|------|--------|
| Models | `SyncRuleMapping`: source type "generated", generation settings (strategy, start, separator, attempt limit), per-system exclusions, `CollisionRemediation` flag. New `GeneratedValueAssignment` (object reference, attribute, value, state, origin, timestamps) and its enums. Appended members on `CausalEdgeType`, `CausalReasonCode`, sync outcome type, `ActivityRunProfileExecutionItemErrorType`, `ConnectedSystemExportErrorType`. |
| Interfaces | New optional connector capability `IConnectorUniquenessProbe` (three-state, batched). Uniqueness-rejection classification added to the export error path; LDAP maps `EntryAlreadyExists` / attribute-uniqueness `ConstraintViolation`; SCIM maps `409`. |
| Application | New unique value service (pattern, candidates, four-gate oracle, assignment persistence) in `JIM.Application/Servers/`. `SyncEngine.AttributeFlow.cs`: generated source type evaluated as a contribution under attribute priority; sticky assignment short-circuits recomputation. `ExportExecutionServer`: classified rejection, remediation retry loop, anchoring check, NeedsDecision entry. `ExportCausalEdgeBuilder`: the new edge. NeedsDecision release on configuration change in `ConnectedSystemServer`, mirroring `ReleaseParkedInitialPasswordsIfDeliveryChangedAsync`. |
| Data | Repository methods: Metaverse-by-attribute-value lookup (index exists); assignment CRUD; NeedsDecision listing. Reuse of connector-space by-value lookups. |
| Worker | Own and pass the reservation set across pages; thread the service into the import processors; remediation loop inside the export processor. |
| Web | `SyncRuleAttributeFlowTab.razor`: source type, generation form, derived system list, Collision Remediation switch. Identity page: attribute chips and scoped Timeline. Activity page: new event labels, tones and the Allow/Retry actions. Needs-attention indicators on Synchronisation Rule and Connected System lists. |
| API / PowerShell | Attribute Flow DTOs and cmdlets gain the generation settings and switch; NeedsDecision list and actions (`202 Accepted` with optional wait); Pester and API tests. |
| Tests | Unit: candidate sequencing, suffix position, four-gate ordering and probe three-state handling, reservation set, stickiness, priority interaction, anchoring, remediation loop, exhaustion, enum ordinals. Database tier: assignment persistence and by-value lookups. Integration: a dedicated scenario covering every positive and negative path and configuration permutation, plus conversion of existing scenarios that source IT-owned attributes from HR feeds (see Integration Testing). bUnit: causality rendering of the new outcomes. Red first. |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/` Synchronisation Rules and expressions concepts | New page or section: generated values, the four gates, Collision Remediation, anchoring, Needs Decision and its exits, import versus export mode. |
| `docs/` connectors | Which connectors probe and which classify uniqueness rejections. |
| `CHANGELOG.md` | `✨` entries for Unique Value Generation and Collision Remediation under `[Unreleased]`. |
| `engineering/` | Developer guide: the unique value service, assignment model and its reuse seam; causality reference: new outcome, edge and reason members. |

## Dependencies

- **#1087 / #1495 causality views (landed).** All transparency requirements are expressed in that model; nothing bespoke.
- **#1121 Initial Password Provisioning (Phases 1-3 landed).** Parked-state pattern (`Pending` / `Parked`, release on configuration change) and the queue-and-follow pattern for portal and API actions are reused. Its password delivery lane is explicitly *not* reused for Metaverse data.
- **#1079 optimistic export apply (landed).** The reason the connector space is a distinct gate from the probe: it holds values the target does not yet have.
- **#223 Initial Export Only (landed).** The per-mapping flag pattern (model field, migration, dialog control, row chip, engine gating) that the generation configuration follows.
- **#843 Inbound Value Processing (Done).** The per-mapping post-processing precedent and the `ConnectedNoValue` result.
- **#91 attribute priority (landed).** Generation participates as a contribution; conditional generation and supersession by a real source both fall out of it.
- **#549 example-data expressions (closed).** Shipped with its interim `ExampleDataValueTrackerStore`; this feature owns the generalised primitive; migrating example data onto it is a non-blocking follow-up.
- No blocking external dependencies.

## Design Decisions

Taken during review of the linked artefact (July to September 2026). Recorded here so implementation planning does not re-open them.

1. **Mechanism:** a dedicated "JIM generates it" source type, with the base value built by the existing expression engine and uniqueness and suffixing applied by the unique value service. Not an expression function (stateless, cached, synchronous evaluator; no place for I/O or a mid-string suffix).
2. **Uniqueness layers:** all four gates always on and not configurable. The set of systems is derived from export Attribute Flows (import mode) or is the one target (export mode), with per-system exclusion only.
3. **Suffix:** numeric by default, first value bare, start 1, no separator; letters and a separator as options; position determined by JIM (before the first `@`, else appended); no authoring token.
4. **Attempt limit:** 1000, hard fail via RPEI on exhaustion.
5. **Export arbitration:** Option B, self-healing. Collision Remediation is a per-Attribute-Flow switch, default on where any participating connector classifies rejections, disabled with reason otherwise. Not per Connected System: the decision is about the value, the anchoring rule already protects live accounts, and per-system capability is shown as information rather than policy.
6. **Feedback to JIM:** a JIM-owned generated value assignment, keyed on object and attribute, replaces the earlier confirming-import model; no import Attribute Flow is required for remediation to work, and generated values are sticky.
7. **Anchoring:** remediate while unanchored; escalate to Needs Decision once any target has accepted the value.
8. **Needs Decision exits:** Allow the rename, Retry, Leave it; release on flow configuration change; never expires.
9. **Set Value:** deferred out of #242 (2026-09-16). If it returns, it takes the schedule-consistent shape described in the artefact (assignment written now, propagated by the next synchronisation and export runs), never an immediate lane; and it brings the delta-synchronisation change needed to pick up Metaverse-side changes.
10. **Both modes:** import and export Attribute Flows are first-class.
11. **Causality:** retry within an item is an outcome; the import-mode cross-item consequence is an edge; wording derived from codes at render time.

## Acceptance Criteria

- [ ] "JIM generates it" is available as a source type on import and export Attribute Flows for single-valued text targets, with the generation form and live preview as mocked (FR 1-3).
- [ ] The derived system list shows probe and classification capability per system and supports exclusion; the Collision Remediation switch follows FR 5 in all three states.
- [ ] Generation participates in attribute priority; a higher-priority source supersedes it visibly (FR 6).
- [ ] Candidates pass the four gates in order; the probe is three-state and never reports NotFound on an undetermined result; probes are batched (FR 7-8).
- [ ] Assignments are persisted, keyed on object and attribute, and committed values are sticky across full and delta runs (FR 9-10, Scenario 8).
- [ ] Intra-run uniqueness holds across pages (FR 12, Scenario 3).
- [ ] Exhaustion fails the object with an attributed RPEI error and writes nothing (FR 11, Scenario 7).
- [ ] A classified, unanchored uniqueness rejection is remediated within the export run; the accepted value is committed and flows to other targets on their next export (FR 13, 17, Scenario 5).
- [ ] An anchored rejection enters Needs Decision with the three exits, needs-attention indicators, release on configuration change and no expiry (FR 14, 16, Scenario 6).
- [ ] Collision Remediation off, or an unclassified rejection, records an ordinary export error (FR 15, Scenario 10).
- [ ] Export-mode generation keys on the Connected System Object and never touches the Metaverse (Scenario 9).
- [ ] All events render through the causality model with the members named in FR 18; the identity attribute history is the scoped Timeline; ordinal tests cover every appended enum member (FR 18-20).
- [ ] The unique value service is caller-agnostic and unit-testable without a synchronisation run (FR 21).
- [ ] Portal, REST and PowerShell parity for configuration and for the Needs Decision list and actions, with tests and docs (FR 22).
- [ ] `JIM.Web` gains no connector dependency.
- [ ] A dedicated integration scenario covers every case in the Integration Testing table and is green in the full suite.
- [ ] Existing scenarios that source IT-owned attributes from HR feeds are converted to generation per the stated criterion, with join-key uses kept, and remain green.
- [ ] Public docs and `CHANGELOG.md` updated; `dotnet build JIM.sln` and `dotnet test JIM.sln` pass with zero errors and warnings; new behaviour covered by tests written red-first; brownfield probe and remediation covered by integration scenarios.

## Open Questions

1. **Derived values in the same flow.** Email built from the generated account name can either read the assigned value within the same object's flow (requires exposing values assigned earlier in the flow to later mappings, in dependency order) or lag by one synchronisation. The artefact assumes the former. Decide at implementation planning; either satisfies the requirements.
2. **Attempt limit scope.** Per identity per run (recommended) or per identity for ever. Per run is simpler and matches "give up after N tries".
3. **Probe batch size** for the LDAP filter, and whether SCIM filtering is worth a probe implementation in v1 or classification only.
4. **Migration of example data** onto the generalised primitive: follow-up issue, not v1.

## Additional Context

- Expression engine: `src/JIM.Application/Expressions/DynamicExpressoEvaluator.cs`; context built with `metaverseAttributes: null` in `ProcessExpressionMapping` (`src/JIM.Application/Servers/SyncEngine.AttributeFlow.cs`), which is the constraint behind Open Question 1.
- Attribute Flow evaluation and mapping model: `SyncEngine.cs`, `SyncEngine.AttributeFlow.cs`, `src/JIM.Models/Logic/SyncRuleMapping.cs` (`InboundValueProcessing`, `CaseNormalisation`, `Priority`, `NullIsValue`, `InitialExportOnly`).
- Page-based worker processing and the delta selection from Connected System Objects modified since the watermark: `SyncFullSyncTaskProcessor.cs`, `SyncDeltaSyncTaskProcessor.cs`, `SyncTaskProcessorBase.cs`.
- Export execution, optimistic apply and error classification: `ExportExecutionServer.cs` (`ApplyOptimisticExportUpdatesAsync`), `LdapConnectorExport.cs` (`IsPlaceholderConstraintViolation` as the classification precedent), `ConnectedSystemExportErrorType`.
- Pending Export reconciliation: `PendingExportReconciliationResult` (Confirmed / Retry / Failed), `SyncEngine.Reconciliation.cs`.
- Causality: `src/JIM.Models/Activities/CausalEdgeEnums.cs` (append-only; edge rules in the type comment), `src/JIM.Web/Causality/` (`CausalityEvent`, `CausalityEnums`, `CausalityCauseWording`), `src/JIM.Worker/Processors/ExportCausalEdgeBuilder.cs`.
- Parked-state and queue-and-follow precedents: `PendingPasswordChangeStatus`, `ReleaseParkedInitialPasswordsIfDeliveryChangedAsync` in `ConnectedSystemServer.cs`, `PasswordChangeOutcomeWaiter` and the `wait` / `202 Accepted` shape in `MetaverseController.cs`; the password delivery lane `src/JIM.Worker/PasswordDeliveryService.cs` (not reused for Metaverse data).
- Connector capability precedent for a live target read: `IConnectorPasswordPolicyDiscovery` and its three-state Fine-Grained signal, including the silent-ACL-filter finding recorded in `engineering/plans/INITIAL_PASSWORD_PROVISIONING.md`.
- Existing per-run collision primitive to generalise: `src/JIM.Application/Servers/ExampleDataValueTrackerStore.cs`.
- Value-lookup building blocks: `GetConnectedSystemObjectIdByAttributeValueAsync` / `GetConnectedSystemObjectsByAttributeValuesAsync`; indexes `IX_MetaverseObjectAttributeValues_AttributeId_StringValue` and `IX_ConnectedSystemObjectAttributeValues_AttributeId_StringValue`.
