# Changelog

All notable changes to JIM (Junctional Identity Manager) will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- 🐛 An object that leaves scope but keeps its join is now recorded as **Left scope, join kept**, naming its Synchronisation Rule, rather than as an Attribute Flow that never happened and inflated the Activity's Attribute Flows count. (#1649)
- 🐛 A Synchronisation Rule or Attribute Flow disabled with a reason (as a schema refresh's "Apply and Disable Dependents" does), or re-enabled afterwards, is now classified in the configuration change history instead of being recorded without a classification. (#1753)

## [0.15.0] - 2026-09-23

### Added

- ✨ An info icon beside key terms such as Projection, Join, Connector Space and Pending Export explains each one where you first meet it, with a link to the glossary. (#1670)
- ✨ **Service Health** on Administration > Operations shows whether the Worker and Scheduler are healthy, what each is doing and which version it runs, with a portal-wide banner if one stops or stalls; also via REST and `Get-JIMServiceHealth`. (#1635)
- ✨ Run Profile Safeguards cap how many creates, updates and deletes an Export may attempt, and how many deletions a Full Import may detect, so a broken filter or mistaken rule change warns instead of making mass changes. (#1618)
- ✨ A new **Data Flow** view under Administration > Schema lists every Attribute Flow across all Connected Systems in both directions, showing where each attribute's value comes from and what writes it out; also via REST and `Get-JIMDataFlow`. (#1199)
- ✨ Data Generation Templates can now be created, updated and deleted through the REST API and PowerShell, validated as a whole before anything is saved; built-in templates stay protected. (#894)
- ✨ The Schema **Object Types** and **Attributes** tabs can now be filtered, by Deletion Rule, data type, plurality, built-in status and Metaverse Object Type.
- ✨ A Connected System's details page shows a **Directory Capabilities** card with what JIM has detected about an LDAP directory, such as its type, vendor, paging support and pinned domain controller; also via REST and PowerShell. (#231)
- ✨ The Synchronisation Rules list can now be filtered by Connected System, direction, action and status alongside the search box, with the same filters on the REST API and `Get-JIMSyncRule`.
- ✨ The Activity history can now be filtered from the REST API and `Get-JIMActivity` by operation, outcome, status, initiator, date range, Connected System, Run Profile and Schedule, with combined filters matching the portal's results exactly.
- ✨ Deleting Metaverse Objects when an authoritative source disconnects can now wait until **all** selected sources have gone (the new default), so one source system failing or being rebuilt cannot trigger deletions. (#119)
- ✨ The LDAP Connector now pins connections to a single Active Directory or Samba AD domain controller, avoiding the replication-lag and Delta Import risks of reaching a different one each run; a Preferred Domain Controller can be named instead. (#230)
- ✨ A **Discover...** action beside the LDAP Connector's Preferred Domain Controller lists every domain controller in the forest with its Site, in the portal, REST API and PowerShell (`Get-JIMConnectedSystemDirectoryServer`). (#1167)
- ✨ A directory's own configuration and operational object classes, such as OpenLDAP's `cn=config` classes and 389 Directory Server's console classes, are now hidden on a Connected System's Schema tab; **Show internal object types** reveals them. (#434, #1745)
- ✨ JIM now warns when a **When Last Connector Disconnected** Deletion Rule will keep Metaverse Objects alive because provisioned objects still count as connectors, and Activities record the values preserved at each disconnection. (#1570)
- ✨ Deleting a Connected System now offers **Deprovision through synchronisation** (the default), processing every object as a normal disconnection as a monitored, resumable operation, or immediate deletion that keeps contributed data. (#809)
- ✨ Deleting a Synchronisation Rule or Attribute Flow that contributed Metaverse values now asks whether to recall them (the default, letting surviving contributors take over by Attribute Priority) or keep them; rule recalls run as a monitored background operation. (#1533, #1537)
- ✨ `Remove-JIMSyncRule -Wait` blocks until a Synchronisation Rule's value recall has finished and the rule has gone, so scripts no longer race the background deletion; a recall that fails or times out is reported as an error. (#1597)
- ✨ After a Connector Space clear and re-import, the next Full Synchronisation applies each type's Deletion Rule to objects that did not return, honouring grace periods, and refuses if far fewer objects returned than were cleared. (#1605)

#### Sync Preview

- ✨ **Sync Preview** shows what synchronising a Connected System Object or Metaverse Object would do, including whether a Metaverse Object would be deleted and which downstream objects would be deprovisioned, without changing anything. (#1519)
- ✨ A Metaverse Object's new **Connections** tab lists every joined Connected System Object with its role, join type and State; the same detail is returned by the REST API and `Get-JIMMetaverseObject`. (#1519)
- ✨ The Connector Space list now shows each object's **State** (In sync, Update pending, Export failed, Obsolete and more), also on the REST API and `Get-JIMConnectedSystemObject`. (#1519)
- ✨ Attribute value changes now record which Synchronisation Rule contributed each value, across change history and Pending Exports, in the portal, REST API and PowerShell. (#1519)

#### Configuration Change Preview

- ✨ A configuration change can now be previewed before it is saved, starting with a Metaverse Object Type's deletion settings: see which Metaverse Objects would become, or stop being, eligible for deletion, and drill into them. (#827, #1114)
- ✨ Saving a configuration change that affects synchronisation now confirms what changed, with before and after values and a plain statement of anything that could delete or disconnect objects; cosmetic edits save without a prompt. (#827)
- ✨ Connected Systems now show when configuration changes are waiting on a Full Synchronisation to take effect, with a distinct warning when one is destructive; also on the REST API and `Get-JIMConnectedSystem`. (#827)
- ✨ A Configuration Change Preview opens with a plain-English summary of what saving would do, worst consequence first, and names the kind of edit each row describes, such as a domain change, a container move or a case-only change. (#827, #1275)
- ✨ Connected System changes can now be previewed before saving: deselecting Object Types, attributes, partitions or Containers, and changing Object Matching Rules, each report which objects would stop importing, disconnect or join differently. (#1251, #1457, #1475)
- ✨ Synchronisation Rule changes can now be previewed before saving: Attribute Flow, Scoping Criteria, deprovisioning actions and the rule's behaviour switches each report which objects would be affected and how. (#1115, #1436, #1437, #1443, #1462)

#### Schema Refresh

- ✨ **Refresh Schema** now shows what changed (additions, removals and data type changes) before anything is applied, so you can apply or discard it; also via REST and `Import-JIMConnectedSystemSchema -Preview`. (#421)
- ✨ A destructive schema refresh now offers a clear choice: cancel, apply as-is, apply and disable the Synchronisation Rules and Attribute Flows it invalidates, or apply and remove them for a decommissioned Object Type or attribute. (#1485)

#### Attribute Flow

- ✨ An individual Attribute Flow can now be disabled (or created disabled) without touching the rest of its Synchronisation Rule, in the portal, REST API and PowerShell. (#1485, #1537)
- ✨ An Attribute Flow's settings, such as its Expression or Initial Export Only, can now be changed in place over the REST API and `Set-JIMSyncRuleMapping`, keeping its Attribute Priority position. (#1361)
- ✨ An Expression can now choose what happens when an attribute it reads has no value: evaluate anyway (the default), contribute nothing, or fail the mapping or the object, so structurally broken values never flow. (#1361)
- ✨ An Expression can now be tested where it is written in the portal, with a box for each attribute it reads, so a malformed result is caught before the Synchronisation Rule is saved. (#1405)
- ✨ The Attribute Flow editor now shows where a new inbound mapping sits in its Metaverse Attribute's priority order, and **Null is a value** can be set there and via `New-JIMSyncRuleMapping`. (#1199)
- ✨ The Attribute Flow editor now suggests the Metaverse Attribute (or, for export, the Connected System attribute) a Standard Mapping pairs with your source, with one-click apply, and explains any suggestion it cannot apply. (#1122)

#### Partitions and Containers

- ✨ A selected Container can now import only the objects held directly in it (**This level only**) rather than its whole subtree, letting Containers beneath it carry their own scope. (#351)
- ✨ Each Container now shows how many objects it holds, read from the Connected System itself, so you can decide what to manage before the first import. (#1276)
- ✨ A Container can now be excluded from a selection made above it, with nesting and previews, and each import reports how many objects every exclusion removed. (#1255)
- ✨ Container Scope can now be edited as text, one statement per line, so it can be pasted, reviewed, kept under version control or copied between Connected Systems; also via PowerShell. (#1255)

#### LDAP Auxiliary Object Classes

- ✨ Auxiliary object classes on OpenLDAP and 389 Directory Server (such as `posixAccount`) can now be merged into an Object Type, making their attributes available to flow; JIM adds the class on export alongside the attributes that need it. (#492)
- ✨ Object Types defined by an auxiliary class can now be provisioned by naming the **Structural Carrier Class** to create them with. (#492)
- ✨ Auxiliary class discovery reports which classes a directory's entries actually carry, by quick sample or full scan, to guide which to merge; it changes no configuration. (#492)

#### SQL Connector

- ✨ The new SQL Connector synchronises with Microsoft SQL Server and Oracle Database tables and views, with Full and Delta Imports and exports, nothing native to install, and encrypted connections by default. (#170)
- ✨ An attribute can now be marked **Set on creation only**, so a table keyed on a natural identifier such as an employee number can be provisioned into without JIM ever rewriting the key. (#170)
- ✨ Oracle whole-number columns are now read as whole numbers, so they can flow into built-in numeric Metaverse Attributes such as Employee Number; refresh an existing Connected System's schema to adopt this. (#1354)
- ✨ The data type JIM inferred for a SQL or Oracle attribute can now be corrected per attribute on the Schema tab until the attribute is in use; also via REST and `Set-JIMConnectedSystemAttribute`. (#1354)

#### Certificates

- ✨ When an LDAPS connection fails because of the directory's certificate, JIM now shows that certificate, which check it failed and what to do, when testing settings and on the failed Activity. (#1132)
- ✨ A server's certificate can now be trusted straight from the failure that reported it, or fetched in advance from a Connected System's settings, after confirming its thumbprint; also via REST and PowerShell. (#1139)

#### Passwords

- ✨ Password Synchronisation now delivers: each change is queued encrypted per Connected System and sent by a dedicated Password Delivery Service within about a second, with retries, parking of refused changes, and only the newest password sent. (#1119, #1635)
- ✨ Password Synchronisation can now be configured per Connected System on a new Passwords tab, setting the target Object Type and retry behaviour, with a separate enable switch so it can be prepared ahead of a change window. (#1119)
- ✨ **Only send passwords over an encrypted connection** makes a Connected System refuse to send any password over a connection JIM cannot confirm is encrypted, leaving the work queued rather than warning and sending anyway. (#1119)
- ✨ REST endpoints that accept a password refuse requests unless the transport is confirmed encrypted, with guidance for TLS terminated at a reverse proxy JIM has not been told to trust. (#1119)
- ✨ **Set Password** resets a person's password on the accounts you choose (none selected by default), generated to satisfy the strictest policy among them, or propagates their own change to every system using Password Synchronisation. (#1119, #1172, #1635)
- ✨ An administrator can now set the password on a single account from its Connected System Object, typing one or generating one that meets the system's policy, with masked copy and every attempt recorded as an Activity. (#1121)
- ✨ Setting a password over the REST API or PowerShell (`-Wait`) can now wait up to 30 seconds and report what each Connected System did with it, so a service desk can confirm a reset has landed. (#1635)
- ✨ Automation can ask JIM to generate a password that satisfies each target system's discovered policy (`-Generate`), and read a system's policy with `Get-JIMConnectedSystemPasswordPolicy`. (#1121)
- ✨ A person's page now has an administrator-only **Password** tab showing what is still owed to each Connected System, with Retry, and what their recent password changes did on each. (#1119, #1635)
- ✨ A new **Passwords** tab on Administration > Operations lists every queued password change with the target system's own error, counts and filters, never the password itself; also via REST and `Get-JIMPendingPasswordChange`. (#1119, #1635)
- ✨ Queued password changes can be retried or cancelled, singly or in bulk, from the portal, REST API and PowerShell; a cancellation is recorded with who and when rather than silently deleted. (#1119)
- ✨ The Connected Systems list now shows each system's Password Synchronisation state with parked and expired counts, and a **Needs attention** filter. (#1119)
- ✨ Password Synchronisation history has its own retention period (a year by default), which also bounds how long JIM holds a password it cannot deliver; changes still owed are never removed. (#1119)
- ✨ A Synchronisation Rule now warns when an Attribute Flow targets an attribute whose name suggests a password, pointing you to Password Synchronisation instead. (#1119)
- ✨ Connected Systems that accept passwords show the password policy JIM read from the system and a safe, read-only **Check password channel** test covering encryption, mechanism, permissions and policy. (#1121)
- ✨ Password policy discovery now covers OpenLDAP (with the ppolicy overlay) and 389 Directory Server as well as Active Directory, so generated passwords meet each directory's own rules. (#1702)
- ✨ Newly provisioned accounts receive their initial password within seconds, owed for a window each Connected System sets; one the target refuses is parked until the Synchronisation Rule's initial password settings are corrected, and saving them retries it. (#1221, #1316, #1697)
- ✨ A Synchronisation Rule can now give every account it provisions one chosen initial password, so new starters can be told it; JIM advises against it, stores it encrypted and write-only, and requires a change at next sign-in by default. (#1273)
- ✨ The Synchronisation Rules list and each rule's **Passwords** tab now flag accounts parked or expired waiting on their initial password, grouped by what the target system said, and confirm how many a fix will release. (#1221)

#### Run Progress

- ✨ A Run Profile execution's Activity now shows its whole journey as a stepped progress bar, including the Connector's own steps, with durations for finished steps and the step a failed run stopped in. (#454)
- ✨ Imports now show how far through they are: file imports show a percentage and time remaining, and directory imports, including large Delta Imports, show counts and rate as objects arrive. (#454)
- ✨ Long-running File and LDAP Connector work now reports what it is doing on the Activity, so a healthy long phase can be told apart from a stuck run. (#637)
- ✨ The Operations queue now shows each running task's steps and progress, and each running Schedule as a step rail with every parallel task's outcome. (#1162)
- ✨ Automation sees the same steps: the REST API, `Start-JIMRunProfile -Wait`, `Get-JIMActivity -Follow`, `Get-JIMWorkerTask` and `Get-JIMScheduleExecution` report progress as "Step 3 of 7: Saving changes". (#454, #1162)

#### Causality

- ✨ A Run Profile Execution Item's Causality Tree is replaced by a redesigned causality panel: a plain-English summary above **Lineage**, **Timeline** and **Table** views, events in plain language, searchable attribute changes and links to every object involved. (#1087, #1495)
- ✨ The **Lineage** view, which opens first, traces an outcome from its source records through the Metaverse Object to its targets and back to the root cause, highlighting this run's events, and still reads correctly after the objects involved are deleted or renamed. (#1223, #1495)
- ✨ Export Run Profile Execution Items now trace their causal chain back through the synchronisation and import that led to each change, linking to each run along the way. (#1223)
- ✨ The **Table** view lists every change flat, filterable and sortable, grouped by the object each one touched. (#1519)
- ✨ Every card on the Lineage view, queued exports included, carries a coloured Created, Updated, Deleted or Joined marker, so an export states what it did rather than a bare "Exported". (#1495)
- ✨ The causality panel notes when a Metaverse Object was deleted after the run you are viewing, with a link to its deletion record, rather than leaving a dead end. (#1495)
- ✨ When a rejoin cancels a scheduled Metaverse Object deletion, the Lineage now records which Connected System rejoined and which Deletion Rule allowed it. (#1620)

#### SCIM 2.0 Client Connector (#545)

- ✨ JIM now synchronises with any system offering a SCIM 2.0 service provider interface, using one standards-based Connector: it reads the provider's schema, imports users and groups with their memberships, and exports changes back.
- ✨ Delta Imports request only what changed where the provider supports it, and exports send only what changed, preserving attributes JIM does not manage and guarding against overwriting changes JIM never saw.
- ✨ A SCIM connection refused over certificate trust now shows the certificate the provider presented, so you can verify its thumbprint and trust it under Admin > Certificates.
- ✨ Provider rate limits are respected: JIM honours `Retry-After`, backs off with jitter and paces itself, reporting throttling as a warning rather than failing the run.
- ✨ A schema import that had to work around gaps in what a provider publishes now says so, on the schema refresh summary and as a warning on its Activity.
- ✨ An optional **Use Bulk Operations** setting sends exports in batches to providers that support SCIM Bulk, considerably faster over high-latency links; it is off by default, and falls back to per-object requests if the provider does not serve it.

#### Schedule Execution visibility (#1196)

- ✨ The Schedules list now shows how each Schedule's last run ended, naming the step a failed run stopped on, and each Schedule's history button opens a list of all its executions.
- ✨ A new Schedule Execution view shows every step of a run with its outcome, duration and a link to its Activity, so a failed overnight run no longer has to be pieced together.
- ✨ Activities produced by a Schedule now link back to the run and step, and the Activity history can be filtered by Schedule, even after the Schedule is deleted.

### Changed

- 🔄 Delta Import against 389 Directory Server now refuses to run, and Schema Discovery warns, when the Retro Changelog plug-in is not recording deletions, naming the setting to turn on so no deletion is silently missed. (#1479)
- 🔄 An LDAP Delta Import with no change watermark to start from now performs a Full Import and completes with a warning, for every directory type and in line with the SQL and SCIM Connectors, instead of failing. (#1725)
- 🖥️ The Partitions & Containers tab has been redesigned around what you import: a summary of the selection, readable Container names, a filter, a clearer Container Scope control, and **Preview Changes** beside Save. (#351)
- 🖥️ In a Connector Space, a value still waiting on a Pending Export is shown muted and italic with a clock, and its tooltip says whether it is staged or exported and awaiting confirmation; the REST API and PowerShell gain `pendingExportStatus` to match.
- 🖥️ Page descriptions now open from an info button beside the page title rather than sitting as text beneath it, and alerts across the portal have a cleaner, borderless style.
- 🔄 The portal now speaks one vocabulary: Connected System Object and Metaverse Object are written in full, objects are named by type and Connected System ("user Jane Smith in Contoso AD"), and the causality panel uses the portal's own outcome names. (#1666, #1667, #1669)
- 🔄 An export no longer writes objects outside the Containers selected on a Connected System, where JIM cannot read them back; it fails with an error naming the Distinguished Name. **Behaviour change:** select any Container you deliberately export into before upgrading. (#827)
- 🔄 The Activity that deletes Metaverse Objects once their grace period ends is now called **Scheduled Metaverse Object Deletion** (formerly Metaverse Object Housekeeping); Activities recorded before this release keep their original name. (#1668)
- 🖥️ Every object the portal names, from Metaverse Objects to Synchronisation Rules and Pending Exports, now appears as the same linked chip everywhere, including the causality panel and Configuration Change Previews, with its external ID in the tooltip.
- 🔄 A Synchronisation Rule's Connected System, direction and Object Types now sit in a strip beneath the breadcrumbs, visible on every tab, with the arrow drawn the way data flows.
- 🔄 Clearing a Connector Space through the REST API or PowerShell now runs as a tracked background operation with a full audit trail, as it does in the portal; `Clear-JIMConnectedSystem` returns a tracking object and gains `-Wait`. (#1549)
- 🔄 An Object Matching Rule whose scope does not suit the Connected System's matching mode is now refused by the REST API and PowerShell rather than created silently inert, and switching matching mode warns about any rules it would strand. (#1569)
- 🔄 When a source disconnects, JIM now recalls its attribute values only while another source still stands behind the object; an object left with only provisioned target accounts keeps its last known values, protecting live accounts from a transient source outage. (#1570)
- 🖥️ A Connected System now shows how many of its objects are obsolete and waiting on a Synchronisation Run Profile, with a **Review** link, and the Connector Space can be filtered by the Obsolete status. (#1527)
- 🔄 A Synchronisation Rule now refuses a second Attribute Flow to an attribute it already flows to, which was accepted but never honoured; an existing duplicate is refused on its next save, so replace it with one `Coalesce` expression or a second Synchronisation Rule. (#1532)
- 🔄 History retention now runs as **History Retention Cleanup**, a built-in Schedule running daily at 02:30, so you can see when it last ran and what it removed, and re-time or pause it like any other Schedule. (#1118)
- 🔄 Built-in configuration added in a new release (Metaverse Object Types, Predefined Searches, Example Data Sets, Schedules and Roles) now reaches existing deployments on upgrade, leaving everything you have changed untouched. (#916)
- 🔄 A factory reset now restores all of JIM's built-in configuration, and re-applies read-only Service Settings (such as SSO endpoints) from the deployment's environment variables. (#916)
- 🔄 **Breaking:** REST API responses for Connector Definitions, Example Data Sets, Data Generation Templates and Predefined Searches are now purpose-built; scripts reading a Predefined Search's `metaverseObjectType` should read `metaverseObjectTypeName`. (#1447)
- 🔄 An outbound Synchronisation Rule can now write back into the Connected System an inbound rule reads from, so a derived value such as an email address reaches the system the data came from. (#1284)
- 🖥️ Lists and tables throughout the portal now scroll continuously instead of paging, showing their size beside the search box and searching and sorting across the whole list, so a list of eight hundred thousand objects reads as easily as one of eight.
- 🖥️ Scrolling table rows are now one line tall: a cell holding several values shows the first with **+n more** to open the rest, and long text shows in full on hover.
- 🔄 An account queued for removal now shows as **Deprovision queued** in the causality views rather than as an ordinary attribute export. (#1087)
- 🔄 **View deletion record** in the causality views now opens the deleted object's own change history, and Deleted Objects accepts bookmarkable `?mvo=<id>` and `?cso=<id>` links. (#1087)
- 🔄 A Pending Export in the causality views now links to that individual Pending Export rather than the Connected System's whole queue. (#1087)
- 🔄 Objects without a display name, such as LDAP groups, now show their common name or name throughout the portal instead of a raw identifier.
- 🔄 Search boxes across the portal now filter as you type; the query forms on Deleted Objects and Admin > Logs still run when you press Search. (#864)
- 🔄 The REST API now limits paging depth by rows retrieved (1,000,000) rather than page number, so far larger result sets can be paged through; every request accepted before is still accepted. (#487)
- 🔄 Error stack traces are now tucked behind a **Show stack trace** toggle wherever JIM reports an error, so the error message itself leads. (#1132)
- 🔄 A Delta Import against Active Directory or Samba AD now fails fast with a clear error if it reaches a different domain controller from the one its watermark came from; run a Full Import to re-establish the baseline. (#230)
- 🔄 A Connected System's **Connector Space** and **Pending Exports** now sit above its tabs with their counts, reachable from anywhere on its page. (#231)
- 🔄 A Metaverse Object reappearing during its deletion grace period now cancels the deletion only if it reverses the disconnection that triggered it. (#119)
- 🔄 Deletion decisions are now explained from facts recorded when they were made, on the Activity and the Pending Deletions page, so the explanation stays accurate after rules are edited. (#119)
- 🔄 **Breaking:** a successful Schedule Execution's status is now `Complete`, not `Completed`, matching Activities; update any REST or PowerShell script that filters on `Completed`. (#1196)
- 🖥️ A Run Profile execution item's detail page is now split into tabs, with any Pending Export the item created or failed on on its own tab.
- 🖥️ The Projection Details and Metaverse Impact sections have been retired from execution item pages; the causality panel now tells that story, including a **Metaverse Object not deleted** step giving the reason a Deletion Rule chose not to delete. (#1223)

### Removed

- 🗑️ The LDAP Connector's **Certificate Validation** setting has gone, and LDAPS certificates are now always validated; trust a directory's certificate in Admin > Certificates, and fix a name mismatch with a host entry rather than weakening validation. (#1132)

### Fixed

- 🐛 A Schedule that cannot start (for example, because one of its steps runs against a Connected System that is being deleted) now fails once per scheduled run and is tried again at its next run time, instead of failing again every few seconds until the cause is fixed. (#1765)
- 🐛 Delta Import from 389 Directory Server and other changelog-based directories now returns changes, applies deletions, follows renames, and no longer fails with "Duplicate external ID" when an object changed more than once since the last import. (#1479, #1725)
- 🐛 A Delta Import from OpenLDAP, 389 Directory Server or a generic LDAP directory now fails with a clear remedy when the account JIM connects as cannot read the accesslog or changelog, instead of completing with no changes; Schema Discovery warns of the same gap. (#1725)
- 🐛 A Delta Import from Active Directory or Samba AD now imports every deletion: it pages its search of the Deleted Objects container, and fails fast or warns when the account JIM connects as cannot list that container, instead of silently importing none. (#1723, #1724)
- 🐛 An import the directory stops at its search limit now refuses before importing anything from that container, naming the container and explaining that the account JIM connects as needs exempting from the directory's search limits.
- 🐛 The LDAP Connector's hierarchy import now skips, with a warning, any naming context the service account cannot read, rather than failing the whole import. (#1715)
- 🐛 A Full Synchronisation no longer loses a group's provisioning when the group is processed on an earlier page than its members; the group is now created with its members rather than failing at export.
- 🐛 Provisioning withdrawn before it was exported is now cancelled cleanly: JIM exports nothing and reports **Provisioning cancelled**, rather than failing a Delete export or later creating an account for someone out of scope.
- 🐛 Provisioning exported but not yet confirmed by an import is now handled correctly: later changes go as a single Update rather than a second Create, an unconfirmed Create is retried, and an object deprovisioned in the meantime is removed once its Delete is exported.
- 🐛 A synchronisation run no longer fails with a duplicate-key database error, or loses a scheduled deletion, when a Metaverse Object is scheduled for deletion or a joined Connected System Object falls out of scope partway through the run. (#1610)
- 🐛 Two outbound Synchronisation Rules targeting the same Connected System for one Metaverse Object no longer crash a synchronisation run with an unreported error; the clash is reported against the object and the run continues. (#1331)
- 🐛 Synchronisation runs no longer end with a warning for every correctly provisioned person when two outbound Synchronisation Rules with non-overlapping scopes export different Object Types to the same Connected System. (#1399)
- 🐛 An export whose reference cannot be resolved yet now writes everything else straight away and fills the reference in later, so an account whose manager is out of scope is still created, and a reference that can never resolve is reported. (#1398)
- 🐛 Import reference resolution now respects Object Types: two sharing anchor values no longer fail the import, an ambiguous reference is reported rather than guessed, and references to earlier-imported objects resolve whatever the anchor's data type. (#1285)
- 🐛 A deleted Connected System Object is now reported once, by the first import that finds it missing, rather than again on every import until a synchronisation runs, which inflated deletion totals and Causality. (#1527)
- 🐛 After a Connector Space is cleared and re-imported, the next Full Synchronisation now recalls the values its departed objects contributed, handing each attribute to a surviving contributor or clearing it, with the outcome reported on the Activity. (#1549)
- 🐛 A data type an administrator chose for a Connected System attribute now survives a schema refresh, instead of being silently reverted in a way that could write values to the wrong place on the Metaverse Object. (#1354)
- 🐛 Object Matching Rules added in the portal's Matching tab now name the Metaverse Object Type they search, so they match instead of silently projecting duplicate Metaverse Objects; rules that could never match are refused or flagged. (#1458)
- 🐛 Object Matching Rules are now kept and cleared correctly: saving an export Synchronisation Rule in advanced matching mode no longer wipes them, and clearing them deletes them rather than leaving hidden orphans that blocked deleting the Connected System. (#1589)
- 🐛 Switching a Connected System's matching mode now works with API key authentication, so `Switch-JIMMatchingMode` and the REST endpoint can be used from automation. (#1569)
- 🐛 Deleting a Synchronisation Rule, or removing one of its Attribute Flow mappings, now removes everything it owns rather than leaving hidden orphaned configuration behind; orphaned mappings from earlier removals are cleaned up on upgrade. (#1477, #1550)
- 🐛 A Connected System that imported a nested Container hierarchy (an OU inside an OU) can now be deleted instead of failing with a save error. (#1477)
- 🐛 Schedules now run on their cron trigger; a cron-triggered Schedule never fired on its own, and only running one by hand worked. (#1514)
- 🐛 The Worker's memory no longer grows with every task it runs, and its housekeeping now acts on Synchronisation Rule and Metaverse Object Type changes straight away, so an export rule switched from Delete to Disconnect stops deleting without a Worker restart.
- 🐛 A failed Worker task can no longer stop the Worker processing further queued operations until it is restarted. (#1568)
- 🐛 A Run Profile execution item's error heading now names the phase the problem happened in, so import errors a Connector reports, and some export errors, are no longer headed "Synchronisation Failed". (#1150)
- 🐛 An instance interrupted while creating its built-in configuration now recovers on the next start, and an upgraded instance now receives any built-in Connector added since it was installed. (#1287)
- 🐛 A factory reset now completes on deployments with SSO configured or custom configuration such as a Predefined Search's criteria, instead of failing with a 409 or foreign-key error. (#1477)
- 🐛 A finished import's Activity now summarises the whole run, with objects read, created, updated, errors and throughput, instead of showing "0 / 0" from its last internal step. (#170)
- 🐛 The causality view now nests Provisioning and Pending Export outcomes beneath the Attribute Flow that produced them, and an execution item's Attribute Flow count includes references resolved at the end of a batch. (#1428)
- 🐛 The Metaverse Object detail API now returns the object's joined Connected System Objects in its `connectedSystemObjects` field, which always came back empty. (#1606)
- 🐛 `Set-JIMApiKey` now updates an API Key with a single role, or no roles, without a validation error. (#1531)
- 🐛 PowerShell cmdlets that accept a name in place of an id now find it however long the list, rather than reporting "not found" for anything beyond the first page. (#894)
- 🐛 Client IP addresses from IPv4 connections are now recorded in plain IPv4 form on Authentication Activities and in logs, without the `::ffff:` prefix, so one client no longer occupies two rate limit buckets.
- 🐛 The home page's **Run your first synchronisation** step now ticks when any Run Profile is run, including by hand, not only when a Schedule fires. (#1482)
- 🐛 Button labels, chips and selected filter chips now meet WCAG AA contrast in every theme, including outlined and text buttons on the light themes and **Black Dark**. (#1495, #1527)
- 🐛 A long attribute value in a Change History card now ends in an ellipsis with the full value on hover, and uses the card's full width, instead of being cut off mid-value.
- 🐛 Several portal elements now read clearly on every theme: the Connector Space and Pending Exports button counts, the Created and Updated chips on hover, selected Object Types on the **Schema** tab, and selection highlights, which now take the theme's own accent.
- 🐛 The Pending Deletions summary cards, the REST API and `Get-JIMPendingDeletion -Summary` now count every object awaiting deletion rather than the first hundred, and honour the Metaverse Object Type filter.
- 🐛 **My Activity** now filters the list when chosen from the Activity page, the navigation no longer highlights both entries at once, and the **Initiator** filter is disabled on that view.
- 🐛 Returning to a stale sign-in page (a restored tab, the back button, or a refresh) now restarts sign-in cleanly and takes you where you were headed instead of showing an error page; every failed attempt is still recorded as a security Activity.
- 🐛 Partition discovery against Active Directory and Samba AD no longer fails with an access denial on a working, authenticated connection: JIM now declines LDAP referrals rather than following them anonymously. (#1352)
- 🐛 A Metaverse Attribute value is now credited to the Synchronisation Rule that wins Attribute Priority, so deleting a contributing Rule no longer leaves values unowned, and a lower-priority source can no longer overwrite the winner's value. (#1292)
- 🐛 Deleting an Attribute Flow from a Synchronisation Rule over the REST API or with `Remove-JIMSyncRuleMapping` now works; it previously failed every time.
- 🐛 Object Types anchored on a 64-bit whole number or a decimal now import correctly, with deletions detected and no duplicates created. Run a Full Import after upgrading to catch deletions missed while this was broken. (#1283)
- 🐛 An export no longer fails when Drift Correction and a fresh Metaverse value are staged against the same attribute of the same object; the new value now supersedes anything else staged for it. (#1199)
- 🐛 An import Synchronisation Rule's Scoping Criteria are now honoured when its Attribute Flows run, so one system can own a defined subset of objects while another holds the rest, and two Rules competing for a single-valued attribute no longer both write. (#1199)
- 🐛 Adding, removing or retargeting an inbound Attribute Flow in the portal now maintains the Metaverse Attribute's priority order, as the REST API and PowerShell already did. (#1199)
- 🐛 Delta Imports from OpenLDAP and other changelog-based directories now honour the selected Containers, so they no longer import objects a Full Import would never return. (#351)
- 🐛 The Connector Space now shows, searches and sorts on every Connected System Object's External Id, including Active Directory and Samba AD objects anchored on `objectGUID`. (#1286)
- 🐛 Containers discovered from directories other than Active Directory are now named after their leaf component (`Sales`) rather than their whole Distinguished Name; refresh the hierarchy to rename existing ones.
- 🐛 A Schedule Execution containing parallel steps now reports its true number of steps, so its progress reaches its own total in the portal and the REST API.
- 🐛 Deleting a Connected System that still had a queued Clear Connected System Objects task no longer empties the Operations queue.
- 🐛 A queued task whose Connected System or Run Profile has since been deleted now names what it was going to act on, rather than reporting "Run Profile not found!".
- 🐛 The Operations queue no longer opens a separate database connection for every row it displays, which on a busy queue added up to a connection per task per update.
- 🐛 A Connected System's settings can now be saved after changing a setting that controls which others apply: settings that no longer apply stop blocking **Save Settings** with leftover "is required" errors.
- 🐛 Saving a change while a Connected System's external system is temporarily unreachable no longer takes away its Schema, Partitions & Containers and Matching tabs.
- 🐛 Renaming or moving a Container in a directory no longer silently takes it out of import scope: JIM now tracks Containers by the directory's immutable identifier, so your selection survives. (#827)
- 🐛 A Container created in a directory since the last hierarchy refresh now appears on the **Partitions & Containers** tab, so it can be selected. (#827)
- 🐛 Deselecting a Partition now takes effect on every Run Profile, and a Run Profile left targeting a deselected or missing Partition is refused with a clear error and marked **Not selected** in the portal, REST API and PowerShell. (#827)
- 🐛 Problems a Connector reports with an individual imported object now appear on the Activity instead of being discarded, so an import of malformed data no longer finishes looking clean. (#637)
- 🐛 A Full Import no longer fails outright when one imported object names an Object Type missing from the schema; that object is reported and the rest import. (#637)
- 🐛 A setting withdrawn from a Connector no longer lingers on Connected Systems that already held a value for it. (#1132)
- 🐛 Saving an LDAPS Connected System's settings, or retrieving its schema or hierarchy, no longer hangs in the portal when certificates are present in **Admin > Certificates**. (#1132)
- 🐛 Retrieving or refreshing a Connected System's hierarchy from the portal no longer fails with a database error when it discovers a new Partition or Container.
- 🐛 A failed schema or hierarchy retrieval now finishes its Activity as failed with the reason, instead of leaving it in progress for ever.
- 🐛 Importing a schema through the REST API or PowerShell now auto-selects a Connected System's only Object Type, as the portal does, so every surface produces the same configuration.
- 🐛 The REST API's pagination depth limit now protects every paginated endpoint, including the largest lists such as the Connector Space and Pending Exports. (#487)
- 🐛 `-All -Force` on the paginated `Get-JIM*` cmdlets now stops at the API's maximum retrieval depth with a warning, after returning everything it could, instead of failing part-way. (#487)
- 🐛 `Get-JIMSyncRule` now returns every Synchronisation Rule rather than only the first 25.
- 🐛 Saving a Metaverse Object Type's Deletion Rules no longer fails with a database error on any type with attributes bound.
- 🐛 Piping a Connected System into `Get-JIMSyncRule`, as its documentation shows, now works.
- 🐛 The Pending Exports list can once again be sorted by its **Source Metaverse Object** column, and searching it by name once again returns matches.
- 🐛 Selecting a Partition for a domain the connected Active Directory or Samba AD domain controller does not host now fails the import with clear guidance, instead of silently importing nothing. (#230)
- 🐛 `Start-JIMSchedule -Wait` now waits for the run to finish rather than returning immediately while it is still queued. (#1196)
- 🐛 Re-running Schema Import after the directory gains new attributes no longer fails with a duplicate key error, and a failed Schema Import no longer partially applies. (#1171)
- 🐛 After an upgrade your browser now loads the new interface straight away, theme colours included, without a hard refresh, because the portal's stylesheets and scripts are versioned by their content.
- 🐛 A newly created Metaverse Object is no longer described as `00000000-0000-0000-0000-000000000000` in the causality record. (#1087)
- 🐛 The Pending Deletions page and API now list Metaverse Objects scheduled for deletion because an authoritative source disconnected, not only those whose last connector had gone. (#119)
- 🐛 A directory entry matching two selected Object Types, such as **user** and **person** on Active Directory, is now imported once rather than twice, and resolves to the same Object Type on every run. (#492)

### Security

- 🔒 LDAPS connections now fully validate the directory's certificate (issuer, validity and host name) before sending credentials, and honour certificates added in Admin > Certificates alongside the operating system's trust store. (#1132)
- 🔒 The container images now build on the current .NET 10.0.11 base images, clearing CVE-2026-62901 and the systemd advisories CVE-2026-15059 and CVE-2026-16742 (`libsystemd0`, `libudev1`).
- 🔒 The container images now apply Ubuntu's published security fixes at build time, resolving more than seventy low and medium advisories (in `openssl`, `util-linux`, `perl-base` and others) that waited on base image rebuilds.
- 🔒 The PowerShell module no longer writes your API key, passwords or Connected System setting values to debug output, so running a cmdlet with `-Debug` no longer leaks credentials into transcripts, CI logs or shared troubleshooting output. (#1119, #1516)
- 🔒 Attributes holding credential material, such as `unicodePwd` and `userPassword`, can no longer be imported, selected for management, or used in an Attribute Flow; any already selected are deselected and locked, leaving Synchronisation Rules intact.

## [0.14.0] - 2026-07-25

### Security

- 🔒 Values imported from connected systems can no longer forge or corrupt service log entries via embedded line breaks; every such value is now sanitised before logging. Identity display names are no longer written to service logs at all.
- 🔒 The expression evaluation engine has been security-reviewed and hardened with defence-in-depth guardrails, with no change to expression functionality.
- 🔒 Every response from JIM now carries defence-in-depth security headers, including a Content Security Policy, clickjacking denial, and MIME-sniffing protection.
- 🔒 Every NuGet dependency, including transitive packages, is now locked to exact known-good versions, making JIM's builds reproducible and tamper-evident from source through to container image.
- 🔒 Sign-ins and API key authentication attempts now appear in the Activity audit log, with failed attempts grouped by key, IP address and reason so the log stays bounded under a credential-spraying attack. Security events carry their own retention period, defaulting to one year.
- 🔒 Patched a transitive dependency (`System.Security.Cryptography.Xml`) to clear four newly published high-severity advisories. The package arrives via ASP.NET Core Data Protection and is not used by JIM at runtime.
- 🔒 LDAP Distinguished Name parsing is now built into the LDAP Connector, removing the third-party DNParser package, and its non-OSI-approved licence, from JIM's supply chain.

### Added

- ✨ Run Profile executions now report live progress with throughput and an estimated time remaining: on the Activity detail page, from a new lightweight progress REST endpoint, and in the terminal via `Get-JIMActivity -Follow` and `Start-JIMRunProfile -Wait`. (#202)
- ✨ The Operations page now updates in real time: the queue and history react the moment tasks are queued, progress or complete, pushed from the database rather than polled, with automatic fallback to polling if the notification channel is unavailable. (#307)
- ⚡ Schedules now advance between steps and complete near-instantly, instead of waiting up to 30 seconds for the Scheduler's next polling cycle. (#307)
- 🖥️ Executing an Example Data Template now shows a live progress bar on the template page itself, so you no longer have to switch to the Operations page to watch it. (#307)
- ✨ `Invoke-JIMExampleDataTemplate` gains `-Wait` (with an optional `-Timeout`), blocking until generation completes with a live progress display, and `-PassThru` now returns the tracking `ActivityId` and `TaskId`. (#1112)
- ✨ Administrators can now create, rename, re-icon and delete custom Metaverse Object Types, from the portal, the REST API or PowerShell. The built-in User and Group types are protected, and deletion is blocked while any object or Synchronisation Rule still uses the type.
- ✨ Administrators can now create, edit, delete and bind custom Metaverse Attributes, from the portal, the REST API or PowerShell, with a live duplicate-name check. Deletion is blocked only when objects hold a value; configuration-only references cascade behind a confirmation.
- ✨ New built-in Metaverse Attributes make SCIM 2.0 systems map cleanly onto JIM's schema: Emails, Account Enabled, Nickname, Preferred Language, Locale, Time Zone, Middle Name, Honorific Prefix and Honorific Suffix. Existing deployments gain them on upgrade. (#1104)
- ✨ Metaverse Attributes now carry Standard Mappings, recording how each corresponds to its SCIM 2.0 and LDAP/Active Directory counterparts so you can see which attribute to target. They are guidance only; what flows between systems is set solely by your Attribute Flows. (#1104)
- ✨ You can now filter a Metaverse Object Type's list to just the objects holding a value for a given attribute, from the portal (a `hasAttribute:` search), the REST API, or `Search-JIMMetaverseObject -HasAttribute`.
- ✨ Attribute Flows on export Synchronisation Rules can now be marked Initial Export Only: the attribute is set once when JIM provisions the object, then left to the Connected System so Drift Correction ignores it. Ideal for initial passwords and one-time tokens. (#223)
- ✨ Attributes can now be typed as Decimal, an exact fractional number for values like FTE fraction or contracted hours. Decimal values compare numerically in scoping and searches, and round-trip losslessly from import to export. (#1046)
- ✨ Each Connected System can now choose how imports treat reference values that cannot be resolved: raise an error on each affected object (the default), complete with a single warning summary, or ignore them entirely. (#873)
- ✨ The REST API is now protected by configurable rate limiting, tunable from Service Settings without a restart and returning standard 429 responses with Retry-After guidance. Infrastructure API keys are exempt, and the PowerShell module backs off and retries automatically.
- ✨ Background housekeeping that deletes Metaverse Objects past their grace period is now recorded as a Metaverse Object Housekeeping Activity, with every deletion and staged Pending Export visible and filterable on the Activities page. Previously it was only in the log.
- ✨ Full Import Run Profiles gain a Verification Mode toggle that temporarily disables the content-hash skip (see Performance) and reports any disagreement as an error, for validating after an upgrade or investigating a suspected discrepancy. (#1082)
- 🖥️ Multi-valued attribute values on Connected System Object and Metaverse Object detail pages are now browsed in a searchable, paginated table inline on the page, rather than behind a "+N more" dialog.

### Changed

- 🔄 An Attribute Flow mapping a Multi-Valued source attribute to a Single-Valued target now raises a per-object error when an object holds more than one value, instead of silently synchronising an arbitrary one. Pre-v1.0 breaking change: review yours before upgrading. (#435)
- 🔄 Deleting an identity now deprovisions downstream accounts according to each export Synchronisation Rule's Deprovisioning Action, rather than only deleting accounts JIM originally created. Existing rules keep the safe Disconnect default; set Delete per rule to opt in. (#655)
- 🔄 **Breaking (REST API and PowerShell):** the object type in Metaverse Object list responses is now a nested `type` object (`{ id, name }`), matching the single-object response, instead of flat `typeId`/`typeName`. Callers must switch to `.type.id`/`.type.name`. (#813)
- 🔄 The REST API now rejects numeric enum values in request bodies with a `400`; send the string name instead (`"mode": "AllOf"`). Responses and the PowerShell module are unaffected, so only a client hand-crafting request bodies must change. Pre-v1.0 breaking change. (#1060)
- 🔄 The JIM PowerShell module now returns PascalCase property names (`$obj.DisplayName`), following PowerShell convention rather than the REST API's camelCase. Member access is case-insensitive, so only scripts comparing property-name strings need updating.
- 🔄 Paginated list APIs and every `-All` auto-paginating cmdlet now guard against runaway pagination: a page beyond 1000 returns a `400` rather than being silently clamped, and `-All` stops at 1000 pages with a warning. A new `-Force` fetches everything. (#487)
- 🔄 Executing an Example Data Template through the REST API now queues the generation and returns the tracking Activity's id, exactly like the portal, instead of running it inside the HTTP request with no Activity recorded. Pre-v1.0 breaking change. (#1112)
- 🔄 Object Matching Rule sources no longer accept a Metaverse attribute as the source value; export matching always needs a Connected System attribute to compare accounts on, and the standard rule shape now serves both import and export matching. (#1053)
- 🔄 Exports now default to a conservative connector-recommended degree of parallelism when a Connected System's Max Export Parallelism is not set, instead of always running sequentially. An explicitly configured value is always respected.
- 🔄 The LDAP Connector's default Modify Batch Size is now 1000 values per request, up from 100, cutting the round trips needed for very large group memberships by an order of magnitude. Existing Connected Systems keep their stored value; raise it in Export settings to benefit.

### Fixed

- 🐛 Accounts queued for deletion when an identity is deleted are now reported on the Activity of the run that queued them, nested beneath the MVO Deleted outcome and counted in the run's Pending Exports total, instead of appearing only in service logs. (#1044)
- 🐛 A Pending Export execution item now shows its Pending Export's details, including its change type, rather than rendering the panel only for export errors; a queued deletion is now described as such. (#1044)
- 🐛 Provisioning now joins to a matching existing account instead of always creating a duplicate: export matching previously ignored every configured Object Matching Rule, so a rehire's retained account failed with errors such as "The object exists".
- 🐛 Two identities being provisioned at the same time can no longer both join the same pre-existing target system account; the join is now claimed atomically, and the identity that loses the race is provisioned a new account as normal. (#1051)
- 🐛 Export matching now works for Object Matching Rules on Long Number attributes, such as numeric badge identifiers, and rules on attribute types that cannot be matched are reported as a warning in the service log instead of silently doing nothing. (#1052)
- 🐛 Import matching now works for Object Matching Rules on Long Number and Decimal attributes; these previously never joined an incoming account to its existing identity, so synchronisation projected a duplicate instead. (#1046)
- 🐛 An object that left an export Synchronisation Rule's scope and returned before the deprovision executed no longer has its live target account deleted by the stale Pending Delete, even when the returning change touches only the scoping attribute.
- 🐛 Deprovisioning a group member no longer destroys the group's other pending exports: a group with an unexported Delete keeps it, and a group provisioned but not yet exported keeps its Create rather than being stranded unprovisioned.
- 🐛 Membership removals staged when deleting Metaverse Objects now appear on the run's Activity, named by their referencing group and counted into the run's totals; previously an Activity could stage thousands of removals while reporting zero Pending Exports.
- 🐛 Deleting identities referenced by many groups no longer over-reports the resulting membership-removal Pending Exports: each group is now recorded once with its coalesced export (on a 500,000-user run, a reported 21,824 became the 5,421 actually staged).
- 🐛 Deleting objects that other objects reference no longer leaves invisible empty entries behind: group member lists no longer show blank rows or inflated member counts, and later exports no longer stage empty attribute changes. Upgrading cleans up any left by earlier deletions.
- 🐛 Exports running with Max Export Parallelism above one no longer send unresolved reference values (raw internal identifiers) to the target system; reference resolutions are now persisted before the parallel batches execute.
- 🐛 Large exports with many reference-bearing objects no longer fail partway with "the connection pool has been exhausted"; each parallel batch's resources are now released as it completes, instead of being pinned for the rest of the run.
- 🐛 The progress shown while an export works through its deferred reference phase is now accurate. It previously restarted the processed count from zero against the full run total, producing a misleadingly low rate and a wildly inflated time remaining.
- 🐛 Very large imports no longer fail with a database statement timeout while Pending Exports are loaded for reconciliation; the load now runs in bounded chunks (measured at 525,000 Pending Exports with 9.8 million attribute value changes).
- 🐛 Very large synchronisation runs no longer fail with a database command timeout while change history reference links are resolved. Resolution now runs in bounded batches, and the export stage resolves the references its own change records create.
- 🐛 Connected System Objects now retain their partition assignment. The high-volume import write paths silently discarded it, leaving objects invisible to their partition's obsoletion sweep, so they could never be flagged as deleted. (#1046)
- 🐛 Long Number attributes now flow correctly everywhere the other data types already did: inbound flows no longer fail the object, expression results are no longer dropped or truncated, and export evaluation no longer skips a genuine change as no-net-change. (#1046)
- 🐛 The REST API now returns Long Number, Decimal and Binary attribute values instead of null, and every attribute type surfaces its real value in its natural JSON type when listing Metaverse Objects with requested attributes. Binary values are returned as Base64 text. (#1046)
- 🐛 Deletion audit records now retain Long Number, Decimal and reference values, which previously recorded blank, and values beyond the 32-bit range are no longer truncated to a wrong number. The stored attribute values themselves were never affected. (#1046, #871)
- 🐛 The File Connector now writes Binary attribute values to export files as Base64 text, instead of silently writing empty cells. (#1046)
- 🐛 Executing an Example Data Template through the REST API no longer crashes with an index-out-of-range error when generating pattern-based values; a template referencing a genuinely empty Example Data Set now fails with a clear message naming the set. (#1112)
- 🐛 Example Data generation no longer crashes intermittently under load. The parallel generator shared a random number generator that is not safe for concurrent use, so its internal state could be corrupted and abort generation partway.
- 🐛 The Example Data generation progress bar now advances about once a second instead of appearing frozen and then jumping; the CPU-bound parallel generation was consuming every worker thread and starving the progress reporter.
- 🐛 The Operations queue progress bar for Example Data generation now sweeps smoothly from 0% to 100% across the whole job, including the database-persistence phase where it previously sat frozen at 100%, with a rolling estimated time remaining.
- 🐛 The rate and time-remaining estimate on a running Activity now reflect recent throughput rather than a whole-run average, which misled badly on long runs with fast and slow phases. A stalled counter now reads "finishing up" instead of showing a fabricated estimate.
- 🐛 The Activity Operations tab no longer pegs the server at 100% CPU for Activities with tens of thousands of execution items; it now reads only the columns the grid needs (measured: a 26,824-item Activity page went from effectively unusable to about a second).
- 🐛 MVO Deleted and MVO Deletion Scheduled outcomes triggered by an out-of-scope disconnection no longer render as bare labels; each now shows the deleted identity's display name, why the deletion rule fired, and a link to the deletion record browser. (#1086)
- 🐛 Synchronisation runs whose only outcomes were out-of-scope disconnections no longer show an empty Outcomes cell in the Operations history and Activity list; new chips display out-of-scope disconnections and out-of-scope retained joins.
- 🐛 Temporal Scope Reconciliation tasks now display their name and type on the Operations queue, instead of "Unknown WorkerTask type".
- 🐛 LDAP Distinguished Names containing escaped separators (an escaped backslash before a Relative Distinguished Name comma, or a comma inside a quoted value) are now parsed correctly when resolving container hierarchies and parent containers.
- 🐛 Recording an API key's last-used timestamp no longer surfaces error-level log entries when the database is briefly saturated by a large synchronisation run. The last-used display is unaffected beyond a coarser precision.
- 🐛 Closing the browser or navigating away from a tabbed admin page no longer records spurious Error-level entries in the JIM.Web log. Remaining browser-disconnect noise is logged at Warning, so Error entries once again indicate genuine problems.
- 🐛 `Add-JIMScheduleStep` works again, sending step type and execution mode as enum names; it also now passes existing steps through verbatim, instead of silently rewriting any PowerShell or parallel step it did not recognise.
- 🐛 Piping a Schedule into `Get-JIMScheduleExecution` now filters executions to that Schedule. Previously the piped Schedule did not bind, so the cmdlet silently returned every execution in the system whilst appearing to filter.
- 🐛 `Reset-JIMServiceSetting` now accepts Service Settings from the pipeline, as its documentation described.

### Performance

- ⚡ Full Imports at large scale are dramatically faster, and confirming a very large group no longer gets disproportionately slower as its membership grows. A Full Import of 210,000 objects that took over 40 minutes now completes in around 8.
- ⚡ Full Import now skips loading and comparing objects whose content has not changed since the previous import, making its cost proportional to the number of changed objects rather than the size of the whole connector space. Any doubt falls back to the full comparison. (#1082)
- ⚡ Full Imports over existing objects are faster again: the per-object database work that dominated them at scale (over half a million separate lookups at 500,000 users) is now done in bulk.
- ⚡ Full Synchronisation at large scale no longer spends most of its time re-verifying large groups for drift; this accounted for 35 minutes of a 52-minute confirming synchronisation at 500,000 users, and is now effectively instant regardless of group size.
- ⚡ Synchronisation runs no longer slow down page by page as they work through a large Connected System. Each page used to take longer than the last (around 200ms early, degrading to 1.5s late; 16 minutes of waiting across a 525,000-object run); every page now costs the same.
- ⚡ Deleting Metaverse Objects that groups reference is now dramatically faster: a 2,000-user leaver cohort at 200,000 objects with 10,000 groups that took over 9 hours to synchronise is projected to finish in well under one.
- ⚡ Deleting Metaverse Objects during synchronisation is dramatically faster: a page of deletions that took around 50 seconds now completes in a fraction of that, and no longer gets slower as the number of objects grows.
- ⚡ Deprovisioning users who are members of large groups no longer slows synchronisation to a crawl, however large those groups are.
- ⚡ Exports no longer stall between batches at large scale. At 200,000 objects with 10,000 reference-bearing groups, an export previously spent hours getting organised before the first group reached the target system.
- ⚡ The tail of a large, reference-heavy export no longer crawls through work it has already identified, so an export that is mostly group memberships finishes promptly instead of trailing off.
- ⚡ Export runs no longer spend around 11 minutes preparing to retry previously deferred references at 525,000 Pending Exports, even when there is nothing left to resolve. (#1102)
- ⚡ Exports now update JIM's own record of an object the moment the export succeeds, rather than waiting for the next confirming import to read the values back, so that import has far less to do. Applies to LDAP and similar connectors; file-based exports are unchanged. (#1079)
- ⚡ Watching a Run Profile execute no longer competes with the run itself: refreshing its statistics cost around 85 minutes of cumulative database time over one 500,000-user run, and is now instant. (#1078)
- ⚡ The worker service no longer places constant background load on the database for the entire duration of any running task, competing with the very run it is monitoring.
- ⚡ Bookkeeping after an export no longer gets disproportionately slower as a batch grows; a batch containing a 100,000-member group spent over ten minutes in it. This also fixes a batch failure that could abort after the target system write had succeeded.

## [0.13.0] - 2026-07-10

### Added

- ✨ Synchronisation Rules can now carry an optional description recording what the rule is for. Set it in the admin portal, with `New-JIMSyncRule`/`Set-JIMSyncRule`, or the REST API; changes appear in the change history.
- ✨ Date/time scope filters and object searches can now be relative to "now" (a count, a unit from Hours to Years, and a direction, for example "30 to 364 days ago") rather than a fixed date, re-evaluating every run so the scope keeps moving with time.
- ✨ Relative-date scopes keep working when source data isn't changing: a new built-in hourly Temporal Scope Reconciliation schedule re-evaluates time-driven transitions, so leavers deprovision and joiners provision as their dates pass. It can be re-timed or disabled, not deleted.
- ✨ Predefined Searches can now filter on any attribute type (Number, Long Number, Date/Time, Boolean and GUID) with type-appropriate operators and case-sensitive or -insensitive text matching. Manage criteria from a new editor, the PowerShell module, or the REST API.
- ✨ Predefined Search criteria can now be combined with AND/OR logic and nested groups, for example "(Department is Finance or Sales) and active", rather than a flat list.
- ✨ Example data templates can now build a text attribute from an expression, using the same `mv["Attribute Name"]` syntax and functions as Attribute Flows, so a generated value can derive from other attributes on the same object. Circular references are detected up front.
- ✨ The Activity list is easier to audit: category (Configuration, Identity, Synchronisation, System), initiator (user, API key, system) and created-date filters narrow the view, and the filter state is reflected in the URL so a view can be bookmarked or shared.
- ✨ An API Key's Name and Description can now be edited directly from its Details tab in the admin portal, without PowerShell or the REST API.

#### Attribute Priority (#91)

- ✨ When more than one Connected System contributes a Metaverse attribute, a configurable per-attribute priority order now picks the winner, so a higher-priority source is never overwritten by a lower one; a "Null is a value" option lets an authoritative source assert "no value".
- ✨ Attribute Priority is manageable in the admin portal: a Metaverse Object Type's Attributes tab shows each attribute's contributor count, and expanding a multi-contributor one lets you drag its Synchronisation Rules into priority order and toggle "Null is a value".
- ✨ The REST API and `Get-JIMMetaverseObject` now show each attribute value's provenance: the Connected System and Synchronisation Rule that won priority resolution. Asserted nulls appear as flagged, value-less rows, distinguishing a deliberate blank from one with no contributor.
- ✨ Synchronisation Activities now report when an attribute became blank with nothing to replace it, as a distinct "MVO No Contributor" outcome alongside "MVO Null Asserted", so you can tell a deliberate clear from every source falling away.

#### Configuration Change History (#14)

- ✨ JIM now tracks a versioned history of who changed what and when across its configuration: Synchronisation Rules, Connected Systems, Schedules, Service Settings, Metaverse schema, and more. Retrieve it in the portal, via `Get-JIMConfigurationChangeHistory`, or the REST API.
- ✨ Secrets are never captured in the change history: encrypted setting values, a Schedule step's SQL connection string, certificate material, and API key secrets are all flagged as changed but never stored, not even as a hash.
- ✨ You can record a reason for any configuration change: `-ChangeReason` on the write cmdlets or an optional REST field, plus a "Reason for change" prompt when saving in the admin portal. The reason shows with the change and on its Activity.
- ✨ Deleting a Connected System records a final snapshot of its configuration, so a decommissioned system's last-known state and who removed it stay auditable; the captured state is shown on the delete Activity as a clearly-marked removal.
- ✨ Configuration change history is retained on its own schedule: a new Configuration change retention period Service Setting (default ~10 years) governs it, separate from general history retention.
- ✨ First-time seeding of built-in configuration now appears as a single System Initialisation Activity with the seeded objects as children, so a new deployment starts with one clear entry instead of a page of system rows.
- 🔄 A factory reset now preserves the change-history provenance of the built-in objects it keeps, re-recording their version-1 baselines under a fresh System Initialisation Activity instead of stripping their factory origin from the audit trail.
- 🔄 Data-generation runs are now a distinct "Data Generation" activity type, separated from Example Data Template configuration changes, so the Activities Configuration filter isn't cluttered by generation runs. Existing runs are reclassified on upgrade.

#### API & PowerShell Coverage (#154)

- ✨ Connected System Objects can now be listed and filtered via a paginated REST endpoint and the extended `Get-JIMConnectedSystemObject` cmdlet, rather than looked up one at a time.
- ✨ Example Data Sets now support full create, update, and delete via the REST API and the new `New-`, `Set-`, and `Remove-JIMExampleDataSet` cmdlets, alongside the existing read access.
- ✨ Queued and in-progress background operations can now be listed, inspected, and cancelled remotely via a new Worker Tasks REST endpoint and the `Get-JIMWorkerTask` / `Stop-JIMWorkerTask` cmdlets.
- ✨ File system browsing, log viewing, and Metaverse Attribute priority management (previously UI-only) are now available as PowerShell cmdlets, giving the module full parity with the REST API.
- ✨ A single Connected System Object Type can now be retrieved by id from the REST API, returning the object type with its attributes, to match the existing update endpoint.

#### PowerShell Log Streaming (#466)

- ✨ Service logs can now be streamed live from PowerShell with the new `Watch-JIMLog` cmdlet: it polls the Logs API, shows only new entries, supports the same filters as `Get-JIMLogEntry`, and keeps polling through transient failures until you stop it with Ctrl+C.

### Changed

- 🔄 Multi-source Metaverse attributes now resolve by attribute priority instead of synchronisation timing (last-writer-wins). Single-source attributes are unaffected; existing multi-source ones resolve deterministically until you set an explicit priority order.
- 🔄 When a source supplying a multi-source attribute disconnects, leaves scope, or stops providing the value, JIM now hands it to the next-priority contributor still supplying it (reference attributes included), clearing it only when none survives.
- 🔄 A deletion grace period no longer freezes attribute hand-over at scope exit: a re-elected attribute is still handed over, and only a single-source value with no surviving contributor is held for the grace window.
- 🔄 Activity displays no longer abbreviate "Synchronisation Rule" to "Sync Rule". The underlying `ActivityTargetType.SyncRule` enum value is renamed to `SynchronisationRule`, a breaking REST/OpenAPI change acceptable pre-v1.0.
- 🔄 The Activity children REST endpoint and `Get-JIMActivityChildren` are now paged, returning a paged envelope rather than every child at once; the cmdlet gains `-Page`, `-PageSize`, and `-All`, and is now exported from the module (previously unreachable).
- 🔄 A Connected System's Settings tab now groups its top-level setting categories into a collapsible accordion and separates second-level headings with a divider, making dense connector settings easier to scan.

### Performance

- ⚡ Synchronisation imports use far less memory: comparison no longer keeps every loaded object (plus a change-tracking snapshot) for the whole run, nor loads referenced objects in full just to compare group memberships; at 100,000 users with ~5,000 groups this had cost gigabytes.
- ⚡ The worker now returns memory to the operating system after each heavy operation completes, instead of holding its peak allocation while idle, and logs its garbage-collection configuration at startup.
- ⚡ Generating example data is dramatically faster: the built-in "Users & Groups" template (10,000 users) now completes in seconds rather than minutes, after moving blocking progress writes out of the parallel generation loop.
- ⚡ Example data value uniqueness is now tracked with constant-time lookups instead of rescanning an ever-growing list under a global lock, removing a cost that grew with the square of the object count at larger template sizes.

### Fixed

- 🐛 Adding a Trusted Certificate via the REST API or `Add-JIMCertificate` no longer returns a "No route matches" error on success (the certificate was stored regardless); `Get-JIMCertificate` on an empty store no longer emits the pagination envelope as a certificate.
- 🐛 Re-keying an identity in a source (so a new record re-matches an identity while the old one is removed) no longer fails a Full Synchronisation with a database constraint violation; two new records matching one identity fail cleanly on the second, not aborting the run.
- 🐛 A Full Synchronisation after a configuration change (attribute priority, enabling/disabling a rule, scoping) now applies it to every object; previously objects whose source data hadn't changed were skipped, so a pure configuration change never took effect for them.
- 🐛 A synchronisation run that both created a Metaverse Object and detected drift on it no longer fails with a database foreign-key violation; drift is now evaluated after new objects are saved, so the corrective export always references a real object.
- 🐛 A Full or Delta Synchronisation no longer aborts with a database concurrency error when updating a Metaverse Object created earlier in the same run, a race seen at scale; a page that fails to persist now reports which objects were affected instead of a generic error.
- 🐛 Deleting a Metaverse Object (for example a deprovisioned leaver) now stages membership-removal exports for every object that referenced it, so groups in target systems without referential integrity no longer keep the deleted user as a member forever.
- 🐛 Deleting a Connected System Object that other objects still reference no longer fails the whole run with a database foreign-key violation; the stale references are cleared as part of the deletion, with the raw strings preserved so the next confirming import reconciles.
- 🐛 A synchronisation run that fails while saving to the database no longer leaves its Activity stuck in progress; the failure is recorded via a fresh database session, since the failing one cannot save anything further.
- 🐛 A Connected System hierarchy refresh that returns no partitions no longer wipes the configured hierarchy: a transient connection or scope problem previously deleted every partition and container, including selected ones. JIM now leaves it untouched and records a warning.
- 🐛 A factory reset no longer strips the built-in "Users & Groups" example data template of its attributes (a side effect of the bulk wipe that left generated objects value-less); the template is now restored as part of the reset.
- 🐛 Editing an API Key or Trusted Certificate now records who made the change and when; previously the "last updated" attribution was silently lost on save.
- 🐛 Activity targets now deep-link to where their subject is managed: an Attribute Flow change to the rule's Attribute Flow tab, imports to the Connected System's Schema and Partitions tabs, and Schedule, Service Setting, and Metaverse activities to their pages.
- 🐛 The Schedules links on the home page now open the Schedules tab on the Operations page directly, instead of landing on the default Queue tab.
- 🐛 Save and create buttons across the admin portal now react as you type instead of waiting for the field to lose focus, and no longer start disabled when editing an existing item whose required fields are already filled in.
- 🐛 The Service Setting edit dialog no longer allows saving an unparseable duration into a time-period setting; the value is validated as you type and Save stays disabled until it is valid.
- 🐛 Updated the bundled Microsoft.OpenApi library to a patched release (2.7.5), clearing a high-severity advisory (GHSA-v5pm-xwqc-g5wc) in JIM's API documentation generation.
- 🐛 The `-ConnectedSystemAttributeName` parameter on `New-`/`Set-JIMScopingCriterion` now resolves the attribute correctly; it previously queried a non-existent endpoint, so scoping criteria specified by attribute name failed (the id-based parameter was unaffected).

## [0.12.0] - 2026-06-23

### Added

- ✨ Inbound attribute mappings can now clean and normalise imported text per mapping: treat whitespace-only and empty values as no value (on by default, so a stray space no longer masquerades as a real value), trim and collapse whitespace, and normalise case (Upper, Lower or Title), configurable in the mapping editor, REST API, and PowerShell module. Switch it off per mapping where whitespace is meaningful, and the portal then flags such values with a "(whitespace)" indicator instead of rendering them blank.
- ✨ Inbound text attribute mappings can now clean and normalise imported values per mapping: treat whitespace-only/empty as no value (default on), trim, collapse internal whitespace, and normalise case. Configurable in the mapping editor, REST API, and PowerShell module.
- ✨ The PowerShell module now persists your interactive SSO sign-in across terminal sessions: after `Connect-JIM`, new terminals reconnect silently, storing only the refresh token in the OS credential store. Use `-NoPersist`, `-Force`, and `Disconnect-JIM` to control it.
- ✨ Factory reset is now available in the portal: a new Administration danger area (`/admin/factory-reset`) with a backup warning, type-to-confirm, and an optional "delete administrators" path.
- ✨ The initial administrator can now be bootstrapped via the PowerShell module or REST API, not just the portal. Their first authenticated call just-in-time creates the identity and grants the Administrator role, so an air-gapped instance is fully CLI-administrable.

### Changed

- 🖥️ The Synchronisation Rule editor is now organised into deep-linkable tabs (Details, Matching, Scope, Attribute Flow, Danger Zone) instead of one long page, with a single save bar beneath every tab so the whole rule still saves in one action.
- 🖥️ The Connected System Schema tab is now split into sub-tabs: a searchable, filterable "Object Types" grid for choosing which types JIM manages, plus a tab per selected type for its attributes. This stays usable when a system exposes hundreds of object types.
- 🖥️ Connected System settings that only apply in certain configurations are now hidden until relevant and required once shown (for example, LDAP Certificate Validation appears only with LDAPS enabled), enforced in the form and for API callers.
- 🔄 The REST API now rejects an invalid Connected System settings update with HTTP 400 and a per-setting list of what failed and why, instead of silently saving it. `Set-JIMConnectedSystem` surfaces these field-level messages.
- 🔄 JIM now requests the `offline_access` scope at interactive sign-in so the identity provider issues a refresh token; this enables in-session token renewal and PowerShell token persistence. Existing SSO deployments must permit `offline_access` on the interactive client.
- 🔄 Factory reset now preserves administrator users by default (so you are not locked out) and records a Reset activity. Removing administrators too is opt-in via `-IncludeAdministrators` on `Reset-JIMSystem` (and `includeAdministrators` on the reset API).
- 🔄 The reconnection overlay now shows live attempt progress (for example, "Attempt 2 of 5...") while JIM re-establishes a dropped connection.
- 🔄 Running a PowerShell cmdlet before connecting now shows a clear one-line prompt to run `Connect-JIM -Url <your JIM URL>` instead of a raw internal error; it is non-terminating by default and can be made fatal with `-ErrorAction Stop`.
- 🔄 The "not authorised" message shown when an authenticated user has no JIM identity now explains that identities arrive via synchronisation or administrator provisioning, rather than directing them to sign in to the portal first.

### Fixed

- 🐛 Editing an existing Synchronisation Rule in the portal now saves. Changes such as disabling a rule appeared to succeed but were silently discarded; the editor now keeps a single database session and fails loudly rather than dropping the change.
- 🐛 Creating a Synchronisation Rule from scratch in the portal no longer fails (previously it raised a database foreign-key violation, so a new rule could not be saved at all), and the page now switches into edit mode once the rule is created.
- 🐛 The Synchronisation Rule expression tester now resolves attribute names case-insensitively, exactly as live synchronisation does, so an expression that works during a sync run no longer reports "no result" in the tester purely because an attribute name's casing differs.
- 🐛 A failed synchronisation expression is no longer silently swallowed, leaving stale metaverse data. The affected object is errored with a distinct "expression evaluation error" and its target left untouched, while the run continues (inbound and export mappings).
- 🐛 The File Connector now enforces "exactly one of Object Type Column or Object Type" at save time, with live form feedback and server-side validation, instead of failing later or silently ignoring a value. Connectors can declare such either/or setting groups generically.
- 🐛 Deleting a Connected System (including a synchronised one) no longer fails with a database error and is now atomic. Dependent objects are removed in the correct order, and metaverse values it contributed are kept with their contributor link cleared.

### Security

- 🔒 A factory reset now invalidates every existing portal sign-in session, so no stale access or privileges survive the wipe; users must re-authenticate. API key access is unaffected.
- 🔒 The REST API now rejects request bodies containing duplicate JSON property names, removing an ambiguous-parsing and request-smuggling vector.

## [0.11.0] - 2026-06-06

### Added

- ✨ Create custom Metaverse Object Types via the API and the new `New-JIMMetaverseObjectType` cmdlet, to model identity types beyond Users and Groups.
- ✨ Scoping criteria now support long-integer and case-sensitive comparisons via the API and `New-JIMScopingCriterion`.
- ✨ Synchronisation Rules can now set their out-of-scope and deprovisioning actions and drift detection via the API and `Set-JIMSyncRule`.
- ✨ New factory reset (`Reset-JIMSystem` / `POST /api/v1/system/reset`) wipes all customer data and configuration in one transaction while preserving the schema, built-ins, and infrastructure access.

### Fixed

- 🐛 Refreshing a Connected System's schema now persists the discovered object types and attributes, so the selection interface appears immediately instead of reading back empty.
- 🐛 Outbound deprovisioning no longer fails with a duplicate-key error when the target object still has a Pending Export from a prior run.
- 🐛 Adding scoping criteria to an existing Synchronisation Rule via the API no longer fails to save.

### Changed

- 🔄 JIM is now distributed under the Tetron Software License Agreement v2.0.

## [0.10.3] - 2026-05-10

### Added

- ✨ Metaverse Object change history is now available via the API and PowerShell module: new `GET /api/v1/metaverse/objects/{id}/change-history` endpoint returns paginated change records, and the new `Get-JIMMetaverseObjectChangeHistory` cmdlet wraps it for automation and compliance scenarios.
- ✨ Connected System Object change history is now available via the API and PowerShell module: new `GET /api/v1/synchronisation/connected-systems/{id}/connector-space/{csoId}/change-history` endpoint returns paginated change records, and the new `Get-JIMConnectedSystemObjectChangeHistory` cmdlet wraps it for automation and compliance scenarios.

### Performance

- ⚡ Metaverse Object detail pages load substantially faster on objects with long change histories: the page no longer materialises the entire change graph upfront, fetching only a count alongside the object and loading change rows on demand when the Changes tab is opened.
- ⚡ Connected System Object detail pages load substantially faster on objects with long import histories: the page no longer materialises the entire change graph upfront, fetching only a count alongside the object and loading change rows on demand when the Change History tab is opened.
- ⚡ Connector Space list pages load substantially faster: the per-page projection no longer materialises full pending-export graphs or attribute-value entities, returning only the scalar columns the table actually renders.

### Fixed

- 🐛 Export Run Profile Execution Items and their linked Connected System Object Change rows now persist with the correct `ConnectedSystemObjectId` foreign key, restoring causality navigation from Operations into the CSO detail page and preventing exported objects from being mis-labelled as "Deleted" on the activity item detail page (#683).
- 🐛 Pending-export reference values in the Causality Tree attribute change table now render the resolved identifier (e.g. group member DN) alongside a clickable link to the stub Connected System Object, instead of showing only a clock icon with no value.

### Changed

- 💄 The Activity Run Profile Execution Item detail page no longer duplicates the Connected System Object's external ID in the Execution Summary prose; the identifier is already shown as a chip directly below.

## [0.10.2] - 2026-04-29

### Added

- ✨ Predefined Searches can now be retrieved individually via the API and PowerShell module: new `GET /api/v1/predefined-searches/{id}` and `GET /api/v1/predefined-searches/by-uri/{uri}` endpoints return the full search graph, and `Get-JIMPredefinedSearch -Id` / `-Uri` now resolve directly against the server instead of filtering the list client-side (#154)

### Fixed

- 🐛 The "Initiated By" link on Activity and Activity Run Profile Execution Item detail pages now points to the correct Metaverse Object URL, derived dynamically from the initiator's Metaverse Object Type plural name (`/t/{typePluralName}/v/{id}`) instead of a broken hardcoded `/identity/person/{id}` path.
- 🐛 Safari sign-in against the development stack at `http://localhost:5200` no longer fails with `Correlation failed`; OIDC correlation cookies are now configured appropriately for plain-HTTP localhost in Development while production HTTPS defaults remain untouched.
- 🐛 The bundled "Users & Groups" example data template now persists at production speed without stalling the worker or pressuring memory; generation has been rewritten to use PostgreSQL `COPY` binary import in bounded batches, mirroring the proven pattern used on the synchronisation hot path.
- 🐛 Filled alerts in the `navy-o6` themes now meet WCAG AA contrast: light-theme info/success/warning/error variants and dark-theme filled info no longer place dark text on saturated backgrounds, and links inside filled alerts pick up the on-colour text colour rather than clashing with the semantic background.

### Changed

- 💄 Example data generation now reports live, batch-level persistence progress with a rolling ETA on the Activity record and progress bar, so administrators can see exactly where a large generation run is up to.
- 💄 Compact row spacing on the Metaverse Object detail Table view now extends to multi-valued reference rows (e.g. group Owners, Static Members), keeping large memberships readable at a glance.
- 🖥️ Refreshed the JIM portal and documentation typography to IBM Plex Sans and IBM Plex Mono, with a Space Grotesk accent on docs hero surfaces and the portal sidebar wordmark, for sharper identifier disambiguation and a more polished, designed feel across the product.
- 🖥️ The production error page now renders in the JIM brand (broken-cog illustration, Plex / Space Grotesk fonts, navy-o6 palette), honours the user's saved dark-mode preference and `prefers-reduced-motion`, and runs without a Blazor circuit so it remains reachable when middleware throws.
- 🛠️ `jim-reset` now stops any natively-run JIM.Web/Worker/Scheduler processes before tearing down the Docker stack, preventing port collisions (e.g. host port 5200) when the Docker stack is restarted after a `jim-build-light` debug session.

## [0.10.1] - 2026-04-27

### Added

- ✨ Interactive browser-based SSO for the JIM PowerShell module now works against identity providers that require a separate public client registration for desktop/CLI tools, including Keycloak. Two new optional environment variables let administrators advertise client-facing SSO configuration to interactive clients without affecting backend token validation: `JIM_SSO_PUBLIC_AUTHORITY` for deployments where the backend and clients reach the identity provider on different URLs (split-horizon reverse proxies, development containers), and `JIM_SSO_PUBLIC_CLIENT_ID` for deployments where the PowerShell module's public OAuth client is a distinct registration from the web application's confidential client. Both variables are optional and fall back to `JIM_SSO_AUTHORITY` / `JIM_SSO_CLIENT_ID` respectively, so single-URL single-client production deployments are unaffected.

### Changed

- 💄 Refined sidebar navigation styling: selected and hover items now show a contrasting rounded "pill" background that is inset from the drawer edges, with the hover background a stronger shade than the selected background so it remains visible when hovering an already-selected item. Active and hover backgrounds are theme-driven (`--jim-nav-active-bg` / `--jim-nav-hover-bg`) and tuned per theme, with sensible derived fallbacks for any future theme that does not set them.
- 🖥️ A more polished sidebar experience: the signed-in user menu is now anchored to the bottom of the drawer for quick access regardless of how many sections are above it, and pinning or collapsing the drawer is now a single click on a dedicated chevron in the drawer header.

### Fixed

- 🐛 Interactive `Connect-JIM` against Keycloak deployments previously failed with `Invalid parameter: redirect_uri` because JIM advertised the confidential web client ID to the PowerShell module. Administrators can now register a separate public client (as the [SSO Setup Guide](https://docs.junctional.io/administration/sso-setup/) has always instructed) and advertise it to interactive clients via the new `JIM_SSO_PUBLIC_CLIENT_ID` environment variable.
- 🐛 `Get-JIMRole` and the `GET /api/v1/security/roles` endpoint now report the correct static member count for each role; previously the count was always zero because the underlying query did not load role memberships. The count is now aggregated directly in SQL, so even roles with very large memberships are returned cheaply.
- 🐛 `Get-JIMRole -Id` and `GET /api/v1/security/roles/{id}` now report the correct static member count when retrieving a single role.
- 🐛 `Get-JIMMetaverseObjectRole` and `GET /api/v1/security/metaverse-objects/{id}/roles` now report the correct static member count for each role a Metaverse Object belongs to.
- 🐛 `GET /api/v1/synchronisation/connected-systems/{id}` now reports the correct Connected System Object count; previously it always returned zero because the navigation property was not loaded. The count is now sourced from a dedicated count query, mirroring how `pendingExportCount` is already computed.

### Security

- 🔒 Patched `Microsoft.AspNetCore.DataProtection` to 10.0.7 to address CVE-2026-40372 (GHSA-9mv3-2cwr-p262, high-severity elevation of privilege / authentication cookie forgery in ASP.NET Core Data Protection). Also drops the now-redundant transitive override of `System.Security.Cryptography.Xml`, which Data Protection 10.0.7 brings in at a patched version directly.

## [0.10.0] - 2026-04-22

### Added

- ✨ Added a Service Name and Service ID so you can tell JIM instances apart at a glance. Set a friendly name per instance on the Service Settings page and see it under "JIM" in the sidebar, in the browser tab title, and in the footer. The Service ID is generated once per instance and never changes, useful for tooling, logs, and telemetry (#583)
- ✨ Predefined Searches can now be disabled and re-enabled without deleting them; disabled searches are hidden from the portal, the search API, and the sidebar navigation, while administrators can still manage them via the admin UI, the new `/api/v1/predefined-searches` endpoints, and the new `Get-JIMPredefinedSearch` / `Set-JIMPredefinedSearch` PowerShell cmdlets (#555)
- ✨ PowerShell cmdlets for System endpoints: `Get-JIMHealth` (with `-Ready` and `-Live` probes), `Get-JIMVersion`, `Get-JIMAuthConfig`, and `Get-JIMUserInfo`; health, version, and auth config cmdlets work without `Connect-JIM` via a `-Url` parameter (#468)
- ✨ Interactive API reference powered by Scalar, available at `/api/reference` in all environments including air-gapped deployments; OpenAPI document is pre-generated at build time for instant loading with zero runtime overhead
- ✨ Public API reference published to the JIM documentation site at [docs.junctional.io/api/reference/](https://docs.junctional.io/api/reference/); automatically updated on every release to match the published JIM version
- ✨ Clear Connected System activity now tracks and displays removal statistics, showing how many Pending Exports and Connected System Objects were removed (#74)
- ✨ New count endpoints for Metaverse Objects, connector space, and Pending Exports, with filtering by object type, partition, change type, and status; suitable for dashboards, SIEM integration, and capacity monitoring (#154)
- ✨ New user menu in the navigation drawer showing the signed-in user's avatar (with initials), display name and username, with pinning, dark mode and sign-out controls in a single polished popover (#49)
- ✨ Automated integration test metrics streaming to central tracking system with Grafana dashboards (#476)
- 🔒 API and PowerShell support for managing Role membership on Metaverse Objects, enabling administrators to appoint or remove additional admins without restarting the service (#467)
- ✨ New API endpoints for Role member management: list members, add member, remove member, get Role by ID, and list the Roles a Metaverse Object is a member of
- ✨ New PowerShell cmdlets `Get-JIMRoleMember`, `Add-JIMRoleMember`, `Remove-JIMRoleMember`, and `Get-JIMMetaverseObjectRole` with full pipeline support
- ✨ `Get-JIMRole` cmdlet now supports `-Id` parameter for direct Role lookup by identifier
- 🔒 Safety checks prevent administrator lockout: self-removal from the Administrator role and removing the last Administrator are both blocked with clear error messages
- 🔒 Sign-out with identity provider, gated by the `SSOEnableLogOut` service setting, with a confirmation dialog to prevent accidental clicks (#49)

### Performance

- ⚡ Connected System detail lookups are much cheaper on write-path and validation API calls: introduced a lightweight `GetConnectedSystemCoreAsync` retrieval variant that loads only essential properties, and migrated the API controllers that previously paid for the full schema, partition and container graph just to verify the system exists (#494)
- ⚡ Connected System container hierarchy loading now handles arbitrary depth and avoids the cartesian-explosion risk of the previous 11-level hard-coded Include chain; containers are loaded flat and rebuilt into a tree in memory (#494)
- ⚡ Full Connected System loads now issue one database query for Object Matching Rules instead of four, eliminating the fan-out that split-query mode introduced when walking `Sources.ConnectedSystemAttribute`, `Sources.MetaverseAttribute`, `TargetMetaverseAttribute` and `MetaverseObjectType` as separate Include branches (#494)
- ⚡ Default all EF Core queries to `AsNoTracking`, reducing memory and CPU overhead for read-heavy operations; write paths explicitly opt in to change tracking (#484)
- ⚡ Enriched diagnostic spans with cumulative object count and wall-clock offset tags for throughput profiling (#476)
- ⚡ Added MetricsCheckpoint log lines for guaranteed throughput tracking at any log level (#476)

### Changed

- 🖥️ Partition-configuration validation errors now pinpoint the exact gap (hierarchy not imported, no partitions selected, or selected partitions have no container selected) and name the partition involved, replacing the previous generic "no partitions or containers have been selected" message and making misconfigurations far faster to diagnose (#564)
- 🖥️ Page footer now links the Tetron name to tetron.io and includes a GitHub link next to the version number (#49)
- 📦 File Connector storage uses the formal Docker named volume `jim-connector-files-volume`, mounted at `/connector-files` inside JIM Web and JIM Worker. Default deployments get working File Connector exports out of the box without any host-side permission setup. Customers integrating with external file shares bind-mount over a subdirectory of `/connector-files`. See the JIM File Connector documentation for both patterns.

### Fixed

- 🐛 Group and other multi-valued-reference sync activities no longer produce duplicate execution items; cross-page reference resolution now merges reference Attribute Flow into the original Projected/Joined record instead of creating a second standalone "Attribute Flow" record for the same object. Fixes inflated activity counts and removes the confusing split-outcome rows that appeared in activity detail
- 🐛 Static member values and other multi-valued references on group activity detail pages now render as clickable user chips with display names instead of raw GUIDs; reference change records now carry their target as a proper foreign key so the link can be materialised on display
- 🐛 Export failures caught by exception handlers now produce Run Profile Execution Items reliably; previously a thrown connector exception could mark a batch failed without producing any RPEI, so the activity appeared to complete successfully despite silent export failures
- 🐛 Metaverse Object and Connected System Object change history is now persisted during sync RPEI flush and on single-object create, ensuring the audit timeline reflects every sync run
- 🐛 Sign-out with the bundled Keycloak no longer fails with "Missing parameters: id_token_hint"; JIM now persists the ID token during sign-in so the OIDC middleware can include it on the end-session request per the OIDC spec (#49)
- 🐛 Keycloak hostname configuration corrected so that browsers and Docker back-channel clients each get the right endpoint URLs, fixing sign-in and sign-out for all four deployment scenarios (Codespaces, devcontainer native, devcontainer Docker, production) (#49)
- 🐛 Connected System partition trees now include nested containers below the top level. Directories with nested organisational units (e.g. `OU=Users,OU=Corp`) are loaded and returned through the API in full, so administrators can select nested containers for import and automation can address them via PowerShell (#586)

### Security

- 🔒 Supply chain hardening: all Docker base images are digest-pinned, all GitHub Actions are pinned by commit SHA, and the main branch is protected with required status checks including automated code review, CodeQL, container scan, and dependency scan (#520, #517, #521)
- 🔒 Patched transitive `System.Security.Cryptography.Xml` to 10.0.6 to address CVE-2026-33116 (low-severity DoS in `EncryptedXml`); the package is pulled in via ASP.NET Core Data Protection but not used by JIM at runtime
- 🔒 Patched `basic-ftp` CRLF injection vulnerabilities (GHSA-chqc-8p9q-pq6q and GHSA-rp42-5vxx-qpwr) and picked up Ubuntu Noble security updates for libldap and cifs-utils in all production container images

## [0.9.1] - 2026-04-08

### Added

#### Search Objects API (#482, #488)

- ✨ New `GET /api/v1/metaverse/objects/search/{predefinedSearchUri}` endpoint for fast, lightweight object searches optimised for 100K+ object deployments
- ✨ New `Search-JIMMetaverseObject` PowerShell cmdlet with predefined search support, sorting, filtering, and auto-pagination

### Performance

#### Paginated List Optimisation (#482, #485)

- ⚡ Metaverse Object list sorting now uses a pre-computed cached display name column, eliminating expensive per-query subqueries for display name resolution
- ⚡ New composite index on metaverse attribute values for faster attribute-based sorting and filtering
- ⚡ Paginated list queries for Metaverse Objects and Connected System Objects rewritten to use keyset pagination with optimised sort subqueries

### Fixed

- 🖥️ Fixed oversized text on avatar chips in Synchronisation Rule list and detail pages
- 🖥️ Multi-valued attribute value counts on Metaverse Object detail pages now display with thousand separators for readability

## [0.9.0] - 2026-04-07

### Added

#### 100K Object Scale (#451, #437, #438)

JIM now supports deployments of 100,000+ objects, validated by Scale100K integration tests across the full import, sync, and export pipeline. A bounded memory architecture ensures stable, predictable resource usage regardless of dataset size.

- ✨ Bounded memory sync and export pipelines: change tracker cleared at every page boundary and caches loaded per-page instead of upfront, enabling 100K+ object operations without out-of-memory crashes
- ✨ Partition-scoped deletion detection for full imports: deletion detection is now scoped to the imported partition, preventing CSOs from other partitions being incorrectly marked as obsolete during large-scale imports
- 🖥️ Import processing now displays throughput (objects/sec) and ETA in progress messages, completing progress tracking coverage across all long-running phases

#### .NET 10 Migration (#174)

- ✨ Migrated from .NET 9.0 (STS) to .NET 10.0 (LTS), extending support from November 2026 to November 2028
- ✨ Upgraded all NuGet packages to .NET 10-compatible versions, including EF Core 10, MudBlazor 9, and Humanizer 3
- ✨ Replaced Swashbuckle with built-in `Microsoft.AspNetCore.OpenApi` + Scalar for modern API documentation UI
- 🔒 All Docker containers now run as non-root (`USER app`, UID 1654), improving security posture for enterprise deployments
- 🔒 Docker container hardening (#333): read-only root filesystem, dropped all Linux capabilities with selective re-add, and `no-new-privileges` flag on all application containers
- 🔒 Moved CIFS/SMB utilities and capabilities from Web to Worker container, applying least-privilege principle (only the Worker executes file connector operations)
- 📦 Docker images migrated from Debian Bookworm to Ubuntu 24.04 Noble base with pinned SHA256 digests
- 📦 Added `global.json` to pin .NET 10 SDK version across all environments

#### Service Settings REST API & PowerShell Cmdlets

- ✨ New REST API for managing service settings (`GET/PUT/DELETE /api/v1/service-settings`), enabling automation of change tracking, sync page size, history retention, and other operational settings
- ✨ New PowerShell cmdlets: `Get-JIMServiceSetting`, `Set-JIMServiceSetting`, `Reset-JIMServiceSetting` for managing service settings from the command line or automation scripts

#### Data Integrity Validation (#465)

- 🔒 Metaverse attribute operations now validate data integrity before executing: deleting attributes with stored values, deleting attributes referenced by Synchronisation Rules, and removing object type mappings with existing data all return structured validation errors instead of silently corrupting state

#### PowerShell Module Enhancements

- ✨ `-Name` parameter added to six `Get-JIM*` cmdlets (`Get-JIMRunProfile`, `Get-JIMSyncRule`, `Get-JIMApiKey`, `Get-JIMCertificate`, `Get-JIMRole`, `Get-JIMConnectorDefinition`), enabling direct filtering without `Where-Object`
- ✨ New `Get-JIMPendingDeletion` cmdlet with List, Count, and Summary parameter sets for monitoring objects awaiting deletion
- ✨ New `Get-JIMActivityChildren` cmdlet for retrieving child activities of a parent activity

#### Integration Test Runner Enhancements

- ✨ `-LogLevel` parameter for integration test runner: override log verbosity (Verbose/Debug/Information/Warning/Error/Fatal) for the test run without permanently modifying `.env`
- ✨ `-DisableChangeTracking` switch for integration test runner: disable CSO and MVO change tracking during large-scale tests to reduce database writes and improve throughput
- 🖥️ Interactive menus for log level and change tracking selection when running tests without explicit parameters

### Fixed

- 🔒 Safe cancellation for sync operations (#339): when an admin cancels a running Full Sync or Delta Sync, the current page's flush pipeline now completes before exiting. Previously, cancellation could leave orphaned Metaverse Objects without corresponding Pending Exports, causing target systems to silently miss updates.
- 🐛 Fixed import tasks continuing to process after cancellation (#339); cancelling a Full Import or Delta Import from the Operations Queue now stops the import between pages and skips persistence. Previously, the import processor ignored the cancellation signal and ran to completion.
- 🐛 Fixed cancelled tasks having their status overwritten to Completed or Failed; the Worker now correctly preserves the Cancelled activity status instead of overwriting it when the processor finishes.
- 🐛 Fixed sync progress bar showing inflated object counts (CSOs + Pending Exports) instead of just CSOs; progress percentage and ETA are now accurate for Full Sync and Delta Sync

### Changed

- ⚡ LDAP export concurrency is now auto-tuned based on the detected directory server type; AD DS and OpenLDAP default to 16 concurrent operations (up from 4), while Samba AD and unknown directories remain at 4 for compatibility. Administrators who have manually configured the value will not be affected.

### Performance

- ⚡ Selective attribute loading for full sync: unchanged CSOs (based on watermark comparison) skip attribute value loading and Attribute Flow entirely, dramatically reducing I/O for large-scale repeat syncs
- ⚡ Eliminated redundant per-page COUNT queries during sync; total count is now passed from sync start, removing 200+ unnecessary full-table scans at 100K objects
- ⚡ Default sync page size increased from 500 to 1,000, halving the number of database round-trips per sync run
- ⚡ Sync progress updates now use direct SQL instead of EF Core change tracker, reducing per-page overhead
- ⚡ Removed explicit RepeatableRead transactions from sync page loading; PostgreSQL MVCC provides sufficient consistency without the round-trip overhead
- ⚡ Pending Exports table on CSO detail page now uses server-side paging; pages with thousands of pending changes (e.g. 10K member adds) load instantly instead of rendering all rows at once
- ⚡ All export evaluation and Pending Export cache queries now use `AsNoTracking`, eliminating unnecessary entity tracking overhead during sync
- ⚡ Per-page memory diagnostics logging: administrators can monitor memory usage across sync pages to verify bounded memory behaviour

## [0.8.1] - 2026-04-02

### Added

- ✨ Pre-export CREATE→DELETE reconciliation — when an object is created and then deleted before export runs, the redundant Pending Exports are automatically cancelled instead of failing during export (#218)

### Performance

- ⚡ Export rule evaluation optimised to reduce per-MVO processing cost, improving sync performance for configurations with many export rules (#417)
- ⚡ Active Directory schema discovery now batches LDAP queries, reducing connection round-trips during schema import (#433)

### Fixed

- 🐛 Fixed entity tracking conflict during cross-page reference resolution at scale — Full Sync no longer fails with "ConnectedSystemObject cannot be tracked" when groups share members across resolution batches (10,000+ users)
- 🐛 Error messages no longer display the internal "EMERGENCY UPDATE" prefix — user-facing messages now show clean, actionable text (#448)
- 🐛 Activity and RPEI detail page breadcrumbs are now context-aware, showing the correct navigation path based on how the page was reached
- 🔒 Sanitised `Request.Method` in global exception handler logging to prevent log injection (CWE-117) (#444)

## [0.8.0] - 2026-04-01

### Added

#### OpenLDAP Connector Support (#72)

- ✨ Full OpenLDAP and RFC 4512-compliant LDAP directory support — connect to OpenLDAP, 389 Directory Server, and other standards-based LDAP directories alongside Active Directory
- ✨ Automatic directory type detection from rootDSE (Active Directory, OpenLDAP, Generic LDAP) with per-type external ID handling (objectGUID vs entryUUID)
- ✨ RFC 4512 schema discovery — object classes and attribute types parsed from the subschemaSubentry with OID-based data type mapping and superclass hierarchy walking
- ✨ Multi-suffix partition discovery via rootDSE namingContexts for non-AD directories
- ✨ Accesslog-based delta import for OpenLDAP — queries `cn=accesslog` for incremental changes with automatic fallback to full import
- ✨ Parallel import with configurable concurrency — each container/objectType combination runs on its own LDAP connection, working around RFC 2696 paging cookie limitations
- ✨ Transparent `groupOfNames` placeholder member handling — automatically manages the RFC 4519 MUST constraint so administrators never see placeholder entries in the metaverse
- ✨ DN-aware RDN attribute detection for correct export naming
- ✨ Partition-scoped imports — Run Profiles can target a specific partition instead of importing all selected partitions (#353)

#### Worker Redesign (#394)

- ✨ Pure domain engine (`ISyncEngine`) — 7 stateless methods with zero I/O dependencies, making core sync logic independently testable with plain objects
- ✨ Formal data access boundary (`ISyncRepository`) — ~80-method interface separating Worker data access from shared EF Core repositories, with purpose-built in-memory implementation for tests
- ✨ Dependency injection throughout Worker and Scheduler — `IJimApplicationFactory`, `IConnectorFactory`, per-task context isolation

#### Bundled Keycloak IdP for Development (#197)

- ✨ Zero-config SSO — `jim-stack` starts a pre-configured Keycloak instance alongside JIM; developers sign in immediately with `admin` / `admin`
- ✨ Pre-configured realm with `jim-web` (confidential + PKCE) and `jim-powershell` (public + PKCE) clients, `jim-api` scope, and two test users
- ✨ `.env.example` defaults point to the bundled Keycloak — no manual IdP configuration needed for local development
- ✨ `jim-keycloak` / `jim-keycloak-stop` / `jim-keycloak-logs` aliases for standalone Keycloak (F5 debugging workflow)
- ✨ Keycloak admin console accessible at `http://localhost:8181`
- 🔒 HTTP OIDC authority support for development (RequireHttpsMetadata conditionally disabled)

#### Object Type Icons (#92)

- 🖥️ Configurable icons for Metaverse Object Types — assign icons to object types, displayed across the homepage, navigation menu, schema pages, and object detail views

#### Pending Export Management

- 🖥️ Pending Export detail page with grouped attribute changes, capped multi-valued attribute loading, and server-side paginated drill-down for large change sets
- 🖥️ `Get-JIMPendingExport` and `Get-JIMConnectedSystemObject` PowerShell cmdlets with corresponding API endpoints
- 🖥️ Pending Exports list now shows display names instead of raw GUIDs

#### Activity Monitoring

- 🖥️ Auto-refresh polling on the activity list page — data updates automatically without manual refresh
- 🖥️ Pause/resume toggle for auto-refresh polling
- 🖥️ Compact determinate progress bar on the History tab for in-progress activities
- 🖥️ Phase-specific activity messages during imports — "Connecting to Connected System" and "Importing objects from Connected System" show the current phase before object processing begins (#342)

#### Run Profile Editing

- 🖥️ Run Profile editing UI — edit name, file path, partition, and page size for existing Run Profiles
- ✨ `SupportsFilePaths` connector capability — File Path fields only appear for connectors that use file-based import/export
- ✨ `SupportsPaging` connector capability — Page Size controls only appear for connectors that support paged queries

#### Navigation and Layout

- 🖥️ Browser back/forward navigation support for all tabbed pages via URL query parameters
- 🖥️ Tabs view mode for Metaverse Object details — attribute categories displayed as horizontal tabs alongside existing form and table views
- 🖥️ Expanded Target section in the Operations sidebar with type-specific links
- 🖥️ Connector capabilities grouped by category on the detail page

#### Infrastructure

- 📦 Docker healthchecks for Worker and Scheduler — file-based heartbeat monitoring detects stalled service loops (#185)
- ✨ Multi-valued to single-valued import Attribute Flow — when a multi-valued source Attribute Flows to a single-valued target, JIM automatically selects the first value and records a warning (#435)

### Performance

#### Worker Redesign (#394)

- ⚡ Parallel multi-connection writes — `ParallelBatchWriter` splits bulk database writes across N concurrent PostgreSQL connections, utilising multiple CPU cores during save phases. Configurable via `JIM_WRITE_PARALLELISM` environment variable
- ⚡ COPY binary protocol for bulk inserts — CSO creates, RPEIs, MVO creates, and sync outcomes now use PostgreSQL's COPY binary import, eliminating SQL parsing overhead and parameter limits (#338)
- ⚡ Worker-exclusive bulk SQL in `SyncRepository` — hot-path operations (RPEI persistence, CSO bulk create, Pending Export operations) moved from shared repositories into dedicated partial classes, reducing shared repo surface by 1,200+ lines

#### Import Pipeline (#427, #440)

- ⚡ Import CSO matching now uses a pre-fetched dictionary for O(1) external ID lookups, replacing N per-object database queries with a single bulk query at import start — eliminates the dominant bottleneck in full imports (#440)
- ⚡ Import reference resolution is now case-insensitive (matching RFC 4514 DN semantics) and batches sort non-referencing objects first with committed ID tracking — eliminates the expensive post-import LOWER() fixup SQL query (#427)
- ⚡ Two-phase parallel write commits CSO rows before attribute values, giving cross-partition references full FK visibility and eliminating post-import fixup queries (#427)

#### Sync and Export

- ⚡ Immediate MVO deletion (zero grace period) skips unnecessary attribute recall and export evaluation, eliminating wasted database round-trips (#390)
- ⚡ Deferred export resolution progress reporting throttled to every 50 items instead of per-item, eliminating ~540 unnecessary database round-trips for typical batches (#426)
- ⚡ Bulk RPEI and CSO change persistence timeouts increased to 300 seconds for large imports (#426)
- ⚡ Log file rolling size reduced from 500 MB to 50 MB per file (100 files retained, ~5 GB max per service)

### Fixed

- 🔒 Attribute change history is no longer cascade-deleted when a metaverse or Connected System attribute definition is removed — the FK is set to null and snapshot `AttributeName`/`AttributeType` properties preserve the audit trail indefinitely (#58)
- 🐛 Expression attribute lookups (e.g. `mv["Department"]`) are now case-insensitive, preventing silent failures when attribute name casing in expressions did not exactly match stored names (#341)
- 🐛 Pending Export reconciliation now correctly matches all 8 attribute data types — Boolean, Guid, and LongNumber exports previously failed to reconcile and appeared permanently stuck (#263)
- 🐛 Deferred export progress bar no longer shows values exceeding 100%
- 🐛 Progress bars on the History tab now update in real-time instead of freezing after initial page load
- 🐛 Worker database operations no longer time out during large imports — command timeout increased from 30s default to 300s (#426)
- 🐛 Connector-level warnings (e.g. delta import fallback) now appear as activity banners instead of phantom RPEIs with no CSO association
- 🐛 MVO reference attribute foreign keys are now reliably persisted across cross-page and cross-batch scenarios
- 🐛 MVO change tracking no longer crashes when recording deletion changes for objects with unloaded reference navigation properties

### Changed

#### Worker Redesign (#394)

- 🔄 All Worker and Workflow tests (~1,300) migrated from mocked `DbContext` to purpose-built `InMemoryData.SyncRepository`, eliminating three-way code path divergence between production, workflow tests, and unit tests
- 🔄 Removed ~32 try/catch EF fallback blocks from repository files (-642 lines) — production and test code paths are now identical

- 🔄 Object type names from camelCase LDAP schemas (e.g. `groupOfNames`) now display correctly as "Group Of Names"
- 🔄 Error type column merged inline with outcome chips on the activity detail page

## [0.7.1] - 2026-03-19

### Fixed

- 🎨 Sidebar background colour in the Navy O6 theme now matches the page background for a seamless, cohesive look

## [0.7.0] - 2026-03-19

### Added

- ✨ `GET /api/v1/userinfo` endpoint — returns the authenticated user's JIM identity, roles, and authorisation status without requiring Administrator privileges
- ✨ `Connect-JIM` now verifies authorisation after authentication and warns if the user has no JIM identity, with clear guidance to sign in via the web portal first
- 🖥️ Improved 403 error messages in the PowerShell module — now explains the likely cause (no JIM identity) and how to resolve it
- 🖥️ Properties tab on the Metaverse Object detail page — shows creation date, last modified, and clickable initiator links
- 🖥️ Form and table view toggle on the Metaverse Object detail page
- 🖥️ Server-side paginated dialog for large multi-valued attributes on the MVO detail page
- 🖥️ Object type chip prefix on reference values in MVO table view
- 🖥️ Server-side paging on the schema attributes table
- 🖥️ Sortable columns on the staging object attribute table
- ✨ Activity tracking for initial admin user creation
- 🔒 `Connect-JIM` now skips the authorisation check when using API key authentication

### Changed

- 🎨 New default theme with a refined colour palette — deeper backgrounds, improved button and chip contrast across dark and light modes, and better visual hierarchy for a more polished, readable experience
- 🎨 Switched web font to Inter — self-hosted for air-gapped deployment, delivering improved readability and a modern feel
- 🗑️ Removed legacy themes consolidated into the new default
- 🔄 "Connected System Objects" pages renamed to "Staging" with cleaner URL structure and improved introductory UX
- 🔄 "Data Generation" renamed to "Example Data" across the entire stack for consistent naming — models, API routes (`/example-data/`), PowerShell cmdlets (`Get-JIMExampleDataTemplate`, `Invoke-JIMExampleDataTemplate`), database tables, and UI all now share the "Example Data" family prefix
- ⚡ Database migrations flattened into a single `InitialCreate` migration for faster first-start performance and simpler codebase
- 🖥️ Redesigned object matching tab layout and combined status chips on the RPEI detail page

### Fixed

- 🐛 Resolved intermittent DbContext concurrency errors across all Blazor Server pages — overlapping async lifecycle methods (e.g. data load and table pagination) no longer share a single database context
- 🐛 FK violation in import change history bulk persistence no longer causes import failures
- 🐛 `HasPredefinedSearches` now returns the correct value for object types with predefined searches
- 🐛 Spurious Pending Exports no longer surface during full sync operations

#### Deleted Object Change History

- 🐛 Deleted MVO change history now shows the full timeline of prior changes (Created, AttributeFlow, Disconnected) — previously only the Deleted record was visible due to a broken FK correlation after deletion
- 🐛 Final attribute values are now captured on MVO deletion change records, showing exactly what the object looked like before it was removed
- 🐛 Final attribute values are now captured on CSO deletion change records — previously only the external ID and display name were preserved
- 🐛 MVO deletion no longer fails with FK constraint violations when the deleted object is referenced by other MVOs (e.g., as a Manager) or by change history records

#### Pending Export Reference Display (#404)

- 🐛 Pending Export reference attributes (e.g. group members) now display meaningful identifiers (DN, External ID) instead of raw GUIDs with a misleading "unresolved reference" warning
- 🐛 References to objects processed later on the same sync page are now resolved via a post-page resolution pass
- 🐛 Resolved reference attributes (e.g. group members) now appear in export causality tree attribute changes — previously they were silently dropped
- 🖥️ Pending Export references show a "Pending Export" indicator to distinguish them from fully resolved and genuinely unresolved references

#### Database Resilience (#408, #409)

- 🐛 Transient database errors now return HTTP 503 (Service Unavailable) with a `Retry-After` header instead of HTTP 400 (Bad Request)
- 🐛 Cross-batch reference fixup hardened against database timeouts and FK gaps at scale
- ⚡ Transient database failures handled gracefully at API level with retry guidance
- ⚡ Connection pool sizing reduced from 50 to 30 per service to leave headroom within PostgreSQL's `max_connections`
- 📦 Development database (`db.yml`) now explicitly sets `max_connections=200` to match the full Docker stack

### Performance

- ⚡ MVO detail page now caps multi-valued attribute values with server-side pagination, dramatically reducing load time for objects with large MVAs
- ⚡ Pending Export reconciliation query optimised with sub-phase progress messages

## [0.6.1] - 2026-03-15

### Added

- ✨ Child activity tracking — sync activities now show nested child activities with drill-down navigation (#298)
- ✨ `Clear-JIMConnectedSystem` PowerShell cmdlet — wipe all objects from a Connected System without deleting the configuration (#365)
- 🛡️ Global error boundary catches unhandled rendering exceptions in the UI — instead of a broken page, users see a friendly error message with "Try Again" and "Go to Dashboard" recovery options (#167)
- 🖥️ "Has child activities" filter on the Activities list and Operations history pages
- 🖥️ Contextual page heading icons, refined operation/outcome chip colours, and improved causality tree display
- 🔒 Log injection sanitisation across all logging calls to prevent CWE-117 log forging
- 🔒 Trivy container image scanning added to CI pipeline

### Changed

- 🔄 Built-in "Employee Status" metaverse attribute replaced with the more generic "Status"

### Fixed

- 🐛 Cross-batch and cross-run reference resolution now correctly handles out-of-order LDAP imports and foreign key persistence
- 🐛 Cross-page reference RPEIs are now merged instead of creating duplicates
- 🐛 LDAP AddRequest now chunks large multi-valued attributes to avoid directory server size limits
- 🐛 Default `userAccountControl` to 512 on Create exports via Coalesce, preventing AD account creation failures
- 🐛 Parent activity progress messages no longer overwritten by child activities
- 🐛 Activity detail page correctly reloads when navigating between parent and child activities
- 🐛 Group member change history no longer shows "(identifier not recorded)" for members imported in a later batch — the DN string is now recorded when the referenced CSO hasn't been persisted yet at change history time

### Performance

- ⚡ Change history and RPEI persistence now uses PostgreSQL COPY binary import, dramatically reducing write time for large sync operations (#398)
- ⚡ Cross-batch reference fixup skipped entirely when no unresolved references exist (#398)
- ⚡ Partial database indexes added for cross-batch reference fixup queries (#397)

## [0.6.0] - 2026-03-12

### Added

- ✨ Disconnection causality tracking — causality tree now traces MVO attribute changes and deletion fate during disconnection and recall, showing exactly what happened and why (#392)
- ✨ Reference attributes rendered as clickable links on RPEI detail page for easy navigation to related objects
- 🖥️ Filter controls on the Activities list page for quick searching by status, connector, and profile
- 🖥️ Initiated-by name now included in activity search results

### Fixed

- 🐛 Export activity detail page now shows display name for Create-type exports even after the target CSO is later deleted — display name is now snapshotted from the Pending Export's attribute changes at export time
- 🐛 Causality tree no longer shows a spurious attribute count chip on MVO Projected nodes when reference attributes were merged into the projection
- 🐛 Export runs no longer silently skip Pending Exports when a batch contains only deferred or ineligible items — all staged exports are now reliably processed in a single export run
- 🐛 Activity detail page now shows display name and object context for Create-type Pending Exports surfaced during sync (previously showed dashes as no CSO exists yet)
- 🐛 RPEI detail page now shows Pending Export attribute changes for staged (informational) Pending Exports, not only for error states
- 🐛 Causality tree no longer shows unrelated Pending Exports when a secondary import connector syncs while a previous connector's Create exports are still queued — only exports caused by the current sync's attribute changes are shown
- 🐛 Group membership exports no longer arrive empty — resolved reference foreign keys are now persisted during import
- 🐛 Resolved reference values now correctly persisted after export, preventing data loss on subsequent sync runs
- 🐛 Duplicate Pending Exports no longer accumulate — stale entries are automatically self-healed
- 🐛 Activities with unhandled errors now correctly marked as completed with error instead of appearing successful
- 🐛 Multi-valued attributes in LDAP group member exports are now consolidated into a single AddRequest, fixing partial membership writes
- 🐛 Export batch queries now include CSO object type, resolving objectClass errors in LDAP targets
- 🐛 Single-valued attribute duplicates no longer occur during Pending Export merges

### Performance

#### CSO Large MVA Pagination (#320)
- ⚡ CSO detail page and API now load capped MVA values (first 100) instead of the full collection, dramatically reducing memory and load time for objects with 10K+ multi-valued attributes
- ✨ New paginated attribute values API endpoint (`GET /api/connected-systems/{csId}/objects/{csoId}/attributes/{attributeName}/values`) with server-side search and pagination
- 🖥️ MVA dialog now fetches data on demand with server-side search and pagination — no longer holds the full value set in Blazor circuit memory
- ✨ API responses include per-attribute value summaries showing total count, returned count, and whether more values are available

#### Large-Scale Import Optimisation
- ⚡ Full import operations now handle 100K+ objects without out-of-memory failures through batch processing, raw SQL persistence, and incremental memory release
- ⚡ Export operations at scale now batch-load to eliminate EF change tracker overhead
- ⚡ Real-time batch progress reporting during large CSO persistence operations

## [0.5.0] - 2026-03-08

### Added
- ✨ Self-contained Object Matching Rules — Synchronisation Rules now carry their own matching logic for import and export, enabling fully portable rule definitions (#386)
- ✨ CRUD API endpoints for Synchronisation Rule Object Matching Rules (`GET`, `POST`, `PUT`, `DELETE` `/api/v1/synchronisation/sync-rules/{id}/matching-rules`)
- ✨ Matching mode switching API — toggle between simple and advanced object matching per Connected System
- 🖥️ Sortable Object Mapping and Capabilities columns on the Synchronisation Rules page

### Fixed
- 🐛 Setup script now correctly detects Docker Desktop alongside Docker Engine

## [0.4.0] - 2026-03-05

### Added
- ✨ One-command deployment — new interactive installer auto-detects the latest release, configures SSO and database, and starts JIM in minutes
- 📦 Production-ready Docker Compose configuration — deploy JIM from pre-built images without needing source code
- 📦 Standalone deployment files attached to each GitHub release for easy download without cloning the repository
- ✨ Welcome banner displayed on successful PowerShell connection
- 📖 Comprehensive [Deployment Guide](https://docs.junctional.io/administration/deployment/) covering prerequisites, topology options, TLS, reverse proxy, upgrades, and monitoring
- 🖥️ Sortable columns on the Attribute Flow table
- 🖥️ Filter controls on the Attribute Flow table
- ✨ Edit Attribute Flow mappings inline on the Synchronisation Rule detail page
- 🖥️ Synchronisation Rule detail page redesign with expression highlighting, table/card views, and improved layout
- 🖥️ Synchronisation Rules quick link on the homepage dashboard
- 🖥️ Filter controls on the Connected System Objects list page
- 🖥️ Full-width layout option for table-heavy pages
- 🖥️ Confirmation dialog before deleting Attribute Flow mappings
- ✨ `Get-JIMMetaverseObject -All` — automatically paginates through all results in a single command
- ✨ Pronouns attribute support (#360, #362)
- ✨ Sync Outcome Graph — full causal tracing of every change during synchronisation, showing exactly why each object was projected, joined, updated, disconnected, or exported (#363)
- ✨ Configurable sync outcome tracking level (None / Standard / Detailed) — control how much causal detail is recorded per synchronisation (#363)
- 🖥️ Colour-coded outcome summary chips on Activity Detail rows for at-a-glance sync result visibility (#363)
- 🖥️ Filter activity results by outcome type — quickly find projections, joins, Attribute Flows, exports, and more (#363)
- ✨ Export change history — drill into exactly which attributes were changed on each exported object, with before/after values
- 🔒 Hardened release pipeline with container scanning, SBOM attestation, and build validation
- 📦 Application blocks readiness until database migrations are applied

### Changed
- 🔄 Replaced "Change Type" filter with richer outcome type filtering on the Activity Detail page (#363)
- 🔄 Renamed Activity statistics labels for clarity ("Stats" → "Outcomes", "Unchanged" → "CSOs Unchanged")

### Fixed
- 🐛 `Get-JIMMetaverseObject` now correctly returns all results when page size exceeds 100
- 🐛 Fixed spurious export operations being generated for objects queued for immediate deletion
- 🐛 Activity Attribute Flow statistics now show accurate object counts instead of inflated per-attribute counts
- 🐛 Connected System Object join state now reliably persisted during synchronisation
- 🐛 Activity Detail rows now show display name and object type even after the Connected System Object has been deleted (#363)
- 🐛 OIDC `Identity.Name` now correctly resolved when claims are unmapped
- 🐛 Two-pass CSO processing prevents false `CouldNotJoinDueToExistingJoin` errors during synchronisation

### Performance
- ⚡ Sync engine performance — up to 37% faster synchronisation through optimised batch persistence of activity results (#338)

## [0.3.0] - 2026-02-25

### Added

#### Scheduler Service (#168)
- Schedule data model with cron and interval-based trigger support
- Background scheduler service with 30-second polling cycle
- Multi-step schedule execution with sequential and parallel step modes
- Schedule management REST API (CRUD, enable/disable, manual trigger, execution monitoring)
- Schedule management UI integrated into Operations page with tabbed interface
- Custom cron expression support with pattern-based UI
- Queue all schedule steps upfront for near-instant step transitions
- PowerShell cmdlets: `New-JIMSchedule`, `Get-JIMSchedule`, `Set-JIMSchedule`, `Remove-JIMSchedule`, `Enable-JIMSchedule`, `Disable-JIMSchedule`, `Add-JIMScheduleStep`, `Remove-JIMScheduleStep`, `Start-JIMSchedule`, `Get-JIMScheduleExecution`, `Stop-JIMScheduleExecution`
- Scheduler integration tests (Scenario 6)

#### Change History (#14, #269)
- Full change tracking for Metaverse Objects and Connected System Objects with timeline UI
- Initiator and mechanism tracking (User, API, Sync, System)
- Deleted objects view with change audit trail
- Configurable retention and cleanup
- Change history records for data generation operations
- Granular per-change-type statistics replacing aggregate activity stats

#### Progress Indication (#246)
- Real-time progress bars for running operations on Operations page
- Percentage tracking and contextual messages
- Progress reporting for deferred exports and cross-page reference resolution
- Import progress tracking with pagination support
- Hidden page number indicator for single-page imports

#### Dashboard
- Home page redesigned as an informative dashboard
- Hover effect on clickable dashboard cards
- Application version displayed in page footer

#### Security and Authentication
- Interactive browser-based authentication for the PowerShell module
- API key authentication support for sync endpoints
- Just-in-time initial admin creation on first sign-in (replaces startup-time creation)

#### LDAP Schema Discovery
- Attribute writability detection during schema discovery
- Support for LDAP omSyntax 66 (Object(Replica-Link)) mapping to Binary data type
- LDAP description attribute plurality override on AD SAM-managed classes

#### Data Generation
- `Split` and `Join` functions for multi-valued attribute transforms
- Centralised GUID/UUID handling with `IdentifierParser` utility

#### PowerShell Module
- Flattened module directory structure
- Version endpoint with server version display on `Connect-JIM`
- Module now includes 75 cmdlets (11 new scheduler cmdlets added to the 64 from 0.2.0)

#### UI Enhancements
- Searchable dialog for large multi-valued CSO attributes
- CSO attribute table sizing and column order improvements
- Persist navigation drawer pin state to user preferences
- Persist category expansion state per object type in user preferences
- Show all attributes on RPEI projection detail page
- Culture-aware thousand separators on all numeric statistics
- Culture-specific day-of-week ordering in schedule configuration
- Theme preview page at `/admin/theme-preview`
- Demo mode for Operations Queue

#### Integration Testing
- `-SetupOnly` flag for integration test runner
- `-CaptureMetrics` flag for performance metrics on large templates
- `-ExportConcurrency` and `-MaxExportParallelism` runner parameters
- Scenario 8: Samba AD group existence checks with retry
- `Assert-ParallelExecutionTiming` validation helper
- `jim-test-all` alias for comprehensive test runs (unit + workflow + Pester)

#### Logging and Observability
- PostgreSQL logs integrated into unified Logs UI
- Diagnostic logging for cache operations and stale entry invalidation
- Separate Disconnected RPEI recorded when processing source deletions

#### Infrastructure
- Automated Structurizr diagram export via `jim-diagrams` alias
- Review-dependabot Claude Code skill for dependency PR review

### Changed
- Purple theme refresh with vibrant logo-inspired colours
- Navy-o5 dark theme improvements
- Execution detail API returns all parallel sub-steps with `ExecutionMode` and `ConnectedSystemId`
- Expression models and `IExpressionEvaluator` moved to JIM.Models for broader use
- Change tracking built into `MetaverseServer` Create/Update methods
- JIM version injected into diagram metadata from VERSION file
- Build timestamp added to dev version suffix
- Reduced logging level for high-rate sync events to improve log readability
- Removed hardcoded `JIM_LOG_LEVEL` overrides from compose files
- Removed fixed height constraint from MVA table on MVO detail page
- Description attribute categorised under Identity on MVO detail page

### Fixed
- Cross-page reference persistence and export evaluation for `AsSplitQuery` materialisation failures
- Post-load SQL repair for `AsSplitQuery` materialisation failures
- LDAP export consolidation and drift merge for multi-valued attributes
- Null-value Update exports now correctly confirmed during reconciliation
- MVO Type included in cross-page reference resolution query
- EF Core identity conflicts during cross-page reference resolution and Pending Export reconciliation
- Pending CSO disconnections now accounted for when validating join constraints
- Connected System settings not persisting on save
- Partition column hidden on Run Profiles tab when connector doesn't support partitions
- Run Profile create/delete and dropdown positioning
- Container tree duplicates and selection not persisting
- Matching rule creation failing with duplicate key violation
- `ExecuteDeleteAsync` used for Pending Export deletion with inner exception unwrapping
- Split child/parent `SaveChanges` calls to prevent FK constraint violation
- `FindTrackedOrAttach` used for untracked Pending Export persistence
- History cleanup interval respected across worker restarts
- Scheduler waits for full application readiness on startup
- Graceful worker cancellation instead of immediate task deletion
- Transient unresolved reference warnings downgraded to debug level
- Button styling improvements and error alert panel overflow prevention
- Visited link hover colour consistency
- Log external ID instead of empty GUID for unpersisted CSOs in reference resolution
- MVA table page size wired to global user preference
- Cache diagnostic logging and stale entry invalidation on external ID changes
- Integration test runner try/finally structure repaired
- Total execution time captured in integration test log files

### Performance
- Batch database operations for export processing (single `SaveChangesAsync` per batch instead of per-object)
- Bulk reference resolution for deferred exports (single query instead of N+1)
- LDAP connector async pipelining with configurable "Export Concurrency" setting (1-16)
- Parallel batch export processing with per-system `MaxExportParallelism` setting (1-16)
- `SupportsParallelExport` connector capability flag (LDAP: true, File: false)
- Parallel schedule step execution (steps at the same index run concurrently via `Task.WhenAll`)
- Raw SQL for import and export bulk write operations (replacing EF Core bulk writes)
- Lightweight ID-only matching for MVO join lookups
- Skip CSO lookups entirely for first-ever imports on empty Connected Systems
- Service-lifetime CSO lookup index to eliminate N+1 import queries
- Tracker-aware persistence for untracked Pending Export entities
- Parallel in-memory Pending Export reconciliation using `Parallel.ForEach`
- Lightweight `AsNoTracking` query for Pending Export reconciliation
- Skip Pending Export reconciliation for CSOs without exports
- Parallel in-memory reference resolution using `Parallel.ForEach`
- Lightweight DB queries for batch reference resolution
- Raw SQL for `MarkBatchAsExecuting` status update
- Diagnostic instrumentation spans for export DB operations
- Worker heartbeat-based stale task detection and crash recovery

## [0.2.0-alpha] - 2026-01-27

### Added

#### PowerShell Module (61 new cmdlets, 64 total)
- Connected Systems management: `Get-JIMConnectedSystem`, `New-JIMConnectedSystem`, `Set-JIMConnectedSystem`, `Remove-JIMConnectedSystem`
- Schema management: `Import-JIMConnectedSystemSchema`, `Set-JIMConnectedSystemObjectType`, `Set-JIMConnectedSystemAttribute`
- Hierarchy management: `Import-JIMConnectedSystemHierarchy`
- Partition and container management: `Get-JIMConnectedSystemPartition`, `Set-JIMConnectedSystemPartition`, `Set-JIMConnectedSystemContainer`
- Connector definitions: `Get-JIMConnectorDefinition`
- Synchronisation Rules: `Get-JIMSyncRule`, `New-JIMSyncRule`, `Set-JIMSyncRule`, `Remove-JIMSyncRule`
- Synchronisation Rule Mappings with expression support: `Get-JIMSyncRuleMapping`, `New-JIMSyncRuleMapping`, `Remove-JIMSyncRuleMapping`
- Object Matching Rules: `Get-JIMMatchingRule`, `New-JIMMatchingRule`, `Set-JIMMatchingRule`, `Remove-JIMMatchingRule`
- Scoping Criteria: `Get-JIMScopingCriteria`, `New-JIMScopingCriteriaGroup`, `Set-JIMScopingCriteriaGroup`, `Remove-JIMScopingCriteriaGroup`, `New-JIMScopingCriterion`, `Remove-JIMScopingCriterion`
- Run Profiles: `Get-JIMRunProfile`, `New-JIMRunProfile`, `Set-JIMRunProfile`, `Remove-JIMRunProfile`, `Start-JIMRunProfile`
- Real-time progress tracking for Run Profile executions
- Activities: `Get-JIMActivity`, `Get-JIMActivityStats`
- Metaverse: `Get-JIMMetaverseObject`, `Get-JIMMetaverseObjectType`, `Set-JIMMetaverseObjectType`, `Get-JIMMetaverseAttribute`, `New-JIMMetaverseAttribute`, `Set-JIMMetaverseAttribute`, `Remove-JIMMetaverseAttribute`
- MVO deletion rule configuration
- API Keys: `Get-JIMApiKey`, `New-JIMApiKey`, `Set-JIMApiKey`, `Remove-JIMApiKey`
- Certificates: `Get-JIMCertificate`, `Add-JIMCertificate`, `Set-JIMCertificate`, `Remove-JIMCertificate`, `Export-JIMCertificate`, `Test-JIMCertificate`
- Security: `Get-JIMRole`
- Example Data: `Get-JIMExampleDataTemplate`, `Get-JIMExampleDataSet`, `Invoke-JIMExampleDataTemplate`
- Expressions: `Test-JIMExpression`
- History: `Get-JIMDeletedObject`, `Get-JIMHistoryCount`, `Invoke-JIMHistoryCleanup`
- Name-based parameter alternatives for all cmdlets (e.g., `-ConnectedSystemName` instead of `-ConnectedSystemId`)

#### API Endpoints
- CRUD endpoints for Connected Systems (`POST`, `PUT` `/api/v1/synchronisation/connected-systems`)
- CRUD endpoints for Synchronisation Rules (`POST`, `PUT`, `DELETE` `/api/v1/synchronisation/sync-rules`)
- CRUD endpoints for Run Profiles (`POST`, `PUT`, `DELETE` `/api/v1/synchronisation/connected-systems/{id}/run-profiles`)

#### Infrastructure
- Release workflow for automated builds and publishing
- Air-gapped deployment bundle support
- PowerShell Gallery publishing

### Changed
- Server-side filtering and sorting for MVO type list pages

## [0.1.0-alpha] - 2025-12-12

### Added

#### Core Platform
- Initial development release
- Core identity management functionality
- Blazor web interface
- REST API
- PostgreSQL database support
- Docker containerisation
- CSV connector
- Basic synchronisation engine

#### PowerShell Module (3 cmdlets)
- Initial preview release published to [PSGallery](https://www.powershellgallery.com/packages/JIM/0.1.0-alpha)
- Connection management: `Connect-JIM`, `Disconnect-JIM`, `Test-JIMConnection`

#### Infrastructure
- Release workflow for automated builds and publishing
- Air-gapped deployment bundle support
- PowerShell Gallery publishing

[Unreleased]: https://github.com/TetronIO/JIM/compare/v0.15.0...HEAD
[0.15.0]: https://github.com/TetronIO/JIM/compare/v0.14.0...v0.15.0
[0.14.0]: https://github.com/TetronIO/JIM/compare/v0.13.0...v0.14.0
[0.13.0]: https://github.com/TetronIO/JIM/compare/v0.12.0...v0.13.0
[0.12.0]: https://github.com/TetronIO/JIM/compare/v0.11.0...v0.12.0
[0.11.0]: https://github.com/TetronIO/JIM/compare/v0.10.3...v0.11.0
[0.10.3]: https://github.com/TetronIO/JIM/compare/v0.10.2...v0.10.3
[0.10.2]: https://github.com/TetronIO/JIM/compare/v0.10.1...v0.10.2
[0.10.1]: https://github.com/TetronIO/JIM/compare/v0.10.0...v0.10.1
[0.10.0]: https://github.com/TetronIO/JIM/compare/v0.9.1...v0.10.0
[0.9.1]: https://github.com/TetronIO/JIM/compare/v0.9.0...v0.9.1
[0.9.0]: https://github.com/TetronIO/JIM/compare/v0.8.1...v0.9.0
[0.8.1]: https://github.com/TetronIO/JIM/compare/v0.8.0...v0.8.1
[0.8.0]: https://github.com/TetronIO/JIM/compare/v0.7.1...v0.8.0
[0.7.1]: https://github.com/TetronIO/JIM/compare/v0.7.0...v0.7.1
[0.7.0]: https://github.com/TetronIO/JIM/compare/v0.6.1...v0.7.0
[0.6.1]: https://github.com/TetronIO/JIM/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/TetronIO/JIM/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/TetronIO/JIM/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/TetronIO/JIM/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/TetronIO/JIM/compare/v0.2.0-alpha...v0.3.0
[0.2.0-alpha]: https://github.com/TetronIO/JIM/compare/v0.1.0-alpha...v0.2.0-alpha
[0.1.0-alpha]: https://github.com/TetronIO/JIM/releases/tag/v0.1.0-alpha
