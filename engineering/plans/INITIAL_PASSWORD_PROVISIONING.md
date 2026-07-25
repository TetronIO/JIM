# Initial Password Generation and Delivery on Provisioning

- **Status:** Doing (Phase 1 complete; Phase 2 next)
- **Issue:** [#1121](https://github.com/TetronIO/JIM/issues/1121)
- **Related:** [#1119](https://github.com/TetronIO/JIM/issues/1119) Password Synchronisation, [#1120](https://github.com/TetronIO/JIM/issues/1120) Defensive password filtering, [#618](https://github.com/TetronIO/JIM/issues/618) Email Notifications
- **UI mockups:** [Initial Password Provisioning: UI Mockups](https://claude.ai/code/artifact/77c228ff-4f9b-48d0-a9b6-1b7b25c833bc) (all seven screens, built against `engineering/DESIGN.md` tokens)

## Overview

JIM can provision accounts but cannot set a password on them, so every newly provisioned account needs an out-of-band manual step before it is usable. This plan delivers: a connector capability for setting passwords, discovery of the target's password policy, a first-class password generator, delivery of a generated initial password during provisioning, and the administrator feedback loop for when a target rejects the generated value.

The connector set-password capability built in Phase 1 is the shared foundation that Password Synchronisation (#1119) later consumes. It is deliberately built once, here, rather than twice.

## Business Value

- Closes a real gap in the provisioning capability set: provisioned accounts are usable on creation.
- Removes the most common reason an administrator has to touch a newly provisioned account by hand.
- Makes defining a compliant default password a first-class configuration action rather than a hand-written expression, supporting the wider goal of making JIM approachable to administrators who are not experienced identity engineers.
- Policy discovery means the correct settings are pre-filled from the target itself, so the common case needs no configuration at all.

## Technical Architecture

### Current state

- No connector can set a password. `LdapConnector` handles only its own bind credential, decrypted via `ICredentialProtection.Unprotect` (`LdapConnector.cs`).
- `IConnectorCapabilities` has no password capability flag; `ConnectorDefinition` mirrors those flags to the database.
- Schema discovery runs through `ConnectedSystemServer` (`GetSchemaAsync` call sites around `ConnectedSystemServer.cs:1359` and `:1539`) and persists a `ConnectorSchema`. It reads no policy information.
- Provisioning produces a `PendingExport` with `ChangeType = Create`, executed by `ExportExecutionServer`, which handles create results and external-id capture (`ExportExecutionServer.cs:1611`, `:1924`).
- `SyncRule` carries lifecycle configuration at top level (`ProvisionToConnectedSystem`, `OutboundDeprovisionAction`, `InboundOutOfScopeAction`, `EnforceState`) with `AttributeFlowRules` as one collection among several.
- The only attribute guard in the LDAP connector is `ProtectedAttributeDefaults`, which concerns *clearing* attributes in Active Directory, not credentials.

### Proposed solution

A password channel that runs parallel to, and never through, the attribute flow and Pending Export machinery.

```
Schema Discovery ──> ConnectedSystemPasswordPolicy (persisted on Connected System)
                                  │
                                  │ pre-populates
                                  v
Synchronisation Rule ── Initial Password section (Discovered | Custom)
                                  │
Provisioning (Create export succeeds, external id known)
                                  │
                                  v
                    PasswordGeneratorService  ──generate at delivery──┐
                                                                      v
                                        IConnectorPasswordManagement.SetPasswordAsync
                                                                      │
                              success ──────────────────────────┬─────┴── policy rejection
                                  │                             │
                          Activity: success            Park + Activity + run stats
                                                                │
                                       generator config changed ┘ (release, retry)
```

### Key decisions

- **Generate at delivery time, never at queue time.** The unit of work records the intent to set an initial password, not a value. No generated secret is ever persisted, and a retry automatically picks up the current configuration, which is what makes "administrator fixes the settings" self-resolving with no invalidation machinery.
- **Order of operations in Active Directory matters.** An account cannot be enabled until it holds a policy-compliant password. The sequence is therefore: create the account disabled, set `unicodePwd` over LDAPS, then set `userAccountControl` to enabled and `pwdLastSet` to 0 for change-at-next-sign-in. Getting this order wrong produces a create that appears to succeed and an account that cannot be enabled.
- **`unicodePwd` requires a quoted UTF-16LE byte value over LDAPS.** A plain string write is silently ineffective, which is why the connector, not the administrator, owns the target attribute and encoding.
- **Policy discovery is a floor, not a guarantee.** Fine-Grained Password Policies and custom password filters are wholly or partly invisible, so rejection handling is a required part of the feature rather than an edge case.

## Implementation Phases

### Phase 1: Connector set-password foundation (shared with #1119) ✅

1. `IConnectorPasswordManagement` in `JIM.Models/Interfaces/`: open/close semantics matching the export capability interfaces, plus `SetPasswordAsync(ConnectedSystemObject target, string password, PasswordSetOptions options, CancellationToken)` returning a classified result.
2. `PasswordSetResult` and `PasswordSetFailureReason` in `JIM.Models/Staging/`, classifying transient / configuration-fault / policy-rejection outcomes. Classification drives everything downstream, so it belongs in the connector contract rather than being inferred from an error string.
3. `SupportsPasswordSet` on `IConnectorCapabilities`, mirrored on `ConnectorDefinition`; migration.
4. `ConnectorFactory` wiring, following the existing `SetCredentialProtection` / `SetCertificateProvider` pattern.
5. LDAP implementation: Active Directory mode (`unicodePwd`, quoted UTF-16LE, refuse unless LDAPS) and generic mode (`userPassword`), selected by a per-system setting; plus the expiry states below and the create-disabled/set/enable ordering.

**Password expiry is one tri-state choice, not two switches.** Active Directory treats "must change at next sign-in" (`pwdLastSet = 0`) and "never expires" (the `DONT_EXPIRE_PASSWORD` flag in `userAccountControl`) as mutually exclusive; the native tooling greys one out when the other is set. Modelling them as independent toggles would let an administrator save a contradiction the target cannot honour, so the configuration carries a single enum:

| State | Active Directory effect |
|-------|------------------------|
| Require a change at next sign-in (default) | `pwdLastSet = 0` |
| Expires according to the target's policy | `pwdLastSet` set normally, `DONT_EXPIRE_PASSWORD` clear |
| Never expires | `DONT_EXPIRE_PASSWORD` set |

**Not every target supports every state**, so the connector declares which it can honour and the UI offers only those, in the same spirit as the existing capability flags. Generic LDAP has no per-entry never-expires equivalent (expiry is governed by the applicable password policy), so that state is unavailable there and the UI says so rather than silently ignoring it. Where a multi-target operation includes a system that cannot honour the selected state, say which system and what it will do instead; never fail the whole operation over it.

**Describe each state; do not recommend one.** Whether passwords should expire is a policy decision for the deploying organisation, and national guidance has moved away from mandatory periodic expiry, so option text states what each state *does* and nothing more. Do not label a state as recommended, and do not characterise "never expires" as being for service accounts: that framing is out of date and JIM should not bake it into the UI. `engineering/COMPLIANCE_MAPPING.md` should record that the setting exists and where it can be set, because it is auditable configuration, without editorialising on its use.

The default of "require a change at next sign-in" stands on different ground from periodic expiry: an initial password is generated by JIM and transmitted by an administrator, so forcing a change means the user ends up holding a secret nobody else knew. That is the rationale for the default, and it belongs in this plan and the public documentation, not in the option label.
6. Credential-attribute denylist (`unicodePwd`, `userPassword`, `dBCSPwd`, `ntPwdHistory`, `lmPwdHistory`, `supplementalCredentials`, `unixUserPassword`, `msDS-ManagedPassword`): excluded from importable schema and from Attribute Flow target selection, with a configuration warning for credential-like names outside the list.
7. Mock connector support so the pipeline is testable without a directory.

**Tests:** capability declaration; LDAPS refusal; `unicodePwd` encoding (byte-level assertion); denylist exclusion from schema and Attribute Flow; result classification; each expiry state produces the correct `pwdLastSet` and `userAccountControl` bits, and an unsupported state is reported rather than silently dropped.

#### What Phase 1 changed against this plan

Three decisions in the built code differ from the plan above, all made during implementation:

- **Directories that are not Active Directory use the RFC 3062 Password Modify extended operation, not a `userPassword` write.** This item originally said "generic mode (`userPassword`)". A directory applies its configured password hashing to the extended operation but stores a directly written `userPassword` value verbatim, so the planned approach is how a cleartext password ends up sitting in a directory. A directory that does not advertise the extended operation is refused with a configuration fault explaining why, rather than written to unsafely.
- **LDAPS is strongly recommended for the password channel, and warned about when absent, rather than required.** An earlier revision refused to open the channel without it. That was reversed deliberately: some deployments cannot offer TLS on their directory at all, and locking those sites out of password management entirely helps nobody. JIM logs a prominent warning on every channel open instead, and the decision belongs to the administrator. Active Directory still enforces encryption itself, and that refusal is reported with encryption named as the likely fix rather than pre-empted, because a signed and sealed bind is a legitimate alternative JIM cannot detect from the settings alone.

  A per-run log warning is the floor, not the finished feedback loop. Phase 4 should surface this where the administrator actually makes the choice: a warning on the Synchronisation Rule when Initial Password is enabled against a Connected System that is not using LDAPS. `ConnectorSettingValueValidationResult` cannot express it today, since it carries only `IsValid` and an invalid result blocks saving; a non-blocking severity would need adding before the Connected System settings page could warn there too.
- **The password channel binds its own connection.** It cannot share the import and export connection: its security requirements differ, and initial password delivery happens partway through an export session.

Two things were found in passing and fixed here rather than left for a later phase:

- **Capability mirroring is now driven off `IConnectorCapabilities` itself.** It was hand-written in two places in `SeedingServer`, so declaring a capability and missing one of them left the flag permanently false in the database with nothing failing. `ConnectorCapabilityMirror` plus reflection-based tests make a new capability covered the moment it is declared. `ConfigurationSnapshotService` still lists capabilities by hand for change-history rendering; that is a display concern and was left alone.
- **A directory's own diagnostic is redacted before it reaches a result or a log.** A rejected value can be echoed back in the directory's error text, and JIM puts those messages into service logs, Activities, and the portal.

#### Integration-testing gaps this phase exposed

Neither blocks Phase 1, and both need resolving before the end-to-end assertions this plan's risk table calls for:

- **The test OpenLDAP container serves no TLS.** `test/integration/docker/openldap/` configures no certificate and exposes no LDAPS port, so with LDAPS now mandatory the RFC 3062 path cannot be integration-tested until TLS is added there. Samba AD is unaffected: it generates a self-signed certificate and enables TLS during provisioning.
- **The Samba AD image disables the password policy.** `post-provision.sh` sets `--complexity=off`, `--history-length=0`, `--min-pwd-age=0` and `--max-pwd-age=0` so the test credentials never expire. A password policy rejection therefore cannot be provoked naturally against it, which Phase 5's rejection handling needs. Either a Fine-Grained Password Policy applied to a test group, or a scenario that turns complexity on for the duration, will be required.

`ConnectorFactory` needed no change. `IConnectorPasswordManagement` has nothing to inject, and the password channel reads the same Connected System settings as import and export, so the existing `SetCredentialProtection` / `SetCertificateProvider` wiring already covers it.

### Phase 2: Password policy discovery

1. `ConnectedSystemPasswordPolicy` in `JIM.Models/Staging/`: connector-neutral (minimum length, complexity-required flag, resolved character-class requirements, history length, maximum age, plus `FineGrainedPolicySignal` as present/absent/could-not-determine and a discovery timestamp).
2. Optional connector capability for policy discovery, invoked from the existing schema import path in `ConnectedSystemServer`.
3. LDAP implementation: read `minPwdLength`, `pwdProperties`, `pwdHistoryLength`, `maxPwdAge`, `minPwdAge` from the domain root; map `pwdProperties` bit `0x1` to Active Directory's fixed 3-of-5 category rule; attempt a Password Settings Container read for the Fine-Grained signal and degrade to "could not determine" on access denied without failing discovery.
4. Persist against the Connected System; migration.
5. UI: display the discovered policy and the Fine-Grained warning on the Connected System.

**Tests:** attribute mapping; complexity-flag translation; access-denied degradation; policy persisted and re-read.

### Phase 3: Password generator

1. `PasswordGenerationPolicy` in `JIM.Models/` (style, plus the per-style options below, ambiguous-character exclusion).
2. `IPasswordGeneratorService` / `PasswordGeneratorService` in `JIM.Application/Services/`, using `RandomNumberGenerator` exclusively, **compliant by construction** (satisfy each requirement, then fill and shuffle) rather than generate-and-test.
3. Derivation of a default `PasswordGenerationPolicy` from a discovered `ConnectedSystemPasswordPolicy`.

**Three generation styles.** Initial passwords are transcribed by humans far more often than permanent ones (read out by a service desk, typed from an onboarding sheet, entered on a phone keyboard), so transcribability is a first-class concern rather than a nicety:

| Style | Example | Options |
|-------|---------|---------|
| Random characters | `t7Rm#qK4vHx2Ndbf` | Length, per-class minimums (upper, lower, digit, symbol), permitted symbol set |
| Words | `Brown-Chicken-Ladder-47` | Word count, separator, capitalisation, append digits, append symbol |
| Pronounceable | `tovanic-hupelo-92` | Length, append digits, append symbol |

**Separator and capitalisation are orthogonal axes, not a preset list.** Separator (none, hyphen, full stop, underscore, digit, random symbol) and capitalisation (lowercase, each word, uppercase, first word only, random word) combine to cover every convention with two controls: `None` + `each word` yields `BrownChickenLadder`, `hyphen` + `lowercase` yields `brown-chicken-ladder`. A single combined enum would need a dozen entries to express the same set.

**Composed-style policy validation is required, not optional.** `brown-chicken-ladder` is lowercase plus a symbol: two character categories, where Active Directory complexity requires three of five. The generator must compose the configured style, evaluate the categories and length it will actually produce, and validate that against the discovered policy, blocking Save (or auto-satisfying via appended digits) when it falls short. Without this, the most natural-looking passphrase configuration is silently rejected by the target on every account.

**Word list.** JIM already ships `src/JIM.Application/Resources/Words.en.txt` (6,771 English words, Diceware-scale at ~12.7 bits per word), currently used only for example data generation via `SeedingServer`. Reuse is attractive but the list needs vetting before it generates credentials handed to real people, and three specific issues are already visible: it opens with initialisms (`Atm`, `Cd`, `Suv`, `Tv`) that make poor passphrase words, it carries a UTF-8 BOM, and it has never been screened for words that would be inappropriate in a password given to a new employee. Decide at implementation time between a curated subset filtered for length and suitability, or a separate purpose-built list; either way the entropy readout must be computed from the list actually shipped, so it cannot drift if the list changes. Coupling credential generation to the example-data resource without that filtering step is not acceptable.

**Tests:** every generated value satisfies its policy across many iterations; composed passphrase styles meet category requirements; ambiguous characters absent when excluded; no `System.Random` anywhere on the path; unbiased selection (no modulo bias in the index draw); entropy calculation matches the shipped list size; word list contains no entry failing the suitability filter.

### Phase 4: Synchronisation Rule configuration and delivery

1. `SyncRule` gains initial-password configuration (enabled flag, Discovered-or-Custom source, and the policy); migration.
2. UI: Initial Password section on the Synchronisation Rule, gated off `ProvisionToConnectedSystem`, pre-populated from the Connected System's discovered policy, overridable.
3. Delivery: after a `Create` export succeeds and the external id is known (`ExportExecutionServer` create-result path), generate and set the password through the connector capability, then apply enable and change-at-next-sign-in.
4. Record an Activity per attempt with its outcome, carrying no password value.
5. Administrator set-password dialog: generate on demand, **masked by default**, with reveal and copy.

**Reveal and copy behaviour.** The value is masked on generation. **Copy must work while masked**, so transferring a password to the user never requires putting it on screen; reveal is the secondary affordance, for reading it aloud or checking a transcription, and re-conceals automatically after 30 seconds. A copy raises a confirmation snackbar. Neither action is separately audited: the administrator performing the reset already knows the value, so a reveal event would record nothing that the password-set Activity does not already cover.

Clipboard access is JS interop (`navigator.clipboard.writeText`) from a Blazor Server circuit, which brings two constraints that must be handled rather than assumed away:

- **Secure context required.** `navigator.clipboard` is unavailable over plain HTTP, so a non-TLS deployment must surface a clear failure instead of a silently dead button. Detect and disable with an explanatory tooltip rather than letting the click no-op.
- **Operating-system clipboard history cannot be suppressed from a browser.** JIM should clear the clipboard on dialog close on a best-effort basis (it can fail without transient user activation), but the password may persist in the platform's clipboard history regardless. Document this honestly rather than implying the copy is transient.

Credential *delivery* (emailing the password to the user or their manager) is out of scope here and belongs with notifications (#618); this dialog hands the value to the administrator to convey through whatever channel their policy allows.

**Tests:** delivery invoked only for Create and only when enabled; correct ordering (create → set → enable); no password value reaches any persisted field; disabled configuration is a no-op; the dialog starts masked, copy succeeds while masked, and reveal re-conceals on the timer.

### Phase 5: Rejection handling and administrator feedback loop

1. Parked state: a policy rejection parks the unit of work in a visible, non-auto-retrying state carrying the target's verbatim reason. Transient and configuration faults retain normal retry.
2. Release on configuration change: saving a changed generator configuration on a Synchronisation Rule clears its parked states and retries immediately rather than waiting out a backoff.
3. Run Profile execution summary reports a policy-rejection count via the existing stat-counter mechanism; the run outcome reflects it without being failed outright.
4. Synchronisation Rule shows parked count and rejection reason inline, at the point of repair.
5. Needs-attention indicators on the Synchronisation Rule and Connected System lists.
6. Time-to-live expiry of a parked item records an explicit expiry outcome; it is never silently removed.

**Tests:** rejection parks rather than retries; configuration change releases and retries; expiry records its own outcome; run statistics report the count; transient failures still retry.

### UI surfaces (delivered across Phases 2, 4 and 5)

Mockups for all seven screens are linked in the header. In build order:

| Screen | Phase | Route |
|--------|-------|-------|
| Discovered Password Policy panel, with the Fine-Grained warning | 2 | `/admin/connected-systems/{id}` (Schema) |
| Initial Password section inheriting the discovered policy | 4 | `/admin/sync-rules/{id}` (Details) |
| Custom generator settings, random-character style | 4 | `/admin/sync-rules/{id}` (Details) |
| Word-based style, with live entropy and policy check | 4 | `/admin/sync-rules/{id}` (Details) |
| Administrator set-password dialog with Generate | 4 | `/metaverse/objects/{id}` |
| Parked-rejection alert with Save-and-retry | 5 | `/admin/sync-rules/{id}` (Details) |
| Needs-attention columns on both list views | 5 | `/admin/sync-rules`, `/admin/connected-systems` |
| Run Profile execution summary rejection statistic | 5 | `/activities/{id}` |

### Testing the password channel before relying on it

There is no dry run. No LDAP control, extended operation, or Active Directory mechanism validates a password set without performing it; Windows' `NetValidatePasswordPolicy` comes closest but is an RPC call, not LDAP, and is unreachable from JIM's Linux containers. Anything that answers "will this password be accepted" has to really set one.

Most failures never reach that question, though, and those are checkable without writing anything. Two tiers are in scope:

**Tier 1: preflight (Phase 2).** No writes, no risk, and it covers the common misconfigurations:

- The bind succeeds (the existing connectivity test).
- Whether the connection is encrypted, what the directory is, and whether it advertises RFC 3062 where that is the mechanism JIM would use.
- **Whether the service account actually holds password-reset rights.** Active Directory exposes `allowedAttributesEffective` (OID 1.2.840.113556.1.4.914) on every object: a constructed, read-only list of the attributes the *calling* account may modify on that object. Reading it for a sample object and looking for `unicodePwd` answers the single most common failure with zero writes, and it is Microsoft's own documented way to ask the question. Directories that are not Active Directory expose no portable equivalent, so the check reports "could not determine" there rather than a false pass.
- Whether the domain password policy could be read (Phase 2 does this anyway).

**Tier 2: generate and evaluate locally (Phase 4).** Produce a password from the configured generator and check the character classes and length it actually yields against the discovered policy. This is what catches the passphrase trap, where `brown-chicken-ladder` offers two character categories against Active Directory's three-of-five.

**A live test set is deliberately out of scope, and not only because it is awkward.** Setting a real password somewhere is the only way to prove the whole chain, and every route to it is unsafe:

- *A canary object JIM creates and deletes* assumes create rights the service account may not have, and assumes JIM can pick a container: either a Distinguished Name calculated from the Synchronisation Rule's Attribute Flow, or one of the Connected System's selected containers. Each of those can be absent or wrong. Creating a user in a production directory also triggers whatever watches for new users (other synchronisation engines, mailbox provisioning, licence assignment, alerting), and a failed delete leaves litter behind.
- *An administrator-supplied Distinguished Name* is a password reset against an arbitrary directory object, exposed through JIM's portal. The service account holds broad reset rights across the directory precisely because that is what makes password management work, so this would re-expose that directory privilege through JIM's own, coarser, permission model: anyone able to configure a Connected System could reset any account the service account can reach, including Domain Admins, break-glass accounts, and the service account itself (which would also brick the Connected System). Denylisting privileged accounts does not close it, because "privileged" is not reliably enumerable across custom admin groups, delegated rights, and tiered administration models.

If a live test is ever revisited, the one route that carries its own authorisation is offering to set a password on **the signed-in administrator's own account**, since they are self-evidently entitled to change it. That has its own gap: an administrator using a dedicated admin account outside JIM's lifecycle management may have no Connected System Object to target.

### Phase 6: REST API and PowerShell parity

Endpoints and cmdlets for generator configuration, the on-demand generate affordance, discovered-policy read, and parked-item read plus manual release. ID-based routes for writes per the API identifier rules; Pester tests for the cmdlets.

### Phase 7: Documentation and changelog

New public documentation page for initial passwords (configuration, policy discovery, what happens on rejection, the security model), LDAP connector reference updates, REST and PowerShell reference updates, `engineering/DEVELOPER_GUIDE.md` for the new component, and a changelog entry.

## Success Criteria

Tracked by the acceptance criteria on #1121. In summary: a provisioned account has a working, policy-compliant password without administrator intervention; discovery pre-fills the configuration; a rejection is impossible to miss and self-resolves on correction; and no password value is ever persisted or logged.

## Risks and Mitigations

| Risk | Mitigation |
|------|-----------|
| `unicodePwd` encoding is easy to get subtly wrong and fails silently | Byte-level unit test on the encoder plus an integration test that authenticates as the provisioned account, proving the password actually works rather than that the write returned success |
| LDAPS unavailable in the development and test environment | Confirm the containerised directory can serve TLS early in Phase 1; the LDAPS refusal path is itself unit-testable without a directory, but the end-to-end test needs it |
| Enable-before-password ordering fails against real Active Directory | Explicit create-disabled/set/enable sequencing in Phase 1, with an integration test asserting the account ends up enabled |
| A generated password leaks into a log, DTO, or persisted field | Never-persist unit tests, generate-at-delivery so there is nothing at rest to leak, and a security review before merge |
| Parked items become invisible with no notification system | Phase 5's six pull-based surfaces; the notification category is proposed on #618 for when it exists |
| Discovery gives false confidence where custom filters exist | Discovery is documented as a floor; the rejection path is a required feature, not a fallback |
| Scope creep into Fine-Grained Policy enumeration | Explicitly out of scope on #1121; the design is additive so it can be added later without rework |
| A passphrase style that cannot meet the target's complexity rule ships looking configured | Compose the style and validate its actual character categories and length against the discovered policy before Save; unit-test the composed-category calculation per separator and capitalisation combination |
| The example-data word list generates an inappropriate password for a real new starter | Filter and vet the list before use, or ship a purpose-built one; assert the suitability filter in tests |

## Dependencies

- None blocking. Phase 1 is the shared foundation #1119 later builds on; #1119 should not start its connector work independently.
