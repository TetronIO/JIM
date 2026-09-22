# A 389 Directory Server Lab, and a Changelog Delta Import That Applies Deletions

- **Status:** Doing
- **Issue:** [#1479](https://github.com/TetronIO/JIM/issues/1479)
- **Related:** [#1725](https://github.com/TetronIO/JIM/issues/1725) (the change source abstraction and the changelog fixes this lab proves at runtime), [#1715](https://github.com/TetronIO/JIM/issues/1715) and [#1716](https://github.com/TetronIO/JIM/issues/1716) (the OpenLDAP and Samba AD delegated service account labs this one mirrors)
- **Created:** 2026-09-22
- **Note:** The PRD was waived by the product owner; #1479 and this document are the requirements source. The encrypted-connection rows for 389 Directory Server in the `RequiresLdaps` tier are a separate piece of work under #1479, so this plan stays `Doing` when its pull request merges and #1479 stays open until that sub-issue closes.

## Overview

JIM recognises 389 Directory Server and treats it as a generic RFC 4512 directory with a changelog-based Delta Import, but no automated test had ever connected JIM to one. This plan adds 389 Directory Server to the integration harness as a third directory type on the same footing as OpenLDAP and Samba AD: a fixture image configured the way the connector documentation tells customers to configure their own server, a delegated `svc-jim` service account with published access control instructions, the shared populate scripts, and the runner's directory type menu, parameters and scenario eligibility rules. It also fixes what reading the source showed the first run would find: a changelog delete reached the import processor with no identity and was silently dropped, and a rename re-fetched the old name.

## Business Value

- The LDAP Connector guide can claim 389 Directory Server support with evidence behind it, and the claim is regression-netted by the same scenarios that protect the other directory types.
- Deletions and renames on 389 Directory Server and other changelog directories are applied by Delta Import instead of being lost, and an administrator whose server is not recording deleted entries is told at Schema Discovery rather than discovering it in a reconciliation.
- Customers get a tested, copy-and-paste access control recipe for the service account, including the changelog and configuration reads a Delta Import needs.

## Resolved design decisions

| # | Decision | Resolution |
|---|---|---|
| D1 | How a changelog delete identifies its object | The Retro Changelog plug-in is configured with `nsslapd-log-deleted: on`, which records the deleted entry in the delete record's `changes` attribute. The changelog source requests `changes` for delete records only and parses `objectClass` and `entryUUID` from it, exactly as the OpenLDAP accesslog source parses `reqOld`; the parsing lives in one helper shared by both sources. A delete record without a usable `changes` is a warning naming the DN, never a silent skip. `nsslapd-attribute` and `nsUniqueId` were rejected: the entryUUID plug-in gives every changelog record its own `entryUUID`, and JIM does not hold `nsUniqueId`. |
| D2 | Telling the administrator up front | The changelog source's readiness check reads `nsslapd-log-deleted` from the Retro Changelog plug-in entry. `on` is available; readable and off is unavailable (Delta Import refuses naming cause and remedy, Schema Discovery warns with the same text, the convention #1725 set for every change source); unreadable is a warning naming the read on `cn=config` the documentation already asks for. |
| D3 | Renames | A `modrdn` record's new name is computed from `newRdn` and `newSuperior` (or the old parent) and that entry is fetched; the old name is not. |
| D4 | Fixture | `test/integration/docker/dirsrv/`: `FROM 389ds/dirsrv:3.1` (mutable tag, an accepted exception under DEPENDENCY_PINNING row 9; not a production image, so outside digest pinning and image scanning), configured at image build time by starting the server, applying `dsconf` and LDIF over LDAPI, and stopping it. Two suffixes mirroring the OpenLDAP lab (`dc=yellowstone,dc=local`, `dc=glitterband,dc=local`), the `jim-extensions` schema, Retro Changelog with `nsslapd-log-deleted: on`, a seven day maximum age and `userPassword` excluded, search limits raised on the service accounts rather than globally, `entryUUID`, `uid` and `cn` indexed. Unencrypted on 3389 like the OpenLDAP lab. No snapshot images yet (population at the small templates takes seconds). |
| D5 | Access control as a single source | The ACI LDIFs under `test/integration/docker/dirsrv/aci/` are applied by the image build and published verbatim in `docs/connectors/jim-ldap-connector.md`, as the OpenLDAP lab's `acl/` files are. |
| D6 | Harness shape | `Get-DirectoryConfig -DirectoryType DirectoryServer389` with the OpenLDAP entry's shape; every entry now carries an explicit `DirectoryType`, and scenarios ask `Test-IsRfcDirectory` instead of inferring the directory from `inetOrgPerson` (which 389 also uses). The OpenLDAP populate scripts take their container, port and administrator DN from the directory configuration and are shared. The runner offers `DirectoryServer389` in its parameter set, interactive menu, `All` fan-out and scenario eligibility rules; 389 is allowed wherever OpenLDAP is except the OpenLDAP-only Scenarios 14, 19 and 22. |
| D7 | Scope | In: D1 to D6, Scenarios 1 and 8 on 389 as the gate, a sweep of the other scenarios OpenLDAP supports, the manual deletion, rename and readiness proof, documentation and changelog entries for the product fixes. Out, as sub-issues of #1479: the `RequiresLdaps` encrypted-connection rows for 389; the visible `ns*` Object Types on the schema screen (the Netscape OID arc also holds `inetOrgPerson`, so an arc filter is wrong); snapshot images. |

## Technical Architecture

**Current state.** `LdapChangelogDeltaSource` (selected for 389 and generic directories by `LdapConnectorRootDse.DeltaSourceKind`) probes the changelog, captures the watermark, reads `changeNumber`, `changeType` and `targetDN`, and for a delete emits an import object with no Object Type and no external id, which `SyncImportTaskProcessor` cannot match and discards. The harness has two directory types, and its scenario scripts decide between them by object class.

**New state.** `LdapDeletedEntryIdentity` turns the LDIF-shaped lines of a deleted entry into an Object Type and an `entryUUID`; `LdapAccesslogDeltaSource` and `LdapChangelogDeltaSource` both use it. The changelog source's readiness returns two findings (the changelog, the deleted-entry recording) through the same `LdapDeltaSourceFinding` vocabulary, so Schema Discovery and Delta Import treat them like every other source's findings. The harness has three directory types, one explicit `DirectoryType` fact per configuration, and one predicate for "RFC-style directory".

## Deviations

Findings against 389-Directory/3.1.2 (the `389ds/dirsrv:3.1` image) that changed the plan as written:

- **The Connected System connects over LDAPS (3636), not plain LDAP.** 389 refuses the RFC 3062 Password Modify operation over an unencrypted connection ("Confidentiality required") and offers no switch to allow it. The image therefore bakes a lab CA and a certificate naming `dirsrv-primary`, and the runner adds that CA to JIM's certificate store before the scenario runs, as the Samba AD lab does. The harness's own `ldapsearch` checks still use 3389 inside the container.
- **`DS_SUFFIX_NAME` creates no backend.** `dscontainer` only records it as a default base DN; both suffixes are created at image build time with `dsconf backend create --create-suffix`.
- **`cn=schema` refuses `dITContentRules` over LDAP** ("Only object classes and attribute types may be added"), so the `jimPersonContent` rule the OpenLDAP lab defines is not loaded on 389. Scenario 19 (auxiliary classes) stays OpenLDAP-only for this reason as well as its fixture.
- **`nsslapd-log-deleted` cannot be named in an ACI attribute list** because 389 ships no schema definition for it. The recipe grants every attribute but `aci` on the plug-in entry instead, which does let the service account read it.
- **The changelog source requests `changes` on every record**, not only on deletes: one search shape, and the accesslog source requests its equivalents for every record too. Adds and modifies are still fetched by DN; the LDIF of the write is ignored for them.
- **A move out of the selected containers is skipped, not staged as a deletion.** The object stays in JIM until a Full Import. The accesslog and USN sources have the same window; closing it is not part of this work.
- **On a changelog directory that is not 389 Directory Server**, the readiness check cannot find the plug-in entry and every Delta Import and Schema Discovery carries the "could not confirm" warning. That is truthful (such a directory's delete records carry no deleted entry, so deletions are missed) and was kept rather than gated on the detected directory type.
- **A global password policy is baked into the fixture** (`passwordCheckSyntax on`, `passwordMinLength 7`), mirroring the OpenLDAP lab's `pwdMinLength 7`, so the password scenarios have something to refuse and JIM's 389 policy discovery reads a real policy.

## Success Criteria

- `./test/integration/Run-IntegrationTests.ps1 -Scenario 1 -DirectoryType DirectoryServer389 -Template Nano` and the Scenario 8 equivalent pass with no other flags; OpenLDAP and Samba AD Scenario 1 still pass.
- Against the lab: a directory-side delete followed by a Delta Import leaves the Connected System Object Obsolete; a rename leaves it carrying the new DN; with `nsslapd-log-deleted` off, Schema Discovery warns and Delta Import fails naming the remedy; with the changelog ACI revoked, the existing refusal holds.
- `dotnet build JIM.sln` and `dotnet test JIM.sln` clean; the Pester suite over `test/integration/utils` covers the new configuration entry and predicate.
- The connector guide's supported directories row for 389 Directory Server states what is verified, and its Service Account Permissions section carries the ACI recipe verbatim from the lab.

## Dependencies

- Docker Hub `389ds/dirsrv:3.1` (pulled at image build time; the harness's `docker pull` retry conventions apply).
- The #1725 stack (merged): `ILdapDeltaSource`, `LdapDeltaSourceFinding`, the working changelog filter and the rootDSE changelog attributes.

## Risks and Mitigations

- **389 refuses the Password Modify extended operation over an unencrypted connection.** Mitigation: move the lab to LDAPS on 3636 with the server's auto-generated CA added to the JIM certificate store, as the Samba AD lab does.
- **`cn=changelog` and `cn=config` appear as partitions** because 389 lists them in `namingContexts`. Mitigation: partition discovery skips the changelog naming context; recorded as a product fix if it surfaces.
- **The subschema is large** (about 2 MB) and is read in one unpaged request. Mitigation: raise the service account's limits in the fixture; fix the read if the lab shows it failing.
- **Scenario scripts have OpenLDAP-specific expectations beyond the object class** (custom schema, DIT content rules). Mitigation: the fixture loads the same `jim-extensions` schema; anything 389 will not accept is dropped for 389 and recorded under Deviations.
