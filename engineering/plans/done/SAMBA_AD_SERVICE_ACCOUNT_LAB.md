# A Delegated JIM Service Account for the Samba AD Integration Lab

- **Status:** Done
- **Issue:** [#1716](https://github.com/TetronIO/JIM/issues/1716)
- **Related:** [#1715](https://github.com/TetronIO/JIM/issues/1715) the same work for the OpenLDAP lab, done first
- **Created:** 2026-09-20

## Overview

Every Samba AD integration scenario connected JIM's Connected Systems as `CN=Administrator,CN=Users,<domain DN>`, a Domain Admin. That identity holds every right in the domain, so the scenarios proved JIM's synchronisation logic but never proved it works within the delegation a customer would grant. The LDAP Connector documentation describes that delegation in prose, offers nothing concrete to apply, and nothing in the lab checked the prose was right.

This work is the sibling of the OpenLDAP service account lab (#1715): each Samba AD domain image gains `CN=svc-jim,OU=Services,<domain DN>`, a member of `CN=JIM Connectors,OU=Services,<domain DN>`, and the group holds an explicit, versioned set of access control entries written against JIM's actual directory operations. The Connected Systems bind as that account; `samba-tool`, population, assertions, snapshot verification and the Compose healthcheck deliberately stay on the Administrator credential. The access control entries are published verbatim in the LDAP Connector documentation, so the lab runs what customers are told to apply.

## Business Value

- Every Samba AD scenario now proves JIM works under a delegation a directory team would actually grant, rather than under an identity that bypasses access control.
- The delegation customers are told to apply is the delegation the lab runs under; the two cannot drift apart.
- It found a defect only a delegated account can find (see Findings): a Delta Import silently imports no deletions when the account cannot read the Deleted Objects container.

## Resolved design decisions

| # | Decision | Resolution |
|---|---|---|
| D1 | Two identities | Unchanged from the OpenLDAP lab. `BindDN`/`BindPassword` stays the domain Administrator and populates data, asserts against the directory and serves the healthcheck. `JimBindDN`/`JimBindPassword` is what JIM's Connected Systems bind as, and becomes `CN=svc-jim,OU=Services,<domain DN>` with password `Svc-Jim@123!`. |
| D2 | Delegate to a group, not to the account | `CN=JIM Connectors,OU=Services,<domain DN>` holds every grant; `svc-jim` is a member. This is what a customer should do (the account can be replaced without redoing the delegation) and it matches the OpenLDAP lab's `cn=jim` group. |
| D3 | One source for the entries | `test/integration/docker/samba-ad-prebuilt/delegation/jim-ad-delegation.acl` holds the SDDL access control entries with the trustee as a placeholder, commented in JIM's terms. `jim-delegate.sh`, baked into every image, applies them over LDAP as the domain administrator (what `dsacls` does on Windows), and the documentation includes the same file verbatim. |
| D4 | Where the delegation goes | On the container at the top of each managed branch, inheritable, so objects and containers created later are covered. The image bakes `OU=Corp`, `OU=TestUsers` and `OU=TestGroups`; containers the lab creates at run time (Scenario 8's `OU=CorpManaged`, Scenario 2's `OU=TestUsers`) are delegated at creation through `Grant-JimAdDelegation`. |
| D5 | Deleted Objects container | Granted (List Contents, Read Property, and Read Permissions so the account can read the container's own permissions), because without it a Delta Import silently misses every deletion. Reaching it needs ownership first, exactly as `dsacls /takeOwnership` does on Windows: the default entries do not even let a domain administrator read the container's permissions. |
| D6 | Password Settings Container | Not granted, deliberately. It is optional for customers and leaving it ungranted is the common case: JIM then reports that it could not tell whether Fine-Grained Password Policies exist, which is the behaviour worth exercising. |
| D7 | Gate | Every Samba AD capable scenario passes at its smallest template under the delegated account, plus one OpenLDAP scenario to prove the plumbing change is inert there. An insufficient-access failure is a finding about the delegation or about JIM, never something to fix by granting Domain Admin. |

## What JIM does to the directory, and what the delegation must carry

| JIM operation | What it needs |
|---|---|
| Bind, rootDSE reads, schema, Configuration naming context (domain controller discovery and pinning) | Nothing: the domain's default read for authenticated accounts covers it |
| Import and Delta Import of the managed containers, including `uSNChanged` | Read over the containers (granted explicitly rather than relying on the default) |
| Delta Import deletions | Read over `CN=Deleted Objects,<domain DN>`, which nothing inherits |
| Export: create, modify, rename, move, delete users and groups | Create and delete child objects on the container, write over descendant objects |
| Container creation ("Create Containers as Needed") | Create child organisationalUnit objects; optional for customers who never let JIM create containers |
| Password writes | The Reset Password control access right, which is separate from attribute write access |
| Reset-rights preflight | Read Control over the objects, read with the security descriptor flags control |
| Password policy | The domain root's policy attributes (default read); the Password Settings Container is optional |

## Findings

1. **A Delta Import silently imports no deletions when the account cannot read the Deleted Objects container.** Active Directory answers the tombstone search with success and no rows rather than refusing it, so JIM reports nothing wrong and the deletions never arrive. Proven on the lab's domain controller with a delegated account. The lab grants the read and the documentation now names it as a requirement; JIM itself still cannot tell the two apart, which is filed separately.
2. The reset-rights preflight works for a delegated account, but only because it asks with the security descriptor flags control: a plain read of `nTSecurityDescriptor` returns nothing even where Read Control is granted.
3. The default entries on the Deleted Objects container grant the administrators group List Contents and Read Property only, not Read Control, so even the domain administrator cannot read the container's permissions until it has taken ownership. An earlier reading of that failure as "the container does not exist until the first deletion" was wrong and was reverted.

## Deviations

- The Deleted Objects grant is `LCRPRC`, not the `LCRP` first planned: Read Control is what lets the account read the container's own permissions, and #1723 (built on top of this work) uses exactly that to tell a missing grant from an empty container.
- The "Samba creates the Deleted Objects container on the first deletion" step was a misreading of a Read Control failure and was removed before the images shipped.
- The sweep found no insufficient-access failure anywhere: every Samba AD capable scenario (1, 2, 4 to 13, 17, 18, 20, 21) passed at Nano under `svc-jim` on 2026-09-20; Scenario 3 is a placeholder with no test logic and was not counted.

## Success Criteria

- Every Samba AD image carries `svc-jim`, the group and the delegation, and fails its own build if any part of it does not work.
- Every Samba AD capable scenario passes at its smallest template with JIM bound as `svc-jim`.
- `docs/connectors/jim-ldap-connector.md` publishes the same access control entries the lab applies.
- The shipped delegation produces no deletion-detection finding from JIM (#1723): Schema Discovery preview clean, Delta Import "Complete" and a directory deletion arrives as an Obsolete Connected System Object. The same account with the group's entry removed from the Deleted Objects container produces the "could not confirm" warning at both, and with Read Control alone the Delta Import refuses to run. Verified against `samba-ad-primary` on 2026-09-20.

## Risks and Mitigations

- **Every Samba AD image and snapshot rebuilds**, because the build hash covers the new files. Expected, once, and slow on the first run of each template.
- **A container created at run time and not delegated** fails at export with an access error. Mitigated by delegating at creation and by the inheritance in D4; the sweep is what proves it.
- **Over-granting hides the defects this exists to find.** The image build fails if `svc-jim` can write outside its containers or is a member of Domain Admins.
