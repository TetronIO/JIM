# Password Policy Discovery for OpenLDAP and 389 Directory Server

- **Status:** Planned
- **Created:** 2026-09-19
- **Author:** JayVDZ (PRD drafted via Claude Code)
- **Issue:** *(not yet filed)*
- **Related:** [#1121](https://github.com/TetronIO/JIM/issues/1121) Initial Password Provisioning (introduced discovery and reconciliation), [#1635](https://github.com/TetronIO/JIM/issues/1635) Set Password, [#1699](https://github.com/TetronIO/JIM/pull/1699) (neutralised the portal wording that presumed a domain)
- **Summary with diagrams:** [Password Policy Discovery](https://claude.ai/artifact/Jg8VJqDhFKNSr4YraCpYvE) (the problem today, the per-directory dispatch, the attribute mapping, the override signal, and where the policy is consumed)

## Problem Statement

JIM discovers a Connected System's password policy so that a generated password satisfies the target's own rules without an administrator restating them, and so that a password set across several systems satisfies all of them at once (`PasswordPolicyReconciliation`). The model that holds a discovered policy, `ConnectedSystemPasswordPolicy`, is directory-neutral: minimum length, history length, maximum and minimum age, a required character-class count, and a three-state signal for "some objects are governed by a policy other than this one".

The reader behind it is not. `LdapConnectorPasswordPolicy` reads Active Directory's domain-root attributes (`minPwdLength`, `pwdProperties`, `pwdHistoryLength`, `maxPwdAge`, `minPwdAge`), probes the Password Settings Container for Fine-Grained Password Policies, and returns no policy at all for any other directory type. The LDAP Connector nonetheless declares `SupportsPasswordPolicyDiscovery = true` for every directory, so an OpenLDAP or 389 Directory Server administrator is told "JIM has not read a password policy from this Connected System. Only Active Directory publishes one that a client can read", which is untrue: both directories publish an equivalent policy, and both support per-subtree or per-object overrides that play the same role as Fine-Grained Password Policies.

The consequences for those deployments are the ones discovery exists to prevent: a generated initial password is produced against JIM's defaults rather than the target's rules, so a target with a longer minimum length or a history rule refuses it and the Connected System Object is parked; and the portal's policy panel cannot show the administrator what the target will accept.

## Goals

- A Connected System whose LDAP directory is OpenLDAP (with the `ppolicy` overlay) or 389 Directory Server shows a discovered password policy on its Password Policy panel, with the same fields Active Directory shows today, populated where the directory publishes them and marked as not published where it does not.
- Generated passwords for those directories satisfy the discovered policy, verified by an integration scenario in which a Synchronisation Rule provisions objects into OpenLDAP with a minimum length above JIM's default and none are parked.
- The "some objects are governed by another policy" signal is defined per directory type and reported for all three (Active Directory, OpenLDAP, 389 Directory Server), with "could not determine" still distinguished from "none".
- A directory that publishes no readable policy (OpenLDAP without the overlay, a Generic RFC 4512 directory) is reported as such, with the panel's explanation naming the reason rather than naming Active Directory.

## Non-Goals

- Enumerating or applying per-object policies. As today, JIM detects that overrides exist and treats the discovered policy as a floor; it does not read each override.
- Writing or changing a directory's password policy.
- Password *quality* modules that cannot be expressed as the existing model (OpenLDAP `pwdCheckModule`, 389 dictionary checks). These are reported as "the directory applies further checks JIM cannot see", not modelled.
- Any change to how passwords are *set*. `LdapConnectorPassword` already uses `unicodePwd` for Active Directory and the RFC 3062 password-modify operation for everything else.
- Policy discovery for the SQL, SCIM or File Connectors.

## User Stories

1. As an administrator running JIM against OpenLDAP, I want the Connected System's Password Policy panel to show the directory's minimum length, history and age rules, so that I can see what a generated password must satisfy without reading the overlay configuration myself.
2. As an administrator provisioning into 389 Directory Server, I want generated initial passwords to satisfy the server's global policy, so that new Connected System Objects are not parked with a policy rejection on their first export.
3. As an administrator, I want JIM to tell me when a subtree or per-object policy may override the one it discovered, so that I understand why a password that satisfied the discovered policy was still refused.
4. As an administrator running a directory that publishes no policy, I want the panel to say so and why, so that I do not go looking for a configuration problem that does not exist.

## Requirements

### Functional Requirements

1. The LDAP Connector must detect 389 Directory Server as a distinct directory type (root DSE `vendorName` "389 Project", or the `vendorVersion` prefix), alongside the existing Active Directory, Samba AD, OpenLDAP and Generic detection. It must be surfaced in the Connected System's capabilities ("Directory Type") like the others.
2. Password policy discovery must be implemented per directory type behind the existing `GetPasswordPolicyAsync` entry point, so that callers (`LdapConnector`, `LdapConnectorPreflight`) are unchanged:
   - **Active Directory and Samba AD:** as today.
   - **OpenLDAP:** read the default policy entry named by the `ppolicy` overlay (`olcPPolicyDefault` on the overlay's config entry, or the overlay's default policy DN where config is not readable), mapping `pwdMinLength` to minimum length, `pwdInHistory` to history length, `pwdMaxAge` and `pwdMinAge` (seconds) to the age fields. `pwdCheckQuality` of 1 or 2 reports "further checks apply" (see requirement 6). Required character-class count is not published and is reported as unknown.
   - **389 Directory Server:** read the global policy from `cn=config`, mapping `passwordMinLength`, `passwordInHistory`, `passwordMaxAge` and `passwordMinAge` (seconds) as above, and `passwordMinCategories` to the required character-class count when `passwordCheckSyntax` is on. The recognised character classes are 389's five (upper, lower, digit, special, 8-bit).
   - **Generic:** no policy, reported as "this directory publishes no policy JIM can read".
3. The per-object override signal (today `FineGrainedPolicySignal`) must be renamed to a directory-neutral name (proposal: `PolicyOverrideSignal`) with the same three states, and detected per directory type:
   - **Active Directory:** as today (Password Settings Container).
   - **OpenLDAP:** any entry in the search base carrying `pwdPolicySubentry`, or more than one `pwdPolicy` entry under the default policy's parent.
   - **389 Directory Server:** any `nsPwPolicyContainer` entry, or any entry carrying `pwdpolicysubentry`.
   - An empty search result must map to "could not determine", never to "none", for the same reason as today: directories filter searches silently for a caller without rights.
4. `SupportsPasswordPolicyDiscovery` on the LDAP Connector must reflect the connected directory type once it is known (true for Active Directory, Samba AD, OpenLDAP and 389 Directory Server; false for Generic), so that the "JIM has not read the rules yet" and "there are no rules to read" states the portal already distinguishes are reported correctly.
5. Discovery must degrade to "could not determine" or "not published" rather than fail when the service account cannot read `cn=config` or the overlay configuration, and the panel must say which it was.
6. The policy panel and the Set Password dialog's policy summary must say "the directory applies further checks JIM cannot see" when `pwdCheckQuality` (OpenLDAP) or a dictionary or syntax check beyond categories (389) is on, so that a refusal after a satisfied policy is explained.
7. The REST API and PowerShell surfaces that already return the discovered policy (`Get-JIMConnectedSystemPasswordPolicy` and its endpoint) must return the renamed signal under a neutral property name, with the old name kept as a deprecated alias for one release so that scripts do not break.
8. `PasswordPolicyReconciliation` must treat an unknown required character-class count from one system as "no constraint from that system", which is its behaviour for an absent policy today; this must be covered by a unit test.

### Non-Functional Requirements

- Discovery runs at the same points it does today (schema refresh, preflight before a password set); it must add no more than two searches per directory type.
- No new NuGet packages. The `System.DirectoryServices.Protocols` searches already in use are sufficient.
- British English throughout; the neutral vocabulary of `engineering/DEVELOPER_GUIDE.md` (Connected System Object, never "account").

## Examples and Scenarios

### Scenario 1: OpenLDAP with the ppolicy overlay

**Given**: a Connected System using the LDAP Connector against OpenLDAP whose default policy sets `pwdMinLength: 12`, `pwdInHistory: 5`, `pwdMaxAge: 7776000`
**When**: the administrator refreshes the schema, then opens the Password Policy panel
**Then**: the panel shows minimum length 12, history 5, maximum age 90 days, minimum age "can be changed straight away", character classes "not published by this directory", and no override warning where no entry carries `pwdPolicySubentry`

### Scenario 2: Generated initial password satisfies OpenLDAP

**Given**: Scenario 1, and an export Synchronisation Rule with Initial Password on and Password Settings following the discovered policy
**When**: the rule provisions ten new Connected System Objects
**Then**: every generated password is at least 12 characters, every object receives its password on the first export run, and none is parked

### Scenario 3: 389 Directory Server with syntax checking

**Given**: 389 Directory Server with `passwordCheckSyntax: on`, `passwordMinLength: 10`, `passwordMinCategories: 3`
**When**: the policy is discovered
**Then**: the panel shows minimum length 10 and "3 of 5 character classes required", and the generator produces passwords drawing on at least three of upper, lower, digit, special and 8-bit

### Scenario 4: Service account cannot read cn=config

**Given**: 389 Directory Server, and a service account without read rights on `cn=config`
**When**: the policy is discovered
**Then**: the panel says "JIM could not read this directory's password policy: the account it connects as cannot read the server configuration", the override signal is "could not determine", and a Set Password from the portal still proceeds with the target as the only arbiter, as it does today when nothing was discovered

### Scenario 5: Generic directory

**Given**: an RFC 4512 directory JIM does not recognise
**When**: the administrator opens the Password Policy panel
**Then**: it says "This directory publishes no password policy that JIM can read", and `SupportsPasswordPolicyDiscovery` is false for the Connected System

## Constraints

- Air-gapped: no external lookups; everything is read from the directory.
- Backward compatible: existing discovered Active Directory policies keep their stored shape; the signal rename is a column rename with a migration, not a data change.
- No change to how passwords are set, and no change to the parking rules.

## Affected Areas

| Area | Impact |
|------|--------|
| Connectors | `LdapConnectorEnums` (new directory type), `LdapConnectorRootDse` (detection), `LdapConnectorPasswordPolicy` (per-type readers and override probes), `LdapConnector.SupportsPasswordPolicyDiscovery` |
| Models | `FineGrainedPolicySignal` renamed to a neutral name; `ConnectedSystemPasswordPolicy` gains a "further checks apply" flag and a "why nothing was read" reason |
| Database | migration for the renamed column and the two new fields |
| Application | `PasswordPolicyReconciliation` handles an unknown character-class count (test only, if the behaviour already holds) |
| API | policy DTO gains the neutral property name, old name kept as a deprecated alias |
| PowerShell | `Get-JIMConnectedSystemPasswordPolicy` output shape updated and documented |
| UI | `ConnectedSystemPasswordPolicyPanel` wording per directory type; Set Password dialog policy summary |
| Integration tests | an OpenLDAP scenario with the overlay configured; the `Build-OpenLDAPSnapshots.ps1` image gains the overlay |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/concepts/passwords.md` | "Discovering the target's rules" section: which directories publish what, and the override signal per directory |
| `docs/connectors/jim-ldap-connector.md` | Password policy discovery per directory type; the rights the service account needs (`cn=config` for 389, overlay config for OpenLDAP) |
| `docs/powershell/connected-systems.md` | `Get-JIMConnectedSystemPasswordPolicy` output shape |
| `docs/api/index.md` | Deprecated alias note if the DTO property is renamed |

## Dependencies

- Integration test infrastructure must run OpenLDAP with the `ppolicy` overlay; `Build-OpenLDAPSnapshots.ps1` currently does not configure it. A 389 Directory Server image is not in the harness; Scenario 3 may be covered by unit tests against a mocked executor until one is added.

## Open Questions

1. Should 389 Directory Server be a first-class `LdapDirectoryType`, or should discovery probe for its policy attributes under Generic? A first-class type is proposed, because delta import and export tuning will also want to know.
2. Is a one-release deprecated alias on the API property worth carrying, or is the signal obscure enough that a straight rename is acceptable? The PRD assumes the alias.

## Acceptance Criteria

- [ ] OpenLDAP with the `ppolicy` overlay shows a discovered policy on the Password Policy panel (Scenario 1), verified by an integration scenario.
- [ ] Generated initial passwords into that OpenLDAP satisfy its minimum length and none are parked (Scenario 2), verified by the same scenario.
- [ ] 389 Directory Server policy mapping, including `passwordMinCategories`, is covered by unit tests against a mocked executor (Scenario 3).
- [ ] A service account without rights on the policy source yields "could not determine" and a panel explanation naming the cause (Scenario 4).
- [ ] A Generic directory reports `SupportsPasswordPolicyDiscovery = false` and the panel says no policy is published (Scenario 5).
- [ ] The override signal has a directory-neutral name across model, database, API, PowerShell and portal, and no product surface names Active Directory except where the directory in front of it is Active Directory.
- [ ] Reconciliation across a system with an unknown character-class count and one with a known count produces the known count (unit test).
- [ ] Docs listed above updated in the same PR.

## Additional Context

- The wording that presumed a domain on the policy panel was neutralised in [#1699](https://github.com/TetronIO/JIM/pull/1699); this PRD is the behavioural follow-up.
- OpenLDAP password policy: the `ppolicy` overlay (draft-behera-ldap-password-policy), attributes `pwdMinLength`, `pwdInHistory`, `pwdMaxAge`, `pwdMinAge`, `pwdCheckQuality`, `pwdPolicySubentry`.
- 389 Directory Server password policy: global settings on `cn=config` (`passwordMinLength`, `passwordInHistory`, `passwordMaxAge`, `passwordMinAge`, `passwordCheckSyntax`, `passwordMinCategories`), subtree and user policies via `nsPwPolicyContainer` and `pwdpolicysubentry`.
