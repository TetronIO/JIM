# Changelog

All notable changes to JIM (Junctional Identity Manager) will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- 🔒 The container images now build on patched .NET 10 base images, clearing CVE-2026-62901.
- 🐛 Deleting an Attribute Flow from a Synchronisation Rule over the REST API or with `Remove-JIMSyncRuleMapping` now works. Every attempt failed with an internal error, so an Attribute Flow could only be removed from the portal.
- 🐛 Moving a Container in a Connected System no longer removes it from JIM along with its selection, which took the objects beneath it out of scope and obsoleted them on the next Full Import. A moved Container is now filed under its new parent. (#1318)
- 🐛 Deletion detection now works for an Object Type anchored on a 64-bit whole number (a `bigint` identity column, for example). It has never worked: an object removed from the Connected System was recorded as "not found", the import completed successfully, and the object stayed in the Connector Space indefinitely, joined to its Metaverse Object. Nothing reported it, because a missing object and an unrecognised anchor type were indistinguishable to the code that decides. **After upgrading, run a Full Import against any Connected System whose Object Types use such an anchor**: deletions that happened while this was broken are detected on the next Full Import, and may make Metaverse Objects eligible for deletion in the synchronisation that follows. Anchors of other types were unaffected. An anchor type deletion detection cannot handle now fails the run rather than passing silently. (#1283)

- 🐛 A table anchored on a decimal key can now be imported. Oracle's `NUMBER` is read as a decimal, so this covers the ordinary sequence-backed primary key: a Full Import previously read the rows correctly and then failed outright in deletion detection, and the objects were never indexed, so every import created duplicates instead of recognising what JIM already held. Anchors are now matched by numeric value, so a key stored as `4200.00` and read back as `4200` is one object rather than two. (#1283)

- 🐛 An export no longer fails when Drift Correction and a fresh Metaverse value are staged against the same attribute of the same object. JIM combined the two into one Pending Export that both set the attribute's new value and removed the old one; the directory applied the set first, so the removal then referred to a value that was already gone and the whole change was rejected. The export retried until it exhausted its attempts, leaving the object uncorrected. A new value for an attribute now supersedes anything else staged for it. (#1199)

- 🐛 An import Synchronisation Rule's Scoping Criteria are now honoured when its Attribute Flows run, not only when it joins or projects. A Connected System Object that was in scope of any one of a system's import rules previously had **every** one of that system's import rules flowed into it, including rules it was explicitly scoped out of, so a narrowly scoped rule contributed its values to every object rather than to the subset it named. This is what gives one system authority over a defined subset of objects while another system holds the rest, so it could not be expressed until now, and a deployment that configured it got the opposite of what it asked for. A single import rule per system, which is the common arrangement, is unaffected. Two import rules on one Connected System contributing to the same Metaverse Attribute in the same synchronisation pass also no longer both write: the winner now replaces the loser's value instead of appending beside it, which on a single-valued attribute left two values. (#1199)

- 🐛 Adding, removing or retargeting an inbound Attribute Flow in the portal now maintains the target Metaverse Attribute's priority order, as the REST API and PowerShell already did. A new Attribute Flow lands at the bottom of the list so it never wins resolution until promoted, and one moved to a different attribute arrives at the bottom rather than carrying its old rank across. (#1199)

- 🐛 An export is no longer permitted into a Container narrowed to One Level, or anywhere beneath one. Container Scope narrows what an import returns, and the export scope guard still assumed a whole subtree, so JIM could write an object where its own next import would not find it: the change went unconfirmed, the Full Import treated the object as deleted, and the following synchronisation disconnected it. The same rule now answers the question for import search scope, export, and the partition and container preview. (#1251)

- 🐛 Delta Imports from OpenLDAP and other changelog-based directories no longer bring in objects a Full Import would never have returned. Those directories publish one directory-wide change log rather than letting JIM search per Container, and JIM was filtering its entries by Partition alone, ignoring which Containers within it were actually selected. A Delta Import could therefore import an object from a Container nobody had selected, which the next Full Import would then mark obsolete. Both the changelog and accesslog paths now apply the same Container selection a Full Import uses, and report how many entries they skipped. Active Directory was unaffected, as its Delta Import searches each selected Container directly. (#351)
- 🐛 Turning provisioning off on an Export Synchronisation Rule now switches its initial password off with it. Only a newly created account has never had a password, so a rule that creates none can never deliver one; the setting stayed on regardless, reading as configured while being unable to run, and the REST API refused the same combination outright. Any accounts parked waiting on those settings stop waiting. Turning provisioning back on brings the panel back with its settings intact, switched off. (#1273)
- 🐛 The portal no longer lets you save initial password settings the REST API would refuse. A Synchronisation Rule whose settings cannot produce a password the Connected System will accept, or which is set to use one password for every account and has none, now lists the reason above **Update Synchronisation Rule** and disables it, instead of saving quietly and parking every account the rule went on to provision. The panel has always said what was wrong; nothing acted on it. A static password the Connected System would refuse is no longer taken onto the rule at all: it is checked as you type it, and the stored password is left exactly as it was, which is what the REST API has always done with one. (#1273)
- 🐛 The Connector Space now shows, searches and sorts on the External Id of every Connected System Object, not just those with a text anchor. Active Directory and Samba AD objects, anchored on `objectGUID`, previously showed a blank External Id and could not be searched for. (#1286)

- 🐛 Containers discovered from a directory that publishes no name of its own are now named after themselves rather than after their whole address. Only Active Directory supplies the `name` attribute JIM asked for; against OpenLDAP and other directories it was absent and JIM fell back to the entire Distinguished Name, so every row of the Container tree read `ou=Sales,ou=Corp,dc=example,dc=com` instead of `Sales`, restating in text the ancestry the tree already draws and burying the one component that tells two Containers apart. Containers now take the value of their leaf component, which is how Containers created during an export have always been named, so the two can no longer disagree about what the same Container is called. Distinguished Names are unchanged and still identify Containers everywhere JIM records them; a directory that does publish its own name still wins. Refresh the hierarchy on an existing Connected System to rename Containers already discovered.

- 🐛 Exports are no longer refused to objects that are plainly inside a selected container. Container membership is now compared component by component rather than character by character, so a Distinguished Name written with the optional whitespace RFC 4514 permits after its separators (`CN=a, OU=Users, DC=example, DC=com`, which some directories emit) is recognised as being within `OU=Users,DC=example,DC=com`. The same comparison closes the opposite error, where `OU=UsersArchive` was read as sitting inside `OU=Users`. (#1251)

- 🐛 A Schedule Execution containing a parallel step no longer reports more steps than it has to run. Its progress counts two things: the position it has reached counts step groups, where steps running concurrently are one position, but the total was counting Schedule Step rows, so a Schedule whose six rows form five groups reported "step 5 of 6" as it finished and never reached its own total. Automation reading a Schedule Execution's progress from the REST API sees the corrected figure. Executions that have already run keep the total recorded at the time; the number of groups they had was never stored, and inferring it from what they left behind would replace a wrong figure with a guess.
- 🐛 Deleting a Connected System that still had a queued Clear Connected System Objects task no longer empties the Operations queue. Naming that task looked its Connected System up and required it to exist, so one orphaned row failed the whole read: every other task in the queue disappeared too, including the ones an administrator would have needed to see to work out what had happened.
- 🐛 A queued task whose configuration has since been deleted now names itself after what it was going to act on, rather than reporting an error. Deleting a Connected System takes its Run Profiles with it while leaving any already-queued task behind, and such a row read "Run Profile not found!", which looks like a fault in JIM rather than a row describing something that is no longer there. Clear, delete, synchronisation and example data tasks now all name themselves the same way.
- 🐛 The Operations queue no longer opens a separate database connection for every row it displays. It re-reads on every progress notification, so a busy queue was making a connection per task per update, on top of reading that second connection's settings from the container's environment rather than from the connection the rest of the read was already using. Reading an Example Data Template's summary did the same thing and no longer does.
- 🐛 A Connected System's settings can now be saved when a Connector offers an either/or choice that only applies to some configurations: the choice is now enforced only where the setting governing it says it applies, and a setting whose governing setting is itself hidden is hidden too, rather than being demanded on the strength of a value left behind by an earlier configuration.
- 🔄 An export no longer writes objects outside the containers selected on a Connected System. Container selection applied only to import, so an Export Attribute Flow that moves an account into an unselected container (moving disabled accounts to their own organisational unit, for instance) wrote it somewhere JIM could not read back: the change was never confirmed, the next Full Import treated the object as deleted, and the following synchronisation disconnected it and then orphaned or re-provisioned it, churning objects JIM had exported itself. Such an export now fails with an error naming the Distinguished Name and what to do about it, and the rest of the run proceeds. Containers the Connector creates during the run are in scope, since JIM selects them as soon as it ends. **Behaviour change:** if you deliberately export into a container you do not import from, select it before upgrading, or those exports will start failing. A Connected System with no container selections is unaffected. (#827)
- 🐛 Renaming or moving a container in a directory no longer silently takes it out of import scope. A container's identity was its Distinguished Name, which both operations rewrite, so a hierarchy refresh read the container you had selected as removed and an unfamiliar one as added; the replacement arrived unselected, and the next Full Import treated every object beneath it as deleted. JIM now identifies containers by the directory's own immutable identifier (`objectGUID` on Active Directory, `entryUUID` on OpenLDAP), so a rename is reported as a rename and your selection survives it. Containers selected before this release adopt their identifier at the next hierarchy refresh and continue to match on Distinguished Name until then. (#827)
- 🐛 A container created in a directory since the last hierarchy refresh now appears on the Partitions & Containers tab, so it can be selected. It was reported as added and then removed again by the same refresh, leaving no way to bring a new OU under management. (#827)
- 🐛 Deselecting a partition now takes effect on every Run Profile, and a Run Profile it leaves inoperable says so. Previously a partition's selection was honoured only by Run Profiles that target no partition in particular; one that named the partition kept importing it regardless, so removing a partition from management did nothing for some Run Profiles and, on a Full Import that targets no partition, obsoleted every object in it. Nothing warned either way, because the only guard asked whether the Connected System had anything selected at all, which passes while any other partition remains selected. A Run Profile that targets a deselected partition, or one no longer present in the directory, is now refused with an error naming both, and is marked **Not selected** on the Run Profiles tab, in the REST API (`targetsDeselectedPartition`) and in `Get-JIMRunProfile`. (#827)
- 🐛 An export now stops showing the Connector's step as running once the Connector has finished with it. The step was left lit until the run entered its next one, so the rail claimed objects were still being written while the run was finalising its results, and the Connector's step absorbed the time that finalising took; on a large export that made the step an administrator would look at first the one least worth trusting. Imports were corrected in the same way already. (#1214)
- 🐛 An import no longer describes the same work three times at once. A Connector's steps now finish the moment its call returns, instead of "Fetching objects" staying lit for the whole time JIM then spent matching what it had been handed; that matching is now a step of its own, "Processing imported objects", shown inside "Importing objects" because a Connected System that returns a page at a time alternates between the two. The steps inside the running step have also moved beneath the line naming it, so each reads after the thing it belongs to rather than above it. (#454)
- 🐛 An import against a Connected System JIM connects to now shows "Importing objects" as the step running while it reads them, instead of showing every step as either finished or still to come for the whole time objects are arriving. The step was entered before the connection was opened, so it was closed out milliseconds later and never reopened; a run that spent minutes reading a directory recorded that step as having taken four milliseconds. (#454)
- 🐛 A Connected System's settings can now be saved after changing a setting that governs which other settings apply. Choosing a different value in a drop-down such as the SCIM Connector's Authentication Method hid the settings the previous value needed, but left their "is required" errors behind, attached to whichever setting had moved up into their place. The form stayed invalid and **Save Settings** could never be enabled again, so a SCIM Connected System could only be configured with OAuth 2.0 Client Credentials. The settings shown were right; only the errors underneath them belonged to settings that were no longer there.
- 🐛 A SCIM service provider's refused certificate is now reported as a certificate problem wherever it surfaces. Testing a SCIM Connected System's settings showed an unhandled error instead of the certificate card, and the REST settings endpoint answered with a generic internal error rather than naming the certificate. An import or export that failed on the certificate reported an opaque transport error too, so the failed Activity carried nothing to act on; schema discovery, imports and exports now all show the certificate, as LDAP runs already did. (#545)
- 🐛 Changing which containers a Connected System imports from now asks you to confirm the change before saving, as every other synchronisation-affecting edit does. Selecting or deselecting a container saved in silence, and the change was then recorded in the configuration change history as synchronisation-affecting, so the confirmation you were shown and the record kept afterwards disagreed. Deselecting one is now treated as destructive, matching deselecting a partition or a Connected System Object Type, and says what it will do to the objects already imported through it. A container renamed in the directory is unaffected.
- 🐛 A confirmation dialog now describes a whole item added to or removed from a list (a container, a Run Profile, an Object Type) as one change, rather than listing each of its properties separately going to or from nothing. Where the removal is the destructive part, the dialog now says so ("Removing this takes objects out of scope") instead of calling the removed item a property.
- 🐛 Problems a Connector reports with an individual imported object now appear on the Activity instead of being discarded. A row whose value would not parse is imported with the values that did parse and the failure is reported against that object; an object the Connector could not identify at all is reported and skipped. Previously none of it was recorded anywhere, so an import of malformed data finished looking clean. (#637)
- 🐛 A Full Import no longer fails outright when a single imported object names an object type that is not in the Connected System's schema. The run now reports that object and imports the rest. (#637)
- 🐛 A setting withdrawn from a Connector no longer lingers on Connected Systems that already held a value for it. The setting was being detached from its Connector Definition rather than deleted, leaving the row behind with the saved values still pointing at it. (#1132)
- 🐛 Saving an LDAPS Connected System's settings, retrieving its schema, or retrieving its hierarchy no longer hangs indefinitely in the portal when certificates are present in Admin > Certificates. Reading the certificate store blocked the page's own thread waiting for work that could only run on that same thread, so the operation never finished and the page sat on its progress spinner. (#1132)
- 🐛 Retrieving or refreshing a Connected System's hierarchy from the portal no longer fails with a database error whenever it discovers a new partition or container. Saving the newly discovered items also marked the system's Connector Definition for insertion, and the save was rejected because that Connector Definition already existed. Retrieving a hierarchy through the REST API or PowerShell was unaffected.
- 🐛 A schema or hierarchy retrieval that fails now records the failure against its Activity, which finishes as failed and carries the reason. Previously the Activity was left in progress for ever with nothing recorded against it, so the Activity log showed the operation as still running and gave no indication of what went wrong.
- 🐛 Importing a schema through the REST API or PowerShell now leaves the same configuration behind as the same import through the portal. Where a Connected System offers exactly one object type and JIM is discovering it for the first time, that object type is selected automatically; only the portal did this, so the two surfaces produced different configuration from identical input.
- 🐛 The REST API's pagination depth limit now protects every paginated endpoint. Around half of them, including the Connector Space, Pending Export, attribute value and deleted object lists (the largest reads JIM performs), accepted any page number at all, so a request for a wildly out-of-range page still asked PostgreSQL to walk to that offset before returning nothing. (#487)
- 🐛 `-All -Force` on the paginated `Get-JIM*` cmdlets no longer fails part-way through with an HTTP error when a result set is larger than the API will let you page through. It now stops at the API's maximum retrieval depth with a warning saying so, after emitting everything it could retrieve. (#487)
- 🐛 `Get-JIMSyncRule` now returns every Synchronisation Rule rather than only the first 25, paging through the full result set.
- 🐛 Changes to a Connected System's connector settings, and to its Simple Mode Object Matching Rules, are now classified in the configuration change history instead of being recorded without a classification. Because the changed-since indicator reads that classification, those changes were not raising it; a Connected System could have a pending settings change and still report as up to date.
- 🐛 Adding or removing a Connected System from a Metaverse Object Type's deletion trigger list is likewise now classified, and recorded as destructive.
- 🐛 Saving a Metaverse Object Type's Deletion Rules now works. It previously failed with a database error on any object type that had attributes bound, which is every real one: the save re-inserted the type's existing attribute bindings instead of leaving them alone. Nothing was written, and the change history recorded the attempt with no detail.
- 🐛 Piping a Connected System into `Get-JIMSyncRule`, as its documentation has always shown, now works instead of failing to bind.
- 🐛 The Pending Exports list can once again be sorted by Source Identity, and searching it by an Identity's name once again returns matches. Both silently matched nothing.
- 🐛 The causality view no longer prints an object's identifier twice, as `<id> (<id>)`, when the object carries no name attribute at all.
- 🐛 Selecting a Partition for a domain the connected Active Directory / Samba AD domain controller does not host now fails the import with clear guidance, instead of silently importing nothing. Partition discovery lists every domain in the forest, but a domain controller only holds its own domain's naming context and does not chase referrals to other domains. (#230)
- 🐛 `Start-JIMSchedule -Wait` now waits for the run to finish. It compared the returned status against a number, but the API reports the status by name, so the comparison was satisfied on the first poll and the cmdlet returned immediately with the run still queued. (#1196)
- 🐛 Re-running Schema Import on a Connected System whose directory schema gained new attributes no longer fails with a duplicate key error. A failed Schema Import also no longer partially applies: previously, recording the failure could flush the half-merged schema to the database alongside the Activity's error, so an import that reported failure had still changed the schema. (#1171)
- 🐛 The portal's stylesheets and scripts now carry a content-based version stamp, so after a JIM upgrade your browser fetches the new assets immediately instead of potentially serving a stale cached copy (which could leave the interface looking or behaving as it did before the upgrade until a manual hard refresh).
- 🐛 A **Pending Export** link in the causality view no longer leads to "Pending Export not found". Pending Exports are removed once they have been exported, while the causality record naming one is permanent, so the link died as soon as the export ran. The link now points at the individual Pending Export while it is still queued and at the Connected System's queue once it is not, and the Pending Export page explains that a link which no longer resolves means the change reached its system rather than that something went wrong. (#1087)
- 🐛 A newly created Identity is no longer described as `00000000-0000-0000-0000-000000000000` in the causality view. Causality outcomes are recorded before the Identity is saved, so asking it for a label fell back to an id it had not been given yet, and the pass that fills in the real details afterwards treated the all-zero id as a name already supplied and left it alone. (#1087)
- 🐛 The Pending Deletions page and API now show identities scheduled for deletion by an authoritative source disconnecting. Previously they only listed identities whose last connector had gone, so authoritative-source deletions awaiting their grace period were invisible until they happened. (#119)
- 🐛 JIM no longer pins a domain controller it cannot reach. A directory advertises its own name, and that name does not always resolve from where JIM runs: split-horizon DNS, a directory reached through an alias or an address, and DMZ deployments all produce one that does not. Pinning it was unrecoverable rather than merely wrong, because clearing the pin on failure only led to the same unreachable name being rediscovered and pinned again on the next run, so every Active Directory or Samba AD run failed from the second connection onwards. JIM now proves a newly discovered domain controller reachable before pinning it, and where it cannot, carries on via the connection that already works and states on the Activity which name it could not reach. An established pin costs no extra connection. (#230)

### Security

- 🔒 Values imported from connected systems (Distinguished Names, identifiers, CSV fields) can no longer forge or corrupt service log entries via embedded line breaks; all such values are now sanitised before logging across the import, synchronisation, and export paths. Identity display names are no longer written to service logs at all.
- 🔒 The expression evaluation engine has been security-reviewed and hardened with defence-in-depth guardrails, with no change to expression functionality.
- 🔒 Every response from JIM now carries defence-in-depth security headers, including a Content Security Policy, clickjacking denial, and MIME-sniffing protection.
- 🔒 Every NuGet dependency, including transitive packages, is now locked to exact known-good versions, making JIM's builds reproducible and tamper-evident from source through to container image.
- 🔒 Sign-ins and API key authentication attempts now appear in the Activity audit log, with successful sign-ins logged individually and failed attempts grouped by key, IP address and reason so the log stays bounded even under a credential-spraying attack. Security events carry their own configurable retention period, defaulting to one year.
- 🔒 Patched transitive `System.Security.Cryptography.Xml` to 10.0.10 to address four newly published high-severity advisories against 10.0.9 (GHSA-23rf-6693-g89p, GHSA-8q5v-6pqq-x66h, GHSA-cvvh-rhrc-wg4q, GHSA-g8r8-53c2-pm3f); the package is pulled in via ASP.NET Core Data Protection but not used by JIM at runtime.
- 🔒 Attributes holding credential material, such as `unicodePwd` and `userPassword`, can no longer be imported, selected for management, or used in an Attribute Flow. Any that a deployment had already selected are deselected and locked rather than deleted, leaving Synchronisation Rules intact.
- 🔒 JIM no longer depends on the third-party DNParser library for LDAP Distinguished Name parsing; DN handling is now performed by a small, self-contained parser built into the LDAP Connector. This removes a Code Project Open License (CPOL) dependency, which software composition scanners commonly flag and which is not OSI-approved, along with an unmaintained package from the supply chain, in keeping with JIM's self-contained, air-gap-deployable design.

### Added

- ✨ A Container can now be **excluded** from a selection made above it: select `OU=Corp` and carve `OU=Service Accounts` out of it, rather than ticking eleven sibling OUs and hoping nobody adds a twelfth. Exclusions nest, and survive a rename. (#1255)

- ✨ A new **Data Flow** view under **Administration → Schema** lists every attribute data flow configured across all Connected Systems, in both directions, so "where does this attribute's value come from, and what writes it out?" can be answered in one place rather than by opening each Synchronisation Rule in turn. Filter by direction, Connected System, Metaverse Object Type or Metaverse Attribute, search across rules, systems, object types, attributes and expression text, or switch on **Multiple contributors** to see just the inbound flows whose Attribute Priority order actually decides something. Each row reads from the Connected System side to the Metaverse side, whichever way the value travels, and shows the setting that applies in its direction ("Null is a value" inbound, Enforce State outbound), the flow's position in its attribute's priority order, and the Synchronisation Rule that owns it. It is a discovery view: nothing is edited here. Available to automation as `Get-JIMDataFlow` and `GET api/v1/synchronisation/data-flows`. (#1199)
- ✨ How long an account stays owed its initial password is now set per Connected System, instead of a fixed seven days everywhere. Raise it before taking a system out of service for longer, and accounts provisioned meanwhile still get a password when it returns. (#1316)

- ✨ The Attribute Flow editor now shows where an inbound mapping sits in its target Metaverse Attribute's priority order, so multi-source contribution is visible at the point you create it rather than only afterwards. A new mapping is shown at the bottom of the list, which is where it is created, so it cannot start winning resolution unannounced. **Null is a value** is now settable here too, and in `New-JIMSyncRuleMapping -NullIsValue`. (#1199)
- ✨ The Partitions & Containers tab has been rebuilt around the decision it exists for. It now opens with what the Connected System actually imports (how many Containers are selected, out of how many, and how many partitions are managed) rather than with a permanent warning about a button. Container rows are named after the Container, with the Distinguished Name on hover, so a row reads `Sales` rather than `ou=Sales,ou=Corp,dc=example,dc=com`; they are roughly half their previous height; and there is a filter, which together make a directory with hundreds of OUs navigable rather than merely renderable. Container Scope is now a two-segment control showing both **Whole subtree** and **This level**, so which one is in effect is readable without hovering anything, and a Container that cannot be ticked says which selected Container already covers it instead of being greyed out with no explanation. The guidance about what a hierarchy refresh does to your selections has moved from a permanent amber alert to a line carrying the more useful fact, when the hierarchy was last retrieved, with the caveat behind a disclosure. Saving now says whether there is anything to save, and offers **Preview Changes** beside **Save Changes** rather than leaving the preview to be found separately. (#351)
- ✨ The JIM SQL Connector is now a built-in Connector, so a Connected System can be created against a relational database from the portal, the REST API and PowerShell. It reads and writes Microsoft SQL Server and Oracle Database through fully managed ADO.NET drivers, so nothing native is installed and JIM stays air-gap deployable. One Connected System covers several tables and views: each Object Type names its own table, its anchor columns, the columns that carry another object's anchor as a reference, and any related table whose rows gather onto the parent as a multi-valued attribute. A Full Import pages through a table by keyset rather than by offset, a Delta Import reads either a change-log table or a watermark column, and an Export inserts, updates and deletes rows, reading back a key the database generated or writing one JIM authored for a table keyed on a natural identifier. Date and time columns carrying no offset are interpreted in a Database Time Zone you declare, and inverted on the way out; columns that state their own offset are left alone. Two Oracle opt-ins decide whether `NUMBER(1)` columns mean Boolean and whether `RAW(16)` columns hold GUIDs, because the catalogue cannot tell either apart. The connection is encrypted by default on both providers, and the server's certificate is always validated. (#170)

- ✨ Deselecting a partition or container on a Connected System can now be previewed before it is saved. The **Preview Changes** button on the Partitions & Containers tab reports how many Connected System Objects would leave import scope, how many of those are joined and would disconnect from their Metaverse Object (taking the attribute values they contributed with them), how many Metaverse Objects those disconnections would leave eligible for automatic deletion, and how many objects JIM still holds would come back into scope. Saving afterwards states those counts on the confirmation and records which preview informed the change. Two limits are stated rather than papered over: objects JIM has never imported cannot be counted, because there is nothing to count until a Full Import discovers them; and an object JIM cannot place (one imported before partitions were recorded, or one whose Connector cannot say what container an object is in) is left out of the counts entirely rather than guessed at. Available to automation as `New-JIMConfigurationChangePreview -ConnectedSystemId` and `POST connected-systems/{id}/scope-selection/preview`. (#1251)

- ✨ A selected Container can now import only the objects held directly in it, rather than everything beneath it. Each selected Container in the Partitions tab carries a scope, shown beside it: **Whole subtree**, which is the default and how Container selection has always behaved, or **This level only**. Narrowing one releases the Containers beneath it, which become selectable in their own right and can carry their own scope, so `OU=Corp` can be imported at its own level while `OU=Sales` beneath it is imported whole and the rest of `OU=Corp`'s sub-OUs are left alone. That arrangement could not be expressed before. Narrowing takes objects out of scope exactly as deselecting those Containers would, so JIM asks you to acknowledge it before saving and says what becomes of the objects already imported. Available to automation as `Set-JIMConnectedSystemContainer -Scope` and on the Container update endpoint. (#351)
- ✨ An attribute can now be declared **settable only when JIM creates the object**, which is what a relational table's primary key actually is: JIM has to supply it when it inserts the row, and it is what ties the Connected System Object to that row from then on. Until now JIM had only "writable" and "read-only", so such an attribute had to be marked read-only to stop it being rewritten, which also stopped any export Attribute Flow from targeting it at all; a table keyed on a natural identifier such as an employee number could therefore never be provisioned into. The third state resolves that: an export Attribute Flow may target the attribute, the value flows with the Create Pending Export, and JIM never sends it again. The exclusion is enforced on the export path rather than left to configuration to get right: the attribute is kept out of every Update Pending Export **even when the Metaverse value has changed**, and Drift Correction does not re-assert a value that has diverged in the Connected System, because rewriting the key would not update the row, it would point JIM at a different one. Where a source value genuinely is reissued, JIM leaves the Connected System alone rather than quietly re-identifying an existing object. The Schema tab shows the state as "Set on creation only" with the reasoning behind it and filters by it, and the Attribute Flow editor marks affected mappings; the REST API and PowerShell report it as `WritableOnCreate` on the attribute's `writability`. No Connector reports the new state yet, so nothing changes for existing deployments. (#170)
- ✨ Automation can now ask JIM for a password rather than inventing one. `Set-JIMConnectedSystemObjectPassword -Generate` has JIM produce a password satisfying the policy it discovered on the Connected System, sets it, and returns what it used as a SecureString; that is the only chance to capture it, because JIM stores nothing and cannot return it again. Prefer it to a generator written into a script: JIM knows what the target demands, and a hand-rolled one rediscovers the passphrase trap, where three words offer two character categories against a directory that wants three. The generate endpoint is the only response body in JIM that carries a password, which is deliberate; what JIM never does is store one, or return one nobody asked for. `Set-JIMMetaverseObjectPassword -Generate` does the same across a person's accounts, and is where it matters most: one password has to satisfy the strictest of several systems at once, and those policies are not something an administrator can see in order to reason about them. JIM reconciles them, and refuses outright where no single password can satisfy them all rather than handing back one that would be accepted on the first account and refused on the second after the first has already changed. `Get-JIMConnectedSystemPasswordPolicy` and a matching endpoint report what a system will accept, which the portal has always shown and no other surface could read; a null there means JIM could not read that rule rather than that no such rule exists, so check `hasAnyDiscoveredConstraint` before treating the figures as the whole story. (#1121)
- ✨ A directory's own configuration and operational object classes no longer crowd out the ones you manage on a Connected System's Schema tab. A stock OpenLDAP publishes 67 structural classes, of which 27 belong to the server rather than to your directory: the `cn=config` backend's `olc*` classes, the accesslog overlay's `audit*` classes, and the root DSE class. The LDAP Connector now marks those Object Types internal and the Schema tab hides them, saying how many it is holding back and offering **Show internal object types** to see them. Nothing is discarded: every class is still discovered, still stored, and still selectable, and an Object Type you have already selected is never hidden. The judgement is made from the class's OID rather than its name, because an OID arc is assigned to its vendor and does not change, so a class of your own that happens to be named like a directory's internals is never affected. Active Directory needed no change, as JIM already asks it to exclude its own hidden and defunct classes. `Get-JIMConnectedSystemObjectType` applies the same default and gains `-IncludeInternal`; the REST API returns every Object Type as before, now carrying the Connector's classification tags and an `isInternal` flag so a caller can decide for itself. (#434)
- ✨ One password can now be set across several of a person's accounts at once, from that person in the portal. Choose some or all of the accounts they have whose Connector can set a password; JIM writes to each Connected System in turn, showing progress on the same stepped rail a Run Profile execution uses. **Nothing is selected by default**, so resetting a forgotten password in one system never silently resets the others. The password is generated to satisfy the strictest of the selected systems' discovered rules: the longest minimum length any of them demands, and only the character categories all of them count, since a category one system does not recognise cannot help satisfy another's complexity rule. There is no transaction across Connected Systems, so a run can end with some accounts changed and others not; JIM says which are unchanged in as many words, and offers to retry only those, reusing the password already in hand. Where a system refused the password itself, retrying it would fail identically, so the guidance offers a fresh one for every account instead, including those that already succeeded, rather than leaving the person with two different passwords. Each failure carries remediation guidance you can open, specific to what actually went wrong. Every account gets its own Activity under one parent, and a single account records no parent. Available to automation as `Set-JIMMetaverseObjectPassword`, which requires the Connected Systems to be named or `-AllAccounts` to be passed. (#1172)
- ✨ An administrator can now set the password on a single account in a Connected System, from that Connected System Object in the connector space. It is written straight to the system: nothing is staged, retried or stored. **Generate** produces a password that satisfies the policy JIM discovered on the system, and puts it behind a mask immediately; **Copy** works while it is still masked, so handing a password to the person who needs it never means putting it on a screen somebody else can read; **Reveal** is there for reading one aloud and hides itself again after thirty seconds. You can type your own instead. Choose what happens to the password afterwards and whether to enable the account at the same time; leaving the enable switch off leaves the account exactly as it was. A system that refuses the password says why, in its own words, and the dialog stays open so you can try another. Every attempt is recorded as an Activity against the account, carrying the outcome and never the password. Available to automation as `Set-JIMConnectedSystemObjectPassword` and a REST endpoint, which either take a password you supply or generate one against the discovered policy. (#1121)
#### SCIM 2.0 Client Connector (#545)

- ✨ JIM now synchronises with any system that publishes a SCIM 2.0 service provider interface, using one standards-based connector rather than one per product: it reads the provider's own schema, imports users and groups with their memberships, and exports changes back.
- ✨ Delta Imports ask the provider for only what changed since the last completed import, falling back to reading everything (and saying so) where a provider cannot filter. Deletions are detected by a Full Import, as the SCIM protocol offers no way to ask what was removed.
- ✨ Exports send only what changed, and against a provider that cannot accept partial updates JIM reads the resource and writes it back intact rather than clearing attributes it does not manage. Entity tags guard updates against overwriting a change JIM never saw.
- ✨ A SCIM connection refused over certificate trust now shows you the certificate the service provider presented, so you can check its thumbprint and add it under Admin > Certificates. That trusts one specific certificate, so a provider later presenting a different one is refused and reported.
- ✨ Rate limits are respected rather than fought: JIM honours a provider's `Retry-After`, backs off with jitter, and pauses before an allowance runs out, reporting throttling as a warning instead of failing the run.
- ✨ A schema import that had to work around gaps in what a system publishes now says so: the discovery warnings appear on the schema screen's refresh summary, and the import's Activity completes with a warning carrying the same detail for the REST API and PowerShell.
- ✨ Where a service provider advertises SCIM's Bulk endpoint, the new **Use Bulk Operations** setting sends exports a batch at a time rather than one request per object, which is considerably faster over a high-latency connection. It is off by default, because a provider that reports outcomes inaccurately would have JIM record changes as applied that were not; per-object exports are always correct, so this is a throughput choice to make once you have seen an export succeed against your provider. JIM stays inside the batch size and payload limits the provider publishes (and halves a batch a provider refuses as too large, which happens when its real limit is lower than the one it advertises), asks it to process every operation regardless of errors, matches each outcome back to the change that produced it rather than counting them off in order, and treats an operation the provider never reported on as failed rather than assuming it applied. A provider that advertises the endpoint and does not serve it falls back to one request per object for the rest of the run.

- ✨ A Connected System's details page now shows a Directory Capabilities card with the facts JIM has detected about the target system: for LDAP directories, the directory type (Active Directory, Samba AD, OpenLDAP or Generic), vendor, DNS host name, paging support, and, where applicable, the pinned domain controller and its invocation ID. These are read from data JIM already captured during a previous connection; nothing here opens a new connection or changes anything. Before the first successful connection, the card shows a subtle hint rather than an error. Available to automation via `GET /connected-systems/{id}/capabilities` and `Get-JIMConnectedSystemCapability`. (#231)
- ✨ Connected Systems that can accept passwords now show a Password Channel panel on their Schema tab, carrying the password policy JIM read from the system itself (minimum length, complexity and the character categories it means, history length, and maximum and minimum password age) and a read-only **Check password channel** button. The check sets no password on anything, so it is safe to run against production, and reports on four things: whether the connection is encrypted, whether the mechanism JIM would use is available, whether the account JIM connects as may actually reset passwords in each container it manages, and whether the password policy could be read. Each result is passed, warning, failed, or **could not tell**, the last kept deliberately distinct: a directory withholds what a caller may not see by omitting it rather than refusing, so reporting a silence as a failure would tell you an account lacks rights it demonstrably has. Where a domain has password policies that apply to only some accounts, or JIM was not permitted to find out, the discovered figures are presented as a floor rather than a guarantee. (#1121)
- 🔒 LDAPS connections to a directory now genuinely validate the certificate it presents, checking the issuer, the validity period, and that the certificate was issued for the host JIM connects to, before the service account's credentials are sent. Certificates added in Admin > Certificates are honoured for the first time: they are trusted in addition to the operating system's trust store, never in place of it, so adding one can only ever allow more connections, never fewer. An internal certificate authority or a directory's own self-signed certificate both work. Previously the validation code could not run at all in JIM's containers, so adding any certificate broke LDAPS with a connectivity error instead. (#1132)
- ✨ The Synchronisation Rules list can now be filtered by Connected System, Direction, Action (Projects, Provisions or Flow Only) and Status. The filters combine with the existing search box, which narrows whatever the filters left.
- ✨ The same filters are available to automation: `Get-JIMSyncRule` gains `-Direction`, `-ActionType` and `-Status`, and the Synchronisation Rules REST endpoint gains matching query parameters.
- ✨ The certificate a server presents can now be trusted from the failure that reported it. Selecting **Trust this certificate** has JIM read the certificate from the server again, confirm it is still the one you were shown, and add it to Trusted Certificates, so you no longer have to obtain the certificate file by other means. You confirm the thumbprint first, and where the server sent the authority that issued its certificate JIM offers that instead and recommends it, because trusting the authority survives the server's certificate being renewed. A server presenting anything other than the certificate you confirmed stops the action and shows you both thumbprints. **Fetch certificate** on a Connected System's settings does the same reading before anything has failed, so configuring a new system is not a cycle of save, fail, come back. The action is offered only where trusting genuinely fixes the failure: an expired certificate still has to be renewed, and a name mismatch still means connecting by a name the certificate carries. Available across the portal, the REST API and PowerShell (`Get-JIMConnectedSystemServerCertificate`, `Approve-JIMConnectedSystemServerCertificate`), with every addition recorded on an Activity naming who trusted it and why. (#1139)
- ✨ When an LDAPS connection to a directory fails because of the certificate it presented, JIM now shows you that certificate rather than an unhelpful "the server is unavailable": its subject, the names it was issued for, its issuer, validity dates and thumbprint, laid out as the certificate itself, alongside which check it failed and what to do about it. It appears when testing a Connected System's settings and on the failed Activity, with the same detail available to automation on the Activity's `errorDetail` field in the REST API. Nothing is trusted in order to show it, and a failure unrelated to the certificate reports exactly as before. (#1132)
- ✨ Saving a configuration change now confirms what you are about to change: a list of the properties that actually changed with their before and after values, a reminder that a Full Synchronisation is what puts them into effect, and, for changes that can delete or disconnect objects, a plain statement of what will happen. Cosmetic edits such as renaming save without a prompt. This covers Synchronisation Rules, Connected Systems (details, settings, schema and partitions), Metaverse Object Types, Metaverse Attributes and Service Settings. Deselecting a Connected System Object Type or a partition, and changing a Metaverse Object Type's deletion settings, previously saved with no confirmation at all.
- ✨ A configuration change can now be **previewed** before it is made: JIM evaluates the proposed change against the objects already in the metaverse and reports which of them would be affected, changing nothing. The first surface to offer it is a Metaverse Object Type's deletion settings, which is the change most worth asking about because it is the one that can make existing Metaverse Objects eligible for deletion the moment it is saved. **Preview Changes**, beside Save on the Deletion Rules panel, reports how many objects would become eligible for deletion, how many would stop being eligible, and how many would keep a deletion date that moves, and lets you drill into the objects behind each figure. Saving afterwards states those figures on the confirmation, and the change's Activity records which preview informed it. Edit the settings after previewing and the preview is marked stale and contributes nothing, because it now describes a different change. A preview that failed part-way shows nothing at all rather than counts over an arbitrary subset of the population. Available to automation too: `New-JIMConfigurationChangePreview` (with `-Wait`), `Get-JIMConfigurationChangePreview`, `Get-JIMConfigurationChangePreviewDelta` and `Stop-JIMConfigurationChangePreview`, plus `POST /metaverse/object-types/{id}/deletion-settings/preview` and `-PreviewActivityId` on `Set-JIMMetaverseObjectType`. (#827, #1114)
- ✨ A Configuration Change Preview now names what kind of edit a summary row describes, where it recognises one: "Email or UPN domain changed" rather than only "Email changed". It recognises a domain change on an address or User Principal Name, a move to a different container, a change of letter case alone, and text added to or removed from the start or end of a value. This is most useful exactly where the values themselves stop being readable: a row covering thousands of distinct value pairs collapses to the attribute alone, and the pattern is what still makes it reviewable. A row is only named where **every** object in it makes the same kind of edit, so a row covering a mixture is left unnamed and the objects behind it carry their own patterns in the drill-down. Surfaced in the portal, and as a `PatternKey` on both groups and rows for the REST API and `Get-JIMConfigurationChangePreview` / `Get-JIMConfigurationChangePreviewDelta`. (#827)
- ✨ Long-running Connector work now narrates itself on the Activity instead of appearing frozen: the File Connector reports loading, merging and writing during an export and rows parsed during an import, and the LDAP Connector reports its root DSE query, the container and page it is fetching, and a Delta Import's watermark queries. Object counts still only move once the Connector returns objects; the moving message is what distinguishes a healthy long phase from a stuck run. (#637)
- ✨ A Run Profile execution now shows its whole journey on the Activity, not just what it is doing this second: the run reads left to right as a stepped progress bar, with a tick and how long it took for the steps that are done, the step running now highlighted with the Connector's own steps, message and object counts beneath it, and the steps still to come greyed out. A step the run did not need (deletion detection on a Delta Import) stays on the rail with a dash and says so on hover, rather than looking outstanding; work a run could never do at all (opening a connection for a file-based import) is not shown as a step, and a failed run marks the step it failed in. The steps stay with the Activity, so a run that finished days ago still answers where its four hours went. (#454)
- ✨ Connectors can tell JIM how many objects a Connected System holds and how many they have handed over so far, so an import shows how far through it is rather than a bar with no end to it. The File Connector counts a file's records before reading them, so a file import now shows a percentage and a time remaining throughout. Importing from a directory reports objects as they arrive, so the counters move during the read instead of standing still until the whole directory has been fetched. This covers Delta Imports as well, which can run to a very large number of changes after an outage, and which previously showed no movement at all while they walked the directory's change log. Directories cannot be asked how many objects a search will return without running it twice, so those imports show the count and rate without a percentage rather than a figure JIM guessed. Connector authors are recommended to report both figures wherever their system can be asked cheaply. (#454)
- ✨ Connectors can declare the steps of their internal work, so long Connector phases appear as steps with an end in sight rather than only as a message that changes. The File Connector declares loading, merging and writing for an export and reading for an import; the LDAP Connector declares its directory query, change queries, object fetching and deleted-object queries; the SCIM Connector declares discovering the service provider and fetching resources. (#454)
- ✨ Automation sees the steps too: the Activity progress endpoint reports the current step and its position in the run, the Activity endpoint returns every step with its duration, and `Start-JIMRunProfile -Wait` and `Get-JIMActivity -Follow` display "Step 3 of 7: Saving changes" instead of a bare object count. (#454)
- ✨ Connected Systems now show when their configuration has changed in a way that needs a Full Synchronisation to take effect: an indicator in the Connected Systems list and a notice on the Connected System page, stating how many changes are waiting and warning distinctly when one of them is destructive. Cosmetic changes such as renames never raise it, and a change to a Metaverse Attribute raises it only on the systems whose Synchronisation Rules actually reference that attribute. Systems that have never completed a Full Synchronisation, and the case where configuration change tracking is switched off, are reported as such rather than as "up to date". Available to automation as `(Get-JIMConnectedSystem -Id <id>).ConfigurationDrift` and on the REST Connected System response.
- ✨ Deleting identities when an authoritative source disconnects now offers two trigger modes: "All sources disconnect" waits until every selected source has gone before deleting (the default for new configurations, so a single source system failing or being rebuilt cannot trigger deletions), while "Specific source(s) disconnect" deletes when any one of them disconnects (existing configurations keep this behaviour). Configurable on the Metaverse Object Type page, the REST API and PowerShell. (#119)
- ✨ The LDAP Connector now discovers and pins a single domain controller for Active Directory and Samba AD, instead of reconnecting via whatever a domain name Host setting happens to resolve to on each connection. On first connection, JIM records the domain controller reached via Host and pins every later connection, in and across Run Profile executions, to that same domain controller; this avoids the replication lag inconsistencies and delta import correctness risk of DNS round-robin landing on a different domain controller each run. A new optional "Preferred Domain Controller" setting lets you name a specific domain controller instead, taking priority over any pin. If the pinned domain controller becomes unreachable, the run fails outright (no mid-run failover) and the pin is cleared; the next run re-discovers and re-pins via Host, and a Full Import is needed to re-establish the delta baseline (see the existing domain controller mismatch guidance). (#230)
- ✨ The causality view on a Run Profile execution item's detail page has been redesigned: a plain-English summary sentence now tells you at a glance what happened to an object and what it caused, every event is named in plain language with the technical term alongside, and the chain can be explored in three switchable views (Flow, Timeline and Graph) with your choice remembered. Attribute change detail gains operation badges, search and filter chips, and every Connected System, Identity and Synchronisation Rule mentioned links straight to its detail page. (#1087)
- ✨ The Operations queue now shows what a running task is actually doing. Each row carries the run's steps above the running step's own progress bar, with a caption naming the step the figures measure ("Step 3 of 7: Saving changes - 12,480 / 40,000"), so a count that restarts between steps reads as the run advancing rather than progress being lost. Tasks that are not Run Profile executions are unchanged. (#1162)
- ✨ A running Schedule now shows its whole shape in the Operations queue: the header above its tasks draws it as a rail, a marker per step with the step names underneath and the step it has reached named beside them ("Step 2 of 5"), including the steps already finished. A step running several tasks at once is drawn as one divided marker carrying each task's own outcome, so one of two parallel imports failing while the other is still running reads as exactly that, even from a collapsed group. (#1162)
- ✨ Automation sees the same picture: `Get-JIMWorkerTask` and `Get-JIMScheduleExecution` each carry a `StepDisplay` reading "Step 3 of 7: Saving changes", the same sentence the portal shows, and a Schedule Execution read by ID reports its progress step by step with each concurrent task's outcome. (#1162)

#### Schedule Execution visibility (#1196)

- ✨ The Schedules list now shows how each Schedule's last run *ended*, not just when it ran, naming the step a failed run stopped on. Expanding a Schedule lists its recent executions with their outcomes.
- ✨ A new Schedule Execution view shows every step of a run with its outcome, how long it took, and a link straight to the Activity that produced it, so a failed overnight run no longer has to be pieced together from the Activity history.
- 🖥️ An Activity that a Schedule produced now says so, linking back to the run it belonged to and the step within it.
- ✨ The Activity history can now be filtered to work a Schedule produced, either any Schedule or particular ones, turning "last night failed" into "this step has been failing all week". The attribution lives on the Activity, so it survives the Schedule being deleted.
- ✨ A Synchronisation Rule can now set **one password you choose on every account it provisions**, so a new starter can be told what to sign in with. **JIM does not recommend it, and the portal says so beside the option**: every account the rule provisions shares that password until each person changes it, so anybody who learns of it can sign in as any new starter who has not, and leaving **Require a change at the next sign-in** on is what ends each account's share of it. It exists because the alternative is worse for the people who need it. Every other initial password is generated per account and stored nowhere, which is right for the password's actual job (getting the account into a state the directory will accept and enable) and leaves nobody able to tell a new starter theirs, so today that means setting a password by hand for every person. This is the only password JIM stores: it is stored encrypted and cannot be shown to you again, protected at rest exactly as a Connected System's credentials are and write-only on every surface (the portal fields are blank whenever you open them, and no REST response or cmdlet returns it), and reaches your configuration change history as a keyed hash, which shows that it changed and when without carrying the value. What JIM will tell you is that one is set and when it last changed, which is what a rotation check needs: change it whenever somebody who knew it leaves. A password the Connected System would refuse is rejected when you set it rather than parking every account the rule provisions, and a rule set to this option with no password stored is refused for the same reason. Available to automation as `Set-JIMSyncRuleInitialPassword -Source Static -StaticPassword <securestring>`, with `staticPasswordSet` and `staticPasswordSetAt` on `Get-JIMSyncRuleInitialPassword`. Delivering a generated password to somebody who should have it is the answer that replaces this one (#1252); it is not built yet. (#1273)
- ✨ JIM now says which Synchronisation Rules and Connected Systems have accounts waiting on you over their initial password, rather than leaving you to go looking. Both lists carry an amber chip counting the accounts parked against a rule and a red one counting those that expired without ever getting a password; the two are never added together, because parked work is fixed by correcting the rule's password settings and expired work cannot be fixed that way at all. A rule or system with nothing outstanding shows no chip. On the Synchronisation Rule itself, the Initial Password section carries the parked count on its heading even while collapsed, and opening it shows the accounts grouped by **what the target actually said**, biggest group first and quoted unaltered, because a directory's rejection code is the one thing precise enough to identify the fault. Correct the settings and the panel confirms, before you save, how many accounts saving will release; it stays quiet for an edit that would not change what is delivered, so it never promises a release that saving will not perform. Available to automation on `Get-JIMSyncRuleInitialPassword` (`parkedAccountCount`, `expiredAccountCount` and the grouped reasons) and on `Get-JIMConnectedSystem -Id`. (#1221)

### Changed

- 🖥️ A Configuration Change Preview now opens with what saving would do, in one sentence, worst consequence first, so a change that disconnects forty thousand objects and deletes two leads with the two deletions. Its transitions are written in English rather than derived from JIM's internal names, counts line up as a column, rows say that they open, and a grace period reads as "45 minutes" rather than "0:45:00". (#1275)
- 🔄 An account whose initial password a system refused is no longer stuck for good. When a target refuses the password itself, for not satisfying the rules in force for that account, JIM parks the account instead of retrying, because the same settings produce another password refused for the same reason. Saving a change to that Synchronisation Rule's initial password settings now releases everything parked against it, and the accounts are attempted again on the Connected System's next export run. Nothing has to be regenerated, because no password was ever stored: the retry uses the corrected settings by construction. Saving an unrelated part of the same rule releases nothing, so a rename cannot set accounts retrying against settings the target has already given its answer on. (#1221)
- 🔄 An account provisioned but never given its initial password now stops being retried after a week, and says so. It is recorded as expired rather than removed, and counted on the export's Activity ("3 expired without one"), because an account that quietly stopped being owed a password, with nothing to say so, is exactly the silent loss the rest of this behaviour exists to avoid. Accounts already waiting when this release is installed keep being retried; the expiry applies to accounts provisioned from here on. (#1221)
- 🖥️ Tooltips no longer stretch across the page. A long explanation grew to one very wide line, because nothing limited how far it could run; it now wraps into a readable block. A value with an explanation behind it now responds to the pointer as well, so it is clearer what the tooltip belongs to.
- 🔄 An account queued for removal now reports itself as **Deprovision queued** in the causality views, instead of appearing as an ordinary **Export queued** that had set a single attribute. A staged delete carries the target's identifying attribute (its distinguished name, for a directory) so the Connector can still find the entry after the record has been disconnected from its Identity; reported as a change, that made a deprovisioning cascade read as an attribute update, which is close to the opposite of what it is. Expanding one now shows those values under **Target identified by** rather than as a change count. Deprovisions still count towards an Activity's Pending Exports total, because that is what they are. Outcomes recorded before this release keep the wording they were written with. (#1087)
- 🖥️ Timeline view spaces its events evenly. A branch of nested events ended with twice the gap that separated plain ones, because a parent's own trailing space was added on top of its last child's, compounding once more at every level of nesting. (#1087)
- 🖥️ Flow view's connectors meet each block on the middle of its header row rather than a fixed distance down from its top, which missed the middle of every block and by a different amount depending on the kind. The dot at the destination end is larger, and a Connected System group's coloured accent now runs the block's full height instead of starting below its header, which also brings the header's name into line with the events beneath it. (#1087)
- 🖥️ Graph view no longer offers a click that does nothing. Every event block invited one, but selecting a block only opens its attribute detail, so a block with no attributes highlighted and then sat there, reading as a click that had failed. Those blocks are now inert, matching how Flow view has always treated the same events. (#1087)
- 🖥️ A Run Profile execution item's Execution Summary reads more evenly. The Operation and External ID values were the only two of the six fields presented as filled, colour-filled chips, so they shouted over the rest of the panel; they now match their neighbours as an icon beside plain text. The status icon that repeated after the page title has gone, since the title and the Operation field below it already say what happened. (#1087)
- 🖥️ Text throughout the causality view is now the same size as the rest of the portal. Every size in the panel had been hand-picked in pixels, most of them smaller than the site's own scale, so the whole panel read as noticeably smaller than the page around it; sizes now come from the theme. Graph nodes are correspondingly wider, so node labels still show the same amount of text at the larger size. (#1087)
- 🔄 **View deletion record** in the causality view now opens the deleted object's own change history, instead of the Deleted Objects browser with every deletion JIM has ever recorded in it. A **CSO Deleted** event now offers the same link, naming the record that was removed; it previously said only that a deletion had happened. Deleted Objects accepts the deep links directly (`/admin/deleted-objects?mvo=<id>` and `?cso=<id>`), so they can be bookmarked and shared. (#1087)
- 🔄 A **Pending Export** outcome in the causality view now links to the individual Pending Export it created, rather than to the target Connected System's whole Pending Exports queue. On a deprovisioning cascade that queue can hold thousands of rows, leaving "which of these did this event create?" as the reader's problem. (#1087)
- 🔄 Objects are now named consistently everywhere JIM shows them. A Connected System Object resolves its name from `displayName`, then `cn`, then `name`, before falling back to its external id; a Metaverse Object resolves Display Name, then Common Name. Group objects imported from LDAP and Active Directory typically carry `cn` but no `displayName`, so they previously appeared as raw identifiers (for example `1f16ccb0-1f01-1041-8be1-eb9f4cb3f25e`) in the causality view, the Connected System Objects list, Pending Exports and change history; they now show their actual name.
- 🔄 While a Run Profile is running, its live progress message now appears once, under the step it describes, instead of twice on the same page. The Activity's Message row shows the completion summary once the run has finished. (#454)
- 🔄 Search boxes across the portal now filter as you type instead of waiting for you to click away. Every list, table and dialog search box behaves the same way, with a short pause after the last keystroke so a search that queries the database is not run per character. The multi-criteria query forms on Deleted Objects and Admin > Logs are unchanged: those apply when you press Search or Refresh, as before. (#864)
- 🔄 The REST API now limits how deep you may page by rows retrieved (1,000,000) rather than by page number (1,000). The database's cost comes from the offset rather than the page number, so the old limit was four times stricter at a page size of 25 than at 100 for identical work, and it capped retrieval at roughly 100,000 objects, well below the 500,000-object scale JIM is validated at. Every request that was accepted before is still accepted; the error returned beyond the limit now names your deepest allowed page for the page size you asked for. (#487)
- 🔄 Stack traces are now hidden behind a "Show stack trace" toggle wherever JIM reports an error (Activity detail, Import Results detail, the Operations history panel and Pending Export detail), so the error message itself leads. The trace is unchanged and one click away. (#1132)
- 🔄 The LDAP Connector's "Certificate Validation" setting has been removed. Its "Skip Validation" option could never be honoured for an individual Connected System, and validation is now always applied to LDAPS connections. Where a directory's certificate is not trusted, add it in Admin > Certificates; where the certificate name does not match the host being connected to, give the JIM containers a host entry for that name (`extra_hosts` in Docker Compose) and use the name in the Host setting, rather than weakening validation. See the [LDAP Connector documentation](https://tetronio.github.io/JIM/connectors/jim-ldap-connector/) for both. (#1132)
- 🔄 A Delta Import against Active Directory or Samba AD now fails fast, with a clear error naming what changed, if it connects to a different domain controller than the one that produced the persisted USN watermark (for example, a domain name configured as Host resolving to a different DC via DNS round-robin, or the DC being restored from backup). USN watermarks are only meaningful when read back against the same DC; previously a DC change was undetected and could silently skip or re-import changes. Run a Full Import to re-establish the delta baseline. (#230)
- ✨ A **Discover...** action now sits beside the LDAP Connector's Preferred Domain Controller field, listing every domain controller in the forest with its Active Directory Site so you can pick one rather than typing a hostname blind. Discovery only ever informs; nothing is written until you select a server and save. Available for Active Directory and Samba AD Connected Systems in the portal, the REST API (`GET /connected-systems/{id}/directory-servers`) and PowerShell (`Get-JIMConnectedSystemDirectoryServer`, aliased `Get-JIMConnectedSystemDomainController`). (#1167)
- 🔄 A running Run Profile's Progress panel now states each figure once. Objects processed, throughput and time remaining were previously printed up to three times between the progress message and the lines beneath the bar, and the two sets of figures disagreed with each other because they were measured separately. They now appear as a single labelled readout under the bar, with the percentage beside it, and the progress message narrates what is happening rather than repeating the numbers. The readout says which step it measures ("Step 2 of 3: Processing Connected System Objects"), because every figure in it is reset by each step that counts its own work; without that, the time remaining read as an estimate for the whole run. A step that cannot know its total in advance, such as a paged import, now reports how many objects it has processed instead of showing an empty bar, and a step whose counter has reached its total while it finishes up says so rather than counting down to a moment that has passed. `Get-JIMActivity -Follow` and `Start-JIMRunProfile -Wait` gain the same count.
- 🔄 Exports and Synchronisations now show the steps they were performing all along. An export records **Exporting deferred changes** (its second pass, re-resolving references whose target did not exist during the first pass and writing what that makes exportable), **Selecting new containers** and **Delivering initial passwords**; previously the run went on working, and narrating what it was doing, while the rail still showed the export as the last thing that had happened. Steps a run does not need, such as the deferred pass when nothing was deferred, show as not needed rather than being hidden. A Synchronisation records **Reviewing export scope**, the pass that re-evaluates objects whose export scope drifted with the clock. Cross-page reference resolution now counts its own work rather than leaving the previous step's totals on screen, so it no longer reads as complete from the moment it starts.
- 🔄 A Connected System's **Connector Space** and **Pending Exports** are now reachable from anywhere on its page: both sit above the tabs rather than inside the Details tab, and each shows how many objects or waiting changes it holds. The page they lead to is now titled Connector Space too, matching the term used everywhere else for where Connected System Objects are staged. (#231)
- 🔄 An identity reappearing during its deletion grace period now only cancels the scheduled deletion when it undoes the disconnection that triggered it; an unrelated system reconnecting no longer rescues an identity that should still be deleted. (#119)
- 🔄 Deletion decisions are now explained from facts recorded at the moment of the decision: the Activity detail page shows the deletion rule, trigger mode, selected sources, triggering system and the date the deletion becomes due, as they were when the decision was made (staying accurate after the rules are edited), and the Pending Deletions page names which system's disconnection triggered each scheduled deletion. (#119)
- 🔄 **Breaking:** a successful Schedule Execution's status is now `Complete`, not `Completed`, matching the word Activities already use for the same outcome. The REST API and PowerShell both send and accept only `Complete`; update any script that filters on `Completed`. (#1196)

- ✨ The Activity history can now be filtered from automation as thoroughly as from the portal: `GET /api/v1/activities` and `Get-JIMActivity` gain filters for operation, outcome, status, initiator, created date range, Connected System, Run Profile and Schedule.
- ✨ Those filters combine, so "which of last week's scheduled Full Imports against Contoso AD recorded errors?" is now one call rather than a sift through pages of Activities. The portal, REST API and PowerShell run the same query, so all three answer identically.

- 🐛 Parked and expired initial password records are now cleared once they have had their retention period, 90 days by default. Nothing removed them before, so a Synchronisation Rule provisioning into a system that refuses its passwords grew one record per account for ever. (#1316)

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

- ✨ The Attribute Flow editor now shows Standard Mappings as hints while you map attributes: each Metaverse Attribute's counterpart name in the applicable standard's vocabulary sits beside it in the picker (`First Name` reads as `givenName` on an LDAP system), and once you choose a source attribute the editor names the attribute the standard says it corresponds to, marks it Suggested, and offers a one-click button to select it. Export Synchronisation Rules get the same treatment in reverse, naming the Connected System attribute that should receive the value. Which vocabulary applies comes from the Connector (the LDAP Connector declares LDAP/AD); where a Connector declares none, attribute names are matched against every standard and labelled with whichever answered. Where the standard names an attribute the mapping cannot target, the editor explains why rather than staying silent: the data types differ (and an Expression source can convert), another Attribute Flow already targets it, or the Connected System reports it read-only. Hints are advisory throughout: nothing is filtered, disabled or chosen for you, an attribute with no counterpart is not flagged as a problem, and synchronisation continues to flow exactly what your Attribute Flows say. (#1122)
- ✨ RFC references in a Connected System's schema attribute descriptions (for example "RFC2256: business category" on an LDAP Connector) are now hyperlinks to the corresponding page on the IETF Datatracker, so you can jump straight to the defining specification.
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

- 🐛 MVO Deleted and MVO Deletion Scheduled outcomes in an execution item's Causality Tree no longer render as bare labels when an out-of-scope disconnection triggered the deletion. Every such outcome now shows the deleted Identity's display name (captured before deletion), why the Metaverse Object Deletion Rule fired (for example "last connector disconnected"), the grace period for scheduled deletions, and a link to the deletion record browser, so you can see exactly which Identity was removed and why without leaving the Activity. (#1086)
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

[Unreleased]: https://github.com/TetronIO/JIM/compare/v0.14.0...HEAD
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
