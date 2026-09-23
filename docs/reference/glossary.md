# Glossary

Key terms and concepts used throughout JIM.

Activity { #activity }
:   A logged operation (such as import, sync, or export) with its status, timing, and outcome details. Activities provide a complete audit trail of every operation JIM performs.

Attribute Flow { #attribute-flow }
:   A rule that maps an attribute between a Connected System Object and a Metaverse Object. Attribute Flows define how data moves during synchronisation, including any transformations applied via expressions.

Attribute Priority { #attribute-priority }
:   The deterministic precedence that decides which Connected System's value wins when several contribute the same Metaverse Object attribute. Each contributing inbound Synchronisation Rule holds a priority for the attribute, and the highest-priority contributor that has a value sets it, so the result never depends on the order synchronisations happen to run in.

Configuration Change History { #configuration-change-history }
:   A versioned, per-object audit of who changed a configuration entity, what changed, and when, captured as a redacted snapshot on the originating Activity. It covers every administrator-mutable configuration type and is retained on its own, typically much longer, retention period.

Configuration Change Preview { #configuration-change-preview }
:   An evaluation of a proposed configuration edit, such as a Synchronisation Rule's Scoping Criteria, Attribute Flow or Deprovisioning Action, an Object Matching Rule change, a Connected System schema or container deselection, or a Metaverse Object Type's deletion settings, against the objects JIM already holds, reporting which of them would be affected before you save. It changes nothing. See [Configuration changes](../configuration/configuration-changes.md#previewing-a-change-before-you-make-it).

Connected System { #connected-system }
:   An external directory, database, file or service that JIM synchronises identity data with, reached through a Connector. Each Connected System has its own connection settings, schema, Connector Space, Run Profiles and Synchronisation Rules. See [Connected Systems](../configuration/connected-systems.md).

Connected System Object (CSO) { #connected-system-object }
:   The object as a Connected System holds it, staged in the Connector Space: an account, a group, an HR row. Each Connected System Object corresponds to one object in that Connected System, and may be joined to a Metaverse Object once synchronisation processes it.

Connector { #connector }
:   An adapter for communicating with an external system. Each connector implements the protocol and logic required to import from and export to a specific type of data source (e.g. LDAP directories, SQL databases, SCIM 2.0 service providers, CSV files). See [Connectors](../connectors/index.md) for the built-in ones.

Connector Space { #connector-space }
:   The staging area where Connected System Objects reside before and after synchronisation. The Connector Space acts as a buffer between external systems and the Metaverse, ensuring that changes are validated before they are applied.

Container Scope { #container-scope }
:   How far beneath a selected Container objects are imported from: the whole subtree beneath it (the default), or only the objects held directly within it. Set per Container when configuring a Connected System's partitions.

Delta Synchronisation { #delta-synchronisation }
:   The Run Profile type that processes only the Connected System Objects that changed since the last Full Synchronisation or Delta Synchronisation, applying Object Matching Rules, Projection, Join and Attribute Flow. Faster than a Full Synchronisation, at the cost of relying on the Connected System's own change tracking.

Deprovisioning { #deprovisioning }
:   The process of removing or disabling accounts in target systems when an identity no longer meets the criteria for access. Deprovisioning ensures that stale or revoked accounts are cleaned up across all Connected Systems.

Deprovisioning Action { #deprovisioning-action }
:   What an export Synchronisation Rule does to a Connected System Object when its identity is deleted or leaves the rule's scope. **Disconnect** (the default) unlinks the objects and leaves the target account in place; **Delete** removes the account from the target system, whether JIM originally provisioned it or matched a pre-existing one.

Drift Correction { #drift-correction }
:   The re-application of JIM's expected values to a Connected System Object after drift detection finds that the target system's values no longer match what JIM's Attribute Flows say they should be. Attributes marked Initial Export Only are exempt.

Export { #export }
:   The Run Profile type that sends Pending Exports to a Connected System. Running an Export applies every Pending Export that is ready, provisioning, updating and deprovisioning accounts as configured by Attribute Flow and Deprovisioning Action.

Expression { #expression }
:   A formula for transforming attribute values during Attribute Flow. Expressions enable string manipulation, conditional logic, and value mapping so that data arriving from one system can be adapted to the format required by another.

Full Synchronisation { #full-synchronisation }
:   The Run Profile type that processes every Connected System Object in scope, applying Object Matching Rules, Projection, Join and Attribute Flow. Confirms the Metaverse and every Connected System agree, and re-applies configuration changes that a Delta Synchronisation does not pick up.

Grace Period { #grace-period }
:   The configurable time window before a scheduled deletion is executed. Grace periods provide a safety net, allowing administrators to recover objects that were marked for deletion before they are permanently removed.

Identity { #identity }
:   What the Metaverse manages: a person, a group, a role, the real-world thing a Metaverse Object represents. Identity is a concept the documentation explains, not the name of an object in the product; JIM's surfaces always say Metaverse Object or Connected System Object instead. See [Identities and the Metaverse](../concepts/architecture.md#identities-and-the-metaverse).

Import { #import }
:   The two Run Profile types, Full Import and Delta Import, that bring a Connected System's objects into the Connector Space. A Full Import reads everything the connection scope covers; a Delta Import reads only what has changed since the last one, where the Connected System supports it.

Initial Export Only { #initial-export-only }
:   An Attribute Flow option on export Synchronisation Rules. The attribute is set once, when JIM provisions the object, and is then treated as unmanaged: the Connected System owns the value from that point on and Drift Correction leaves it alone. Intended for initial passwords, one-time tokens, and other set-once values.

Join { #join }
:   Linking a Connected System Object to an existing Metaverse Object. A Join happens when an Object Matching Rule finds that an incoming Connected System Object corresponds to one that already exists.

Metaverse { #metaverse }
:   The central, authoritative repository within JIM. The Metaverse holds one Metaverse Object for each real-world thing JIM manages, aggregated from every Connected System via Synchronisation Rules.

Metaverse Object (MVO) { #metaverse-object }
:   The canonical object the Metaverse holds for one real-world thing JIM manages: a person's identity, a group, a role. Each Metaverse Object is assembled from the Connected System Objects that contribute values to it. See [Identities and the Metaverse](../concepts/architecture.md#identities-and-the-metaverse) for the full picture.

Null is a value { #null-is-a-value }
:   A per-contributor Attribute Priority setting. When an in-scope contributor with this setting supplies no value, JIM positively asserts "no value" and clears the attribute downstream, rather than falling through to a lower-priority source. It distinguishes a deliberate, authoritative clear from a contributor that simply has no opinion.

Object Matching Rule { #object-matching-rule }
:   A rule that decides whether an incoming Connected System Object corresponds to an existing Metaverse Object. Import matching joins a Connected System Object to a Metaverse Object; export matching joins a Metaverse Object being provisioned to an account that already exists in the target system, rather than creating a duplicate.

Obsoletion { #obsoletion }
:   The process of marking a Connected System Object as no longer existing in its source system. Obsoletion is detected during import when an object that was previously present is no longer returned by the Connected System.

Partition { #partition }
:   A logical division within a Connected System. Partitions allow JIM to scope imports and exports to specific segments of a directory or data source, such as organisational units in an LDAP directory.

Password Synchronisation { #password-synchronisation }
:   Delivery of one password change to every Connected System configured to receive it that the Metaverse Object has a Connected System Object in. Each system gets its own queued, encrypted change, delivered and retried independently by the Password Delivery Service, so one unavailable system cannot hold up the rest. Configured per Connected System. See [Passwords](../concepts/passwords.md).

Pending Export { #pending-export }
:   A queued change waiting to be sent to a target system. Pending Exports are created during synchronisation and held until an export Run Profile is executed, at which point they are applied to the Connected System.

Projection { #projection }
:   Creating a new Metaverse Object when no existing one matches an incoming Connected System Object. Projection is enabled per Synchronisation Rule.

Provisioning { #provisioning }
:   The process of creating accounts in target systems when a new identity meets the criteria defined by export Synchronisation Rules. Provisioning ensures that identities are represented in all systems where they require access.

Run Profile { #run-profile }
:   A configured operation that defines what action to perform on a Connected System. Run Profiles include Full Import, Delta Import, Full Synchronisation, Delta Synchronisation, and Export, each with configurable parameters such as page size and target partition.

Safeguards { #safeguards }
:   Optional Run Profile limits that stop one run becoming a mass change: an Export Run Profile can cap how many creates, updates and deletes it attempts, and a Full Import Run Profile can cap how many objects its deletion detection may newly mark as deleted. A run that would exceed a limit attempts none of that change type, leaves it untouched for a later run, and completes with a warning. See [Run Profiles > Safeguards](../configuration/run-profiles.md#safeguards).

Schedule { #schedule }
:   An automated sequence of ordered steps, such as Run Profile executions, that JIM runs on a cron trigger or on demand. Steps run sequentially or in parallel, and each run is recorded as a Schedule Execution. See [Schedules](../configuration/schedules.md).

Scoping { #scoping }
:   The Scoping Criteria that decide which objects a Synchronisation Rule applies to. An object outside the criteria is left alone by that rule, whatever its Attribute Flows and Object Matching Rules say. Configured per Synchronisation Rule as a filter over the object's attributes.

Service Health { #service-health }
:   Whether JIM's background services, the Worker and the Scheduler, are alive and what each is doing, read from a heartbeat each service writes every few seconds. Shown at the top of **Administration > Operations** and available through the REST API and `Get-JIMServiceHealth`. See [Operations > Service Health](../configuration/operations.md#service-health).

Standard Mappings { #standard-mappings }
:   The recorded correspondence between a Metaverse Attribute and its counterparts in the SCIM 2.0 and LDAP/Active Directory standards, with notes where the correspondence needs care. Built-in attributes come pre-populated and are kept current by JIM; you can record your own on custom attributes. Standard Mappings are guidance for choosing which attribute to target when connecting a system that speaks either standard; what actually flows between systems is determined solely by your Attribute Flows.

Sync Preview { #sync-preview }
:   A dry run of what synchronising one Connected System Object, or exporting one Metaverse Object, would do with the configuration already saved: which objects would be projected, joined, updated, deleted or deprovisioned, and why. Nothing is staged, persisted or exported. See [Sync Preview](../configuration/sync-preview.md).

Synchronisation Rule { #synchronisation-rule }
:   A complete mapping configuration between a Connected System and the metaverse. Synchronisation Rules define object type mappings, Attribute Flows, Scoping Criteria, Object Matching Rules, and the direction of data flow (inbound or outbound).

Temporal Scope Reconciliation { #temporal-scope-reconciliation }
:   A scheduled reconciliation that re-evaluates the scoping criteria of Synchronisation Rules which depend on relative dates, so objects move in or out of scope as time passes even when their own attributes have not changed.
