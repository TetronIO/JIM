---
title: LDAP Connector Domain Controllers and Containers
description: "How the LDAP Connector finds and pins a domain controller, follows referrals across a forest, and which containers it imports."
type: object
audience: administrator
---

# LDAP Connector Domain Controllers and Containers

Where JIM reads a directory from, and which part of it, matter as much as how. This page covers finding and pinning a domain controller, multi-domain forests and referrals, Container Scope and excluding a container, and tuning OpenLDAP for large groups.

## :material-magnify: Discovering Domain Controllers

Administrators often do not know which domain controller to enter in Preferred Domain Controller. Rather than typing a hostname blind, use the **Discover...** action beside the field on the Connected System's settings page: it lists every domain controller in the forest, with the Active Directory Site each belongs to, and clicking one fills the field with your choice. Read-only domain controllers appear in the list and are not marked as such. Choose a writable one: JIM exports to the domain controller it connects to, and a read-only one cannot accept the changes.

Discovery only ever informs; it never writes to the setting on its own. Preferred Domain Controller remains ordinary free text throughout, and nothing changes until you click a discovered server (or type a value yourself) and save the Connected System's settings.

The action is enabled once the connectivity settings above (Host, Port, Username, Password) are filled in; you do not need to have saved them first, so a system can be configured and its domain controllers discovered in one sitting. Discovery is only supported for Active Directory and Samba AD; for OpenLDAP, 389 Directory Server or Generic directories, which have no concept of Sites, the dialog reports that discovery is not supported and you can type a hostname instead. Discovery works by querying the forest's `CN=Sites,CN=Configuration` hierarchy, so it uses the same credentials already configured for this Connected System and needs no extra directory permissions or DNS lookups. If the directory cannot be reached, or the credentials are refused, the dialog shows the failure with a Retry action rather than crashing the page.

The same discovery is available beyond the portal:

```powershell title="PowerShell"
Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId 3
```

```http title="REST API"
GET /api/v1/synchronisation/connected-systems/3/directory-servers
```

See [`Get-JIMConnectedSystemDirectoryServer`](../powershell/partitions-and-containers.md#get-jimconnectedsystemdirectoryserver) in the PowerShell reference.

## :material-pin-outline: Domain Controller Discovery and Pinning

For Active Directory and Samba AD, JIM connects to a single, consistent domain controller rather than reconnecting via whatever the Host setting happens to resolve to each time. This matters for two reasons:

- **Replication consistency.** If Host is a domain name that DNS round-robins across multiple domain controllers, an export could write to one domain controller while the confirming import reads from another before replication catches up, making confirmed objects appear temporarily missing.
- **Delta import correctness.** USNs are scoped to the domain controller that issued them, so a Delta Import against a different domain controller than the one that produced the persisted watermark can silently skip or re-import changes. See [Delta import fails with a domain controller mismatch error](jim-ldap-connector-troubleshooting.md#delta-import-fails-with-a-domain-controller-mismatch-error) below.

**How it works:** leave Preferred Domain Controller blank and JIM auto-discovers. On the first connection, JIM connects via Host, reads the domain controller it reached from the directory's rootDSE, and pins every subsequent connection, within a run and across Run Profile executions, to that same domain controller (by FQDN, not IP; this also keeps LDAPS certificate name validation working, since the pinned name is the one the certificate's SAN needs to match). Setting Preferred Domain Controller to a specific FQDN always takes priority over any pin.

**If the discovered domain controller cannot be reached by name:** JIM does not pin it. Before pinning a domain controller it has just discovered, JIM opens a throwaway connection to that name to prove it works; a directory's advertised name is not guaranteed to resolve from wherever JIM runs, which happens with split-horizon DNS, with a directory reached through an alias or an address, and in DMZ deployments. Where the check fails, the run continues on the connection that already works (Host, or Preferred Domain Controller), nothing is pinned, and the Activity carries a warning naming the domain controller JIM could not reach. Nothing breaks, but Delta Imports do not get the domain controller affinity a pin buys them, so it is worth fixing: either make the name resolve from the JIM host, or set Preferred Domain Controller to a name that does. The check costs one connection, and only when the name being considered is not the one JIM is already connected through, so an established pin re-affirms itself for free on every run.

**If the pinned domain controller becomes unavailable:** the Run Profile execution fails outright rather than silently failing over mid-run, and the pin is cleared. The next Run Profile execution resolves via Host again, discovers whichever domain controller answers, and re-pins to it. Because that may be a different domain controller than before, a Full Import is needed to re-establish the Delta Import baseline; see [Delta import fails with a domain controller mismatch error](jim-ldap-connector-troubleshooting.md#delta-import-fails-with-a-domain-controller-mismatch-error).

Pinning only applies to Active Directory and Samba AD; OpenLDAP, 389 Directory Server and generic directories are unaffected.

## :material-sitemap-outline: Multi-domain forests

A Connected System manages one domain today. During Partition discovery on Active Directory or Samba AD, JIM lists every domain in the forest, because that is what the directory's crossRef objects expose; it has no way to tell from that list alone which domains the connected domain controller actually holds. A domain controller only ever holds its own domain's naming context and does not chase referrals to serve objects from another domain in the forest.

If you select a Partition for a domain the connected domain controller does not host, the import fails fast with an error naming the Partition and the domain controller, rather than silently returning zero objects. To manage more than one domain, create a separate Connected System per domain, each with its Host setting pointing at that domain's own domain controllers.

## :material-arrow-decision-outline: Referrals

**JIM does not follow LDAP referrals.** A referral is the directory's way of saying "the objects you asked for live on another server"; JIM ignores it and works only with what the connected server returns directly.

This is deliberate. The platform LDAP client will follow a referral, but it does so on a new connection that carries none of the credentials JIM bound with, making the follow-up read anonymous. Active Directory refuses anonymous reads by default, so a chased referral fails, and it fails against the original search rather than against the referral. The result is an access-denied error on a connection that is authenticated and healthy, which is a considerably worse outcome than not following the referral at all.

In practice this affects Partition discovery on Active Directory and Samba AD, where the search for the forest's domain list can return referrals alongside its results. JIM takes the results and discards the referrals.

**What to do instead:** connect a Connected System to each directory server whose objects you need, as described under [Multi-domain forests](#multi-domain-forests) above. If a server has no network path from the JIM host and is only reachable by referral from a server that does, JIM cannot manage its objects.

## :material-folder-outline: Container Scope

Each selected Container carries a scope, set with the two-segment control on its row in the Container tree on the Connected System's **Scope** tab:

| Scope | What is imported | Containers beneath it |
|-------|------------------|-----------------------|
| **Whole subtree** (default) | Objects in the Container and in every Container beneath it. | Covered automatically; they cannot be selected separately, and each says which Container covers it. |
| **This level** | Objects held directly in the Container. | Not imported. Each can be selected in its own right, with its own scope. |

Whole subtree is the default and is how Container selection has always behaved, so existing Connected Systems are unaffected.

**This level** is for directories where a branch holds a mixture you do not want wholesale. Selecting `OU=Corp` with **This level**, then selecting `OU=Sales,OU=Corp` with **Whole subtree**, imports the users sitting directly in `OU=Corp` and everything under `OU=Sales`, while leaving the rest of `OU=Corp`'s sub-OUs alone.

Containers are listed by their own name rather than by their full Distinguished Name, which is shown on hover. Use the filter above the tree to find a Container in a large directory; it matches both the name and the Distinguished Name, so a Distinguished Name pasted from elsewhere finds its Container. A hierarchy of more than a couple of dozen Containers opens only as far as its current selections, so what the system imports is visible without scrolling through everything it does not.

!!! warning "Narrowing a Container takes objects out of scope"
    Changing a Container from **Whole subtree** to **This level** stops the objects beneath it being imported. JIM asks you to acknowledge this before saving, because the Connected System Objects already imported from those Containers become obsolete on the next Import Run Profile, and whatever they are joined to is deprovisioned on the next synchronisation. Re-selecting the Containers you still want, before running an import, avoids that.

Scope is also settable from the REST API (`PUT /api/v1/synchronisation/connected-systems/{id}/containers/{containerId}`) and from PowerShell with [`Set-JIMConnectedSystemContainer -Scope`](../powershell/partitions-and-containers.md#set-jimconnectedsystemcontainer).

!!! note "Objects that reference something out of scope"
    A reference attribute pointing at an object outside the imported Containers cannot be resolved, and JIM reports it as an unresolved reference naming Container Scope as the likely cause. Narrowing a Container is a common way to create these; if a group's members live in a Container you have just excluded, either bring that Container back into scope or expect the membership to import incompletely.

## :material-folder-cancel-outline: Excluding a Container

Service Account, mailbox-archive and staging OUs sitting inside an otherwise wholly-managed branch are the ordinary shape of a production directory. **Exclude** carves one of those out of a selection made above it: select `OU=Corp` as a whole subtree, then exclude `OU=Service Accounts,OU=Corp`, and JIM imports everything in `OU=Corp` except that branch.

The action appears on a Container's row in the tree wherever a selection above already reaches it, which is exactly where an exclusion means anything. An excluded Container reads **Excluded from *X***, naming the selection it was carved out of, and offers **Include** to hand it back. Containers beneath it read **Excluded by *X*** and are left unimported too.

**Whichever statement is nearest to an object decides its fate.** That is what makes re-inclusion work: tick a Container inside an excluded branch and it comes back into scope, along with everything beneath it, while the rest of the exclusion stands. Exclusions and re-inclusions nest to any depth.

Excluding is not the same as selecting the siblings you want. Ticking eleven of twelve sibling OUs looks equivalent, and is silently wrong over time: an OU created under the parent afterwards is not in the enumerated set, so its objects are never imported and nothing says so. An exclusion has the opposite and safer failure mode, because the parent is what was selected: a new OU beneath it is imported.

!!! warning "Excluding a Container takes objects out of scope"
    Objects already imported from an excluded branch become obsolete on the next Import Run Profile, and whatever they are joined to is deprovisioned on the next synchronisation, exactly as narrowing a Container does. Preview the change before saving.

An exclusion is honoured everywhere the selection is: on Full Import, on the delta paths, and on export, where a write into an excluded branch is refused for the same reason it is refused outside the selected Containers entirely.

It is enforced as entries arrive rather than by searching around the branch, so an exclusion inside a selected branch costs a transfer that produces nothing. **That cost is reported rather than hidden.** An import that discarded entries carries an **Entries Discarded by Container Scope** panel on its Activity, breaking the figure down per excluded Container, and the same counts are in the run's log. A branch of 500,000 objects carved out of a 510,000-object parent then shows up as a number you can act on, by moving the excluded branch outside the selected one, rather than as an unexplained slow import.

!!! note "Why not just search around the excluded branch?"
    JIM could replace one subtree search with a search per sibling and skip the excluded one, and it deliberately does not. The set of siblings comes from the last **Retrieve Hierarchy**, so a Container created since would be missing from it: its objects would never be searched, never imported, and marked obsolete on the next Full Import. Import scope must not depend on how recently the hierarchy was refreshed, so the transfer cost is accepted and reported instead.

An exclusion survives a rename or a move of the Container, because it is keyed on the directory's own immutable identifier (`objectGUID` on Active Directory, `entryUUID` on OpenLDAP) rather than on the Distinguished Name.

A Container can be selected or excluded, never both. An exclusion beneath a **This level** selection is inert, since such a selection reaches no Container beneath it, and the tree therefore never offers one there.

Exclusions are settable from the REST API (`PUT /api/v1/synchronisation/connected-systems/{id}/containers/{containerId}` with `excluded`) and from PowerShell with [`Set-JIMConnectedSystemContainer -Excluded`](../powershell/partitions-and-containers.md#set-jimconnectedsystemcontainer), and previewable before they are made with `excludedContainerIds` on the scope-selection preview endpoint or [`New-JIMConfigurationChangePreview -ExcludedContainerIds`](../powershell/previews.md#new-jimconfigurationchangepreview).

A whole scope of selections and exclusions can also be stated at once as text, which is how a directory with hundreds of Containers is practically managed:

```text
include OU=Corp,DC=example,DC=com
exclude OU=Service Accounts,OU=Corp,DC=example,DC=com
include OU=App1,OU=Service Accounts,OU=Corp,DC=example,DC=com
```

See [Stating Container Scope as text](../configuration/partitions-and-containers.md#stating-container-scope-as-text-advanced-mode) for the full syntax, the portal's **Advanced** mode, and the [`Get-`](../powershell/partitions-and-containers.md#get-jimconnectedsystemcontainerscopetext) and [`Set-JIMConnectedSystemContainerScopeText`](../powershell/partitions-and-containers.md#set-jimconnectedsystemcontainerscopetext) cmdlets.

## :material-tune-variant: Directory Tuning for Large Groups (OpenLDAP)

When provisioning groups with very large memberships (tens of thousands of members and up) to OpenLDAP, the directory's own write path becomes the bottleneck: each membership modification makes slapd duplicate-check the new values against every existing value with a linear scan, so the cost of appending members grows with the group's current size.

OpenLDAP's `sortvals` directive addresses this by storing the values of the listed attributes in sorted order, turning the duplicate check into a binary search:

```text
# slapd.conf
sortvals member

# or cn=config (on the frontend database entry)
dn: olcDatabase={-1}frontend,cn=config
changetype: modify
add: olcSortVals
olcSortVals: member
```

JIM's own large-scale integration testing (up to 500,000 users, with individual groups of up to 495,000 members) runs OpenLDAP with `sortvals member` enabled, and we recommend it for any deployment where large group memberships are provisioned. Note that `sortvals` only affects entries written after it is enabled; enable it before loading data, or reload existing data (`slapcat`/`slapadd`) afterwards. See the [OpenLDAP tuning guide](https://www.openldap.org/doc/admin26/tuning.html) and the `slapd.conf(5)` man page for details.
