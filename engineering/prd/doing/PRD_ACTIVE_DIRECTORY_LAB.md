# Active Directory Lab

- **Status:** Doing (probe tier scaffolded; lab, harness, scenarios and gate to follow)
- **Created:** 2026-09-27
- **Author:** Jay
- **Issue:** [#1853](https://github.com/TetronIO/JIM/issues/1853) (sub-issue of [#518](https://github.com/TetronIO/JIM/issues/518), the release gate)

## Problem Statement

JIM has never been run against a real Windows Server domain controller. Every Active Directory-family integration scenario runs against Samba AD, and the LDAP Connector treats Samba as a different directory type from Active Directory: it detects Samba by vendor name (`LdapConnectorUtilities.DetectDirectoryType`) and takes a different branch, most visibly in `LdapConnectorRootDse.SupportsPaging` (the paged-results control is never sent to Samba and always sent to Active Directory) and in the export concurrency default (4 for Samba, 16 for Active Directory). The `ActiveDirectory` branch has therefore never executed end to end.

The Samba lab also relaxes two things a Windows Server 2025 domain controller enforces by default: it sets `ldap server require strong auth = no`, where 2025 requires LDAP signing (a simple bind over plain LDAP is refused; LDAPS is accepted), and it switches password complexity off, where 2025 ships with it on.

A source survey on 2026-09-27 found gaps that will surface on the first connection, before any scenario's first import:

- Schema discovery fetches every `attributeSchema` entry in one unpaged search (`LdapConnectorSchema.FetchAllAttributeSchemaEntries`). A stock forest has well over 1,000 non-defunct attributes, and Active Directory's MaxPageSize (1,000) makes an unpaged search return sizeLimitExceeded, which the .NET client raises as an exception. The container enumeration in `LdapConnectorPartitions` has the same shape.
- There is no ranged retrieval (`member;range=`) anywhere in the connector. Active Directory returns `member;range=0-1499` for a group over MaxValRange (1,500) and omits plain `member`, so such a group imports with no membership. Samba returns every value.
- The AD Recycle Bin is not modelled (no `isRecycled` handling), so a deletion is seen twice when the Recycle Bin is on.
- AD LDS is routed down the AD DS branch (domain-root password policy, `userAccountControl` disables), while the docs claim full support.
- The NTLM authentication type has never been exercised; Linux .NET cannot sign or seal LDAP sessions, and 2025 enforces signing on SASL binds.
- Probable, to confirm on a domain controller: adding an already-present group member returns result 68 (entryAlreadyExists) where only result 20 is treated as "already present"; the Object Type matcher assumes `objectClass` values are listed most-specific first, where Active Directory lists `top` first; the domain controller discovery filter also matches read-only domain controllers.

Every password feature (policy discovery, Initial Password, park then release, Password Synchronisation and its retries) and every synchronisation feature (Delta Import with tombstones, renames and moves, disables, deletion rules, matching, entitlements, scoping, safeguards) is likewise proven on Samba only. Releasing V1.0.0 on that evidence risks a blocking issue that a real domain controller would have shown in an afternoon.

## Goals

- Every Active Directory-family integration scenario (001, 002, 004 to 010, 017, 020, 021, 023) passes against real Windows Server 2025 domain controllers, confirmed by a green run of the lab workflow on the release commit.
- The gaps above are each either fixed with a test that fails on a real domain controller before the fix, or documented as a stated limitation before V1.0.0.
- A `RequiresActiveDirectory` test tier exercises the connector primitives that only real Active Directory can prove (paging at MaxPageSize, ranged retrieval, signing rules for `unicodePwd`, error mapping, `objectClass` ordering), and runs on the lab in minutes.
- Active Directory password policy discovery is proven against a domain with complexity on and a Fine-Grained Password Policy present, by a new Scenario 024 that mirrors Scenario 022.
- A release cannot be tagged unless the commit carries a green `jim-ad-lab` commit status (the hard gate agreed for 1.0; closes the Active Directory leg of #518).
- The lab is code: a domain controller is built, populated, checkpointed, reverted and rebuilt by scripts in the repository, and nobody has to remember how a VM was configured.

## Non-Goals

- Running the lab on pull requests. Pull request jobs run fork code on GitHub-hosted runners and must never reach the lab.
- Cloud-hosted domain controllers. The lab is on-premises on the hypervisor host; a cloud fallback can reuse the same build script later if availability ever demands it.
- Multi-domain-controller forests, read-only domain controllers, AD LDS and older operating system variants in the first delivery. The lab is designed so they are additions, not redesigns.
- Kerberos or SASL authentication in the connector. The lab tests what the connector offers today (simple bind and NTLM) and decides what to do about NTLM.
- A Windows runner. Existing self-hosted jobs are Linux and the JIM stack is Linux containers.
- Changing scenario script logic. Scenarios keep their steps and assertions; only the directory abstraction underneath them gains an Active Directory configuration.
- Replacing the Samba, OpenLDAP and 389 Directory Server legs. They stay as they are for local development and the `-PreRelease` sweep.

## User Stories

1. As the maintainer cutting V1.0.0, I want the release to be refused unless the commit has passed against real domain controllers, so that a customer's Active Directory is never the first one JIM has met.
2. As a developer changing the LDAP Connector, I want to dispatch the lab run on my branch and read one commit status, so that an Active Directory regression is found before merge rather than after release.
3. As a developer without the lab in reach (a cloud session), I want the probe tier and the scenarios to self-skip cleanly, so that the ordinary `dotnet test` and local integration runs are unaffected.
4. As the person who maintains the lab, I want domain controllers rebuilt from a script on a schedule after Patch Tuesday, so that the lab stays patched and the build script never rots.
5. As an administrator reading the LDAP Connector documentation, I want "Active Directory" support to mean verified against a named Windows Server version by JIM's own lab, as the 389 Directory Server entry already says.

## Requirements

### Functional Requirements

#### Lab

1. The hypervisor host runs Windows Server Datacenter with the Hyper-V role, activated, so that guests activate through Automatic Virtual Machine Activation with the generic per-version key and no per-VM licence management. No domain controller runs an evaluation edition: an evaluation domain controller cannot be converted, and its timer counts real time regardless of checkpoint reverts. The free evaluation media may be used to build them, because each guest is converted to full Datacenter with the AVMA key before promotion (the build's `License` phase), which is the point at which Microsoft supports that conversion.
2. Three single-domain-controller forests mirror the existing container roles and names: `dc-primary` (PANOPLY.LOCAL), `dc-source` (RESURGAM.LOCAL) and `dc-target` (GENTIAN.LOCAL), at the Windows Server 2016 forest functional level on Windows Server 2025 domain controllers. `dc-primary` has the AD Recycle Bin enabled.
3. A dedicated Linux runner VM on the same host, registered in its own `jim-ad-lab` runner group (repository access limited to JIM, workflow allowlist limited to the two lab workflows) with the label `jim-ad-lab`, runs the JIM stack in Docker exactly as the existing self-hosted runners do. It is the only machine with a route to the domain controller network, an internal virtual switch on which the host serves NTP and nothing routes out. A runner carrying the default labels in `tetron-trusted` would be offered ordinary CI jobs, which is why the group is separate.
4. Every domain controller is built by a PowerShell script from installation media: unattended install with the AVMA key, promotion, an LDAPS certificate whose Subject Alternative Names match the rules `post-provision.sh` enforces, the OUs, `svc-jim` in the JIM Connectors group with the existing SDDL delegation file applied unchanged, ownership of the Deleted Objects container as `jim-delegate.sh --tombstones` does, "password never expires" on `svc-jim` and Administrator only, Windows Update automatic installation switched off, and w32time pointed at the same NTP source as the host and the runner. The domain password policy stays at Windows defaults (complexity on) except where a scenario sets its own.
4a. Every forest carries the full Exchange organisation, as the enterprise directories JIM targets do: Exchange Setup's own `/PrepareSchema`, `/PrepareAD` (an organisation named after the forest) and `/PrepareAllDomains` from the Exchange Server SE media, the only supported way to add Exchange's schema; no Exchange server is installed (#2036). Products that extend a forest are listed in order in the build settings (`directoryExtensions`), so another, such as Skype for Business, can be added later as one more entry. Exchange mailbox provisioning, which would need a running Exchange server in the lab, is out of scope here.
5. Each state worth returning to is a Hyper-V production checkpoint: `baseline` after the build, and `populated-<template>-<hash>` after population, where the hash covers the populate scripts exactly as the Samba snapshot label does. The runner rejects a checkpoint whose hash does not match and rebuilds it.
6. Population reuses the LDIF that `Populate-SambaAD.ps1` already generates, delivered with `ldapadd` over LDAPS instead of `ldbadd`, binding as the domain administrator.
7. The runner reverts and starts domain controllers through a constrained control plane on the host (OpenSSH, a dedicated account in Hyper-V Administrators, PowerShell remoting), never through hypervisor credentials on the runner.
8. A scheduled rebuild job runs after Patch Tuesday: install from the latest media plus the cumulative update, promote, baseline, populate, checkpoint; run the full suite once against the new set before the nightly adopts it; keep the previous set for a week as rollback; record the domain controller OS build in run metadata. Patching happens by rebuild only.

#### Harness

9. `Get-DirectoryConfig -DirectoryType ActiveDirectory` returns a configuration with no container: host names, addresses, bind identities and base DNs come from `JIM_AD_LAB_*` environment variables, and the `Test-IsRfcDirectory` answer is the same as for Samba AD.
10. The LDAP helpers that today `docker exec` into the directory container (`Invoke-LDAPSearch`, `Test-LDAPBind`, the password-modify helpers) run the same commands from a small `ldap-utils` toolbox container on `jim-network` when the configuration has no container.
11. The JIM containers resolve every domain controller's FQDN through `extra_hosts` entries the runner injects, so that domain controller pinning by name works and LDAPS name validation holds. The domain controller's certificate is fetched over the wire (`openssl s_client`) and uploaded to JIM's certificate store the way `Add-SambaCertificateToJimStore` does.
12. The reset step reverts every domain controller the scenario uses to its populated checkpoint and starts it; the readiness check waits for an LDAPS bind as `svc-jim` and asserts the root DSE `currentTime` is within 5 seconds of the runner's clock. SYSVOL state is not part of readiness.
13. `-DirectoryType All` and `-PreRelease` do not include `ActiveDirectory`; the lab has its own entry point (`-DirectoryType ActiveDirectory`, and the lab workflow) because it needs the host.
14. Scenario scripts and setup scripts do not change beyond what the directory abstraction needs. Scenario 017 and Scenario 023's Collision step, which are Samba AD only today, run on Active Directory too.

#### Probe tier

15. A `RequiresActiveDirectory` NUnit category, gated on `JIM_TEST_AD_HOST` and modelled on `RequiresLdaps`, connects the connector's own code paths to a domain controller directly: schema discovery on the full 2025 schema; paged import with cookies across containers and object types; ranged retrieval of a group over 1,500 members; a Deleted Objects search over 1,000 tombstones; `unicodePwd` over LDAPS under the 2025 signing defaults, and the refusal over plain LDAP surfaced with an actionable message; the NTLM authentication type; duplicate member add and non-member remove; `objectClass` ordering with two selected types in one hierarchy; the Reset Password preflight on an AdminSDHolder-protected user.
16. Each probe is written before its fix and fails against the domain controller for the right reason first.

#### Scenarios

17. Scenario 024, Active Directory Password Policy: a domain with complexity on, minimum length 12 and history 24, plus a Fine-Grained Password Policy applied to a group; JIM reads the domain policy and reports the FGPP as present; a negative control (a 5-character change) is refused; provisioning with generated Initial Passwords parks none; a static Initial Password that fails complexity parks with the directory's words and is released when the rule is corrected.
18. Restore from backup: because every reset is a checkpoint revert, a scenario proves that a Delta Import after a revert fails fast naming the invocationId change, and that a Full Import re-establishes the baseline.
19. Delta Import runs with the Recycle Bin on (`dc-primary`) and off (`dc-source`), and a deletion is reported once in both cases.

#### CI and release gate

20. `.github/workflows/ad-lab.yml` runs on `schedule` (nightly on `main`) and `workflow_dispatch` (`main`, or a branch whose ref the `jim-ad-lab` runner group has been told to allow, the same mechanism as a release tag), only on `runs-on: [self-hosted, jim-ad-lab]`, with a concurrency group that serialises runs, and never on `pull_request`. It posts a commit status named `jim-ad-lab` on the tested SHA with a link to the run.
21. The `/release` skill and `engineering/RELEASE_PROCESS.md` refuse to tag a commit without a `success` `jim-ad-lab` status. There is no override flag; the remedy for a red night is a fix and a dispatched re-run. The gate is switched on by the repository variable `JIM_AD_LAB_GATE_ENFORCED` (`true`) after the lab's first green run; until then releases carry a warning instead, because no commit can carry the status before the lab exists (decided 2026-09-30 to unblock v0.16.0).
22. The run uploads the regression report and the domain controller OS build as artefacts, and submits results to the metrics API with `DirectoryType` `ActiveDirectory`.

### Non-Functional Requirements

- **Trust boundary:** the lab is reachable only from the runner VM; lab credentials are runner secrets; the workflow's `runs-on` expression and the runner group's workflow allowlist keep fork code off it, as `ci.yml` already documents.
- **Duration:** a nightly run of every Active Directory-family scenario at the Medium template, with a 1 to 2 minute revert per scenario per domain controller, completes within four hours so it never overlaps the next night.
- **Determinism:** a failure after a rebuild must be attributable to the Windows update or to JIM; the OS build is recorded with every run.
- **Reversibility:** reverts are unbounded (safe restore renews the invocationId and consumes a 500-RID pool per revert); the age of a checkpoint set is bounded by the monthly rebuild and never approaches the 180-day tombstone lifetime.

## Examples and Scenarios

### Scenario 1: The nightly run

**Given**: `main` moved during the day and the lab's checkpoint set is current
**When**: the schedule fires at 02:00
**Then**: the runner reverts `dc-primary`, `dc-source` and `dc-target`, runs every Active Directory-family scenario at Medium, and posts `jim-ad-lab: success` on the SHA with a link to the run

### Scenario 2: A release is refused

**Given**: last night's run was red because Scenario 008 imported a 1,600-member group as empty
**When**: `/release 1.0.0` runs on that SHA
**Then**: the skill stops before tagging, names the failing status and the run, and says to fix and dispatch a re-run

### Scenario 3: A revert is a restore from backup

**Given**: JIM has a Delta Import watermark from `dc-primary`
**When**: the domain controller is reverted to its populated checkpoint and started
**Then**: the next Delta Import fails fast naming the previous and current invocationId, and a Full Import re-establishes the baseline

### Scenario 4: Schema discovery on a real forest

**Given**: `dc-primary` with the stock Windows Server 2025 schema
**When**: the `RequiresActiveDirectory` schema probe runs before the fix
**Then**: it fails with sizeLimitExceeded from the unpaged `attributeSchema` search; after the fix it discovers every non-defunct attribute in one paged pass

### Scenario 5: Rebuild after Patch Tuesday

**Given**: the second Tuesday of the month has passed
**When**: the rebuild job runs
**Then**: each domain controller is rebuilt from media plus the cumulative update, baseline and populated checkpoints are retaken, the full suite passes once against the new set, and the old set is deleted a week later

## Constraints

- All automation is PowerShell; the domain controller build script runs on Windows, the harness on Linux.
- British English throughout; Active Directory is a supported directory and may be named, competing identity products may not.
- No new NuGet packages are expected; the probe tier uses the connector's own client as `RequiresLdaps` does.
- Nothing in the lab may trust or be trusted by any real domain, and the domain controller VLAN has no inbound route except from the runner VM.

## Affected Areas

| Area | Impact |
|------|--------|
| `test/integration/ad-lab/` (new) | Domain controller build, unattend file, checkpoint, populate, revert and rebuild scripts; the host control plane |
| `test/integration/utils/Test-Helpers.ps1`, `LDAP-Helpers.ps1` | `ActiveDirectory` directory configuration; container-less LDAP helpers via the toolbox container |
| `test/integration/Run-IntegrationTests.ps1`, `Wait-SystemsReady.ps1` | Reset by checkpoint revert; readiness with the time check; `extra_hosts`; certificate trust |
| `test/integration/docker/docker-compose.integration-tests.yml` | `ldap-toolbox` service |
| `test/JIM.Worker.Tests/Connectors/` | `RequiresActiveDirectory` probe fixtures |
| `src/JIM.Connectors/LDAP/` | Fixes the probes force: paged schema and container searches, ranged retrieval, Recycle Bin, error mapping, NTLM decision (each on its own stacked layer) |
| `test/integration/scenarios/`, `Setup-Scenario-024.ps1` | Scenario 024; the restore-from-backup and Recycle Bin steps |
| `.github/workflows/ad-lab.yml`, `ad-lab-rebuild.yml` (new) | Nightly run, dispatch, commit status; monthly rebuild |
| `.claude/skills/release/SKILL.md` | Gate on the `jim-ad-lab` status |

## Documentation Impact

| Doc | Change |
|------|--------|
| `engineering/TESTING_STRATEGY.md` | New tier: Active Directory lab probes; the lab in the integration tier |
| `engineering/INTEGRATION_TESTING.md` | `ActiveDirectory` directory type, the lab section (topology, lifecycle, patching), Scenario 024 |
| `engineering/RELEASE_PROCESS.md` | The `jim-ad-lab` gate |
| `engineering/COMPLIANCE_MAPPING.md` | The #518 entry moves from Planned to Implemented for the Active Directory leg |
| `docs/connectors/jim-ldap-connector.md` | "Verified against Windows Server 2025 by JIM's integration lab" once true; any stated limitation the lab produces (NTLM, AD LDS) |
| `docs/developer/testing.md` | How to run the probes and the lab |

## Dependencies

- Host preparation (owner: Jay): Datacenter activation and the Hyper-V role; a virtual switch on the CI VLAN; OpenSSH Server with a dedicated Hyper-V Administrators account; NTP; Windows Server 2025 media (full, or the free evaluation ISO, which the build converts before promotion).
- The runner VM registered with the `jim-ad-lab` label (mirrors the existing self-hosted runners).
- #518 for the gate's compliance framing; this PRD delivers its Active Directory leg first.

## Open Questions

1. Production checkpoints (the VM boots, the clock is set from the host, safe restore runs as on a normal boot) are proposed over standard checkpoints (saved RAM state, faster, but Microsoft notes the NTDS service may need a restart to request a new RID pool). Confirm production.
2. Whether NTLM survives: retract it, or keep it gated to LDAPS. The probe decides; the documentation change follows.
3. Nightly schedule time, and whether the rebuild job runs on the host as a scheduled task or as a second workflow on the runner.

## Acceptance Criteria

- [ ] `dc-primary`, `dc-source` and `dc-target` exist, built by the script, with `baseline` and `populated-medium-<hash>` checkpoints
- [ ] `Run-IntegrationTests.ps1 -DirectoryType ActiveDirectory -Scenario All -Template Medium` is green from the runner VM
- [ ] The `RequiresActiveDirectory` tier runs on the lab and every probe that failed before its fix passes after it
- [ ] Scenario 024 passes; Scenario 017 and Scenario 023's Collision step pass on Active Directory
- [ ] The restore-from-backup and Recycle Bin behaviours are proven by scenario steps
- [ ] `ad-lab.yml` posts the commit status nightly and on dispatch; `/release` refuses a commit without it, proven by a dry run
- [ ] The rebuild job has completed at least once after a Patch Tuesday and the suite passed on the rebuilt set
- [ ] Every gap in the Problem Statement is fixed or documented as a limitation
- [ ] `docs/connectors/jim-ldap-connector.md` states what was verified and against which Windows Server version
- [ ] `engineering/COMPLIANCE_MAPPING.md` and `engineering/RELEASE_PROCESS.md` describe the gate
- [ ] `JIM_AD_LAB_GATE_ENFORCED` is set to `true` after the first green run, and the compliance and release docs no longer describe the gate as pending

## Additional Context

- Design review, 2026-09-27: topology, domain controller lifecycle, one scenario run, cadence and gate, coverage map, patching and checkpoint lifespan (private artefact: https://claude.ai/artifact/BZ1QhHfPsL5xuoN3eGpq42).
- Decisions taken in that review: AVMA over evaluation editions (D1), a Linux runner VM on the host over a Windows runner (D2), a hard gate for 1.0 (D3).
- Windows Server 2025 LDAP defaults: https://learn.microsoft.com/windows-server/identity/ad-ds/ldap-signing
- Virtualised domain controller safe restore: https://learn.microsoft.com/windows-server/identity/ad-ds/get-started/virtual-dc/virtualized-domain-controller-architecture
- Automatic Virtual Machine Activation: https://learn.microsoft.com/windows-server/get-started/automatic-vm-activation
- Related: `engineering/prd/doing/PRD_PASSWORD_SYNCHRONISATION.md` names "a real Active Directory lab" as a validation gap; `engineering/plans/done/UNIQUE_VALUE_GENERATION.md` asks for real Active Directory collision strings.
