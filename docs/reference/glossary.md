# Glossary

Key terms and concepts used throughout JIM.

Activity
:   A logged operation (such as import, sync, or export) with its status, timing, and outcome details. Activities provide a complete audit trail of every operation JIM performs.

Attribute Flow
:   A rule that maps an attribute between a Connected System Object and a Metaverse Object. Attribute flows define how data moves during synchronisation, including any transformations applied via expressions.

Connector
:   An adapter for communicating with an external system. Each connector implements the protocol and logic required to import from and export to a specific type of data source (e.g., LDAP directories, CSV files).

Connector Space
:   The staging area where Connected System Objects reside before and after synchronisation. The connector space acts as a buffer between external systems and the metaverse, ensuring that changes are validated before they are applied.

CSO (Connected System Object)
:   An external system's representation of an identity within JIM. Each CSO is a staged copy of an object from a connected system, held in the connector space until synchronisation processes it.

Deprovisioning
:   The process of removing or disabling accounts in target systems when an identity no longer meets the criteria for access. Deprovisioning ensures that stale or revoked accounts are cleaned up across all connected systems.

Expression
:   A formula for transforming attribute values during attribute flow. Expressions enable string manipulation, conditional logic, and value mapping so that data arriving from one system can be adapted to the format required by another.

Grace Period
:   The configurable time window before a scheduled deletion is executed. Grace periods provide a safety net, allowing administrators to recover objects that were marked for deletion before they are permanently removed.

Join
:   The process of linking a Connected System Object to an existing Metaverse Object. A join occurs when JIM's object matching rules determine that an incoming CSO corresponds to an identity already represented in the metaverse.

Metaverse
:   The central authoritative identity repository within JIM. The metaverse holds the consolidated, canonical view of every identity, aggregated from all connected systems via synchronisation rules.

MVO (Metaverse Object)
:   The central identity entity stored in the metaverse. Each MVO represents a single real-world identity and may be linked to multiple Connected System Objects across different systems.

Obsoletion
:   The process of marking a Connected System Object as no longer existing in its source system. Obsoletion is detected during import when an object that was previously present is no longer returned by the connected system.

Partition
:   A logical division within a connected system. Partitions allow JIM to scope imports and exports to specific segments of a directory or data source, such as organisational units in an LDAP directory.

Pending Export
:   A queued change waiting to be sent to a target system. Pending exports are created during synchronisation and held until an export run profile is executed, at which point they are applied to the connected system.

Projection
:   The process of creating a new Metaverse Object when no existing match is found for an incoming Connected System Object. Projection establishes a new identity in the metaverse based on the sync rule configuration.

Provisioning
:   The process of creating accounts in target systems when a new identity meets the criteria defined by export sync rules. Provisioning ensures that identities are represented in all systems where they require access.

Run Profile
:   A configured operation that defines what action to perform on a connected system. Run profiles include Full Import, Delta Import, Full Sync, Delta Sync, and Export, each with configurable parameters such as page size and target partition.

Sync Rule
:   A complete mapping configuration between a connected system and the metaverse. Sync rules define object type mappings, attribute flows, scoping criteria, object matching rules, and the direction of data flow (inbound or outbound).
