# One Case-Sensitivity Policy

- **Status:** Done
- **Issue:** [#2051](https://github.com/TetronIO/JIM/issues/2051)
- **PRD:** [PRD_COMPARISON_AND_EXPRESSION_VOCABULARY.md](../../prd/doing/PRD_COMPARISON_AND_EXPRESSION_VOCABULARY.md) (section "Case sensitivity: one policy")
- **Supersedes, in part:** [CASE_SENSITIVITY_STRATEGY.md](CASE_SENSITIVITY_STRATEGY.md) (its data-flow table; its `StringComparison` rules stand)

## Overview

JIM documents a case-sensitivity policy that its code does not follow, and the policy contradicts itself. This plan does two things:

- replaces the policy with one rule per question JIM asks (changed? same thing? meets a condition? finding?);
- brings every comparison in the 2026-10-11 audit into line with it.

It also adds the one thing the new policy needs that JIM lacks: a per-attribute **case-exact** declaration, discovered by Connectors and overridable by administrators.

The work lands as five PRs. PR 1 is small and goes first: it is the bottom layer of the #207 stack, beneath Object Matching expressions (#2052).

## Business Value

- **The same rule joins the same way whatever created it.** Today an Object Matching Rule made in the portal ignores case, and the same rule made over the REST API or PowerShell respects it.
- **No duplicates from casing differences between systems.** JIM already treats `JBloggs` and `jbloggs` as one identifier for uniqueness; matching, External IDs and references will agree with that.
- **Systems with genuinely case-exact identifiers stay safe.** SCIM `id`, Oracle and case-sensitive SQL columns, and AD case-sensitive strings are discovered as case-exact and compared exactly.
- **Real defects are fixed:**
  - LDAP case-only renames never sent;
  - CSV headers whose case changes silently clearing values;
  - SQL anchors compared ordinally against case-insensitive collations;
  - the drift contributor check misreading attribute names;
  - schema refresh orphaning mappings on a case-only rename;
  - `Set-JIM*Criterion` silently resetting a criterion's case setting.
- **Enforcement stops culture-sensitive comparisons from coming back.**

## Technical Architecture

### Current state (audit, 2026-10-11)

| Area | What the code does | Documented | Verdict |
|---|---|---|---|
| Change detection, export confirmation (text), drift values | Ordinal | Exact | Conforms |
| Scoping evaluation | Ordinal or OrdinalIgnoreCase by criterion | Per criterion | Conforms |
| Generated-value uniqueness | Ignores case everywhere | Ignores case | Conforms |
| External ID lookup and duplicate detection | Lower-cased keys, `OrdinalIgnoreCase` (`ConnectedSystemRepository.GetExternalIdValueString` 2551-2561, `SyncImportTaskProcessor.LookupCsoImportStateByExternalId` 2796-2836, duplicate sets 231 and 2129-2130, the CSO cache `ConnectedSystemServer` 5491-5664) | Exact | Ignores case, and cannot honour a case-exact system |
| References | In-memory primary anchor `Ordinal` (`SyncImportTaskProcessor` 4215); secondary and every database fallback ignore case (4261, 3705-3735, `ConnectedSystemRepository` 2777-3010, `SyncRepository.CsOperations` 482-600 and 1494-1523); export staging ignores case for DNs (`SyncEngine.ExportStaging.ValuesMatch` 1059, 1073); confirmation is exact (`SyncEngine.Reconciliation` 321-327, 388-395) | Exact | Inconsistent paths |
| Object Matching default | Model, portal and preview DTO ignore case; REST and cmdlets respect it; portal caption contradicts itself; no database default | Exact | Differs by surface |
| LDAP rename | `LdapConnectorExport` 369 and 818 compare DNs ignoring case; RDN attribute skipped (1497-1516) | Case changes flow | A case change never reaches the directory |
| CSV headers | CsvHelper exact header match; `MissingFieldFound = null` | Names ignore case | A header case change clears values |
| SQL anchors | `SqlConnectorImport` 503 and 1534 `StringComparer.Ordinal` | n/a | Breaks on case-insensitive collations |
| Configuration names | Exact in drift contributor check (`DriftDetectionService` 560), schema refresh (`ConnectedSystemServer` 2703-3071), attribute-name API filters (`MetaverseRepository` 2989, 3015, 3779, 4309; `ConnectedSystemRepository` 2229, 5265), Activity filters (`ActivitiesRepository` 392-397), Predefined Search URI (`SearchRepository` 51), Connector definition by name (`ConnectedSystemRepository` 91), Example Data `{Attribute}` (`ExampleDataServer` 1296), expression input de-duplication (`ExpressionInputResolver` 93) | Ignore case | Deviates |
| Criterion PUT | `CreateScopingCriterionRequest` / `PredefinedSearchCriterionRequest` reused for PUT with `CaseSensitive = true` default | n/a | Omitting the flag resets it |
| Culture comparisons | 18 CA1310 sites (5 in `src/`: expression `StartsWith`/`EndsWith`, API key auth header, two `Program.cs` checks); 17 CA1309 sites (2 in `src/`) | Never culture | Deviates |

### Proposed solution

**The policy:** four questions, one rule each. It is defined, with the per-feature quick-reference table, in the PRD.

**The case-exact declaration.** Two new properties on `ConnectedSystemObjectTypeAttribute`:

- `CaseExact` (bool, default false): this attribute's values compare exactly when JIM asks whether two values are the same thing.
- `CaseExactSetByAdministrator` (bool): schema refresh restates `CaseExact` from the Connector unless this is set. This copies the existing `TypeSetByAdministrator` guard at `ConnectedSystemServer.MergeSchemaIntoConnectedSystem` 2790.

`ConnectorSchemaAttribute` gains `CaseExact`. Discovery by Connector:

| Connector | Rule |
|---|---|
| LDAP, Active Directory and Samba AD | `omSyntax` 27 (2.5.5.3, String(Case Sensitive)) and the IA5 and Printable syntaxes (`omSyntax` 22 and 19, 2.5.5.5) are case-exact. All other strings ignore case. Read in `LdapConnectorSchema.GetAdSchemaAttribute` (232-308), where `omSyntax` is already read. |
| LDAP, RFC 4512 servers | Parse `EQUALITY` in `Rfc4512SchemaParser.ParseAttributeTypeDescription` (199-241). Inherit it through `SUP` the way `LdapConnectorSchema.ResolveSyntaxOid` (605-629) inherits syntax. `caseExactMatch`, `caseExactIA5Match`, `octetStringMatch` and `uuidMatch` are case-exact; `caseIgnore*`, `distinguishedNameMatch` and no rule at all ignore case. |
| SCIM | `ScimSchemaAttribute.CaseExact`, carried through `ScimAttributeMapper.Build` and `ScimFlattenedAttribute.ToConnectorSchemaAttribute`. The synthesised `id` and `externalId` (`ScimCommonAttributes.For`) are case-exact, per RFC 7643. |
| SQL Server | Fold `COLLATIONPROPERTY(collation_name, 'ComparisonStyle') & 1` (the uniqueness probe's query, `SqlServerProvider` 306-312) into `ColumnsCommandText`. Case-exact when the collation does not ignore case. A composite anchor is case-exact if any of its parts is. |
| Oracle | Case-exact: Oracle compares binary by default. Read `ALL_TAB_COLUMNS.COLLATION` where the server has it (12.2 and later), and treat `BINARY_CI`/`BINARY_AI` as ignoring case. |
| File (CSV) | Nothing to discover. Administrator only. |

**Using the declaration.** One helper normalises identifier keys: `IdentifierKey.For(string value, bool caseExact)`, in `JIM.Utilities`. It returns the value unchanged if case-exact, else `ToLowerInvariant()`. Every identity lookup keys through it under `StringComparer.Ordinal`, never through a comparer fixed at construction. That is what lets one dictionary hold attributes of both kinds.

**References are the object they point to.**
- Resolved references compare the referenced Connected System Object's id, in the import diff and in export confirmation.
- Unresolved references compare through `IdentifierKey.For` using the anchor attribute's declaration.

**SQL that compares identifiers** (cross-batch fix-up, reference recall, database fallbacks) is grouped by anchor attribute and issues the exact or `LOWER()` variant for each group. It never uses one `CASE` predicate, which would defeat the `LOWER("StringValue")` indexes.

### Data flow (identity comparison after this plan)

```
Connector schema --> ConnectorSchemaAttribute.CaseExact --> merge (unless set by administrator) --> ConnectedSystemObjectTypeAttribute.CaseExact
                                                                                                        |
Import / export / references / matching --> IdentifierKey.For(value, attribute.CaseExact) --> Ordinal dictionaries and per-group SQL
```

## Implementation Phases

Each PR follows TDD, keeps the build and tests green, and updates the policy page rows it makes true. The page always documents shipped behaviour.

### PR 1: Policy page and one Object Matching default (bottom of the #207 stack) ✅

- **Docs:**
  - Rewrite `docs/concepts/case-sensitivity.md` into the four-question shape, with the quick-reference table listing every row true at this point.
  - Fix `docs/configuration/synchronisation-rules.md` 146 ("case-sensitive by default").
  - Fix `docs/powershell/synchronisation-rules.md` 1242 (the scoping criterion default is `$true`). Lines 1490 and 1652 already said `$false` for matching rules, which this PR makes true.
- **REST:** `CreateObjectMatchingRuleRequest.CaseSensitive` and `CreateSyncRuleObjectMatchingRuleRequest.CaseSensitive` (`ObjectMatchingRuleRequestDtos.cs` 68, 101) become `bool?`. Null takes the policy default, which is false until PR 3 adds the declaration.
  - The response DTO default (`ObjectMatchingRuleDtos.cs` 107) is mapped from the entity only.
- **Preview:** `StartObjectMatchingPreviewRequest.CaseSensitive` (97) follows the same rule. The preview cmdlet's hashtable path (`New-JIMConfigurationChangePreview.ps1` 328) states its default in help.
- **Portal:**
  - Fix the `ConnectedSystemObjectMatchingTab.razor` caption at 627.
  - Make both tabs read the default from one place rather than two literals: `ObjectMatchingDefaults.CaseSensitive` in `JIM.Models`, which the entity initialiser, the Simple mode tab, the REST create paths and the preview all read. It is a constant for now; PR 3 adds `CaseSensitiveFor(attribute)` once there is a declaration to derive from, rather than this PR shipping a parameter nothing reads.
- **PowerShell help:** `New-JIMMatchingRule` and `New-JIMSyncRuleMatchingRule` state the default.
- **Tests:**
  - Controller tests for null/true/false on create.
  - A preview controller test that a proposed rule without the setting ignores case. The portal tabs are covered by reading the constant directly (the Advanced tab through the entity initialiser); a source-shape test for that was judged not worth its upkeep.
- **Changelog:** 🔄 "Object Matching Rules created through the REST API or PowerShell now ignore case by default, as the portal always has; existing rules are unchanged."

### PR 2: Comparison hygiene and configuration names ✅

- **Enforcement:** set `dotnet_diagnostic.CA1309.severity = warning` and `dotnet_diagnostic.CA1310.severity = warning` in `.editorconfig`. `TreatWarningsAsErrors` makes them errors.
  - **CA1307 is deliberately not enabled.** Measured on 2026-10-11 it raises 197 diagnostics, 45 of them in `src/`. Most are `char` overloads, `PathString.StartsWithSegments` or EF predicates, where a `StringComparison` overload would stop the query translating to SQL. It would add noise without catching the defect class.
  - CA1862 is not enabled either, for the same EF reason.
- **Fix the 35 sites the two rules raise:**
  - **Expression `StartsWith`/`EndsWith`** (`DynamicExpressoEvaluator` 225-226) become ordinal. This is a 🔄 behaviour change: it differs from today only for ignorable or canonically equivalent characters. Phase 2 (#2053) then adds `ignoreCase`.
  - **The API key authentication header check** (`ApiKeyAuthenticationHandler` 76) and `Program.cs` 974 and 1248 become ordinal.
  - **`FileConnectorImport` 92** becomes `OrdinalIgnoreCase`.
  - **`ConnectedSystemRepository` 91** becomes a case-insensitive name lookup (EF `ToLower()`), per the policy for configuration names.
  - **The 28 test-project sites.**
- **Configuration names ignore case:**
  - `DriftDetectionService` 560. A 🐛: drift could correct the values a contributing system itself supplied.
  - **Schema refresh name matching** (`ConnectedSystemServer` 2703-2704, 2723, 2732-2733, 2759, 2830, 2866, 2880, 2892, 3069-3071, and the preview dictionaries at 2391 and 2426). A case-only rename keeps the attribute's id, so mappings survive, and the stored `Name` takes the Connector's new casing.
  - **The attribute-name API filters and projections** (`MetaverseRepository` 2989, 3015-3017, 3779, 4309, 2775; `ConnectedSystemRepository` 2229, 5265).
  - **Activity filters by Connected System name** (`ActivitiesRepository` 392-397).
  - **Predefined Search URI** (`SearchRepository` 51).
  - **Example Data `{Attribute}`** (`ExampleDataServer` 1296).
  - **`ExpressionInputResolver.Resolve`** (93).
  - **AD's `AddAttributesFromSchemaProperty` name check** (`LdapConnectorSchema` 219).
- **Criterion PUT:** `CaseSensitive` becomes `bool?` on the shared criterion request DTOs. Create treats null as true (the condition default); update treats null as "keep the stored value". This follows `UpdateObjectMatchingRuleRequest`.
  - Covered by controller tests and Pester tests for `Set-JIMScopingCriterion` and `Set-JIMPredefinedSearchCriterion` without `-CaseSensitive`.
- **Changelog:**
  - 🐛 drift contributor check;
  - 🐛 schema refresh on a case-only rename;
  - 🐛 `Set-` cmdlets resetting the case setting;
  - 🔄 expression `StartsWith`/`EndsWith`;
  - 🔄 name lookups across the REST API and PowerShell.

### PR 3: The case-exact declaration ✅

- **Model and migration:**
  - `ConnectedSystemObjectTypeAttribute.CaseExact` and `CaseExactSetByAdministrator`, both non-nullable with a default of false.
  - `ConnectorSchemaAttribute.CaseExact`.
  - There is no bulk raw-SQL writer for the table, so no `*BulkColumns` change is needed. Confirm with the completeness tests.
- **Discovery** per the table above:
  - LDAP AD branch: `GetAdSchemaAttribute`.
  - LDAP RFC branch: parser `EQUALITY`, `Rfc4512AttributeTypeDescription.EqualityOid`, `SUP` inheritance and a matching-rule classifier.
  - SCIM: the `Build` path and the `id`/`externalId` synthesis. Also update `ScimConnectorUniquenessProbe` 228's widened copy, and remove the "JIM does not model per-attribute case sensitivity" comment in `ScimSchemaAttribute`.
  - SQL: `ColumnsCommandText` for both providers, `SqlDiscoveredColumn`, and attribute emission at `SqlConnectorSchema` 140-152, 188-197 and 237-247.
  - The auxiliary class merger (`AuxiliaryClassAttributeMerger` 140-157).
- **Merge:** `MergeSchemaIntoConnectedSystem` copies `CaseExact` at its three sites unless `CaseExactSetByAdministrator`. The pre-refresh snapshot records it.
- **Surfaces:**
  - **Portal:** the Schema tab's attribute dialog gains **Values are case-exact** for Text attributes. It sets `CaseExactSetByAdministrator`. It is offered for every Connector, not only those with `SupportsUserSelectedAttributeTypes`.
  - **REST:** `UpdateConnectedSystemAttributeRequest.CaseExact` (`bool?`), the single PUT and the bulk PUT (widen the tuple at `SynchronisationController` 478-481 and `BulkUpdateAttributesAsync`), and `ConnectedSystemAttributeDto.CaseExact`.
  - **PowerShell:** `Set-JIMConnectedSystemAttribute -CaseExact`.
  - **Configuration History:** `ConfigurationSnapshotService.BuildObjectTypeAttributes` (539-555).
- **Object Matching default** now derives from the source attribute's declaration, through PR 1's helper. Expression sources default to ignoring case.
- **Tests:**
  - Parser tests for `EQUALITY` and its inheritance.
  - Connector schema tests per Connector.
  - Merge tests: discovered, admin-set and refreshed.
  - Controller and Pester tests.
  - `MigrationDesignerChainTests` passing.
  - A real-directory check of the AD syntax mapping in the Samba AD integration environment.
- **Changelog:** ✨ "Connected System attributes now say whether their values are case-exact, discovered from LDAP, Active Directory, SCIM and SQL schemas and adjustable by administrators."

### PR 4: Identity comparisons follow the declaration ✅

Delivered in two parts:

- **PR 4a ✅ (change comparisons, #2195):** export confirmation (linear and indexed paths), export staging's no-net-change check and Drift Detection compare an identifier (Reference, External ID or Secondary External ID) through `AttributeValueComparison`: ignoring case unless the attribute is case-exact, with resolved references also matched by referenced Connected System Object id. Every other attribute stays exact. This is PRD decision 5 with a carve-out for references, in place of a Connector-declared "case-insensitive" flag (#2225).
- **PR 4b ✅:** the External ID and reference lookups below (import state, duplicate and deletion detection, Secondary External ID matching, reference resolution in .NET and SQL, the cross-batch fix-ups, reference recall, in-memory repository parity), with the `RequiresPostgres` and non-ASCII tests and Scenario 008. `CsoLookupKey` builds the import's lookup key in one place; `IdentifierKey.For` lowers `İ` as PostgreSQL's `lower()` does (a sweep of every BMP character found no other difference outside Unicode 16 letters). The CSO cache is left ignoring case: nothing in production reads it.

- **External IDs.** `IdentifierKey.For` is used in:
  - `GetAllCsoImportStateLookupAsync`, which projects the attribute's `CaseExact` by joining the attribute row;
  - `LookupCsoImportStateByExternalId`, where the attribute is in hand at 2801;
  - duplicate detection (231, 2129-2130, 2241-2254), with keys normalised under `Ordinal`;
  - the CSO cache. `BuildCsoCacheKey`, `AddCsoToCache` and `EvictCsoFromCache` take the flag. Callers that hold only an attribute id use a per-run `attributeId -> CaseExact` map loaded with the Connected System.
  - the EF lookup `GetConnectedSystemObjectByAttributeAsync`, which branches on the attribute's declaration before querying.
- **References:**
  - `BuildExternalIdLookups`: primary and secondary both follow the anchor's declaration. This ends the primary-`Ordinal` versus fallback-`LOWER()` disagreement.
  - `DeduplicateImportObjectAttributes`: the callers pass the object type.
  - The database fallbacks, cross-batch fix-ups and recall query issue a per-anchor-group exact or `LOWER()` variant.
  - Import diff and export confirmation compare resolved references by referenced Connected System Object id, and unresolved ones through `IdentifierKey.For`.
  - `SyncEngine.ExportStaging.ValuesMatch` follows the same rule.
- **In-memory repository parity** (`JIM.InMemoryData/SyncRepository` 406-412, 501-511, 693-707, 1212-1226), so unit tests see production behaviour.
- **Tests:**
  - Red-first unit tests per site.
  - `RequiresPostgres` tests for every SQL variant, including non-ASCII values, to pin .NET `ToLowerInvariant` against PostgreSQL `lower()` on the deployment collation.
  - Scenario 008 (required by `src/JIM.Application/CLAUDE.md` for export staging changes) and the LDAP scenarios.
- **Changelog:** 🔄 "External IDs and references compare exactly for attributes their Connected System declares case-exact; everything else continues to ignore case."

### PR 5: Connector fixes ✅

- **LDAP:**
  - Issue a ModifyDN when the object's name changes only in case. As delivered, the rename is driven by the RDN attribute (`cn`, compared exactly), or else by the leaf of a staged DN; the full DN keeps the identity rule and compares ignoring case, because the parts above the object's name are the directory's. The new DN takes the directory's parent as it stands. (`GetCaseOnlyRenameDn`, on both the sequential and parallel export paths.)
  - Keep the RDN attribute out of the ModifyRequest, since the rename carries it (already the case: `IsRdnAttribute`).
  - Test against Samba AD that the directory accepts and keeps the new case (Scenario 001's Mover step).
- **CSV:**
  - Match headers ignoring case (`PrepareHeaderForMatch`).
  - Fail the import with a clear error when a header the schema expects is missing, rather than reading it as no value (`FileConnectorImport` 131 onward).
- **SQL:** key anchors through `IdentifierKey.For` using the discovered collation flag (`SqlConnectorImport` 403, 503-513, 1534-1587). As delivered, each anchor part is folded unless its column is case-exact, for matching related-table and change-log rows back to their object; the anchor JIM is given stays exactly as read.
- **Tests:** Connector unit tests, plus an integration step per Connector where the harness covers it.
- **Changelog:** 🐛 for each fix.

## Success Criteria

- Every row of the quick-reference table is true, and is covered by a test named for its question.
- The audit's sites conform, or are removed.
- The same Object Matching Rule behaves identically whatever surface created it.
- CA1309 and CA1310 are enforced, with zero suppressions in `src/` other than any EF predicate that cannot carry a `StringComparison`. Each of those is justified at its site.
- Scenario 008, the LDAP scenarios and Scenario 014 (performance) pass with no regression.

## Benefits

- **Administrators** learn one rule per question instead of a table of exceptions.
- **Correctness:** fewer duplicate identities; case-exact systems compared safely; five defects fixed.
- **Maintainability:** one key normaliser and one default helper instead of scattered `ToLowerInvariant()` calls and comparers.

## Dependencies

- None blocking. PR 1 must land before #2052's layers, which build on its default helper.
- PR 4 benefits from PR 3 (without the declaration it would change nothing), so they land in that order.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| .NET `ToLowerInvariant()` and PostgreSQL `lower()` fold non-ASCII characters differently on the deployment's collation, so a key built in .NET misses one built in SQL | `RequiresPostgres` tests with non-ASCII anchors (accented, Turkish dotted I, German sharp s) in PR 4; if they diverge, fold on one side only (SQL `lower()` for lookups, .NET for in-memory keys built from SQL results) |
| Discovery marks an AD or Oracle attribute case-exact that the deployment treats otherwise, changing joins on upgrade | Only identity comparisons change; the declaration is visible and overridable; the upgrade changelog says so; Samba AD and Oracle verification in PR 3 |
| A per-attribute `CASE` in SQL defeats the `LOWER()` indexes | Group by anchor attribute and run the exact or `LOWER()` variant per group (PR 4) |
| Schema refresh adopting a Connector's new casing surprises an administrator | Configuration names ignore case, so expressions and filters keep working; the schema refresh preview shows the rename |
| CSV imports that relied on a missing column reading as empty start failing | That is the intent (silent clearing is data loss). The error names the missing header, and the changelog states the change |
