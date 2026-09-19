# A Delegated JIM Service Account for the OpenLDAP Integration Lab

- **Status:** Doing
- **Issue:** [#1715](https://github.com/TetronIO/JIM/issues/1715)
- **Related:** [#1716](https://github.com/TetronIO/JIM/issues/1716) Samba AD lab: bind JIM as a delegated account, not Administrator
- **Created:** 2026-09-19

## Overview

Every OpenLDAP integration scenario connected JIM as `cn=admin,<suffix>`, the suffix database's rootDN. That identity bypasses all access control and every policy overlay, so the scenarios proved JIM's synchronisation logic but never proved it works within the permissions a customer would actually grant a service account. Two consequences of that gap surfaced together: the ppolicy overlay could not make Scenario 20's parked-retry test exercise a genuine password refusal on OpenLDAP, because the rootDN is exempt from policy, and more importantly, any insufficient-access failure on import, export, delta import or a password write under real access control would first be discovered at a customer site rather than in the lab. The LDAP Connector documentation told customers to use a least-privilege service account but offered no OpenLDAP access-control example to follow.

This work gives the lab a second bind identity, `cn=svc-jim,ou=Services,<suffix>`, with an explicit, versioned `olcAccess` set written against JIM's actual LDAP operations, and turns on the ppolicy overlay so password refusals are exercised on OpenLDAP the same way they already are on the Samba AD domain. The access-control and password-policy LDIFs are not a lab-only artefact: they are published verbatim in the LDAP Connector's customer documentation, so the lab runs exactly what customers are told to set up. Delivered as layer 4 of the #1697 stack (it needs layer 2's Scenario 20 OpenLDAP support), on branch `feature/initial-passwords-on-delivery-service-stack-openldap-service-account`. The equivalent gap on the Samba AD lab, which still binds as the domain Administrator, is filed as follow-up work under #1716 rather than folded in here.

## Business Value

- Every OpenLDAP-capable scenario now proves JIM's synchronisation logic under the same access-control constraints a customer's directory team would impose, not under an administrator bind that hides insufficient-access defects until a customer hits them.
- The access-control recipe customers are told to apply is the recipe the lab runs under. A change to what JIM needs changes both at once; they cannot drift apart.
- Scenario 20's parked-change retry test now exercises a genuine password policy refusal on OpenLDAP, matching the coverage it already had against the Samba AD domain's minimum password length.

## Resolved design decisions

Settled before implementation began; the work packages below implement them rather than revisiting them.

| # | Decision | Resolution |
|---|---|---|
| D1 | Two identities in the lab | The directory administrator (rootDN, today's `cn=admin,<suffix>`) keeps populating data, running out-of-band `ldapsearch`/`ldapmodify` assertions, and serving the compose healthcheck. JIM's Connected Systems bind as a new service account. `Get-DirectoryConfig` gains `JimBindDN` and `JimBindPassword`; the setup scripts that create Connected Systems switch to them; nothing else changes identity. For Samba AD the new fields equal today's Administrator values, so those scenarios are untouched. |
| D2 | The service account(s) | `cn=svc-jim,ou=Services,<suffix>` on each suffix (Yellowstone and Glitterband), `organizationalRole` + `simpleSecurityObject`, password `Svc-Jim@123!` (deliberately different from the admin password so an accidental rootDN bind cannot pass unnoticed). Seeded by the bootstrap LDIF (Yellowstone) and the second-suffix script (Glitterband). A third account, `cn=svc-jim-partitions,ou=Services,dc=yellowstone,dc=local` (same shape, password `Svc-Jim-Partitions@123!`), is for a Connected System that imports more than one partition from the same server: it is a member of *both* suffixes' `cn=jim` groups (see D3), where each single-suffix `svc-jim` account is a member of its own suffix's group only. |
| D3 | Access control, explicit, versioned, and group-based | Replace the base image's inherited rules on both suffix databases, the accesslog database and the frontend with a set written for JIM's actual operations (inventory below). Each suffix's rules grant `by group.exact="cn=jim,ou=Services,<suffix>" ...` rather than naming a service account's DN directly: `cn=jim,ou=Services,<suffix>` is a `groupOfNames` whose members are the service account(s) permitted to manage that suffix, so delegating a new Connected System (or a multi-partition one, per D2) is a membership change, not an ACL edit. Set at build time in `01-add-second-suffix.sh`; it lives in `cn=config`, which the snapshots preserve, so no start-time reconcile. Anonymous read is withdrawn except where a bind needs `auth`; scenario assertions are unaffected because they bind as the rootDN. |
| D4 | Password policy on | ppolicy module and overlay on both suffix databases with a default policy `cn=default,ou=Policies,<suffix>`: `pwdMinLength 7`, `pwdCheckQuality 2`, and lockout, expiry, history and must-change all off. Matches the Samba domain's minimum. Scenario 20's Test 8 then runs on OpenLDAP. |
| D5 | Customer docs are the lab's files, not a copy | The access-control set and the ppolicy entries live in `test/integration/docker/openldap/acl/` as LDIF files with placeholders substituted by the build script. `docs/connectors/jim-ldap-connector.md` gains an OpenLDAP section under Service Account Permissions that includes those files verbatim with `--8<--` snippet includes, plus the bind DN shape, the `cn=accesslog` read rule and non-rootDN `olcSizeLimit` note, and the ppolicy note (JIM cannot discover OpenLDAP policy yet, #1703; a refused password parks with the server's words). One source; the lab runs exactly what customers read. |
| D5a | Stricter than the defaults | OpenLDAP's built-in default is `access to * by * read` (anonymous can read the tree), unlike Active Directory. The lab withdraws anonymous read on both suffixes and the accesslog: only `auth` on `userPassword` for binds, rootDSE and `cn=Subschema` readable. A JIM proven against the locked-down shape also works against a permissive one; the lab's own population and assertions bind as the rootDN and are unaffected. The base image's inherited Yellowstone and accesslog rules were dumped from a running container and are recorded below before replacement. |
| D6 | Where it lands | Layer 4 of the #1697 stack (it needs layer 2's Scenario 20 OpenLDAP support): branch `feature/initial-passwords-on-delivery-service-stack-openldap-service-account` (the empty `-stack-openldap-ppolicy` branch was renamed). Its own issue and plan document; Samba AD delegation filed as a separate issue (#1716). |
| D7 | Gate | Every OpenLDAP-capable scenario passes under the service account: 1, 2, 4, 5, 6, 7, 8, 9, 10, 14, 19, 20 at their smallest templates, plus Scenario 1 on Samba AD to prove the plumbing change is inert there. An insufficient-access failure is a finding about the ACL set or about JIM, never something to fix by widening to `manage`. |

## Technical Architecture

### Current state

The base image's inherited access-control rules, dumped from a running container before replacement:

```
olcDatabase={-1}frontend,cn=config: no olcAccess (slapd default "to * by * read")
olcDatabase={2}mdb,cn=config (dc=yellowstone,dc=local): no olcAccess (default "to * by * read")
olcDatabase={3}mdb,cn=config (cn=accesslog): no olcAccess (default; world-readable)
olcDatabase={4}mdb,cn=config (dc=glitterband,dc=local): olcAccess: {0}to * by dn.exact="cn=admin,dc=glitterband,dc=local" manage by * read
olcPasswordHash: not set (slapd default SSHA); modules: pw-sha2.so, accesslog.so
```

Every OpenLDAP scenario connected as `cn=admin,<suffix>`, the rootDN, which is exempt from access control and from the ppolicy overlay. No password policy overlay existed on either suffix, so Scenario 20's parked-retry test could not exercise a genuine OpenLDAP refusal and was skipped there.

### New state

**Five LDIF files**, not two, live in `test/integration/docker/openldap/acl/` and are the single source for both the lab and `docs/connectors/jim-ldap-connector.md`:

| File | Applies to | Bound as |
|---|---|---|
| `jim-service-account-access.ldif` | Each suffix database (`olcDatabase={n}mdb,cn=config`) | Configuration rootDN |
| `jim-accesslog-access.ldif` | The shared `cn=accesslog` database | Configuration rootDN |
| `jim-frontend-access.ldif` | `olcDatabase={-1}frontend,cn=config` | Configuration rootDN |
| `jim-password-policy.ldif` | Ordinary suffix data (`ou=Policies,<suffix>`, `cn=default,ou=Policies,<suffix>`) | That suffix's own rootDN |
| `jim-ppolicy-overlay.ldif` | `olcOverlay=ppolicy,<suffix DB DN>` | Configuration rootDN |

**Apply order**, in `test/integration/docker/openldap/scripts/01-add-second-suffix.sh`, once all three database DNs (Yellowstone, Glitterband, accesslog) and both service account DNs are known: load the ppolicy module; add the Glitterband database, its accesslog overlay and its service account; raise the Yellowstone mapsize and the accesslog mapsize/size limit; apply `jim-service-account-access.ldif` to each suffix in turn; apply `jim-frontend-access.ldif` once; apply `jim-accesslog-access.ldif` once, naming both service accounts; apply `jim-password-policy.ldif` to each suffix, bound as that suffix's rootDN; apply `jim-ppolicy-overlay.ldif` to each suffix, bound as the configuration rootDN. The access-control and overlay-attachment files go last so nothing earlier in the script (which still needs to read and write freely as the rootDNs) is affected by the narrower rules it is about to apply to everyone else.

**Why the policy entries cannot go in Bitnami's `/ldifs` load.** Bitnami's `ldap_initialize()` loads `/ldifs/*` before slapd is ever started with the ppolicy module configured, so a `pwdPolicy` entry in that load fails with "object class not defined": the schema an overlay contributes is only registered once the module is loaded into a running slapd. `jim-password-policy.ldif` and `jim-ppolicy-overlay.ldif` are therefore applied by `01-add-second-suffix.sh`, which runs after Bitnami starts slapd temporarily for its own second-suffix work and after this script has loaded the ppolicy module itself.

**The two identities in `Get-DirectoryConfig`** (`test/integration/utils/Test-Helpers.ps1`): every directory config now carries `BindDN`/`BindPassword` (the rootDN, used for population, out-of-band assertions and the healthcheck) alongside `JimBindDN`/`JimBindPassword` (what a Connected System's Username/Password settings are configured with). For OpenLDAP the two differ; for Samba AD they are currently identical, which is what keeps that lab's scenarios unaffected pending #1716.

**The connector operation inventory that drove the rule set** (from `src/JIM.Connectors/LDAP/`): simple bind only (no SASL, referrals never chased); reads of the rootDSE, `cn=Subschema`, managed subtrees including `entryUUID` and `objectClass`, and container discovery; one-level reads of `cn=accesslog` for delta import; add, modify, modrdn and delete for export, plus optional container creation under the suffix root; and RFC 3062 Password Modify for password writes, never a direct `userPassword` write. Each rule in `jim-service-account-access.ldif` traces to one of these: `{0}` to the Password Modify path, `{1}`/`{2}` to export under the managed OUs, `{3}` to optional container creation, `{4}` to the read-only rest of the tree that schema discovery and browsing need.

## Deviations

- **A product bug surfaced on the first sweep, fixed in a layer beneath this one.** Under the service account, Scenario 1 failed at hierarchy import with "The object does not exist": partition discovery searched every naming context the server advertises (`dc=glitterband,dc=local` and `cn=accesslog` alongside `dc=yellowstone,dc=local`) and let one unreadable context fail the whole import. Only a rootDN bind had hidden it. `feature/initial-passwords-on-delivery-service-stack-hierarchy-import-unreadable-partitions` makes partition discovery skip naming contexts and crossRef partitions the account cannot read (noSuchObject or insufficientAccess), log them, and fail clearly only when it can read none; this lab layer is stacked on top of it.

- **Five files, not the two D5 originally named.** The design decision anticipated one access-control file and one policy file; implementation split access control by database (suffix, accesslog, frontend) because each needs a different bind identity or placeholder set, and split the policy in two because the policy entries (ordinary suffix data, suffix rootDN) and its overlay attachment (`cn=config`, configuration rootDN) cannot be written by the same bind. Keeping them as separate files, each documented with its own required bind identity, was judged clearer than one file with mixed instructions partway through it.
- **Scenario 9 forced the DN-exact rules to become group-based.** Scenario 9 (partition-scoped imports, #72) runs one Connected System importing *both* Yellowstone and Glitterband, which the original `by dn.exact="__SERVICE_DN__"` rules could not express: a DN-exact clause names exactly one account, and the Yellowstone service account granted on Yellowstone's database is (correctly) not granted on Glitterband's. Widening the DN-exact rule to the account, rather than restructuring the grant, was rejected: it would have meant either running Scenario 9 as rootDN (reintroducing the very gap this lab closes) or granting `svc-jim` on a suffix it has no business touching. The fix generalises the access model instead of special-casing the scenario: each suffix now grants to a `cn=jim,ou=Services,<suffix>` group (D3), and a dedicated `cn=svc-jim-partitions` account (D2) is a member of every suffix's group it needs. This is also the customer-facing recipe (D5), since a real customer with one Connected System spanning several partitions hits the identical limitation.
- **Suffix administrators can no longer read `cn=accesslog`, by design.** The inherited rule made the accesslog database world-readable; the new rule restricts it to the service accounts that run delta imports, which means a suffix's own rootDN loses implicit read access to it too (a database's rootDN is exempt from that database's own rules, but is not automatically granted access to a different database). An administrator who needs to inspect the accesslog can still bind as the accesslog database's own rootDN.

## Success Criteria

The WP4 verification sweep, all sequential (never two runners at once), `-SkipBuild` once images are built:

- `Run-IntegrationTests.ps1 -Scenario "<n>" -DirectoryType OpenLDAP -Template <smallest>` passing for scenarios 1, 2, 4, 5, 6, 7, 8, 9, 10, 14, 19 and 20. Scenario 8 additionally confirms delta import did not silently fall back to full import under the service account's narrower access.
- `Run-IntegrationTests.ps1 -Scenario "Scenario1-HRToIdentityDirectory" -DirectoryType SambaAD -Template Nano` passing, proving the `Get-DirectoryConfig` plumbing change is inert on Samba AD.
- Any `insufficientAccessRights` seen in `jim.worker` logs during the sweep is triaged as a finding about the ACL set or about JIM; the ACL set is corrected only where JIM's need is legitimate, and the documentation is updated to match, rather than the rule being widened to `manage` to make a failure go away.
- Manual proof from a started container, as `svc-jim`: `ldapwhoami`; read `entryUUID` under `ou=People`; add, modify, modrdn and delete a scratch entry under `ou=People`; `ldappasswd` a seeded user with a six-character value (refused: "Password fails quality checking policy") and a seven-character one (accepted); one-level `ldapsearch` on `cn=accesslog`; read `cn=Subschema` and the rootDSE; a write attempt on `ou=Services` (refused). As anonymous: nothing readable except rootDSE and subschema; a user bind still works.

## Dependencies

- Layer 2 of the #1697 stack, which added Scenario 20's OpenLDAP support that this work's ppolicy overlay makes genuinely exercise a refusal against.
- `Build-OpenLdapImage.ps1` and `Build-OpenLDAPSnapshots.ps1` build hashes cover the five new/changed `acl/*.ldif` files and `01-add-second-suffix.sh`, so every snapshot tier rebuilds once on the first run after this change.

## Risks and Mitigations

- **Snapshot invalidation makes the first OpenLDAP run after this change slow (every tier rebuilds).** Expected and one-off; subsequent runs use the rebuilt snapshots as before.
- **The base image's default ACLs were not in the repository before this work**, so replacing them changes anonymous access on Glitterband (previously `by * read`) and Yellowstone (previously no explicit rule, so the slapd default). Mitigation: every scenario assertion binds as the rootDN, so nothing in the suite depends on anonymous read; confirmed by the WP4 sweep.
- **ppolicy applies to every non-rootDN password write**, including any scenario step that sets a short password deliberately. Only Scenario 20 does this, and it now expects the refusal rather than working around it.
- **Server-side sort on `cn=accesslog` is requested with criticality true.** Unchanged behaviour: without `sssvlv` loaded, the connector already falls back to unsorted results, and the access-control change does not affect this.
- **Container creation under the suffix root needs rule `{3}`.** If no scenario in the current suite exercises "Create Containers as Needed" on OpenLDAP, the rule is proven only by the manual `ldapadd` step above and is documented as optional for customers who never let JIM create OUs.
