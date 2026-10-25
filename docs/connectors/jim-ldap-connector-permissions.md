---
title: LDAP Connector Service Account Permissions
description: "The rights the LDAP Connector's service account needs in each directory to import, export, set passwords and read changes."
type: object
audience: administrator
---

# LDAP Connector Service Account Permissions

The LDAP service account used by JIM should follow the principle of least privilege:

- **For import only**<br /> Grant read access to the containers and attributes that JIM needs to import.
- **For export (provisioning)**<br /> Grant create, modify, and delete permissions on the target containers. For Active Directory, this typically means delegated control over the relevant OUs.
- **For container provisioning**<br /> If **Create containers as needed?** is enabled, the service account must have permission to create organisational units.
- **For delta import**<br /> The service account needs read access to where the directory keeps its record of changes: the `uSNChanged` attribute for Active Directory and Samba AD (ordinary read access covers it), `cn=accesslog` for OpenLDAP (the access-control rule under [OpenLDAP](#openldap) below), and the changelog for 389 Directory Server and generic directories (`cn=changelog`, or the DN the root DSE's `changelog` attribute names; on 389 Directory Server the changelog and plug-in rules under [389 Directory Server](#389-directory-server) below, the second of which is what lets JIM confirm that deletions are being recorded). JIM checks this read at Schema Discovery, where a missing right is a warning, and before every Delta Import, where it is a refusal rather than a run that imports nothing.
- **For importing deletions (Active Directory)**<br /> A Delta Import finds deletions by searching `CN=Deleted Objects,<domain DN>` with the Show Deleted Objects control, so the service account needs **List Contents**, **Read Property** and **Read Permissions** over that container. No delegation on an OU reaches it: its permissions are protected from inheritance and its owner is SYSTEM, so even a domain administrator has to take ownership before granting anything. Withholding this read is not refused by the directory: the search succeeds and returns nothing. JIM does not take that silence at face value: it reads the container's own permissions at Schema Discovery and before every Delta Import, and Read Permissions is what lets that check give a definite answer rather than could not tell. See [Active Directory](#active-directory) below for what JIM does with the answer and for the grant.
- **For probing generated values**<br /> Read access to each attribute JIM probes (for example `sAMAccountName`, `userPrincipalName`, `uid` or `mail`) across the whole partition, not only the containers JIM imports: a probe searches from the partition root, and an account the service account cannot see is an account the probe cannot find. Where a forest-wide attribute is searched through a Global Catalog, the account needs to read it there too, which authenticated users can by default. JIM detects a probe it cannot trust, because a value it already holds fails to come back, and reports it on the synchronisation's Activity; it then checks that directory against its own records only. See [Probing for values already in use](jim-ldap-connector-values-in-use.md#probing-for-values-already-in-use).
- **For setting passwords**<br /> Grant the **Reset Password** permission on the containers JIM manages. In Active Directory this is a control access right, delegated on the OU (Delegate Control, "Reset user passwords and force password change at next logon"), and it is a separate thing from write access to attributes: an account with full write permission on an OU still cannot set a password without it. **The service account does not need to be a Domain Admin**, and should not be.
- **For checking reset rights**<br /> To answer the reset-rights preflight rather than reporting that it could not tell, the service account also needs read access to the `nTSecurityDescriptor` attribute of accounts in those containers. Reading an object's permissions is normally covered by ordinary read access; where it is not, the check reports an unknown rather than a denial.
- **For discovering Fine-Grained Password Policies (Active Directory)**<br /> Detecting whether any exist requires read access to the domain's Password Settings Container (`CN=Password Settings Container,CN=System,<domain DN>`), which by default is restricted to Domain Admins. Without it JIM reports that it could not tell, and treats the domain policy it read as a floor. Granting read on that container is optional; it buys a definite answer in the Password Channel panel and nothing else.
- **For reading the password policy (389 Directory Server)**<br /> Grant read on the `password*` attributes of `cn=config`, which hold the global policy; the `cn=config` rule under [389 Directory Server](#389-directory-server) below grants exactly those. This is optional, and it buys the policy itself: without it the panel reports the configuration as not readable, the override answer is could not tell, and generated passwords follow JIM's defaults rather than the directory's rules, so a stricter rule parks the Connected System Objects it refuses.
- **For reading the password policy (OpenLDAP)**<br /> Two optional reads, each buying something different. Read on the policy entry named by `olcPPolicyDefault` (and its siblings) gives JIM the rules themselves. Read on the overlay's configuration under `cn=config` (the `olcOverlay=ppolicy` entries) tells JIM which policy is the default where more than one exists, and whether a check module is configured; without it, a directory with a single policy entry is still read in full, while one with several is reported as not readable.
- **For a definite answer on policy overrides (OpenLDAP and 389 Directory Server)**<br /> Read access over the objects JIM manages that includes the operational `pwdPolicySubentry` attribute (OpenLDAP) or the policy containers (389) lets JIM report that overrides exist. Neither directory offers a way to prove that none do, so the best answer there is could not tell, and the figures shown remain a floor.

!!! tip "Dedicated service account"
    Always use a dedicated service account for JIM rather than sharing credentials with other applications or using a personal account. This simplifies auditing and ensures that permission changes do not inadvertently affect JIM's operations.

The bullets above say what access JIM needs; the three subsections below are the concrete recipes for granting it. Active Directory delegates on a container, through control access rights and inheritable access control entries. OpenLDAP has none of that machinery: permissions come from `olcAccess` rules on the directory's own configuration. 389 Directory Server keeps access control instructions as `aci` values on the entries themselves. The mechanics differ; the goal, least privilege, does not.

## Active Directory

Bind JIM as a dedicated account, never a Domain Admin. An ordinary user object is enough; JIM needs no rights at all outside the branch it manages.

Grant the access to a **group** and make the account a member of it, rather than granting anything to the account itself. `CN=JIM Connectors,OU=Services,<domain DN>` holding `CN=svc-jim,OU=Services,<domain DN>` is the shape. Replacing the service account, or adding a second one for another Connected System, is then a membership change and the delegation is left alone.

Delegate on the **container at the top of the branch JIM manages**, normally the OU holding the accounts and groups it provisions. Every entry below is inheritable, so objects and containers created later are covered without a second delegation. That inheritance is also what covers moving an object between containers, which needs delete in the source and create in the target.

These are the access control entries, as SDDL. JIM's integration lab runs against exactly these, which is what keeps them honest: they are the lab's own file, published here verbatim.

```text
--8<-- "test/integration/docker/samba-ad-prebuilt/delegation/jim-ad-delegation.acl"
```

On Windows, apply them with `dsacls` against the container's DN, naming the delegation group as the trustee. Substitute your own domain and container:

```text
dsacls "OU=Corp,DC=corp,DC=local" /I:T /G "CORP\JIM Connectors:CCDC;user"
dsacls "OU=Corp,DC=corp,DC=local" /I:T /G "CORP\JIM Connectors:CCDC;group"
dsacls "OU=Corp,DC=corp,DC=local" /I:T /G "CORP\JIM Connectors:CCDC;organizationalUnit"
dsacls "OU=Corp,DC=corp,DC=local" /I:S /G "CORP\JIM Connectors:RPWPLCLORCSDDT;;user"
dsacls "OU=Corp,DC=corp,DC=local" /I:S /G "CORP\JIM Connectors:RPWPLCLORCSDDT;;group"
dsacls "OU=Corp,DC=corp,DC=local" /I:S /G "CORP\JIM Connectors:RPWPLCLORCSDDT;;organizationalUnit"
dsacls "OU=Corp,DC=corp,DC=local" /I:S /G "CORP\JIM Connectors:CA;Reset Password;user"
```

Leave the `organizationalUnit` lines out unless **Create containers as needed?** is switched on.

The Delegation of Control wizard is the other route to the same place: run it on the container and grant the group "Create, delete and manage user accounts", "Create, delete and manage groups", and "Reset user passwords and force password change at next logon".

Then grant read over the Deleted Objects container, which is where a Delta Import finds deletions and which no delegation on an OU reaches. Its permissions are protected from inheritance and its owner is SYSTEM, so take ownership first:

```text
dsacls "CN=Deleted Objects,DC=corp,DC=local" /takeOwnership
dsacls "CN=Deleted Objects,DC=corp,DC=local" /G "CORP\JIM Connectors:LCRPRC"
```

List Contents and Read Property are what the tombstone search needs, and JIM only ever reads tombstones. Read Permissions lets the account read the container's own permissions, which is how JIM confirms the grant is in place. Skip this and the directory refuses nothing: the search succeeds and returns nothing. JIM checks rather than trusts that: Schema Discovery, and the schema refresh preview, warn when the account is not allowed to list the container or JIM cannot confirm that it is, and a Delta Import refuses to run when the account is provably not allowed to list it (the Activity says so and names the container), or completes with a warning when JIM cannot confirm. Without Read Permissions the account cannot read the container's own permissions, so JIM can only report that it could not tell; with it, the answer is definite either way.

Read over the Password Settings Container (`CN=Password Settings Container,CN=System,<domain DN>`) stays optional and is deliberately not in the entries above. Without it JIM reports that it could not tell whether Fine-Grained Password Policies exist, and treats the domain policy as a floor; with it, the Password Channel panel gives a definite answer.

## OpenLDAP

Bind JIM as a dedicated service account, never the directory's rootDN. Any entry with a `userPassword` attribute can bind; a typical shape is an `organizationalRole` plus `simpleSecurityObject` entry such as `cn=svc-jim,ou=Services,dc=example,dc=com`.

Access is granted to a **group**, not to an individual service account's DN. Create `cn=jim,ou=Services,dc=example,dc=com` (a `groupOfNames`) on each suffix and add the service account(s) allowed to manage that suffix as a `member`. Delegating a new Connected System, or adding a second one against the same suffix, is then a membership change, never an ACL edit. A Connected System that imports more than one partition from the same server (one import scoped across two suffix databases) uses a single service account that is a member of each suffix's group, rather than needing a bespoke rule per suffix for that account; OpenLDAP resolves group membership server-wide, so the group and the ACL that names it can live in different databases.

The access-control and password-policy rules below are exactly what JIM's own integration tests run under, so they are proven working, not illustrative. Two placeholders run through the access-control files: the suffix DN (`__SUFFIX__`, for example `dc=example,dc=com`) and the suffix database's configuration entry (`__DB_DN__`, its `olcDatabase={n}mdb,cn=config` DN, found with `ldapsearch -b cn=config "(olcSuffix=<suffix>)" dn`). The accesslog file's placeholders name the group(s) instead (`__JIM_GROUP_DN__`, `__JIM_GROUP_DN_2__`). The ppolicy overlay file's modify form takes the overlay's own DN (`__OVERLAY_DN__`) rather than `__DB_DN__`: slapd assigns the overlay an ordinal RDN when it is added (`olcOverlay={0}ppolicy,<database DN>`, not the unindexed name used to add it), so a later modify has to address the DN slapd actually assigned, found with `ldapsearch -b '<database DN>' "(objectClass=olcPPolicyConfig)" dn`.

Apply the access-control files bound as the configuration administrator (`cn=admin,cn=config`), since `olcAccess` and `olcOverlay` entries live under `cn=config`; apply the password policy files bound as the suffix's own administrator, since those are ordinary directory entries. Substitute the placeholders in a copy of each file, then apply it with `ldapmodify`.

```ldif
--8<-- "test/integration/docker/openldap/acl/jim-service-account-access.ldif"
```

Rule `{3}` is what lets JIM create organisational units when **Create containers as needed?** is enabled; if you always create target OUs by hand, leave the rule out (or leave it unused) without affecting anything else JIM does.

A service account is also subject to the database's search limits, which the rootDN it replaces was not. OpenLDAP applies `olcSizeLimit` (default 500 entries) across a paged search as a whole, so an import of a container holding more than 500 objects stops at the limit with "The size limit was exceeded", whatever page size the Run Profile uses. Exempt the group from the size and time limits on each suffix; every other client keeps the limits you have set:

```ldif
--8<-- "test/integration/docker/openldap/acl/jim-service-account-limits.ldif"
```

Delta import reads `cn=accesslog` one level deep. Grant that database's own access-control rule too, naming the group(s):

```ldif
--8<-- "test/integration/docker/openldap/acl/jim-accesslog-access.ldif"
```

How long the accesslog keeps entries is the overlay's own `olcAccessLogPurge` (the integration lab keeps 7 days: `07+00:00 01+00:00`). Keep that maximum age longer than the longest gap between Delta Imports; a Delta Import that finds the purge has gone past its watermark asks for a Full Import (see [Import](jim-ldap-connector.md#import)).

OpenLDAP enforces `olcSizeLimit` against non-rootDN clients even with paging controls in play, so lift it for JIM on the accesslog database too, or, as the integration lab does, set it unlimited there. A Delta Import the limit stops part-way fails, naming the limit, rather than carry on from a point that could miss a change. A server hosting only one suffix has only one group: delete the second `by group.exact=... read` clause and its placeholder. Under this rule a suffix administrator can no longer read `cn=accesslog` themselves; that is intended, since only the service accounts that run delta imports need to. An administrator who needs to inspect it can still bind as the accesslog database's own rootDN.

A frontend rule covers what every client, including anonymous ones, needs for ordinary connection setup:

```ldif
--8<-- "test/integration/docker/openldap/acl/jim-frontend-access.ldif"
```

This set withdraws anonymous read and grants nothing to authenticated users other than the service account: OpenLDAP's own default (`to * by * read`) lets anonymous binds read the whole tree, which these rules deliberately close. If you already have "by users read" style rules for other applications, append them after JIM's rather than replacing JIM's with them, so JIM's `by * none` fallback stays last.

### Password policy

JIM discovers the `ppolicy` overlay's default policy (see [Password policy discovery](jim-ldap-connector-passwords.md#password-policy-discovery) above) and checks a static Initial Password against it before sending. A password the server still refuses parks with the server's own words, exactly as a policy refusal from Active Directory does. The ppolicy overlay only applies to non-rootDN binders, which is another reason to keep JIM off the rootDN: binding it as the service account is what makes password quality checking, lockout, expiry and history apply to JIM's password writes at all.

```ldif
--8<-- "test/integration/docker/openldap/acl/jim-password-policy.ldif"
```

```ldif
--8<-- "test/integration/docker/openldap/acl/jim-ppolicy-overlay.ldif"
```

## 389 Directory Server

Bind JIM as a dedicated service account, never `cn=Directory Manager`. Any entry with a `userPassword` attribute can bind; `cn=svc-jim,ou=Services,dc=example,dc=com` is the shape. The Directory Manager bypasses every access control instruction and the password policy, which is exactly why it is the wrong identity for JIM.

As on OpenLDAP, access is granted to a **group**, not to an individual service account's DN. Create `cn=jim,ou=Services,<suffix>` (a `groupOfNames`) on each suffix and add the service account(s) allowed to manage that suffix as a `member`. A Connected System that imports more than one partition from the same server uses a single service account that is a member of each suffix's group; 389 Directory Server resolves group membership server-wide, so the group and the entry carrying the rule can live in different backends.

389 Directory Server denies whatever no rule allows. The three files below are exactly what JIM's own integration lab applies to its 389 Directory Server (`test/integration/docker/dirsrv/aci/`), so they are proven working, not illustrative. Three placeholders run through them: the suffix DN (`__SUFFIX__`, for example `dc=example,dc=com`), the suffix's group DN (`__JIM_GROUP_DN__`, its `cn=jim,ou=Services,<suffix>`), and a second suffix's group DN (`__JIM_GROUP_DN_2__`) in the two files whose rules serve every suffix on the server. Substitute them in a copy of each file, then apply it with `ldapmodify` bound as `cn=Directory Manager`.

Two things in the rules will look odd to a reader used to OpenLDAP, and both are deliberate. The rules say `targetattr != "aci"` rather than `targetattr = "*"`, because on 389 Directory Server `*` means user attributes only: the operational `entryUUID` that identifies every object to JIM is only reachable through the negated form. And every rule with an explicit attribute list includes `objectClass`, because the directory requires the bound account to have search rights on the attribute a filter names, and JIM's base reads use `(objectClass=*)`; without it the read returns nothing at all, with no error.

The suffix rules cover import (read across the suffix), export (add, delete and write under `ou=People` and `ou=Groups`) and passwords:

```ldif
--8<-- "test/integration/docker/dirsrv/aci/jim-suffix-access.ldif"
```

The commented-out rule at the end of the file is the counterpart of the OpenLDAP recipe's rule `{3}`, for **Create containers as needed?**. The ACI language has no one-level scope, so it lets JIM add an organisational unit anywhere in the suffix; leave it out if you create target OUs by hand.

Delta Import reads `cn=changelog`, which the Retro Changelog plug-in creates with no access control at all, so out of the box only the Directory Manager reads it and a Delta Import as anyone else is refused (see [Delta Import change source checks](jim-ldap-connector-delta-import.md#delta-import-change-source-checks)). One changelog serves every suffix on the server, so the rule names every suffix's group:

```ldif
--8<-- "test/integration/docker/dirsrv/aci/jim-changelog-access.ldif"
```

The plug-in has to be on before this rule can be applied, because `cn=changelog` does not exist until it is. Enable it, turn on the recording of deleted entries (which `dsconf` does not expose, so it is set on the plug-in entry directly), give the changelog a maximum age longer than the longest gap between Delta Imports, keep `userPassword` out of it, and restart the directory:

```text
dsconf <instance> plugin retro-changelog enable
dsconf <instance> plugin retro-changelog set --max-age 7d --exclude-attrs userPassword
ldapmodify -x -H ldap://<host>:389 -D "cn=Directory Manager" -W <<'LDIF'
dn: cn=Retro Changelog Plugin,cn=plugins,cn=config
changetype: modify
replace: nsslapd-log-deleted
nsslapd-log-deleted: on
LDIF
```

Once the plug-in is on, the root DSE advertises `changelog`, `firstchangenumber` and `lastchangenumber`, and JIM uses all three. `cn=changelog` also appears among the directory's naming contexts, and therefore among the partitions JIM offers on the Connected System; do not select it.

Two reads under `cn=config` remain, both optional and both read-only: the global password policy, so that the passwords JIM generates satisfy the directory's rules rather than JIM's defaults ([Password policy discovery](jim-ldap-connector-passwords.md#password-policy-discovery)), and the Retro Changelog plug-in's own settings, so that JIM can confirm the plug-in is recording deleted entries rather than warn on every Schema Discovery and Delta Import that it could not. The password policy rule names only the attributes JIM reads. The plug-in rule grants every attribute of that one entry but `aci`, because 389 Directory Server ships no schema definition for `nsslapd-log-deleted` and refuses a rule whose attribute list names an attribute the schema does not define; the entry holds nothing but the plug-in's settings.

```ldif
--8<-- "test/integration/docker/dirsrv/aci/jim-config-access.ldif"
```

Lift the search limits on the account JIM binds as. 389 Directory Server's defaults (`nsslapd-sizelimit` 2000, `nsslapd-lookthroughlimit` 5000) apply across a paged search, so a Full Import of more entries than that is subject to them. Set `nsSizeLimit`, `nsLookThroughLimit` and `nsPagedSizeLimit` to `-1` on the service account's own entry, as the lab does, rather than raising them server-wide.

Connect over LDAPS. 389 Directory Server accepts the Password Modify operation only over a secure connection (see [Setting Passwords](jim-ldap-connector-passwords.md#setting-passwords)), and the suffix rules above grant a password write that it will otherwise refuse with "Confidentiality required".
