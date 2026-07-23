# Changelog

All notable changes to JIM (Junctional Identity Manager) will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Security

- 🔒 Values imported from connected systems (Distinguished Names, identifiers, CSV fields) can no longer forge or corrupt service log entries via embedded line breaks; all such values are now sanitised before logging across the import, synchronisation, and export paths. Identity display names are no longer written to service logs at all.
- 🔒 The expression evaluation engine has been security-reviewed and hardened with defence-in-depth guardrails, with no change to expression functionality.
- 🔒 Every response from JIM now carries defence-in-depth security headers, including a Content Security Policy, clickjacking denial, and MIME-sniffing protection.
- 🔒 Every NuGet dependency, including transitive packages, is now locked to exact known-good versions, making JIM's builds reproducible and tamper-evident from source through to container image.
- 🔒 Sign-ins and API key authentication attempts now appear in the Activity audit log, with successful sign-ins logged individually and failed attempts grouped by key, IP address and reason so the log stays bounded even under a credential-spraying attack. Security events carry their own configurable retention period, defaulting to one year.
- 🔒 Patched transitive `System.Security.Cryptography.Xml` to 10.0.10 to address four newly published high-severity advisories against 10.0.9 (GHSA-23rf-6693-g89p, GHSA-8q5v-6pqq-x66h, GHSA-cvvh-rhrc-wg4q, GHSA-g8r8-53c2-pm3f); the package is pulled in via ASP.NET Core Data Protection but not used by JIM at runtime.
- 🔒 JIM no longer depends on the third-party DNParser library for LDAP Distinguished Name parsing; DN handling is now performed by a small, self-contained parser built into the LDAP Connector. This removes a Code Project Open License (CPOL) dependency, which software composition scanners commonly flag and which is not OSI-approved, along with an unmaintained package from the supply chain, in keeping with JIM's self-contained, air-gap-deployable design.

### Added

- ✨ New built-in Metaverse Attributes close the gap to SCIM 2.0's core and enterprise User schema, so SCIM resources map cleanly onto JIM's canonical vocabulary: the multi-valued Emails, Account Enabled (the natural boolean target for SCIM's active flag), Nickname, Preferred Language, Locale, Time Zone, Middle Name, Honorific Prefix, and Honorific Suffix. Existing deployments gain the new attributes automatically at service startup. (#1104)
- ✨ Built-in Metaverse Attributes now carry Standard Mappings: advisory metadata recording each attribute's counterpart in the SCIM 2.0 and LDAP/AD vocabularies, with notes where the correspondence has nuance. They will power Attribute Flow editor hints, connector wizard default-flow suggestions, and schema documentation; they are never consulted by the synchronisation engine, so your Attribute Flow configuration remains the single source of mapping truth. (#1104)
- ✨ Administrators can now create, rename, re-icon and delete custom Metaverse Object Types, from the Schema area of the portal, the REST API, or PowerShell. The Object Types tab gains a New Object Type button and per-row Edit, Delete and change-history actions, matching the Attributes tab beside it, with a live case-insensitive name and plural-name check as you type. The built-in User and Group types are protected: their structure cannot be renamed, re-iconed or deleted, though their deletion rules remain editable. Deleting a type is blocked while any Metaverse Object of it exists, or while any Synchronisation Rule targets it (both would otherwise be silently removed); once clear, its Predefined Searches, Example Data Template entries and attribute bindings are cascade-removed behind a type-the-name confirmation (the bound attributes themselves are kept). Every change is audited, with the cascade recorded as child Activities of the deletion.
- ✨ Administrators can now create, edit and delete custom Metaverse Attributes and bind them to Metaverse Object Types, from the Schema area of the portal, the REST API, or PowerShell. A live, case-insensitive name check prevents duplicates as you type (so "CostCentre" clashes with an existing "costCentre"), and a new attribute can be bound to one or more Object Types in the same step as creating it. Deleting an attribute or removing a binding is blocked only when Metaverse Objects hold a stored value for it; when only configuration references exist (Attribute Flows, scoping criteria, Object Matching Rules), those are cascade-removed behind a type-the-name confirmation, so schema can evolve without leaving orphaned references. Every change is audited, with the cascade recorded as child Activities of the deletion.
- ✨ You can now filter a Metaverse Object Type's list to just the objects that hold a value for a given attribute, from the portal (a `hasAttribute:` search on the object list), the REST API (a `hasAttribute` query parameter), or PowerShell (`Search-JIMMetaverseObject -HasAttribute`). The Object Type and attribute deletion flows use it to show exactly which objects are blocking a destructive action.
- ✨ The REST API is now protected by configurable rate limiting: per-client request limits with sensible defaults, tunable from Service Settings without a restart, returning standard 429 responses with Retry-After guidance. Infrastructure API keys (the trusted backend-automation key created from the `JIM_INFRASTRUCTURE_API_KEY` environment variable) are fully exempt, so CI/CD, integration tests, and bulk-configuration scripts are never throttled; ordinary API keys are limited as normal. The JIM PowerShell module honours 429 responses automatically, backing off for the indicated Retry-After interval (or bounded exponential backoff if absent) and retrying within a small budget, so a burst of cmdlet calls rides out the limit instead of failing.
- ✨ RFC references in a Connected System's schema attribute descriptions (for example "RFC2256: business category" on an LDAP Connector) are now hyperlinks to the corresponding page on the IETF Datatracker, so you can jump straight to the defining specification.
- ✨ Each Connected System can now choose how imports treat reference values that cannot be resolved, commonly because the referenced object is outside the configured Container Scope: raise an error on each affected object (the default, unchanged), complete with a single warning summary, or ignore them entirely. Configurable from the portal, the REST API, or PowerShell; unresolved values remain visible on the affected Connected System Objects and in the service log whichever mode is chosen. (#873)
- ✨ Background housekeeping that deletes Metaverse Objects whose deletion grace period has expired (and stages the resulting membership-removal Pending Exports) is now recorded as a Metaverse Object Housekeeping Activity with per-object detail: each deleted object, each staged Pending Export, and any per-object failure appears as an execution item, visible and filterable on the Activities page. The Activity's detail page presents the batch like a Run Profile execution: summary cards, outcome and object-type filters, and a searchable table of every deletion, with per-item drill-down describing what was deleted and what was staged as a result. Previously this work was only visible in service logs. A quiet housekeeping pass with nothing to delete records no Activity.
- ✨ Attribute Flows on export Synchronisation Rules can now be marked Initial Export Only: the attribute is set once when JIM provisions the object, then becomes unmanaged so the Connected System owns the value and Drift Correction leaves it alone. Ideal for initial passwords, one-time tokens, and other set-once attributes; configurable from the portal, the REST API, or PowerShell. (#223)

### Changed

- 🔄 An Attribute Flow that maps a Multi-Valued source attribute to a Single-Valued target now raises a per-object error when an object holds more than one value, instead of silently synchronising an arbitrary one. On import this previously flowed the first value (in an order the connected system does not guarantee) and recorded only a warning; on export it previously staged one change per value against a target that can hold just one, failing later at the connector or losing data. Now, when an object has more than one value, nothing flows or exports for that attribute and the object is recorded with a Multi-Valued to Single-Valued error, while its other attributes synchronise as normal; an object with a single value is unaffected. The Attribute Flow editor warns you when a mapping is Multi-Valued to Single-Valued and flags such mappings in the list, so you can target a Multi-Valued attribute or use an Expression to pick one value deterministically. This is a pre-v1.0 breaking change; review any existing Multi-Valued to Single-Valued mappings before upgrading. (#435)
- 🔄 The REST API now rejects numeric enum values on request bodies, returning a `400 Bad Request`; enum-typed fields must be sent as their string name (for example `"mode": "AllOf"`, not `"mode": 0`). This closes a gap where an out-of-range number (such as `"mode": 99`) bound to an undefined enum value that then slipped past model validation. Responses are unchanged (they already emit string names) and the JIM PowerShell module is unaffected (it sends strings); only a client hand-crafting request bodies with numeric enum ordinals is affected and must switch to the string name. This is a pre-v1.0 breaking change. (#1060)
- 🔄 Deleting an identity now deprovisions downstream accounts according to each export Synchronisation Rule's Deprovisioning Action, rather than only deleting accounts JIM originally created. Set the action to Delete and every account the rule covers is removed from the target system when its identity is deleted or leaves scope, whether JIM provisioned it or matched a pre-existing account; leave the default Disconnect and accounts are left in place, untouched. The action is now configurable in the Synchronisation Rule editor and at creation time via the API and PowerShell, and a new Downstream Deprovisioning panel on the Metaverse Object Type page shows and edits the action for every export rule targeting the type. Existing rules keep the Disconnect default, so opt in per rule where deletion is wanted. (#655)
- 🔄 The JIM PowerShell module now returns output objects with PascalCase property names (`$obj.DisplayName`, `$obj.Type.Name`), following PowerShell convention, instead of the REST API's camelCase wire casing. PowerShell member access is case-insensitive, so most scripts are unaffected, but `Get-Member`, `ConvertTo-Json` and `Format-Table` now surface PascalCase; scripts that compare property-name strings or round-trip output through JSON may need updating. Dictionaries keyed by your own data (a Metaverse Object's Attributes, a log entry's Properties) keep their keys exactly as supplied.
- 🔄 Exports now default to a conservative connector-recommended degree of parallelism when a Connected System's Max Export Parallelism isn't explicitly configured, instead of always defaulting to sequential. The LDAP Connector recommends two parallel batch pipelines for capable directories (those tuned to a high Export Concurrency) and stays sequential otherwise. An explicitly configured Max Export Parallelism value is always respected.
- 🔄 The LDAP Connector's default Modify Batch Size has been raised from 100 to 1000 values per request, cutting the number of round trips needed to export very large group memberships by an order of magnitude. Some directory servers' per-modification cost grows with a group's current size, so fewer, larger batches dramatically reduce total export time for groups with tens of thousands of members and up. Existing Connected Systems keep their stored value (the setting is captured per system at creation), so raise Modify Batch Size in the Connected System's Export settings to benefit; newly created Connected Systems use the new default. Constrained directories can still lower the setting (minimum 10). The connector documentation now also recommends OpenLDAP's `sortvals` tuning directive for deployments provisioning large groups.
- 🔄 Object Matching Rule sources no longer accept a Metaverse attribute as the source value. The shape could never function (export matching always needs a Connected System attribute to compare accounts on) and the standard rule shape, a Connected System attribute matched against the rule's Target Metaverse Attribute, now works for both import and export matching. The REST API rejects sources without a Connected System attribute, and the PowerShell matching rule cmdlets no longer offer -SourceMetaverseAttributeId. (#1053)

### Fixed

- 🐛 MVO Deleted and MVO Deletion Scheduled outcomes in an execution item's Causality Tree no longer render as bare labels when an out-of-scope disconnection triggered the deletion. Every such outcome now shows the deleted Identity's display name (captured before deletion), why the Metaverse Object Deletion Rule fired (for example "last connector disconnected"), the grace period for scheduled deletions, and a link to the deletion record browser, so you can see exactly which Identity was removed and why without leaving the Activity. (#1086)
- 🐛 Piping a Schedule into `Get-JIMScheduleExecution` now filters executions to that Schedule. Previously the piped Schedule did not bind, so the cmdlet silently returned every execution in the system whilst appearing to filter.
- 🐛 `Reset-JIMServiceSetting` now accepts Service Settings from the pipeline, as its documentation described.
- 🐛 Recording an API key's last-used timestamp can no longer surface error-level log entries when the database is briefly saturated by a large synchronisation run. The stamp was written on every authenticated API request as a tracked read-then-save on the same row, so continuous API polling during heavy import write bursts queued stamp writes behind each other until they timed out, and the resulting failures logged as errors despite being tolerated best-effort bookkeeping. The stamp is now a single set-based update, throttled to at most one write per key per 30 seconds; the last-used display is unaffected beyond that coarser precision.
- 🐛 The Activity Operations tab no longer pegs the server at 100% CPU and take many seconds (or fail) to load for Activities with tens of thousands of execution items. The page's paginated query previously loaded each item's full Connected System Object and its entire attribute-value collection into memory just to render one hundred rows, so a page landing on large groups pulled tens of thousands of member values per request; it now reads only the columns the grid needs (measured: a 26,824-item Activity page dropped from effectively unusable to about a second).
- 🐛 Very large imports no longer fail with a database statement timeout whilst Pending Exports are loaded for reconciliation. The load now runs in bounded chunks (measured: under a second per chunk at 525,000 Pending Exports with 9.8 million attribute value changes, where the previous single query exceeded the five-minute database timeout and aborted the run).
- 🐛 Very large synchronisation runs no longer fail with a database command timeout whilst change history reference links are resolved at the end of an import. Resolution now runs in bounded batches sized to stay inside the command timeout regardless of backlog, and the export stage now resolves the references its own change records create instead of leaving the entire backlog to the next import; a 500,000-user run that previously aborted at the confirming import (6.5 million unresolved references in a single statement) now completes.
- 🐛 Deleting objects that other objects reference (for example leavers who were group members) no longer leaves invisible empty entries behind on the referencing objects. Group member lists no longer show blank rows or inflated member counts after members are deleted, and later exports of a referencing group no longer stage empty attribute changes. Upgrading automatically cleans up any empty entries left behind by earlier deletions.
- 🐛 The rate and time-remaining estimate shown on a running Activity's progress now reflect recent throughput rather than a whole-run average, which misled in both directions on long runs with fast and slow phases (for example "3 obj/s, 20 hours remaining" during a phase actually processing 16 objects per second, or "2 seconds remaining" persisting for many minutes). When the progress counter stops advancing, the display now says "finishing up" instead of showing a fabricated estimate.
- 🐛 An object that left the scope of an export Synchronisation Rule and returned to scope before the deprovision executed no longer has its live target account deleted by the stale deprovision. The Pending Delete is now cancelled when the object is evaluated back in scope, even when the returning change touches only the scoping attribute and flows to no exported attribute; a scope-out staged in the same synchronisation page keeps its Delete.
- 🐛 Deprovisioning a group member no longer destroys the group's other pending exports: a group with an unexported Delete Pending Export keeps it (deprovisioning supersedes membership updates; previously the Delete was silently replaced by a membership-removal Update, leaving the group alive in the target system forever), and a group provisioned but not yet exported keeps its Create Pending Export (previously it was silently deleted, stranding the group unprovisioned).
- 🐛 Membership removals staged when deleting Metaverse Objects now appear on the synchronisation run's Activity: each staged Pending Export is reported with its referencing group's name and counted into the run's totals (previously these exports were only written to the log, so an Activity could stage thousands of removals while reporting zero Pending Exports).
- 🐛 Deleting identities referenced by many groups no longer over-reports the resulting membership-removal Pending Exports on the synchronisation run's Activity. A referencing group is now recorded once, with its single coalesced Pending Export and the full count of members removed, instead of once per synchronisation page that deleted one of its members (on a 500,000-user run this corrected a reported 21,824 Pending Exports down to the 5,421 actually staged, and removed the duplicate execution items that had bloated the Operations tab). Each membership-removal execution item now also carries its Pending Export link and the group's external ID and object type, so drill-down and identification keep working even if the group is deleted later.
- 🐛 Large exports with many reference-bearing objects (for example thousands of groups) no longer fail partway with "the connection pool has been exhausted". Each parallel export batch previously pinned a database connection for the remainder of the run, draining the pool after around 29 batches; batch resources are now released as each batch completes.
- 🐛 The activity progress shown while an export works through its deferred reference phase is now accurate. It previously restarted the processed count from zero against the full run total (for example "2,884 of 209,984"), producing a misleadingly low objects-per-second rate and a wildly inflated time-remaining estimate.
- 🐛 Exports running with Max Export Parallelism above one no longer send unresolved reference values (raw internal identifiers) to the target system. Reference resolution results are now persisted before parallel export batches execute, so every batch sees the resolved values; previously group memberships could be exported as invalid values and fail (for example "invalid per syntax" from an LDAP directory).
- 🐛 Synchronisation runs whose only outcomes were out-of-scope disconnections (for example a leaver cohort passing a relative-date scope boundary) no longer show an empty Outcomes cell in the Operations history and Activity list; new chips display out-of-scope disconnections and out-of-scope retained joins alongside the existing outcome chips.
- 🐛 Closing the browser or navigating away while an admin page with tabs (such as Operations) was open no longer records spurious Error-level "Navigation failed" and "Unhandled exception in circuit" entries in the JIM.Web log. Any remaining browser-disconnect noise from the framework is now logged at Warning, so Error entries once again indicate genuine problems.
- 🐛 LDAP Distinguished Names containing escaped separators (for example an escaped backslash immediately before a Relative Distinguished Name comma, or a comma inside a quoted value) are now parsed correctly when resolving container hierarchies and parent containers. The previous hand-rolled splitting could mis-identify the separator in these edge cases, producing an incorrect parent DN.
- 🐛 Provisioning now joins to a matching existing account in the target system instead of always creating a duplicate. Export matching previously ignored every Object Matching Rule configured through the portal, API or PowerShell, so an identity whose account already existed in the target (for example a rehire whose account was left in place by the Disconnect Deprovisioning Action, or a pre-existing account in a newly connected system) was re-provisioned, failing with errors such as "The object exists". Matching also now only ever joins to live, unclaimed objects: an account already joined to another identity, or one pending deletion, is never taken over.
- 🐛 Export matching now works for Object Matching Rules on large-number (LongNumber) attributes, such as numeric badge or employee identifiers; these previously never matched, so provisioning created duplicates. Matching rules on attribute types that cannot be matched (for example Boolean or DateTime) are now reported as a warning in the service log instead of silently doing nothing. (#1052)
- 🐛 Two identities being provisioned at the same time can no longer both join the same pre-existing target system account. The join is now claimed atomically; the identity that loses the race is provisioned a new account as normal, and the contention is recorded as a warning in the service log instead of one join being silently lost. (#1051)

### Performance

- ⚡ Watching a Run Profile execute no longer costs the database an aggregation over every execution item on each progress refresh. The Activity detail page's stats (change counts, object types, errors, outcomes) were re-derived from scratch every few seconds while a run was in progress, and again on every visit to a completed Activity (measured: around 85 minutes of cumulative database time serving progress polling over one 500,000-user run, competing with the run itself). Stats are now maintained as incremental counters while the run executes and finalised to exact values when the Activity completes, making every stats read a constant-time lookup; previously completed Activities are finalised automatically the first time they are viewed. (#1078)
- ⚡ Full Synchronisation at large scale no longer spends most of its time re-verifying large groups for drift. Comparing a group's expected and actual member sets was quadratic in membership size (measured: around 36 seconds of processor time per very large group, accounting for 35 minutes of a 52-minute confirming synchronisation at 500,000 users with 25,000 groups); the comparison is now hash-based and completes in milliseconds regardless of group size, with the original comparison retained as a fallback for mixed-type values.
- ⚡ Synchronisation runs no longer slow down page by page as they work through a large Connected System. Object pages were loaded with offset pagination, which re-scans everything before the requested page (measured: ~200ms early pages degrading to ~1.5s late ones, 16 minutes of load time across a 525,000-object synchronisation); pages are now loaded with a keyset cursor so every page costs the same regardless of position.
- ⚡ Full Imports over existing objects are significantly faster. Reference lookups previously ran one database query per imported object (535,000 round trips at 500,000 users with 25,000 groups) and are now fetched one page at a time, and an internal duplicate check that scanned a growing list once per object (quadratic across the run) is now a constant-time set lookup.
- ⚡ The worker service no longer floods the database with liveness updates while a task is running. The main loop previously spun as fast as its two status queries allowed for the entire duration of any running task (measured: 6.7 million heartbeat updates and 16.4 million connection resets over one large run, around 380 queries per second of constant background load); it now paces itself to one check every two seconds, matching its idle cadence. Post-export bookkeeping ran change detection once per changed value, which is quadratic in the number of changes; a batch containing a 100,000-member group spent over ten minutes in it while the progress display showed seconds remaining. The bookkeeping now detaches the batch in a single pass. This also fixes an export batch failure ("another instance with the same key value is already being tracked") that could abort a batch after the target system write had already succeeded.
- ⚡ Deleting Metaverse Objects that groups reference (for example a leaver cohort) is now dramatically faster. Staging the groups' membership-removal Pending Exports previously reloaded the Synchronisation Rules and every referencing group's full membership on each page of deletions, costing over 80 seconds per page against large groups; it now works set-based from the pre-deletion capture and a targeted database query, sized by the number of deletions rather than group size. A 2,000-user leaver cohort at 200,000 objects with 10,000 groups that took over 9 hours to synchronise is projected to complete in well under an hour. Recall staging also no longer piggybacks unrelated drift corrections onto membership-removal exports; drift correction remains the job of drift detection.
- ⚡ Exports no longer stall between batches at large scale. Batch collection now walks the Pending Export queue in a single pass (keyset pagination, backed by a new index) instead of rescanning it from the start for every batch; at 200,000 objects with 10,000 reference-bearing groups the old behaviour spent hours re-reading already-collected rows before the first group reached the target system.
- ⚡ The tail of a large, reference-heavy export no longer spends time paging through Pending Exports it has already identified as deferred. Once a batch turns out to be entirely reference-bearing, JIM now collects the remaining deferred exports in a single query instead of continuing to page through them 100 at a time.
- ⚡ Full Imports at large scale are dramatically faster: matched objects are no longer hydrated one database round trip at a time, confirming a large group no longer degrades quadratically with its membership size, and bulk attribute value writes now stream via PostgreSQL binary COPY instead of parameterised inserts. A Full Import of 210,000 objects that previously took over 40 minutes now completes in around 8.
- ⚡ Deprovisioning users who are members of large groups no longer slows synchronisation to a crawl. Updating or deleting a large group's pending changes no longer reloads the group's full membership from the database each time.
- ⚡ Deleting Metaverse Objects during synchronisation (0-grace-period deprovisioning) is now set-based instead of object-by-object. Each page of deletions previously cost around 75 sequential database round trips per object (per-object lookups, per-attribute change record inserts, and an unindexed audit-history update); a leaver-cohort page flush that took around 50 seconds now completes in a small, fixed number of bulk operations, and a new index covers the audit-history detach that previously scanned the whole Activities table once per deleted object.

### Removed

- 🗑️ The JIM PowerShell module's `Invoke-JIMExampleDataTemplate` cmdlet no longer has a `-Wait` parameter, since it never actually waited; monitor progress via Activities instead.

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

- 🔄 **Breaking (REST API and PowerShell):** the object type in Metaverse Object list responses is now a nested `type` object (`{ id, name }`), matching the single-object response, instead of the flat `typeId`/`typeName` properties. This affects `GET /api/v1/metaverse/objects`, the Pending Deletions list (`GET /api/v1/metaverse/pending-deletions`), and Role members (`GET /api/v1/security/roles/{id}/members`), plus the `Get-JIMMetaverseObject`, `Get-JIMPendingDeletion`, and `Get-JIMRoleMember` cmdlets that wrap them. Callers reading `.typeId`/`.typeName` on list results must switch to `.type.id`/`.type.name`. The attribute representation is deliberately left as-is (a name-to-string map on the list, typed value objects on the single object) because the shapes genuinely differ.
- 🔄 When more than one Connected System contributes to the same Metaverse attribute, JIM now resolves the value by attribute priority instead of by synchronisation timing (last-writer-wins). Single-source attributes are unaffected; existing multi-source attributes resolve deterministically until you set an explicit priority order.
- 🔄 When the source currently providing a multi-source attribute disconnects, JIM now hands the attribute to the next-priority Connected System still contributing it, rather than clearing the value. The attribute is only cleared when no other source contributes.
- 🔄 The Activity children REST endpoint (`GET /api/v1/activities/{id}/children`) and the `Get-JIMActivityChildren` cmdlet are now paged, so a parent with many child activities no longer returns them all in one response. The endpoint now returns a paged envelope instead of a bare array, and the cmdlet gains `-Page`, `-PageSize`, and `-All` (it still emits one object per child to the pipeline). `Get-JIMActivityChildren` is also now exported from the module, having previously been unreachable.
- 🔄 Attribute hand-over on source disconnection now also covers reference attributes (such as a manager) in the same synchronisation run, and correctly retains a value when the surviving source holds an identical copy; previously an identical value could be cleared until the surviving source next synchronised.
- 🔄 Attribute hand-over now also triggers when a still-connected source simply stops supplying a value: the next-priority contributor takes over in the same synchronisation run instead of the attribute staying blank until that contributor next synchronises.
- 🔄 Paginated list APIs and every `-All` auto-paginating PowerShell cmdlet now guard against runaway pagination at scale. Requesting a page beyond 1000 (roughly 100,000 objects at the maximum page size) returns a `400` rather than being silently clamped, so a caller learns they have over-paged and the database is never asked to scan such a deep offset. Correspondingly, `-All` fetches at most 1000 pages and then stops with a warning; a new `-Force` switch overrides the cap to fetch everything, and `-All` warns up front when it is used on a large result set. This now covers `Get-JIMMetaverseObject`, `Get-JIMConnectedSystemObject`, `Search-JIMMetaverseObject`, `Get-JIMMetaverseObjectChangeHistory`, `Get-JIMConnectedSystemObjectChangeHistory`, `Get-JIMConnectedSystemObjectAttributeValue`, `Get-JIMPendingExport`, `Get-JIMConfigurationChangeHistory` and `Get-JIMActivityChildren`, so no `-All` cmdlet can page unbounded or fail mid-loop against the API's page-depth cap.
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
- ✨ The PowerShell module now persists your interactive SSO sign-in across terminal sessions: after `Connect-JIM`, opening a new terminal reconnects silently without a browser. Only the refresh token is stored, in the operating system's credential store (Credential Manager on Windows, login Keychain on macOS, libsecret on Linux), with no extra password beyond your normal OS sign-in. Use `Connect-JIM -NoPersist` to opt out for a session, `-Force` to re-authenticate and overwrite the stored token, and `Disconnect-JIM` to remove the stored token for the current instance (`-Url` for a specific instance, `-All` for every instance). Headless Linux without a keyring falls back to in-memory tokens and points you to `-ApiKey`.
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

[Unreleased]: https://github.com/TetronIO/JIM/compare/v0.13.0...HEAD
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
