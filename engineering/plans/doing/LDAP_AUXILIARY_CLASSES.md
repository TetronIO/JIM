# LDAP Auxiliary Object Classes (RFC 4512 Directories)

- **Status:** Doing
- **Issue:** [#492](https://github.com/TetronIO/JIM/issues/492)
- **Blocked by:** [#845](https://github.com/TetronIO/JIM/issues/845) (classification tag model)
- **Follow-on:** [#1168](https://github.com/TetronIO/JIM/issues/1168) (Advanced objectClass mode, deferred)

## Overview

On RFC 4512 directories (OpenLDAP, 389 Directory Server), auxiliary object classes attach to entries rather than to the schema, so JIM's SUP-chain-only schema discovery never surfaces their attributes, and JIM cannot provision auxiliary-typed objects because the export path writes a single `objectClass` value. This plan implements the design agreed on #492: admin-driven auxiliary class merging as the source of truth, discovery aids (DIT Content Rules and entry sampling) as suggestions only, Managed-only export-side `objectClass` computation with per-entry delta convergence, and structural-first import precedence.

Design references: issue #492 (full requirements and rationale), the #845 comment thread (data model split), and the two design artefacts produced during refinement (visual explainer and portal UI mock, linked from the session that authored this plan).

## Business Value

- Customers on non-AD directories can import and export attributes contributed by auxiliary classes (`inetOrgPerson` + `posixAccount` is near-universal in OpenLDAP estates); today those attributes are invisible to JIM.
- Customers with object populations defined by an auxiliary class (generic structural carrier + auxiliary identity) can have JIM create those objects; traditional ILM solutions forced bespoke connectors for exactly this population.
- Managed `objectClass` semantics remove a class of export failure (`objectClassViolation`) that admins would otherwise have to reason about themselves.

## Technical Architecture

### Current state

- `LdapConnectorSchema.cs` > `GetRfcSchemaAsync` walks each class's SUP chain via `CollectClassAttributes`; auxiliary classes are parsed (`Rfc4512ObjectClassKind.Auxiliary`) but merged nowhere. The subschema query requests only `objectClasses` and `attributeTypes`.
- `LdapConnector.cs` > `Include Auxiliary Classes` setting exposes auxiliary classes as their own object types (import side of the aux-typed scenario).
- `LdapConnectorExport.cs` > `GetObjectClass` returns a single `objectClass` value (preferring an undocumented flowed value over the type name); `BuildAddRequestWithOverflow` writes it single-valued.
- `LdapConnectorImport.cs` > `ConvertLdapResults` matches an entry to the first selected object type in the entry's `objectClass` value order, which relies on AD's most-specific-first ordering; RFC directories do not guarantee ordering.

### Proposed changes (summary; full rationale in #492)

1. **Data model:** `ConnectedSystemObjectTypeExtension` (base type FK + extension type FK, unique pair, cascade delete) persists the admin's per-structural-type auxiliary class selection; a nullable `StructuralCarrierObjectTypeId` FK on `ConnectedSystemObjectType` names the carrier for aux-typed provisioning; discovery results persist per Connected System (run metadata: scope, timestamps, entries read, status, Activity id, initiating user) and per type (auxiliary class name + entry count). Class-kind classification comes from #845's tag model.
2. **Parser:** `Rfc4512SchemaParser` gains DIT Content Rule parsing (rules are keyed by the structural class's OID, so an OID-keyed class lookup is added; a rule's own MUST/MAY/NOT lists are applied, not just its AUX references).
3. **Discovery merge:** `GetRfcSchemaAsync` merges enabled extensions' MUST/MAY attributes (including their SUP chains) into the structural type, as Optional, with provenance recorded via the existing `ConnectorSchemaAttribute.ClassName`.
4. **Usage discovery:** a worker task (Activity-tracked, cancellable, one per Connected System at a time) reads entries' `objectClass` values in one of two scopes: quick sample (first N per structural class, N configurable) or full scan (every entry, requesting only `objectClass`). Results persist as suggestions; they never change the schema.
5. **Export:** JIM computes multi-valued `objectClass`. On add: structural class (or carrier + auxiliary for aux-typed types) plus enabled auxiliary classes whose attributes are in the add. On modify: an entry gains an enabled auxiliary class in the same operation that first flows one of that class's attributes (delta convergence); the class's MUSTs are enforced at that moment with a clear error naming missing attributes. `objectClass` is blocked as an Attribute Flow target (credential-attribute pattern); the legacy flowed-value preference in `GetObjectClass` is removed.
6. **Import:** type matching becomes order-independent and structural-first (using persisted class-kind); an auxiliary-typed match applies only when no selected structural class matches, preventing double-import.

## Implementation Phases

TDD throughout: each phase's tests are written red-first. AD-path code (`GetSchemaAsync`, `GetObjectClassAttributesRecursively`) is deliberately untouched; AD regression coverage is asserted, not modified.

### Phase 1: Data model and persistence

- `ConnectedSystemObjectTypeExtension`, `StructuralCarrierObjectTypeId`, discovery run/result entities in `JIM.Models/Staging`; EF migration.
- Repository methods (`JIM.PostgresData`) + `ConnectedSystemServer` methods on the application layer (UI/API never bypass layers). Mutating paths follow the `AsTracking` + `RequireTracked` rules.
- Tests: model tests; `RequiresPostgres` round-trip tests on a `NoTracking` context for each mutating path; cascade behaviour on object type removal (refresh data-loss semantics).

### Phase 2: DIT Content Rule parsing

- `Rfc4512SchemaParser`: parse `dITContentRules` values (OID, NAME, AUX, MUST, MAY, NOT); add OID-keyed class dictionary alongside the name-keyed one.
- Tests: parser unit tests over real-world rule strings, including NOT lists, multi-value AUX (`$` separators), and unknown OIDs.

### Phase 3: Schema discovery merge

- Subschema request additionally asks for `dITContentRules`. `GetRfcSchemaAsync` merges enabled extension types' attributes (Optional, `ClassName` provenance); DIT Content Rule findings persist as suggestions; class-kind tags populated per #845.
- Refresh reconciliation: admin selections survive refresh unless the auxiliary type itself disappears (FK cascade); the existing schema refresh preflight/confirmation surfaces removals.
- Tests: discovery unit tests (mocked subschema responses); merge semantics (aux MUST arrives Optional; dedupe against structural attributes; provenance correct); refresh reconciliation tests.

### Phase 4: Usage discovery worker task

- New worker task + Activity (progress counters, cancellation) implementing quick sample and full scan (objectClass-only paged reads per selected structural class); persisted results replace the previous run's; one-at-a-time guard per Connected System.
- Tests: worker task unit tests (paging, counting, cancellation persists partial results marked partial); Activity progress conformance.

### Phase 5: Export objectClass management

- `LdapConnectorExport`: multi-valued `objectClass` on add; delta class-add on modify; MUST enforcement at class-add time; carrier class for aux-typed adds; remove the legacy flowed-`objectClass` preference; block `objectClass` as a flow target.
- All failures surface via Pending Export / RPEI error reporting; batch summary statistics logged per Synchronisation Integrity rules.
- Tests: export request construction tests for every branch above; error-path tests asserting the missing-MUST message names the attributes.

### Phase 6: Import type matching

- `ConvertLdapResults` (and the USN/tombstone paths): order-independent, structural-first matching via persisted class-kind; auxiliary-typed match only when no structural match; regression tests proving one entry cannot yield two Connected System Objects.

### Phase 7: Portal UI

- `ConnectedSystemSchemaTab`: Auxiliary Classes panel per structural type (RFC-path systems only): merge switches, suggestion chips (DIT Content Rules + usage counts), discovery scope controls (quick sample with editable N / full scan), persisted status strip (never run / running with Activity link and cancel / last completed / cancelled-partial); Structural Carrier Class select on aux-typed types; merged rows appear in the existing attribute table via the existing Class column.
- Follows `JIM.Web/CLAUDE.md` conventions (panel spacing, alerts, gating). `dotnet build` required; bUnit coverage in `test/JIM.Web.Tests` where the scope rules allow.
- The UI mock produced during design is the reference for placement and states.

### Phase 8: Surface parity (REST + PowerShell) and docs

- REST: read/update endpoints for a type's extensions and carrier (ID-based writes), trigger-discovery endpoint, discovery-status read; DTOs + OpenAPI docs; tests in `JIM.Web.Api.Tests`.
- PowerShell: cmdlet support for reading/setting auxiliary class merges and carrier, and starting/observing discovery; Pester tests; documented output shapes.
- `docs/` LDAP connector documentation updated in the same PR; `CHANGELOG.md` entries (✨) per changelog rules.

### Phase 9: Integration testing

New OpenLDAP-backed scenario via `Run-IntegrationTests.ps1` (reusing the existing OpenLDAP container infrastructure, cf. Scenario 8): merge selection > import aux attributes > export flows add the class per entry (delta convergence) > aux-typed provisioning with carrier > full-scan discovery run end-to-end.

**This phase must be authored in the devcontainer, against a running stack.** Every assertion below depends on what the test directory actually serves, and the phase starts by changing the image that serves it. Written from the Windows host it would be unverifiable in a way the earlier phases were not: those had unit coverage standing behind them, and an integration scenario has nothing behind it but the run.

#### 9a. Test directory schema (prerequisite; changes the shared image)

`test/integration/docker/openldap/scripts/01-add-second-suffix.sh` already loads a `cn=jim-extensions` schema block on the `1.3.6.1.4.1.99999` test arc. Add to it:

- a **JIM-owned auxiliary class**, so the scenario does not depend on whichever schemas the `bitnamilegacy/openldap` base image happens to load. Give it one MUST and two MAYs, so both the MUST-enforcement path and the ordinary merge path are exercisable.
- a **DIT Content Rule** on `jimPerson` naming that class, so the "suggested: DIT Content Rule" path has something real to read. This is the only fixture in the estate that would exercise `Rfc4512SchemaParser`'s DIT Content Rule support against a live directory.

Then rebuild via `test/integration/docker/openldap/Build-OpenLdapImage.ps1` and confirm the existing OpenLDAP scenarios (1, 8, 9, 14) still pass: the new class and rule change what every schema import on that container discovers, and they are a shared fixture.

#### 9b. Scenario 18

Two Connected Systems on the two suffixes, following Scenario 14's shape (which avoids needing a CSV source connector): Yellowstone imports and projects, Glitterband exports. `Populate-OpenLDAP-Scenario18.ps1` seeds, in Yellowstone, entries that carry the auxiliary class and entries that do not, and in Glitterband the corresponding entries without it.

Steps, each mapping to a success criterion:

1. **Merge** – `Set-JIMConnectedSystemAuxiliaryClass` merges the class into `jimPerson`, then `Import-JIMConnectedSystemSchema`; assert the contributed attributes appear on the Object Type carrying the class in their `ClassName`.
2. **Import** – Full Import on Yellowstone; assert the auxiliary attributes' values arrive, and that an entry carrying both `jimPerson` and the auxiliary class produces exactly one Connected System Object (the Phase 6 criterion, which only a real directory's `objectClass` ordering can test).
3. **Delta convergence** – flow one auxiliary attribute to a Glitterband entry that lacks the class; assert the export adds the class in the same modify, read back over `ldapsearch`.
4. **MUST enforcement** – flow an auxiliary attribute to an entry that cannot satisfy the class's MUST; assert the export is refused and the RPEI names the missing attribute.
5. **Carrier provisioning** – select the auxiliary class as its own Object Type, set a Structural Carrier Class, provision; assert the created entry carries both classes.
6. **Discovery** – run a full scan; assert the run completes and its results name the class with the count the seed created. Then run a quick sample and assert its count is bounded by the sample size.

#### 9c. Runner registration

Scenario 18 is OpenLDAP-only and self-populating, so it needs the same six touchpoints Scenario 14 has in `Run-IntegrationTests.ps1`: the description line, the no-template-scaling list, the OpenLDAP-only filter, the snapshot exclusions (two), and the `SkipPopulate` exclusion.

## Success Criteria

- Auxiliary class attributes are importable and exportable on RFC 4512 directories once merged, with provenance visible in the schema UI.
- Provisioning an aux-typed object succeeds against OpenLDAP with a multi-valued `objectClass` (carrier + auxiliary).
- An entry lacking an enabled auxiliary class gains it in the same modify that first flows one of its attributes; an export missing the class's MUSTs fails with an error naming them.
- One directory entry can never import as two Connected System Objects regardless of `objectClass` value ordering.
- Schema refresh never silently drops merged attributes; removals surface through the existing confirmation flow.
- Discovery state (running / completed / cancelled-partial) survives navigation and is visible on the Activities page.
- All three surfaces (portal, REST, PowerShell) ship in the same release; AD-path behaviour is unchanged.

## Dependencies

- #845 must land first (tag model + class-kind population). Phases 2 and 4 have no hard dependency on it and can proceed in parallel if #845 slips; Phases 1, 3, 6 and 7 consume it.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Refresh drops an auxiliary type an admin had merged | FK cascade makes the removal explicit; existing refresh preflight/confirmation lists it; selections otherwise survive refresh |
| Double-import of entries carrying both a structural and a selected auxiliary type | Structural-first precedence (Phase 6) with dedicated regression tests |
| Full scan too heavy on very large directories | objectClass-only attribute request; Activity progress + cancellation; quick sample default; partial results retained |
| Blanket class-stamping corrupts entries | Explicitly not done; classes are only added alongside satisfying attribute flows, MUSTs enforced at class-add time |
| AD path regression | No AD-path code changes; existing AD schema tests must stay green |
| Removing the legacy flowed-`objectClass` preference changes behaviour for anyone relying on it | Called out in #492 as superseded; #1168 restores manual control as an explicit mode if demand appears |
