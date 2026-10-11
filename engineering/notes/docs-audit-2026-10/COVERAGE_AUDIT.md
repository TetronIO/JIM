# JIM documentation coverage audit (issue #1454)

Date: 2026-10-11. Scope: public docs under `docs/` (MkDocs, nav in `mkdocs.yml`), compared against source on the current checkout (VERSION 0.16.0 plus `[Unreleased]`). Read-only research; no repository file was changed. The throwaway extraction scripts used were not kept.

"Customer docs" below means `docs/**` excluding `docs/developer/**`, `docs/reference/changelog.md`, `docs/reference/roadmap.md` and `docs/CLAUDE.md`.

## Summary table

| # | Inventory | Source of truth | Total | Documented | Gaps |
|---|-----------|-----------------|------:|-----------:|-----:|
| 1a | Glossary entries | `docs/reference/glossary.md` | 41 | 38 with an explanation section (10 link to one) | 3 with no home section; 31 with no outbound link |
| 1b | Vocabulary map nouns | `engineering/DEVELOPER_GUIDE.md` > Vocabulary (+ root CLAUDE.md proper-noun list) | 7 (+7, overlapping) | 7 in glossary | 0, but the map covers ~10% of real product nouns |
| 1c | Product nouns used in docs | Title Case n-gram mining over customer docs, cross-checked against JIM.Models / UI | ~65 curated | 41 in glossary | ~25 high-priority nouns absent from glossary (list below) |
| 2 | Portal routes | `@page` directives in `src/JIM.Web` (48 directives) | 43 customer-facing (+5 dev/infra) | 33 (3 of them partial) | 10 undocumented |
| 3 | PowerShell cmdlets | `JIM.psd1` FunctionsToExport (182) + 1 alias | 182 | 172 with a heading; 7 more only in a summary table | 1 with no page (Get-JIMDataFlow); 3 stale names in prose; index counts wrong for 3 categories |
| 4 | REST endpoints | `[Route]`/`[Http*]` on 23 controllers | 282 endpoints / 35 resource groups | 282 in Scalar (all have `<summary>`); 29 groups have a concept page | 6 groups with no concept/overview page; 1 broken path in docs (4 occurrences) |
| 5a | Service Settings | `Constants.SettingKeys` (34) / seeded (32 in `SeedingServer` + 1 lazily created) | 34 (33 visible) | 13 in the reference table; 20 named somewhere | 13 not named anywhere in docs; 1 constant never seeded |
| 5b | Environment variables (runtime) | `Environment.GetEnvironmentVariable` / env constants in `src` | 24 | 23 | `JIM_CACHE_WARM_PARALLELISM` |
| 5c | Environment variables (deployment/installer) | `docker-compose.yml`, `deploy/docker-compose.production.yml`, `deploy/podman`, `deploy/setup.sh` header | 22 | 11 | 11 installer `JIM_SETUP_*` variables |
| 6 | Connector settings | `GetSettings()` of LDAP, File, SQL, SCIM connectors (Heading/Label excluded) | 65 (LDAP 21, File 8, SQL 16, SCIM 20) | 65 (2 only under normalised matching) | 0; 0 stale settings in docs tables |
| 6b | Expression functions (bonus) | `DynamicExpressoEvaluator.SetFunction("...")` | 36 | 35 | `ToDecimal` |
| 7 | Changelog features | ✨ entries in `[Unreleased]`, 0.16.0, 0.15.0 | 121 entries (24 + 8 + 89) | ~55 spot-checked: all findable | naming drift only (see section 7) |
| - | Docs pages reachable from nav | `docs/**/*.md` vs `mkdocs.yml` nav | 100 | 100 | 0 orphan pages, 0 dangling nav entries |
| - | Internal links / anchors | all `](...md#...)` links | 1,254 links, 690 anchored | all resolve | 0 (but nothing in CI checks anchors) |

---

## 1. Domain concepts and terms

### 1a. Glossary entries (41)

Source: `docs/reference/glossary.md`, entries of the form `Term { #anchor }` + `:   definition` (def_list). Mechanically extractable with one regex; already parsed by `test/JIM.Web.Tests/GlossaryTermConsistencyTests.cs`, which checks the 10 `TermDefinitions` entries the portal's `<TermHint>` shows appear verbatim in the glossary with their anchor.

All 41 terms occur in portal source (`src/JIM.Web` razor/cs), so none is developer-only.

Entries: Activity, Attribute Flow, Attribute Priority, Configuration Change History, Configuration Change Preview, Connected System, Connected System Object, Connector, Connector Space, Container Scope, Delta Synchronisation, Deprovisioning, Deprovisioning Action, Drift Correction, Export, Expression, Full Synchronisation, Grace Period, Identity, Import, Initial Export Only, Join, Metaverse, Metaverse Object, Null is a value, Object Matching Rule, Obsoletion, Partition, Password Synchronisation, Pending Export, Projection, Provisioning, Run Profile, Safeguards, Schedule, Scoping, Service Health, Standard Mappings, Sync Preview, Synchronisation Rule, Temporal Scope Reconciliation.

**Glossary terms with no concept/explanation section of their own** (no heading anywhere in customer docs that explains them, so nothing to anchor-link to):

| Term | Where it is explained today | Recommendation |
|------|-----------------------------|----------------|
| Drift Correction | A paragraph under the "enforce state" text in `configuration/synchronisation-rules.md` (Export (outbound)); `connected-systems.md` mentions it in a bullet | Add a `### Enforce State and drift correction` section; the UI calls the feature **Enforce State**, which is not in the glossary at all |
| Temporal Scope Reconciliation | One bullet in `configuration/schedules.md` > Built-in schedules | Link the glossary entry to `schedules.md#built-in-schedules`; consider a short section on relative-date scoping in `synchronisation-rules.md` |
| Join | Covered implicitly in `concepts/synchronisation-pipeline.md` (Inbound Sync) and `synchronisation-rules.md#object-matching-rules`; no heading names it | Add a "Projection and Join" heading in the pipeline concept page |

**Glossary entries with no outbound link to their home page:** 31 of 41 (only Configuration Change Preview, Connected System, Connector, Identity, Metaverse Object, Password Synchronisation, Safeguards, Schedule, Service Health, Sync Preview link out). Every entry has a plausible home section (e.g. Attribute Priority -> `concepts/attribute-priority.md`, Pending Export -> `configuration/connected-systems.md#pending-exports`), so this is cheap to fix and cheap to enforce.

**Definition drift / developer-flavoured wording** (none is developer-only, but these read against the docs style rules):
- **Run Profile**: "configurable parameters such as page size"; the product and `run-profiles.md` call it **Batch size**.
- **Connector Space**: "ensuring that changes are validated before they are applied" is implementation-flavoured and not something the product does as a distinct step.
- **Scoping**: the product noun is **Scoping Criteria** (the entry's own text uses it, the UI tab is "Scope"); the entry title is the odd one out.
- **Synchronisation Rule**: "between a Connected System and the metaverse" (lower-case Metaverse; proper noun rule).
- **Full Synchronisation / Delta Synchronisation**: the concept page they belong to is titled "Sync Pipeline" (nav says "Synchronisation Pipeline") with a heading "Full Sync vs Delta Sync"; the "Sync" shorthand conflicts with the one-name rule.

### 1b. Vocabulary map (DEVELOPER_GUIDE.md > Vocabulary)

The table has 5 rows naming 7 nouns: Connected System Object, Connector Space, Metaverse Object, Metaverse, Pending Export, Projection, Join. The root `CLAUDE.md` proper-noun list adds Synchronisation Rule, Connected System, Run Profile, Attribute Flow, Object Matching Rule. **All 12 are in the glossary.** No vocabulary-map term is missing.

The real gap: the vocabulary map is not an inventory. It names ~10% of the product nouns the docs and UI use, so "every vocabulary term is in the glossary" is vacuously true. It is also prose in a Markdown table, which is mechanically parseable but has no "place" column populated for 3 of 5 rows.

### 1c. Product nouns used in docs but absent from the glossary

Derived by mining Title Case n-grams from customer docs, filtering to product nouns confirmed in `JIM.Models` (DbSet entity types, 91 DbSets / ~85 entity types) or in portal labels, then counting occurrences in customer docs. Occurrences / pages are approximate (case-insensitive, plural tolerant).

High priority (cross-cutting product nouns, named in the UI, used on many pages):

| Term | Occurrences / pages | Source entity / UI | Explained in |
|------|--------------------:|--------------------|--------------|
| Object Type (Metaverse Object Type, Connected System Object Type) | 462 / 35 | `MetaverseObjectType`, `ConnectedSystemObjectType`; Schema > Object Types | `configuration/metaverse.md#object-types` |
| Container | 415 / 36 | `ConnectedSystemContainer`; Scope tab (glossary has only Container Scope) | `connected-systems.md#partitions-and-containers` |
| Schema / Refresh Schema | 249 / 36 | Schema tab; Admin > Schema | `connected-systems.md#refreshing-the-schema` |
| Full Import / Delta Import | 194 / 22, 162 / 16 | `ConnectedSystemRunType`; glossary folds both into "Import" with no per-type anchor | `concepts/synchronisation-pipeline.md` |
| Role | 163 / 27 | `Role` | `configuration/roles.md` |
| API Key | 138 / 25 | `ApiKey` | `configuration/api-keys.md` |
| Metaverse Attribute | 125 / 25 | `MetaverseAttribute` | `configuration/metaverse.md#attributes` |
| Worker / Scheduler (service names) | 155 / 28, 60 / 16 | `JimService`; Service Health | `concepts/architecture.md`, `operations.md` |
| Change History (object change history) | 103 / 34 | `MetaverseObjectChange`, `ConnectedSystemObjectChange`; "Changes" tabs | `metaverse.md#change-history` (glossary has only Configuration Change History) |
| Service Setting | 74 / 20 | `ServiceSetting` | `configuration/service-settings.md` |
| Generated Value (+ uniqueness token, retired value, Collision Remediation) | 73 / 14 (+10, 16, 13) | `SyncRuleMappingGeneration`, `GeneratedValueAssignment`, `RetiredGeneratedValue`; Operations > Generated Values | `synchronisation-rules.md#generated-values` |
| Deletion Rule (+ When Last Connector Disconnected, When Authoritative Source Disconnected, Deletion Trigger) | 59 / 10 | `MetaverseObjectDeletionRule` | `configuration/metaverse.md` > Deletion behaviour |
| Initial Password | 48 / 12 | `SyncRuleInitialPassword` | `concepts/passwords.md` |
| Schedule Execution / Schedule Step | 34 / 7, 4 / 3 | `ScheduleExecution`, `ScheduleStep` (glossary mentions Schedule Execution inside "Schedule" only) | `configuration/schedules.md#executions` |
| External ID / Secondary External ID | 31 / 15 | Connector Space columns | scattered |
| Password Delivery Service | 27 / 11 | Worker background service; Service Health row | `concepts/passwords.md` |
| Data Flow | 27 / 13 | Admin > Schema > Data Flow | `configuration/data-flow.md` |
| JML / Joiner / Mover / Leaver | 16 / 5 (+12, 10, 27) | concept page `concepts/jml-lifecycle.md` exists | `concepts/jml-lifecycle.md` |
| Worker Task | 26 / 8 | `WorkerTask`; Operations > Queue | `operations.md` |
| Preview Feature / Feature Flag | 26 / 5, 20 / 4 | `FeatureFlagDefinition` | `administration/preview-features.md` |
| Predefined Search | 22 / 5 | `PredefinedSearch` | `configuration/predefined-searches.md` |
| Pending Deletion | 22 / 6 | Admin > Pending Deletions | `metaverse.md#pending-deletions` |
| Missing Input Behaviour | 17 / 4 | `MissingInputBehaviour` | `concepts/expressions.md` |
| Trusted Certificate | 15 / 8 | `TrustedCertificate` | `configuration/certificates.md` |
| Enforce State | 10 / 4 | Synchronisation Rule toggle | `synchronisation-rules.md` |
| Run Profile Execution Item (execution item) | 5 / 4 | `ActivityRunProfileExecutionItem` | `activities.md#execution-items` |

Medium priority (feature-specific, used on fewer pages): Example Data Set, Example Data Template / Data Generation Template (two names for one thing, see section 7), Connector Definition, Derived Attribute Flow, Out-of-Scope Action, Unresolved Reference handling, Verification Mode, Attribute Writability / Set on creation only, Credential Attribute, Auxiliary Class / Structural Carrier Class, Security Audit Event, Causality / Lineage, Inspect / Provenance, Connections / Not connected, Parked (and the other Pending Password Change states), Password Policy, Export Scope Review, Scheduled Metaverse Object Deletion, History Retention Cleanup, Maintenance Mode, Infrastructure API Key, Detected (facts strip), value Recall, Confirming Import, Change-Log Table / Watermark Column (SQL Delta Import modes).

Connector-specific (arguably belong on the connector page, not the glossary): Global Catalog, Preferred Domain Controller, Directory Server, Fine-Grained Password Policies.

---

## 2. Portal features (routable pages)

Source: 48 `@page` directives across `src/JIM.Web/Pages/**.razor` and `*.cshtml`. Note: 10 of the `.razor` files start with a UTF-8 BOM, so a naive `grep '^@page'` over `*.razor` finds only 35 of their 45 directives; reflection over `RouteAttribute` (Razor compiles `@page` to `[Route]`) is the reliable extractor.

Excluded as dev/infrastructure (5): `/admin/theme-preview`, `/admin/loading-preview`, `/dev/error-pages`, `Error.cshtml`, `_Host.cshtml` (`/{handler?}`).

| Route | Page | Docs |
|-------|------|------|
| `/` | Home ("Welcome", counts) | **None** |
| `/activity` | Activity list | `configuration/activities.md` |
| `/activity/mine` | My Activity | **None** (no mention of "My Activity") |
| `/activity/{id}` | Activity detail | `configuration/activities.md` |
| `/activity/item/{id}` | Run Profile Execution Item detail | `configuration/activities.md#execution-items` |
| `/admin` | Administration index | Partial: named once in `operations.md` ("the Administration index") |
| `/admin/apikeys`, `/admin/apikeys/{id}` | API Keys | `configuration/api-keys.md` |
| `/admin/certificates` | Certificates | `configuration/certificates.md` |
| `/admin/connected-systems`, `/new`, `/{id}` | Connected Systems | `configuration/connected-systems.md` |
| `/admin/connected-systems/connectors` | Connectors (Connector Definitions) list | **None** (`connectors/index.md` never mentions the portal page) |
| `/admin/connected-systems/connectors/{id}` | Connector detail | **None** |
| `/admin/connected-systems/{id}/connector-space`, `/{csoId}` | Connector Space, CSO detail | `connected-systems.md#the-connector-space` |
| `/admin/connected-systems/{id}/pending-exports`, `/{id}` | Pending Exports | `connected-systems.md#pending-exports` |
| `/admin/deleted-objects` | Deleted Objects (CSO and MVO tabs) | **None** for the portal page (only `Get-JIMDeletedObject` in `powershell/history.md`) |
| `/admin/example-data` | Example Data Templates | **None** as a portal how-to (`powershell/example-data.md` only; quickstart says "use the Example Data feature") |
| `/admin/example-data/datasets`, `/datasets/{id}` | Example Data Sets | **None** (same) |
| `/admin/example-data/templates/{id}` | Template detail | **None** (same) |
| `/admin/factory-reset` | Factory Reset | **None** for the portal page (`Reset-JIMSystem` in `powershell/system.md` only) |
| `/admin/logs` | Logs | Partial: one sentence in `administration/deployment.md` |
| `/admin/operations` | Operations (Queue, History, Schedules, Passwords, Generated Values) | `configuration/operations.md`, but "The tabs" lists 4 of 5: **Generated Values tab missing** |
| `/admin/operations/schedule-executions/{id}` | Schedule Execution | `schedules.md#the-schedule-execution-view` |
| `/admin/pending-deletions` | Pending Deletions | `metaverse.md#pending-deletions`; **but no portal link reaches this page** (no nav, tile or Href anywhere in `src/JIM.Web`) |
| `/admin/predefined-searches`, `/{uri}` | Predefined Searches | `configuration/predefined-searches.md` |
| `/admin/schema`, `/attributes`, `/data-flow`, `/object-types/{id}` | Schema | `configuration/metaverse.md`, `configuration/data-flow.md` |
| `/admin/settings` | Service Settings | `configuration/service-settings.md` |
| `/admin/sync-rules`, `/new`, `/{id}` | Synchronisation Rules | `configuration/synchronisation-rules.md` |
| `/claims` | Claims | `administration/sso-setup.md`, `administration/configuration.md` |
| `/sign-in-failed` | "Sign-in could not complete" | `administration/troubleshooting.md` |
| `/t/{type}`, `/t/{type}/s/{search}`, `/t/{type}/v/{id}` | Metaverse Object lists and detail | `configuration/metaverse.md`, `predefined-searches.md` |

**Undocumented routes (10):** `/`, `/activity/mine`, `/admin/connected-systems/connectors`, `/admin/connected-systems/connectors/{id}`, `/admin/deleted-objects`, `/admin/example-data`, `/admin/example-data/datasets`, `/admin/example-data/datasets/{id}`, `/admin/example-data/templates/{id}`, `/admin/factory-reset`.

**Partially documented (counted as documented, 3):** `/admin`, `/admin/logs`, `/admin/pending-deletions`.

Count: 43 customer-facing routes, 33 documented (3 partial), 10 undocumented.

**Tab-level gaps** (MudTabPanel labels vs the mapped page): Operations "Generated Values" (missing from `operations.md` tab list); Deleted Objects' two tabs (no page); Connected System "Matching" tab (only named in passing; content lives in `synchronisation-rules.md#simple-vs-advanced-matching-mode`); Metaverse Object "Properties" tab and API Key "Activity" tab (not named).

**Incidental product findings (not docs, but surfaced by the route inventory):**
- `NavMenu.razor` has an Administrator-only **Dashboard** group linking to `/dashboard/daily` and `/dashboard/weekly` (Monthly and Annually also point at `/dashboard/weekly`). No such routes exist, so all four links land on the not-found page.
- `/admin/pending-deletions` is orphaned: reachable only by typing the URL, yet `metaverse.md` describes "the Pending Deletions page" as if it were navigable.

Portal pages do not link to their docs: only the glossary (via `InfoPopover`/`TermHint`) and two hard-coded URLs in `OidcSignInRecovery.cs` / troubleshooting point at `docs.junctional.io`. That is why route -> docs mapping cannot be derived from source today.

---

## 3. PowerShell cmdlets

Source: `src/JIM.PowerShell/JIM.psd1` `FunctionsToExport` (182, parse between `FunctionsToExport` and `CmdletsToExport` after stripping comments; a naive `@\((.*?)\)` regex stops at the `(#242)` comment) and `AliasesToExport` (`Get-JIMConnectedSystemDomainController`). Every exported function has a file under `Public/`; `Connect-JIMInteractive` and `Connect-JIMWithApiKey` are helpers inside `Connect-JIM.ps1`, deliberately not exported.

Documented = an `## <Cmdlet>` heading on a `docs/powershell/*.md` page. 172 of 182 have exactly one heading; no cmdlet has two; no heading names a non-existent cmdlet.

| Page | Cmdlets with headings |
|------|----------------------:|
| system.md 6, connection.md 3, connected-systems.md 35, run-profiles.md 5, synchronisation-rules.md 31, metaverse.md 24, password-synchronisation.md 3, predefined-searches.md 2, schedules.md 12, activities.md 4, api-keys.md 4, certificates.md 6, service-settings.md 3, feature-flags.md 3, security.md 5, history.md 4, previews.md 6, example-data.md 10, expressions.md 1, worker-tasks.md 2, file-system.md 2, logs.md 3 | 172 |

**Gaps (exhaustive):**
- **No heading, no syntax/parameter/output reference anywhere (1):** `Get-JIMDataFlow`. Only three example lines in `configuration/data-flow.md`; `powershell/index.md` links the "Data Flow" category to the configuration page, not a cmdlet page.
- **Documented only as rows of a summary table, no per-cmdlet heading/parameters (7):** `Get-JIMPredefinedSearchCriteriaGroup`, `New-JIMPredefinedSearchCriteriaGroup`, `Set-JIMPredefinedSearchCriteriaGroup`, `Remove-JIMPredefinedSearchCriteriaGroup`, `New-JIMPredefinedSearchCriterion`, `Set-JIMPredefinedSearchCriterion`, `Remove-JIMPredefinedSearchCriterion` (`powershell/predefined-searches.md` > "Criteria groups and criteria"). Either accept the pattern formally (allow-list) or give them headings.
- **Documented cmdlets that do not exist (stale names in prose, 3):**
  - `Invoke-JIMRunProfile` in `configuration/run-profiles.md:104` (real: `Start-JIMRunProfile`)
  - `Invoke-JIMSchedule` in `configuration/schedules.md:187` (real: `Start-JIMSchedule`)
  - `New-JIMCertificate` in `configuration/certificates.md:56` (real: `Add-JIMCertificate`)
- **Category counts in `powershell/index.md` are wrong for 3 rows:** Activities says 3 (actual 4), Metaverse says 21 (actual 24), Synchronisation Rules says 28 (actual 31). Table total 175 vs 182 exported.
- Known, not re-audited here: parameter/output tables are hand-maintained claims about DTOs (`docs/CLAUDE.md` records 8 of 20 pages naming non-existent properties). `scripts/Lint-DocExamples.ps1` already AST-parses every public cmdlet for alias misuse in fenced examples; the same AST pass can enumerate parameters for a parameter-coverage check.

---

## 4. REST API

Source: `[Route]` + `[Http*]` attributes on 23 controllers in `src/JIM.Web/Controllers/Api/` (282 endpoints; every action carries an XML `<summary>`, and `GenerateDocumentationFile` is on). The Scalar reference at `/api/reference` is generated from the OpenAPI document (`scripts/Generate-OpenApiDoc.ps1`, run by `docs.yml`/`publish-api-reference.yml`), so endpoint-level reference coverage is complete by construction.

Docs strategy (`docs/api/index.md`): the API section has only cross-cutting pages (index, authentication, rate limiting); "per-object behaviour lives in the Configuration section". Note `docs/CLAUDE.md` still describes per-resource `docs/api/<resource>/index.md` overview pages; none exist, so that guidance is stale and contradicts `api/index.md`.

| Resource group (endpoints) | Concept / overview page |
|----------------------------|-------------------------|
| activities (7) | configuration/activities.md |
| apikeys (8) | configuration/api-keys.md |
| auth (1), health (4), userinfo (1), system/health (1) | api/index.md > System endpoints; operations.md#service-health |
| certificates (12) | configuration/certificates.md |
| features (2) | administration/preview-features.md |
| generated-values (6) | synchronisation-rules.md#generated-values (Operations tab undocumented) |
| history/connected-systems (1), history/deleted-objects (2) | powershell/history.md only |
| metaverse/attributes (18), object-types (11), objects (11), pending-deletions (3) | configuration/metaverse.md |
| password-synchronisation (4) | concepts/passwords.md |
| predefined-searches (15) | configuration/predefined-searches.md |
| previews (3) | configuration/configuration-changes.md |
| schedules (11), schedule-executions (4) | configuration/schedules.md |
| security/roles (8), security/metaverse-objects (1) | configuration/roles.md |
| service-settings (7) | configuration/service-settings.md |
| synchronisation/connected-systems (65), pending-exports (2) | configuration/connected-systems.md |
| synchronisation/connector-definitions (6) | **None** (connectors/index.md does not describe Connector Definitions) |
| synchronisation/sync-rules (37) | configuration/synchronisation-rules.md |
| synchronisation/data-flows (1) | configuration/data-flow.md |
| synchronisation/test-expression (1) | concepts/expressions.md |
| example-data (17) | **None** outside powershell/example-data.md |
| logs (4) | **None** outside powershell/logs.md (one line in deployment.md) |
| filesystem (3) | **None** outside powershell/file-system.md |
| worker-tasks (3) | operations.md (Queue) partially; powershell/worker-tasks.md |
| history/cleanup (1) | schedules.md built-in History Retention Cleanup; powershell/history.md |
| system/reset (1) | **None** outside powershell/system.md |

**Resources with no concept/overview coverage (6):** connector-definitions, example-data, logs, filesystem, system/reset (factory reset), history/deleted-objects (portal page undocumented; only the cmdlet is).

**Broken API path in docs (found by matching every `/api/v1/...` string in docs against the route templates):** `docs/api/authentication.md` lines 24, 48, 57, 64 use `https://jim.example.com/api/v1/connected-systems`; the real route is `/api/v1/synchronisation/connected-systems`, so all four copy-paste examples on the authentication page return 404. Every other API path in docs resolves.

---

## 5. Settings

### 5a. Service Settings

Source: `Constants.SettingKeys` in `src/JIM.Models/Core/Constants.cs` (34 keys); definitions (display name, category, default, read-only) are seeded in `src/JIM.Application/Servers/SeedingServer.cs` (32) plus `Security.ConfigurationChangeHashKey`, created lazily in `ServiceSettingsServer.GetOrCreateConfigurationChangeHashKeyAsync`.

Docs: `docs/administration/configuration.md` > Service Settings has a 13-row table and says "the settings listed below are the ones most commonly adjusted; the full list is discoverable in the admin UI". `docs/configuration/service-settings.md` explains the mechanism but names no keys.

In the reference table (13): Instance.Name, Instance.Id, SSO.EnableLogOut, Sync.PartitionValidationMode, History.RetentionPeriod, History.ConfigurationChangeRetentionPeriod, History.SecurityEventRetentionPeriod, History.PasswordEventRetentionPeriod, History.CleanupBatchSize, ChangeTracking.ConfigurationChanges.Enabled, Security.RateLimiting.Enabled, Security.RateLimiting.AuthenticatedRequestsPerMinute, Security.RateLimiting.UnauthenticatedRequestsPerMinute.

Named elsewhere but not in the table (5): Sync.PostClearReconciliation.MaxMissingPercent (`connected-systems.md`), ChangeTracking.SyncOutcomes.Level (`activities.md`), Sync.PageSize and ChangeTracking.CsoChanges.Enabled (PowerShell examples only), Maintenance.IsEnabled (by display name "Maintenance mode" only).

Named only in passing by display name, never by key (2): `SSO.Secret` ("SSO secret", as the env var), `Security.CredentialEncryptionEnabled` ("Credential encryption", once in `service-settings.md` prose).

**Not named anywhere in docs, by key or display name (13):**
- Editable (5): `Sync.VerboseNoChangeRecording`, `Preview.WorkerThreshold`, `Preview.FullDataSetPromptThreshold`, `ChangeTracking.MvoChanges.Enabled`, `UI.ProgressUpdateInterval`.
- Read-only mirrors of environment variables (7; documented as the env var, not as the setting): `SSO.Authority`, `SSO.ClientId`, `SSO.ApiScope`, `SSO.ClaimType`, `SSO.MvAttribute`, `SSO.UniqueIdentifierClaimType` (default `sub`; it has no env var row at all), `Security.EncryptionKeyPath`.
- System-managed (1): `Security.ConfigurationChangeHashKey`.

**Documented keys that no longer exist:** none.

**Source finding:** `Worker.StaleTaskTimeout` (`Constants.SettingKeys.StaleTaskTimeout`) is read by the Scheduler with a 5-minute default and classified for change history, but is never seeded, so it never appears in the portal and cannot be set; either seed it or remove the constant.

### 5b/5c. Environment variables

Runtime (string literals passed to `Environment.GetEnvironmentVariable` or held in env-name constants; excludes `JIM_RELATED`/`JIM_SOURCE`, which are SQL aliases in the SQL Connector, and `JIM_OPENAPI_*`, build tooling): 24. All except one are in `docs/administration/configuration.md` tables.
- **Undocumented:** `JIM_CACHE_WARM_PARALLELISM` (`src/JIM.Worker/Worker.cs:94`).

Deployment (production compose, Podman yaml): `JIM_VERSION`, `JIM_WEB_PORT`, `JIM_DB_IMAGE`, `JIM_DB_SHARED_BUFFERS`, `JIM_DB_EFFECTIVE_CACHE_SIZE`, `JIM_DB_MAINTENANCE_WORK_MEM`, `JIM_DB_WORK_MEM`, `JIM_DB_SHM_SIZE`, `JIM_DB_LOG_MIN_DURATION`: all documented. `JIM_BUILD_*` (5 build args) are documented in `developer/building.md`, which is the right home. `JIM_DB_CHECKPOINT_TIMEOUT`, `JIM_DB_WAL_*`, `JIM_DB_MIN/MAX_WAL_SIZE` appear only in `docker-compose.override.yml` (dev); exclude.

Installer (`deploy/setup.sh` header documents them as the non-interactive interface): `JIM_INSTALL_DIR` documented; `JIM_SETUP_BACKUP_CONFIRMED` documented (`upgrading.md`).
- **Undocumented (11):** `JIM_SETUP_DB_MODE`, `JIM_SETUP_AUTO_START`, `JIM_SETUP_TLS_MODE`, `JIM_SETUP_TLS_NAMES`, `JIM_SETUP_TLS_CERT_FILE`, `JIM_SETUP_TLS_KEY_FILE`, `JIM_SETUP_RUNTIME`, `JIM_SETUP_PODMAN_ROOTLESS`, `JIM_SETUP_PODMAN_ACCOUNT`, `JIM_SETUP_FIX_APPARMOR`, `JIM_SETUP_OPEN_FIREWALL`. No docs page describes a non-interactive/unattended install.
- Script-internal, exclude: `JIM_UID`, `JIM_READY`, `JIM_RELEASE_VERSION`, `JIM_PODMAN_ACCOUNT`, `JIM_PODMAN_SYSTEMD` (install-state records).

**Documented env vars that no longer exist:** none found (every `JIM_*` in customer docs maps to source, compose, Podman yaml or the installer).

---

## 6. Connectors and their settings

Built-in connectors with docs pages: File, LDAP, SCIM 2.0 Client, SQL (plus two SQL Delta Import how-tos). The `Mock` connector is test-only.

Settings from each connector's `GetSettings()` (Heading and Label types excluded): LDAP 21, File 8, SQL 16, SCIM 20 = **65, all documented** in the connector's settings tables. Two match only after normalisation: LDAP "Create containers as needed?" (docs: "Create Containers as Needed") and SCIM "Authentication Header Value" (docs row "Authentication Header Name / Value"). Reverse check of every docs settings table against source: **0 stale settings**.

Extraction note: a regex parse of `GetSettings()` initially missed SQL's "Delta Import Mode" (multi-line initialiser) and could not resolve names held in `LdapConnectorConstants`/`SqlConnectorConstants` without a second pass. Reflection (instantiate each `IConnectorSettings` and call `GetSettings()`) is the only robust extractor.

Bonus inventory, expression functions (`DynamicExpressoEvaluator.SetFunction("...")`, 36): 35 documented in `concepts/expressions.md` tables. **Undocumented: `ToDecimal`.**

Optional enum inventory (API- and PowerShell-visible enum names, spot-checked): run types, Schedule step/trigger/pattern types, deletion rules, deprovisioning actions, Missing Input Behaviour, search operators, Activity and Schedule Execution statuses, Pending Password Change statuses are fully named in docs. `ActivityTargetOperationType` has 8 of 22 members unnamed (SchemaRefreshRemoval, RetryPasswordDelivery, CancelPasswordDelivery, DiscoverAuxiliaryClasses, RecallAttributeValues, RestartGeneratedValues, AllowGeneratedValueRename, RetryGeneratedValue), which matters to anyone filtering Activities by operation over REST/PowerShell. Sync outcome types are shown with friendly labels, so enum names are the wrong unit there.

---

## 7. Changelog features (Unreleased, 0.16.0, 0.15.0)

121 ✨ entries (24 + 8 + 89), plus 52 🔄. About 55 distinct named features were grepped for in customer docs (cmdlets, UI labels, setting names, feature names). **Every one was findable.** `Lint-DocsCoupling.ps1` already forces a docs change per ✨/🔄 entry, which explains the high hit rate; it cannot tell whether the right page changed.

Naming drift found while spot-checking (violations of "one name per thing"):
- **Example Data Template vs Data Generation Template**: UI 14 vs 3 uses, docs 13 vs 15; cmdlets say ExampleDataTemplate; `powershell/index.md` says "Data Generation Templates".
- **Container Scope options**: UI toggle says "Whole subtree" / "This level", the same component's tooltip says "This and below"; `jim-ldap-connector.md` says "Whole subtree"/"This level"; `connected-systems.md` says "This and below"/"One Level". The 0.15.0 changelog calls it "This level only".
- **Directory Capabilities card** (0.15.0 changelog) is now the **Detected** strip in UI and docs; changelog is history, so only a note.
- **Sync vs Synchronisation**: "Sync Pipeline" page H1 vs "Synchronisation Pipeline" nav title; "Full Sync vs Delta Sync" heading vs glossary's Full/Delta Synchronisation.
- **Run Profile "page size"** (glossary) vs **Batch size** (UI and run-profiles.md).

---

## 8. Nav reachability and links

- 100 docs pages, 100 nav entries, exact match: no page unreachable from nav, no nav entry without a file.
- 1,254 internal Markdown links (690 with anchors) all resolve, using a Python-Markdown-compatible slugifier.
- CI gap: `mkdocs build --strict` runs only on push to `main` (`docs.yml`) and checks pages, not anchors (`validation.anchors` is not set in `mkdocs.yml`). There is no PR-time docs build, so broken anchors would publish.

---

## 9. Recommended mechanical coverage check

### Principle

Each inventory is generated from source by the most reliable extractor available, and "documented" means the item appears in a **structured location** (a heading, a table's first cell, a glossary `{ #anchor }`), never just somewhere in free text. Substring matches over prose are what make coverage checks noisy ("Password" matches everything).

### Inventories that can be generated reliably

| Inventory | Extractor | Reliability | "Documented" assertion |
|-----------|-----------|-------------|------------------------|
| Cmdlets + aliases | `Import-PowerShellDataFile JIM.psd1` (FunctionsToExport, AliasesToExport), cross-checked with PowerShell AST of `Public/**` (as `Lint-DocExamples.ps1` already does) | High | Exactly one `## <Cmdlet>` heading under `docs/powershell/`; aliases need no heading. Reverse: every `Verb-JIM*` token in docs (headings, inline code, fenced code) is an exported function or alias. `powershell/index.md` counts equal heading counts per page. |
| Cmdlet parameters (phase 2) | Same AST pass (`ParamBlockAst`) | High | Each parameter appears in the cmdlet's Parameters table; reverse check catches documented parameters that do not exist. |
| Connector settings | Reflection in an NUnit test: every `IConnectorSettings` from the built-in connector list, `GetSettings()`, excluding Heading/Label | High (regex is not: it missed a multi-line initialiser) | Setting name appears (case-insensitive, trailing `?` stripped, `A / B` rows split) in the first cell of a table on that connector's page. Reverse check on the same tables. |
| Service Settings | Reflection over `Constants.SettingKeys`, plus the seeded set (run `SeedingServer` against the in-memory repository, or reflect its definitions) | High | Key appears backticked in the first column of the Service Settings table in `administration/configuration.md`. Separate assertion: every `SettingKeys` constant is seeded (would catch `Worker.StaleTaskTimeout`). |
| Environment variables | Regex over `GetEnvironmentVariable("JIM_...")` call sites and the env-name constants class (Roslyn if it needs hardening); `${JIM_*}` in `docker-compose.yml`, `deploy/docker-compose.production.yml`, `deploy/podman/*.yaml`; the documented variable block in `deploy/setup.sh` | Medium-high (restrict to call sites, or `JIM_RELATED`/`JIM_SOURCE` SQL aliases become false positives) | Name appears backticked in a table first cell in `administration/configuration.md` (installer variables: `deployment.md`). `docker-compose.override.yml` and `JIM_BUILD_*` excluded by rule (developer docs). |
| REST endpoints | The OpenAPI JSON `Generate-OpenApiDoc.ps1` already produces (paths, operationIds, tags); fallback: regex over `[Route]`/`[Http*]` (worked here for all 282, but misses conventions such as `[controller]` casing) | High | Reference completeness is by construction, so assert instead: (a) every `/api/v1/...` path in docs matches an OpenAPI path template (would have caught the authentication.md 404s); (b) every OpenAPI tag maps to a concept page through a small committed map (or `api_tags:` front matter on Configuration pages), and that page exists. |
| Portal routes | Reflection over `JIM.Web` for components carrying `RouteAttribute` (avoids the BOM problem that hid 10 pages from grep) | High | Make the mapping source-owned: add a `DocsPath` parameter to the existing `PageInfo` component (or a `[DocsPage("configuration/x.md#anchor")]` attribute). A test asserts every routable component declares one and that the file and anchor exist. Bonus: users get an in-product docs link. |
| Expression functions | Regex `SetFunction("Name"` (reliable: single literal per call) or reflection over the interpreter | High | Function appears as a row in a function table in `concepts/expressions.md`. |
| Glossary terms | Parse glossary (`Term { #anchor }`); source the noun list from a machine-readable vocabulary (extend the DEVELOPER_GUIDE Vocabulary table to the ~50 real product nouns, or move it to a YAML file the guide renders from) plus `TermDefinitions` (already tested) | Medium (the list is curated, not derived) | Each vocabulary noun has a glossary entry with an anchor; each glossary entry links to an existing page/anchor ("See ..."). |
| Doc links and anchors | Python-Markdown-compatible slugifier over all docs | High | Every relative link and anchor resolves; also set `validation: anchors: warn` in `mkdocs.yml` and run `mkdocs build --strict` at PR time. |

Not mechanically enforceable, and should not be pretended to be: whether prose is correct, whether a changelog feature is explained on the *right* page (Lint-DocsCoupling proves only that some page changed), and discovery of new product nouns. For the last, a Title Case n-gram miner (`titlecase.py` here) is useful as a **report** that lists frequent capitalised phrases absent from the glossary and the allow-list, but it is too noisy to gate on ("Active Directory", "Jane Smith", "Before You Install" all surface).

### Where it runs

- **Text-only checks** (cmdlets via AST, env vars, API paths in docs, glossary structure, links/anchors, expression functions): one PowerShell script, `scripts/Test-DocsCoverage.ps1`, with Pester tests under `scripts/Tests/`, added as a step in the existing `changelog-lint` job next to `Lint-DocExamples.ps1`. That job runs on every PR, including docs-only PRs, and is already a required check.
- **Reflection checks** (connector settings, Service Settings seeding, portal routes' `DocsPath`, OpenAPI tags): NUnit tests following the `GlossaryTermConsistencyTests` precedent (locate `JIM.sln`, read `docs/` from the repository), in `test/JIM.Web.Tests` / `test/JIM.Worker.Tests`. These run in `build-and-test`. The OpenAPI tag check can reuse the build with `Generate-OpenApiDoc.ps1 -NoBuild`.

### Deliberate exclusions

One allow-list file, e.g. `docs/coverage-exclusions.yml`, one entry per item:

```yaml
- inventory: route            # cmdlet | route | api-tag | service-setting | env-var | connector-setting | term | expression-function
  id: /admin/theme-preview
  reason: Developer-only design preview, not reachable from navigation
  review-by: 2027-01-31       # optional; required for "documentation pending" entries
  issue: 1454                 # optional
```

Rules: `reason` is mandatory; an entry whose item no longer exists in source, or which is now documented, fails the check (prevents allow-list rot, the same discipline `.trivyignore` uses); "pending" entries need an issue and a review date; the check prints the allow-list size so growth is visible in review. Category-level rules (exclude `docs/developer/`, `docker-compose.override.yml`, `JIM_BUILD_*`, Heading/Label settings) live in the script, not the list.

Initial allow-list candidates from this audit: dev routes (`/admin/theme-preview`, `/admin/loading-preview`, `/dev/error-pages`, Error, `_Host`); the 7 predefined-search criteria cmdlets if the summary-table pattern is accepted; read-only SSO/encryption Service Settings mirrors (pointing at their env var rows); `Security.ConfigurationChangeHashKey`.

### False-positive risks and mitigations

- **Free-text matching**: generic names (Password, Host, Port, Mode) match everywhere. Mitigate by asserting on headings and table first cells only, scoped to the owning page.
- **Normalisation**: trailing `?`, case, plurals, combined rows ("Name / Value"), display name vs key. Define one normaliser and unit-test it.
- **Anchors**: slugs must match Python-Markdown's `toc` (emoji stripped, duplicate suffixes `_1`, explicit `{ #id }`); reuse one slugifier, or let MkDocs validate anchors.
- **Renames**: UI labels drift from docs ("This level" vs "One Level"); checks keyed on stable identifiers (keys, cmdlet names, route templates, setting names) are robust, label-keyed checks are not.
- **Extraction brittleness**: regex over C# missed a multi-line initialiser and BOM-prefixed `@page` lines in this audit; prefer reflection/AST/OpenAPI wherever a build is already available.
- **Partial-by-design pages**: the Service Settings table says "most commonly adjusted". Decide whether the table becomes complete (recommended: 34 rows is manageable) or the check accepts "named anywhere on the page"; do not leave it ambiguous.
- **Docs-only PRs and reflection tests**: deleting a heading in a docs-only PR is caught by the PowerShell checks at PR time; reflection-based checks catch it on the next code PR. Keep every assertion that can be text-only in the PowerShell script.

### Suggested rollout

1. Fix the concrete defects found (section 10) so the first run is green with a small allow-list.
2. Phase 1 gates (cheap, high signal): cmdlets (both directions + index counts), API paths in docs, connector settings, Service Settings (+ seeding), env vars, expression functions, links/anchors.
3. Phase 2: `PageInfo DocsPath` for routes; OpenAPI tag-to-page map; cmdlet parameter tables.
4. Phase 3: canonical vocabulary file driving the glossary check; n-gram miner as a non-blocking report.

---

## 10. Concrete defects to fix (all docs-only unless noted)

1. `docs/api/authentication.md` (4 examples): `/api/v1/connected-systems` -> `/api/v1/synchronisation/connected-systems`.
2. Stale cmdlet names: `Invoke-JIMRunProfile` (`configuration/run-profiles.md:104`) -> `Start-JIMRunProfile`; `Invoke-JIMSchedule` (`configuration/schedules.md:187`) -> `Start-JIMSchedule`; `New-JIMCertificate` (`configuration/certificates.md:56`) -> `Add-JIMCertificate`.
3. `powershell/index.md` counts: Activities 4, Metaverse 24, Synchronisation Rules 31.
4. Add a `Get-JIMDataFlow` reference section (syntax, parameters, output).
5. `configuration/operations.md` > The tabs: add Generated Values.
6. Document `ToDecimal` in `concepts/expressions.md`.
7. Document `JIM_CACHE_WARM_PARALLELISM` and the 11 `JIM_SETUP_*` installer variables (an "Unattended install" section in `deployment.md`).
8. Complete the Service Settings table (20 visible settings are missing from it).
9. Add how-to coverage for the Deleted Objects, Example Data (templates and data sets), Connectors (Connector Definitions), Factory Reset, Logs, Home and My Activity portal pages.
10. Glossary: add the ~25 high-priority nouns in section 1c; add "See ..." links to the 31 entries without one; fix Run Profile "page size", rename Scoping -> Scoping Criteria, add Enforce State.
11. Resolve naming drift: Example Data Template vs Data Generation Template; Container Scope option names; "Sync Pipeline" title.
12. `docs/CLAUDE.md`: remove or correct the reference to `docs/api/<resource>/index.md` pages, which do not exist.
13. Product (code), outside docs scope: dead Dashboard nav links (`NavMenu.razor`); no link to `/admin/pending-deletions`; `Worker.StaleTaskTimeout` never seeded.
