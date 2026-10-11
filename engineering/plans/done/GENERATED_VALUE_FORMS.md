# Generated Value Forms - Implementation Plan

- **Status:** Done
- **Note:** Anchoring over values written only by an export expression (Phase 1, step 3) is deferred: Collision Remediation still counts only values written to another target unchanged by a direct export Attribute Flow.
- **Created:** 2026-10-11
- **Issue:** [#2023](https://github.com/TetronIO/JIM/issues/2023) (absorbs [#2014](https://github.com/TetronIO/JIM/issues/2014))
- **PRD:** [PRD_GENERATED_VALUE_FORMS.md](../../prd/done/PRD_GENERATED_VALUE_FORMS.md)

## Overview

A Generated Value Attribute Flow checks only the attribute it generates. Values built from it (Email from Account Name, UPN from Email) are corrected only after a target refuses them, and nothing refuses a duplicate `mail` or `proxyAddresses`. This plan delivers the PRD: before a candidate is issued, JIM checks every value built from it on the generated flow's own Synchronisation Rule (its **forms**; user-facing wording "values built from Account Name"), all or nothing, with a **Keep unique with Account Name** switch per derived value, one probe search per Connected System and route, and parity across the portal, REST API and PowerShell.

Three facts about the code shape the plan:

- **A candidate is chosen before its forms exist.** Generation resolves at derived level L (`SyncTaskProcessorBase.ResolveGenerationsAndDerivedLevelsAsync`); Email and UPN are derived at L+1 and L+2.
- **Everything below the generation server is single-attribute:** the gates, the reservation set, `UniquenessProbeRequest`, and the probe session's latching.
- **Anchoring sees only direct exports.** Collision Remediation's anchoring check (`CollisionRemediationRun`, `IsAnchoredAsync` over `ComputeParticipatingTargets`) only considers systems receiving the generated attribute unchanged, so correcting Account Name after a UPN refusal can silently rename a mailbox that has already accepted the Email.

## Delivery

Three sequential pull requests from one branch, each complete on its own (surface parity, docs, changelog), the branch restarted from `main` after each merge.

| Phase | Pull request | Closes |
|-------|--------------|--------|
| 1 | Shared "built from" resolver, the **Keep unique** switch, anchoring over built values | #2014 |
| 2 | One probe search per Connected System and route | – |
| 3 | Values built from the generated value checked before issuing | #2023 |

Phase 2 precedes Phase 3 deliberately: checking forms without batched probing roughly triples directory searches on an initial load, breaking the PRD's directory-cost requirement on `main` in between.

## Design

### Shared resolver (Phase 1)

`src/JIM.Application/UniqueValues/GeneratedValueBuiltValues.cs` replaces the private `SourceResolver` nested in `GeneratedValueRejectionAttribution`. It is a pure index built once from the import rules, export rules and `DerivedFlowGraph`, with two views:

- **Forward:** generated mapping to every value built from it alone. Each is classified as a **Form** (every derived hop on the generated flow's own rule), **CorrectedOnly** (a hop on another rule), **BuiltFromSeveral** (more than one generated value), or **NotCheckable** (non-text, or a multi-valued export expression output). Each carries its host rule, its path ("via Email") and its switch.
- **Reverse:** export change to the generated value it is built from, for attribution.

Result records live in `src/JIM.Models/Logic/`. One resolver feeds attribution, anchoring, the panel, REST, the Worker caches, Sync Preview and remediation, and it is the only reader of the switch.

### Keep unique switch (Phase 1)

`SyncRuleMapping.KeepUnique` (bool, default true), threaded the way `InitialExportOnly` is: `JimDbContext` and a migration defaulting existing mappings to true, snapshot key `keepUnique` with a `ConfigurationChangeClassifier` entry, and `SyncRuleMappingSettingsUpdate` / `ApplySyncRuleMappingSettings`. Switching it off is refused on a direct or generated mapping; it is accepted on any expression mapping and has no effect until the expression reads exactly one generated value, because whether it does depends on other mappings that may be saved in any order.

Semantics are per value: switching off Email does not stop UPN-via-Email being checked; Email is still evaluated on the way, just not checked or corrected itself. Changing the switch releases any held decision for the generated value it is built from.

### Evaluating forms: replay the engine on a scratch copy (Phase 3)

Re-implementing derivation against a dictionary would diverge from the real result (priority gate, no-value rules, Null Is Value, text processing, provenance). Instead, `SyncEngine.EvaluateDerivedMappings` is extracted from the loop in `EvaluateDerivedLevel`; for each candidate JIM clones the Metaverse Object, applies the candidate through `ApplyGeneratedValue`, and replays only the backward closure of the forms, level by level. Parity tests assert the replay equals the real level output.

- **Applicability:** each hop passes the Attribute Priority incumbent check; a chain that loses priority anywhere does not move with the candidate and is not checked.
- **Wait or not:** wait only when an input outside the chain is absent (`ExpressionInputResolver.FindMissingInputs`); a chain that itself yields no value has nothing to check.
- **Fail closed:** an expression failure means the candidate is not issued, recorded with the existing `ExpressionEvaluationError`.
- **Export expression forms:** evaluated against the scratch Metaverse Object through an internal wrapper over `EvaluateExportExpressionSource`, for export rules of the same Metaverse Object Type, with no per-object scoping (as today's direct participation).
- **The generation server stays rule-agnostic:** `GenerationRequest` carries an `IGeneratedValueFormSource` (candidate to checks, cached per candidate, so the probe window can look ahead), built by one shared request builder used by the Worker, Sync Preview and Collision Remediation.

### Gates over checks (Phase 3)

A request holds one or more checks: the generated value plus each applicable form, each with its own attribute, value, scope, connector space attributes, probe targets, exempt values and rejected values. Every gate filters checks; a request survives only if all its checks do. With no forms there is exactly one check, so today's queries and messages stay unchanged.

| Gate | Generated value | Forms |
|------|-----------------|-------|
| Reservation | yes | yes |
| Retired | yes | no |
| Metaverse value | yes | yes |
| Connector space | yes | yes |
| Other generated assignments | yes | yes |
| Probe | yes | yes |

`UniqueValueReservationSet` gains `TryReserveAll` (with rollback) and `Release(owner, key)`.

### Probe batching (Phase 2)

`IConnectorUniquenessProbe.ProbeAttributesAsync` is a default interface member looping `ProbeAsync`, so the SCIM 2.0 Client and SQL Connectors are unchanged. The LDAP Connector overrides it: the route is chosen per attribute (`ChooseForestRoute`), then one combined Global Catalog search and one partition search per root, matched per attribute with each attribute's own control value and the `proxyAddresses` alias. A combined search that reaches the size limit or is refused is re-run per attribute, which answers each exactly as a search for it alone would; an LDAP failure (unreachable, timed out) fails every attribute at once, since asking again per attribute only multiplies the wait. A Global Catalog that refuses or fails hands all its attributes to one domain search; one blind to a single attribute's control value hands over that attribute alone. The probe session gains a per-system batch call allowing the timeout once per attribute; a failure for one attribute latches the system for the rest of the run, while the same call's other answers stand. The probe gate groups targets by Connected System, which also ends the double search, and the double count in the run warning, when a value goes to two attributes of one system.

### Collision Remediation and re-election (Phase 3)

Remediation loads the Metaverse Object, the hosting Connected System Object and an `AttributePriorityContext` before `RegenerateAsync`, so the next value is checked with its forms (against JIM's records; remediation has no probe). The refused form value goes into that form's rejected values; a wait or form failure becomes an ordinary export error. Re-election carries the surviving Connected System Object in memory. Derived-input marks extend to inputs of export expression forms, so a waiting value is retried when another system supplies the input.

### Reporting (Phase 3)

Two appended `SyncOutcomeType` members, `GeneratedValueWaiting` and `GeneratedValueCandidatesSkipped`, with the causality display map, model builder, summary wording and ordinal tests, `engineering/CAUSALITY_REFERENCE.md` regenerated, and a Sync Preview message code for waits.

## Decisions

- **D1. Anchoring covers every value built from the generated value, whatever its switch** (Phase 1). A switched-off value still moves when the generated value is corrected, so a system that has accepted it anchors the generated value. More corrections wait for a decision instead of silently renaming an account in use.
- **D2. A generated value's per-system Exclusions apply to every value built from it bound for that system.** Exclusions turn checks off per system; Keep unique turns them off per value. Neither governs correction.
- **D3. Residual risk, documented rather than built:** a value produced only by an export expression is invisible to the next run's checks between synchronisation and export. Collision Remediation still corrects it if refused.
- **D4. Chains through non-text or multi-valued values are out of scope** this release, and listed as not checked.
- **D5. Waiting stays**, and the docs advise scoping provisioning export rules on the generated value (PRD Decision 5).

## Phases

### Phase 1: Shared resolver, Keep unique switch, anchoring (closes #2014) ✅

1. Characterisation tests pinning today's attribution; extract the resolver with forward-view tests.
2. Attribution honours the switch.
3. Anchoring and the decision server's participating systems widened to every value built from the generated value. Values written by an export expression are not anchored yet: they are evaluated against the Metaverse Object in Phase 3, which is when anchoring can read them.
4. Model, migration, change history, settings path, held-decision release; `RequiresPostgres` tests.
5. REST, PowerShell, portal (UI mock approved first), docs, changelog. The provisioning scoping advice (D5) ships with Phase 3, where waiting first appears. Also added at review: the Attribute Flow tab's Mapping Type filter gains Derived, so derived flows and generated values can be shown together or apart.

### Phase 2: One probe search per Connected System and route ✅

1. Default interface member.
2. LDAP combined searches.
3. Session batch call.
4. Gate grouping.
5. LDAP Connector docs, changelog.

### Phase 3: Values built from the generated value checked before issuing (closes #2023) ✅

1. Engine extraction with parity tests.
2. Form source: applicability, wait, fail closed.
3. Gates over checks, reservations with rollback.
4. Probe checks, remediation and re-election wiring, derived-input marks.
5. Outcomes, causality reference, previews, Exclusions widening.
6. Surfaces, docs, changelog; PRD and plan to `done/`.

## Verification

- Per phase: targeted build and tests, then the whole solution with zero warnings; `dotnet ef migrations has-pending-model-changes` and `MigrationDesignerChainTests` for the migration; the OpenAPI generator after DTO changes; Pester for the cmdlets.
- Runtime at Medium (a single-page run does not count): Scenario 023 on Samba AD for Phases 1 and 2 (Phase 2 also OpenLDAP Large, with search counts from the diagnostics); Scenario 001 with `-GenerateAccountName -DeriveFromAccountName`, a seeded `proxyAddresses` alias and a UPN in another domain, plus Scenarios 023 and 026, for Phase 3, comparing generation time on Medium and Scale initial loads.

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| The scratch replay diverges from the real derivation | One implementation by construction (the extracted engine loop), pinned by parity tests |
| Checking forms multiplies directory searches | Phase 2 lands first; one search per system and route |
| Forms consume the candidate budget faster | Same `AttemptLimit`; rejections are reported per candidate so exhaustion is explainable |
| Waiting holds back new starters | Reported as Waiting on the execution item; docs advise provisioning scope on the generated value |
