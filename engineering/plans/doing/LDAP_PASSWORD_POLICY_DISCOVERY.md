# Password Policy Discovery for OpenLDAP and 389 Directory Server

- **Status:** Doing
- **Created:** 2026-09-19
- **Issue:** [#1702](https://github.com/TetronIO/JIM/issues/1702)
- **PRD:** [PRD_LDAP_PASSWORD_POLICY_DISCOVERY.md](../../prd/doing/PRD_LDAP_PASSWORD_POLICY_DISCOVERY.md)
- **Follows:** [#1121](https://github.com/TetronIO/JIM/issues/1121) Initial Password Provisioning (introduced discovery and reconciliation), [#1699](https://github.com/TetronIO/JIM/pull/1699) (neutralised the panel wording)

## Overview

JIM reads a Connected System's password policy so a generated initial password satisfies the target without an administrator restating its rules, and so one password set across several systems satisfies all of them. The model (`ConnectedSystemPasswordPolicy`) is directory-neutral; the only reader (`LdapConnectorPasswordPolicy`) is Active Directory only, and the LDAP Connector claims discovery for every directory. OpenLDAP with the `ppolicy` overlay and 389 Directory Server both publish an equivalent policy and both have per-subtree or per-object overrides that play the role of Fine-Grained Password Policies.

This plan adds 389 Directory Server as a first-class `LdapDirectoryType`, implements a policy reader and an override probe per directory type behind the existing entry point, renames the override signal to a directory-neutral name across model, database, API, PowerShell and portal, records why nothing was read when nothing was, and proves the OpenLDAP path with an integration scenario in which generated passwords satisfy a minimum length above JIM's default.

## Business Value

- Administrators running OpenLDAP or 389 see the target's real rules on the Password Policy panel instead of a message that names Active Directory and is untrue.
- Generated initial passwords into those directories satisfy the discovered policy, so Connected System Objects are not parked on their first export by a length or history rule JIM could have read.
- The "another policy may apply" caveat and the "further checks JIM cannot see" caveat explain a refusal after a satisfied policy, on every supported directory, in the same words.
- A directory that publishes nothing readable says so and why, so nobody chases a configuration problem that does not exist.

## Technical Architecture

### Current state

| Concern | Where | What it does today |
|---|---|---|
| Directory type | `src/JIM.Connectors/LDAP/LdapConnectorEnums.cs` (`LdapDirectoryType`), `LdapConnectorUtilities.DetectDirectoryType` | AD via capability OIDs (Samba by `vendorName`), OpenLDAP by `vendorName` or `structuralObjectClass`, else `Generic`. 389 is detected as `Generic`. |
| Root DSE reads | `LdapConnectorUtilities.GetBasicRootDseInformation` (used by schema, policy discovery, preflight and the password channel), `LdapConnectorImport.GetRootDseInformation` (used by imports and persisted) | The basic read asks for four attributes; neither asks for `vendorVersion`, `configContext` or `supportedControl`. `LdapConnectorRootDse` is persisted as JSON with enum ordinals. |
| Per-type behaviour | `LdapConnectorRootDse` computed properties (`ExternalIdAttributeName`, `UseUsnDeltaImport`, `UseAccesslogDeltaImport`, `RecommendedExportConcurrency`, `SupportsPaging`), `LdapConnectorExport.cs`, `LdapConnectorImport.cs`, `LdapConnector.DescribeDirectoryTypeForCapabilities` and `DescribeDirectory` | Switch expressions, each with a `_ =>` default. |
| Policy reader | `src/JIM.Connectors/LDAP/LdapConnectorPasswordPolicy.cs` | `GetPasswordPolicyAsync(string domainRootDn)`: returns null unless AD or Samba; base read of the domain root; `DetectFineGrainedPoliciesAsync` reads `domainFunctionality` then searches the Password Settings Container; empty result is `CouldNotDetermine`. Three searches. |
| Reader callers | `LdapConnector.GetPasswordPolicyAsync` (opens its own connection during schema import) and `LdapConnectorPreflight.CheckPolicyDiscoveryAsync` (returns early for non-AD) | Both derive the domain root from `defaultNamingContext`. |
| Capability flag | `IConnectorCapabilities.SupportsPasswordPolicyDiscovery`, mirrored onto `ConnectorDefinition` by `ConnectorCapabilityMirror` at startup, read in `ConnectedSystemServer.GetAccountsForPasswordSetAsync` into `MetaverseObjectAccount.ConnectorCanDiscoverPasswordPolicy` | Static per Connector; `true` for the LDAP Connector regardless of directory. |
| Persistence of a discovered policy | `ConnectedSystemServer.DiscoverPasswordPolicyAsync` | Null return: log and keep the previous row. Non-null: `PasswordPolicyDiscovered = true`, then insert or copy field by field onto the existing row. |
| Model | `src/JIM.Models/Staging/ConnectedSystemPasswordPolicy.cs`, `PasswordPolicyEnums.cs` (`FineGrainedPolicySignal`) | `HasAnyDiscoveredConstraint` drives every "nothing was read" branch. |
| Consumers | `PasswordGeneratorService` (`Reconcile`, `Combine`, `CheckAgainstTarget`, `DeriveFrom`), `PasswordPolicyReconciliation`, `PasswordGenerationDtos.cs` (`ConnectedSystemPasswordPolicyResponse`), `Get-JIMConnectedSystemPasswordPolicy.ps1`, `ConnectedSystemPasswordPolicyPanel.razor`, `SetPasswordDialog.razor` (`SystemsAwaitingSchemaRefresh`, `SystemsThatPublishNoPolicy`) | A null `RequiredCharacterClassCount` already means "no constraint" (nullable `Max`); `RecognisedCharacterClasses.None` already means "count them all". |
| Tests | `test/JIM.Worker.Tests/Connectors/LdapConnectorPasswordPolicyTests.cs` (Moq `ILdapOperationExecutor`, `LdapTestResponses` helpers, `SetupDirectory` routes by request DN), `LdapConnectorPreflightTests.cs`, `Services/PasswordPolicyReconciliationTests.cs`, `test/JIM.Web.Api.Tests/SynchronisationControllerPasswordGenerationTests.cs`, `test/JIM.Web.Tests/SetPasswordDialogTests.cs` | |
| Integration | `test/integration/docker/openldap/` (Bitnami `bitnamilegacy/openldap`, `01-add-second-suffix.sh` already writes overlays and ACLs through `cn=config` as the config admin), `Setup-Scenario17.ps1` (composes `Setup-Scenario1.ps1`, then `Set-JIMSyncRuleInitialPassword`), `Get-DirectoryConfig` binds as the rootdn | No ppolicy overlay; no TLS. |

### Proposed design

**Directory type.** `LdapDirectoryType.DirectoryServer389` appended after `Generic` (ordinal 4, because the enum is persisted as an integer inside `PersistedConnectorData`). Detected when `vendorName` contains "389" or `vendorVersion` starts with "389-Directory" (the second also covers Red Hat Directory Server builds that brand the vendor name differently). Every per-type switch gains an arm; for everything except password policy the arm has Generic's behaviour (entryUUID, changelog delta, paging supported, default export concurrency), so existing 389 deployments change nothing except the label. Capability display: "389 Directory Server".

**Root DSE.** `LdapConnectorRootDse` gains `VendorVersion`, `ConfigContext`, `SupportedControls` and `DefaultNamingContext` (all nullable, so old persisted JSON deserialises as today). Both root DSE reads request `vendorVersion`, `namingContexts`, `configContext`, `supportedControl` and `defaultNamingContext`, which removes the separate `GetDefaultNamingContext` search from the two policy callers.

**Reader dispatch.** `LdapConnectorPasswordPolicy` becomes the entry point and dispatcher, keeping its constants and shared helpers (`ParseInterval`, `IsComplexityRequired`, `ReadInt`, `ReadRaw`), with the AD reader moved unchanged into `LdapConnectorPasswordPolicyActiveDirectory.cs` and two new readers, `LdapConnectorPasswordPolicyOpenLdap.cs` and `LdapConnectorPasswordPolicy389.cs`, all internal and all taking `ILdapOperationExecutor` so the existing Moq pattern covers them. The input changes from a domain root string to an `LdapPasswordPolicyScope` record (`DefaultNamingContext`, `NamingContexts` filtered to user contexts, `ConfigContext`, `AdvertisesPasswordPolicyControl`), built by a static `LdapPasswordPolicyScope.From(LdapConnectorRootDse)`. Generic returns a row with `DiscoveryOutcome = NotPublished` and no constraints.

**OpenLDAP reader.** If the ppolicy request control (`1.3.6.1.4.1.42.2.27.8.5.1`) is not advertised in `supportedControl`: `NotPublished`. Otherwise, in order: (1) one search of `configContext` for `(|(objectClass=olcPPolicyConfig)(objectClass=olcDatabaseConfig))` returning `olcPPolicyDefault`, `olcPPolicyCheckModule` and `olcSuffix`, correlating each overlay with its database by DN; (2) one subtree search of the first user naming context for `(objectClass=pwdPolicy)` returning the policy attributes, which supplies both the default policy entry (matched by DN from step 1, or the sole entry when step 1 was refused) and the count of policy entries; (3) one subtree probe `(pwdPolicySubentry=*)` with size limit 1. Mapping: `pwdMinLength` (absent or 0 is null), `pwdInHistory` (absent or 0 is null), `pwdMaxAge` and `pwdMinAge` in seconds (absent or 0 is null), `ComplexityRequired` and `RequiredCharacterClassCount` null, `RecognisedCharacterClasses` None. `FurtherChecksApply` when `pwdCheckQuality` is 1 or 2 and a check module is named (`pwdCheckModule` on the entry or `olcPPolicyCheckModule` on the overlay); when the overlay configuration was refused, fall back to the coarser rule (`pwdCheckQuality` of 1 or 2). Outcomes: step 1 refused and step 2 finds exactly one policy: `Read`; step 1 refused and step 2 finds several: `ConfigurationNotReadable` (which one is the default cannot be known); step 2 finds none: `NoPolicyConfigured`. Override signal: `Present` when any `pwdPolicySubentry` entry, more than one `pwdPolicy` entry, or two databases with different defaults; `CouldNotDetermine` on an empty probe or a refused search; `Absent` only when the control is not advertised.

**389 reader.** (1) one base read of `cn=config` for the `password*` attributes; (2) one subtree probe per user naming context (capped at five) for `(|(objectClass=nsPwPolicyContainer)(pwdpolicysubentry=*))` with size limit 1. Mapping honours 389's switches: `passwordMinLength` and `passwordMinCategories` only when `passwordCheckSyntax` is on (then `ComplexityRequired = true` when categories exceed one, and the five recognised classes with 8-bit mapped to `OtherUnicodeLetter`); `passwordInHistory` only when `passwordHistory` is on; `passwordMaxAge` only when `passwordExp` is on and the value exceeds 0; `passwordMinAge` when it exceeds 0; all in seconds. `FurtherChecksApply` when syntax checking is on and any of `passwordDictCheck`, `passwordPalindrome`, `passwordMaxRepeats`, `passwordMaxSequence`, `passwordMaxSeqSets`, `passwordMaxClassChars`, `passwordMinDigits`, `passwordMinAlphas`, `passwordMinUppers`, `passwordMinLowers`, `passwordMinSpecials`, `passwordMin8Bit` or `passwordMinTokenLength` is set. An empty or refused read of `cn=config`: `ConfigurationNotReadable` with `CouldNotDetermine`. The probe: entries means `Present`; empty, refused or an administrative limit (unindexed search) means `CouldNotDetermine`. `Absent` is never provable for 389.

**Model.** `FineGrainedPolicySignal` renamed to `PolicyOverrideSignal` (type and property; values unchanged, so stored integers are unchanged). Two new columns: `FurtherChecksApply` (bool, default false) and `DiscoveryOutcome` (`PasswordPolicyDiscoveryOutcome`: `Read = 0`, `NotPublished = 1`, `ConfigurationNotReadable = 2`, `NoPolicyConfigured = 3`; default `Read` so existing AD rows are right). One migration, checked to contain `RenameColumn`.

**Capability per Connected System.** `SupportsPasswordPolicyDiscovery` stays `true` on the LDAP Connector (the Connector can discover, where the directory publishes). `GetAccountsForPasswordSetAsync` derives `ConnectorCanDiscoverPasswordPolicy = definition.SupportsPasswordPolicyDiscovery && policy?.DiscoveryOutcome != NotPublished`, so a Generic directory that has been schema-refreshed reports "publishes nothing" in the Set Password dialog and one that has not reports "refresh the schema", which is the distinction the dialog already draws. The API's `ConnectedSystemPasswordPolicyResponse` carries `discoveryOutcome` so a script can tell the same thing.

**Persistence.** `DiscoverPasswordPolicyAsync` sets `PasswordPolicyDiscovered = discovered.HasAnyDiscoveredConstraint` and copies the three new or renamed fields. A null return still means "the Connector could not say anything at all" and keeps the previous row.

**Reconciliation.** `PasswordPolicyReconciliation` gains `SystemsApplyingFurtherChecks` (names); the Set Password dialog states it beside the constraints. `MayBeStricterThanDiscovered` is unchanged.

**Portal.** The panel's "nothing read" alert becomes a switch on `DiscoveryOutcome`: `NotPublished` ("This directory publishes no password policy that JIM can read"), `ConfigurationNotReadable` ("JIM could not read this directory's password policy: the account it connects as cannot read the server configuration that holds it"), `NoPolicyConfigured` ("This directory's password policy mechanism is loaded but no policy is configured, so no rules apply"), and `Read` with no constraints (today's "refresh the schema" wording). The override alert names the three mechanisms neutrally. A new line under the table when `FurtherChecksApply`: "The directory applies further checks JIM cannot see, so a password satisfying everything above can still be refused." The preflight's `CheckPolicyDiscoveryAsync` words its result per directory type from the same outcome. The panel cannot know the directory type (`PersistedConnectorData` is opaque to it by design), so its wording is per outcome and directory-neutral.

### Data flow

Schema import or refresh (`ConnectedSystemServer`) calls `LdapConnector.GetPasswordPolicyAsync` on its own connection: the basic root DSE read gives type and scope; the dispatcher picks the reader; the row (constraints, outcome, further-checks flag, override signal) is persisted. Preflight (`RunPasswordPreflightAsync`) follows the same path on a throwaway connection and words the result. Reconciliation, the generator and the dialog consume the row as today, plus the two new fields.

## Decisions (where the code makes the PRD harder than written)

1. **PRD requirement 4 is met in effect, not in the literal property.** `SupportsPasswordPolicyDiscovery` is a per-Connector flag mirrored onto `ConnectorDefinition` at startup with no connection open, so it cannot be type-dependent. The per-directory answer travels on the policy row as `DiscoveryOutcome`, and `ConnectorCanDiscoverPasswordPolicy` is derived from both. PRD Scenario 5 is satisfied by `ConnectorCanDiscoverPasswordPolicy` being false and `discoveryOutcome` being `NotPublished` on the policy endpoint; the Connector Definition DTO stays `true`.
2. **PRD requirement 3 wins over Scenario 1's "no override warning".** An empty `pwdPolicySubentry` probe is `CouldNotDetermine` (the attribute is operational and may be ACL-hidden), so OpenLDAP shows the softer "could not establish" alert unless the overlay is absent. Scenario 1 in the PRD is amended accordingly.
3. **Further checks on OpenLDAP flag only a named check module.** The PRD's rule (`pwdCheckQuality` 1 or 2) would flag every policy that enforces its minimum length, because the overlay only checks length when `pwdCheckQuality` is above 0. The rule falls back to the PRD's when the overlay configuration could not be read.
4. **389's gating switches are honoured.** `passwordCheckSyntax`, `passwordHistory` and `passwordExp` gate the numbers; reading the numbers alone would report rules the server does not apply.
5. **The search budget** is read as "the policy read plus at most two probes" (AD today is three searches). OpenLDAP: three; 389: two.

## Implementation Phases

Phase 1 is sequential and must land first: it renames a type used everywhere, so nothing else can compile against both names. After it, Phases 2 to 3 (one agent, sequential, they share `LdapConnector.cs`), Phase 4, Phase 5 and the authoring half of Phase 6 run in parallel with disjoint file ownership. Phase 6's verification runs last against the merged branch.

### Phase 1: Model, migration and the rename (sequential; foundation)

Owns: `src/JIM.Models/Staging/PasswordPolicyEnums.cs`, `src/JIM.Models/Staging/ConnectedSystemPasswordPolicy.cs`, `src/JIM.Models/Staging/MetaverseObjectAccount.cs` (doc comment only), `src/JIM.PostgresData/Migrations/` (new migration and snapshot), `src/JIM.Application/Servers/ConnectedSystemServer.cs`, `src/JIM.Web/Models/Api/PasswordGenerationDtos.cs`, `src/JIM.PowerShell/Public/ConnectedSystems/Get-JIMConnectedSystemPasswordPolicy.ps1`, `docs/powershell/connected-systems.md`, `docs/api/index.md`, `CHANGELOG.md`, and mechanical compile fixes (rename only, no behaviour) in `LdapConnectorPasswordPolicy.cs`, `LdapConnectorPreflight.cs`, `PasswordGeneratorService.cs`, `ConnectedSystemPasswordPolicyPanel.razor` and the tests that name the enum.

Deliverables:
- `PolicyOverrideSignal` enum (rename of `FineGrainedPolicySignal`, values and summaries kept, wording neutral), `PasswordPolicyDiscoveryOutcome` enum, and on the model `PolicyOverrideSignal`, `FurtherChecksApply`, `DiscoveryOutcome`.
- Migration `RenamePolicyOverrideSignalAddDiscoveryOutcome` via `dotnet ef migrations add ... --project src/JIM.PostgresData`; verify the scaffold used `RenameColumn` (precedent: `20260404212013_RenameLastDeltaSyncCompletedAtToLastSyncCompletedAt.cs`) and hand-edit if it produced drop-and-add; run `dotnet ef migrations has-pending-model-changes`; regenerate after merging `main` if `main` gained migrations.
- `DiscoverPasswordPolicyAsync`: `PasswordPolicyDiscovered = discovered.HasAnyDiscoveredConstraint`; copy the three fields. `GetAccountsForPasswordSetAsync`: derive `ConnectorCanDiscoverPasswordPolicy` as above.
- API DTO: `policyOverrideSignal`, `furtherChecksApply`, `discoveryOutcome` (null when no row). PowerShell help and the docs table renamed and extended; `docs/api/index.md` breaking-changes entry.
- Changelog, both entries now so no later phase touches the file: a ✨ Added entry for discovery on OpenLDAP and 389 (customer wording, link to the passwords concept page, `(#1702)`), and a 🔄 Changed entry: "**Breaking (REST API and PowerShell):** the discovered password policy's `fineGrainedPolicySignal` is now `policyOverrideSignal`, with the same three values, because the signal is no longer specific to Active Directory. Pre-v1.0 breaking change. (#1702)".

Tests: existing suites compile and pass under the new names; `SynchronisationControllerPasswordGenerationTests` and `ConnectedSystemServer` tests cover `PasswordPolicyDiscovered` false for a row with no constraints and the `ConnectorCanDiscoverPasswordPolicy` derivation for `NotPublished`; a reflection completeness test asserting the copy block in `DiscoverPasswordPolicyAsync` covers every settable property of `ConnectedSystemPasswordPolicy` (the pattern of `SyncRuleInitialPasswordComparisonCompletenessTests`), so the next field cannot be forgotten. `MigrationDesignerChainTests` and `ReleasedMigrationImmutabilityTests` pass.

### Phase 2: 389 Directory Server detection and root DSE facts (after Phase 1)

Owns: `src/JIM.Connectors/LDAP/LdapConnectorEnums.cs`, `LdapConnectorRootDse.cs`, `LdapConnectorUtilities.cs` (`DetectDirectoryType`, `GetBasicRootDseInformation`, the fallback), `LdapConnectorImport.cs` (`GetRootDseInformation`), `LdapConnectorExport.cs`, `LdapConnector.cs` (the two describe methods and `GetDetectedCapabilities`), and the existing `DetectDirectoryType` and root DSE tests in `test/JIM.Worker.Tests/Connectors/`.

Deliverables:
- `LdapDirectoryType.DirectoryServer389` appended after `Generic`, with a summary saying why the position matters.
- `DetectDirectoryType(supportedCapabilities, vendorName, structuralObjectClass, vendorVersion)`; both root DSE reads request `vendorVersion`, `namingContexts`, `configContext`, `supportedControl` and `defaultNamingContext`; `LdapConnectorRootDse` gains `VendorVersion`, `ConfigContext`, `SupportedControls`, `DefaultNamingContext`.
- Every switch gains the arm with Generic's behaviour; `SupportsPaging` true; capability label "389 Directory Server".

Tests: detection by `vendorName` "389 Project", by `vendorVersion` prefix alone, and that an unknown vendor stays `Generic`; the computed properties for the new type equal Generic's; old persisted JSON (no new properties) still deserialises; the enum ordinal of `Generic` is still 3 (a guard against reordering).

### Phase 3: Per-directory policy readers and the preflight (after Phase 2; same agent as Phase 2)

Owns: `src/JIM.Connectors/LDAP/LdapConnectorPasswordPolicy.cs`, new `LdapConnectorPasswordPolicyActiveDirectory.cs`, `LdapConnectorPasswordPolicyOpenLdap.cs`, `LdapConnectorPasswordPolicy389.cs`, `LdapPasswordPolicyScope.cs`, `LdapConnectorPreflight.cs`, `LdapConnector.cs` (`GetPasswordPolicyAsync`, `RunPasswordPreflightAsync`, removal of `GetDefaultNamingContext`), and `test/JIM.Worker.Tests/Connectors/LdapConnectorPasswordPolicyTests.cs`, new `LdapConnectorPasswordPolicyOpenLdapTests.cs`, `LdapConnectorPasswordPolicy389Tests.cs`, `LdapConnectorPreflightTests.cs`.

Deliverables: the dispatch, scope record, two readers and mappings described under Proposed design; the preflight's `CheckPolicyDiscoveryAsync` drops its `!IsActiveDirectory` early return, keeps `CouldNotDetermine` with "publishes no policy" for Generic, and words the other outcomes per type ("the directory's password policy", not "the domain password policy"); every reader degrades to an outcome rather than throwing, catching `DirectoryOperationException` and `LdapException` as the AD reader does; each search that can be large carries a size limit.

Tests (Moq executor, routing by request DN or filter as `SetupDirectory` does): OpenLDAP full mapping (PRD Scenario 1 values: 12, 5, 7776000 seconds is 90 days, min age 0 is null), zero and absent values are null, control not advertised is `NotPublished` and `Absent`, config refused with one policy is `Read`, config refused with two policies is `ConfigurationNotReadable` and `Present`, `pwdPolicySubentry` present is `Present`, empty probe is `CouldNotDetermine`, check module named sets `FurtherChecksApply`; 389 full mapping (PRD Scenario 3: 10, 3 of 5), each gating switch off nulls its figure, `cn=config` empty is `ConfigurationNotReadable` and `CouldNotDetermine` (PRD Scenario 4), container or subentry found is `Present`, dictionary check sets `FurtherChecksApply`; Generic is `NotPublished`; AD tests unchanged in substance; preflight tests per type, and the existing OpenLDAP "could not determine" preflight test moved to Generic.

### Phase 4: Reconciliation, generator, dialog and panel (after Phase 1; parallel with 2 to 3)

Owns: `src/JIM.Models/Staging/PasswordPolicyReconciliation.cs`, `src/JIM.Application/Services/PasswordGeneratorService.cs`, the reconciliation DTO in `src/JIM.Web/Models/Api/` (the response built in `SynchronisationController.cs`), `src/JIM.Web/Shared/SetPasswordDialog.razor`, `src/JIM.Web/Pages/Admin/Components/ConnectedSystemPasswordPolicyPanel.razor`, `test/JIM.Worker.Tests/Services/PasswordPolicyReconciliationTests.cs`, `test/JIM.Worker.Tests/Services/PasswordGeneratorServiceTests.cs`, `test/JIM.Web.Tests/SetPasswordDialogTests.cs`.

Deliverables: `SystemsApplyingFurtherChecks` on the reconciliation and its DTO; the dialog line "X applies further checks JIM cannot see"; the panel outcome switch, neutral override wording and further-checks line; `DescribeConstraints` unchanged.

Tests: requirement 8, `Reconcile_WhenOneSystemDoesNotPublishACategoryCount_UsesTheKnownCount` (one system with `requiredClasses: 3`, one with a null count and no classes; expect "3 of 5" and no narrowing); further-checks names surface; dialog renders the further-checks line and, with `ConnectorCanDiscoverPasswordPolicy` false, the "publishes no rules" line; panel wording per outcome (bUnit, following `SetPasswordDialogTests`).

### Phase 5: Documentation (after Phase 1; parallel)

Owns: `docs/concepts/passwords.md` ("Discovering the target's rules": per-directory table of what is published, the override mechanism per directory, further checks), `docs/connectors/jim-ldap-connector.md` (directory type table gains 389; a "Password policy discovery" subsection per type; Service Account Permissions gains `cn=config` read for 389 and overlay configuration read plus the policy entry for OpenLDAP, both optional with what each buys), `docs/configuration/connected-systems.md` (the "Only Active Directory and Samba AD publish" paragraph and the panel outcomes), `engineering/DEVELOPER_GUIDE.md` 3b if it names Active Directory as the only publisher. No code, no changelog (Phase 1 wrote it).

### Phase 6: Integration scenario 22 (authoring after Phase 1 in parallel; verification after 1 to 4)

Owns: `test/integration/docker/openldap/scripts/01-add-second-suffix.sh`, new `test/integration/Populate-OpenLDAP-Scenario22.ps1`, `Setup-Scenario22.ps1`, `scenarios/Invoke-Scenario22-OpenLdapPasswordPolicy.ps1`, `Run-IntegrationTests.ps1`, `test/integration/README.md`, `engineering/INTEGRATION_TESTING.md`.

Step 0, a spike before anything else: against the running `openldap-primary`, as a non-root user, run an `ldappasswd` (RFC 3062) change over `ldap://`. If the container refuses a cleartext extended operation, switch the OpenLDAP service to `LDAP_ENABLE_TLS=yes` with a generated certificate trusted via `Add-JIMCertificate` (Scenario 15's pattern) before continuing. `LdapConnector.OpenPasswordConnection` only warns on an unencrypted channel, and initial password delivery does not refuse, so the scenario can run without TLS if the container permits it.

Deliverables:
- Base image: `01-add-second-suffix.sh` loads the `ppolicy` module and adds an `olcOverlay=ppolicy` entry (with no `olcPPolicyDefault`) to both databases, in the same style as its accesslog overlay block. No default policy in the image, so no other scenario's behaviour changes. State in the PR that this invalidates existing OpenLDAP snapshots (base build hash). Bitnami's `LDAP_CONFIGURE_PPOLICY=yes` is the alternative if the frozen `bitnamilegacy` image supports it; the init script route is explicit and hashed, so preferred.
- `Populate-OpenLDAP-Scenario22.ps1` (self-populating like 14 and 19, excluded from snapshots): as config admin, set `olcPPolicyDefault: cn=default,ou=Policies,dc=yellowstone,dc=local` on the Yellowstone overlay and prepend an `olcAccess` granting `cn=jim-provisioner,dc=yellowstone,dc=local` write on the database; as data admin, create `ou=Policies`, the policy entry (`pwdPolicy` with a structural class, `pwdAttribute: userPassword`, `pwdMinLength: 12`, `pwdInHistory: 5`, `pwdMaxAge: 7776000`, `pwdCheckQuality: 2`), the provisioner (`simpleSecurityObject` plus `organizationalRole`) and a probe user with a known password. The rootdn is exempt from ppolicy (slapo-ppolicy(5)), so JIM must bind as the non-root provisioner or "none parked" proves nothing.
- `Setup-Scenario22.ps1`: OpenLDAP only (throw on Samba, as 17 throws on OpenLDAP); clone `Get-DirectoryConfig -DirectoryType OpenLDAP` with the provisioner's `BindDN` and `BindPassword`; compose `Setup-Scenario1.ps1` with it; `Set-JIMSyncRuleInitialPassword -Enable -Source Discovered -ExpiryBehaviour ExpiresAccordingToTargetPolicy`.
- `Invoke-Scenario22-OpenLdapPasswordPolicy.ps1` steps: (1) populate; (2) negative control: `ldappasswd` as the probe user with a 5-character password is refused with a constraint violation, else fail loudly ("enforcement not proven"); (3) setup, then `Get-JIMConnectedSystemPasswordPolicy` shows `minimumLength 12`, `passwordHistoryLength 5`, `maximumPasswordAgeDays 90`, `minimumPasswordAgeDays` null, `complexityRequired` null, `requiredCharacterClassCount` null, `discoveryOutcome Read`, `policyOverrideSignal CouldNotDetermine` (per decision 2), `furtherChecksApply false`; (4) run Scenario 1's import, synchronisation and export profiles at Micro; (5) every provisioned entry carries `pwdChangedTime` (the overlay processed the set) and `Get-JIMSyncRuleInitialPassword` reports `parkedAccountCount 0`; with step 2 and the non-root bind, a password under 12 characters would have been parked, so this is the evidence for PRD Scenario 2; (6) add `pwdPolicySubentry` to one entry, refresh the schema, expect `policyOverrideSignal Present`; (7) the preflight (`Check Password Channel` via the API) reports policy discovery passed with "12" in its details.
- Runner: description switch, `$templateIrrelevantScenarios`, the OpenLDAP-only coercion and skip blocks, and the snapshot and populate exclusions, each extended for `*Scenario22*`; grep for every `*Scenario2*` pattern, because it also matches 22. README scenario table and `engineering/INTEGRATION_TESTING.md` section in Scenario 17's shape (the chain table, why non-root, why `pwdCheckQuality` 2, why no TLS is acceptable here).

Verification: `Run-IntegrationTests.ps1 -Scenario Scenario22 -DirectoryType OpenLDAP` green; an OpenLDAP sweep of Scenarios 1, 8, 14 and 19 still green (the overlay is loaded everywhere now).

## Success Criteria

- Scenario 22 green: discovered values as in PRD Scenario 1, no Connected System Object parked, the negative control proves enforcement, the override step flips the signal.
- Unit tests cover PRD Scenarios 3, 4 and 5 against the mocked executor, and requirement 8.
- No product surface names Active Directory except where the directory in front of it is Active Directory: grep of `src/` for "Fine-Grained", "domain password policy" and "Only Active Directory" returns only AD-specific branches.
- `fineGrainedPolicySignal` appears nowhere under `src/`, `docs/` or `test/` except the changelog entry; `policyOverrideSignal` is on the API, the cmdlet help and the docs table.
- Existing AD rows keep their stored signal through the migration (`RenameColumn`, checked by reading the migration).
- Existing 389 deployments change only their capability label.

## Benefits

- Correct behaviour: generated passwords into OpenLDAP and 389 stop being parked on rules JIM can read.
- Honesty: the panel distinguishes "not published", "not readable by this account", "nothing configured" and "not read yet", each with an action or the absence of one.
- Architecture: per-directory readers behind one entry point, one scope record built from the root DSE, and a directory-neutral model, so a fourth directory is a reader and an enum arm.
- Coverage: the first integration scenario that drives the RFC 3062 password path against a real directory.

## Dependencies

- No new NuGet packages; `System.DirectoryServices.Protocols` searches only.
- Integration: the Bitnami OpenLDAP image must load the `ppolicy` module (the init script already writes overlays through `cn=config` as the config admin); OpenLDAP 2.5 and later carry the ppolicy schema in the overlay, so no schema LDIF is needed (verify on the frozen image). No 389 container exists; PRD Scenario 3 is unit-tested only until one is added (a follow-up issue).
- Phase 1 must land before Phases 2 to 6 can compile.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| The harness OpenLDAP refuses a cleartext RFC 3062 operation, or a hashed value reaches the overlay and bypasses the length check | Phase 6 step 0 spike; fall back to TLS with a trusted certificate; the negative control makes a vacuous "none parked" impossible |
| The rootdn exemption makes enforcement invisible | Scenario 22 binds JIM as a non-root provisioner; documented in the scenario write-up |
| The scaffolder emits drop-and-add for the rename and stored signals are lost | Migration reviewed for `RenameColumn`; the precedent file shows the shape |
| A reordered `LdapDirectoryType` corrupts persisted directory types | Append only; a test pins `Generic` at ordinal 3 |
| A subtree probe on a large directory is slow or refused by an administrative limit | Size limit 1 on every probe; administrative limit errors map to `CouldNotDetermine`; naming contexts capped at five |
| `pwdPolicySubentry` is an operational attribute and a filter on it may be ACL-hidden | Empty is `CouldNotDetermine` by rule, never `Absent` |
| Multiple ppolicy overlays with different defaults | The first user naming context's default is read; a differing second default counts as `Present` |
| 389 figures reported when their switch is off | The mapping honours `passwordCheckSyntax`, `passwordHistory` and `passwordExp`, with tests per switch |
| The `*Scenario2*` runner pattern swallows Scenario 22 | Explicit exclusions added wherever 14 and 19 are listed; grep for the pattern is a checklist item |
| Changing the base init script invalidates every OpenLDAP snapshot | Stated in the PR; one-off rebuild via `Build-OpenLDAPSnapshots.ps1` |
| Two agents edit `LdapConnector.cs` | Phases 2 and 3 are one agent in sequence; no other phase touches the file |
