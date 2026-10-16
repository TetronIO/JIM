---
title: JIM LDAP Connector
description: Synchronise identities with Active Directory, OpenLDAP, 389 Directory Server and other LDAP directories, and the settings it takes.
type: object
audience: administrator
validated: v0.16.0
---

# JIM LDAP Connector

The JIM LDAP Connector enables bi-directional synchronisation with LDAP-compliant directory services. It supports a wide range of directories and provides full lifecycle management of identity objects, from importing existing data to provisioning new accounts and groups.

**Capabilities:** Full Import, Delta Import, Export, Probe

## Supported Directories

| Directory | Notes |
|-----------|-------|
| **Microsoft Active Directory (AD DS)** | Full support including USN-based delta import, userAccountControl, FILETIME dates, and binary attributes (objectGUID, objectSid) |
| **Active Directory Lightweight Directory Services (AD LDS)** | Untested. JIM treats an AD LDS instance as Active Directory. Use Full Import and export; do not rely on Delta Import, password setting or domain controller discovery, which are built for a domain and have not been tried against an instance |
| **OpenLDAP** | Full support including parallel import, accesslog-based delta import, and RFC 4512 schema discovery |
| **Samba AD** | Full support with Active Directory compatibility |
| **389 Directory Server** | Full support, verified against 389 Directory Server 3.1 by JIM's integration lab: schema discovery, Full Import, export, changelog Delta Import (deletions and renames included), password setting over LDAPS, and password policy discovery. Detected from the root DSE vendor name "389 Project", or a vendor version beginning "389-Directory". Identifies objects by `entryUUID`, imports in parallel with paged results, reads the Retro Changelog plug-in's changelog for Delta Import (see [Import](#import)) and its password policy from `cn=config` (see [Password policy discovery](jim-ldap-connector-passwords.md#password-policy-discovery)); the access to grant is under [389 Directory Server](jim-ldap-connector-permissions.md#389-directory-server) |
| **Other RFC 4512-compliant directories** | Supported via generic LDAP mode with automatic directory type detection |

JIM automatically detects the directory type during schema discovery by inspecting the Root DSE and adjusts its behaviour accordingly. No manual directory type configuration is required.

## Features

### Import

- **Full Import**<br /> Reads all objects from selected partitions and object types.
- **Delta Import**<br /> Imports only changes since the last import run.
    - **Active Directory**<br /> Uses update sequence number (USN) change tracking, against the domain controller JIM pinned.
    - **OpenLDAP**<br /> Uses the accesslog overlay (`cn=accesslog`).
    - **389 Directory Server and generic directories**<br /> Uses the directory's changelog.
    - **Every directory type**<br /> Before it queries a single change, a Delta Import confirms that the account JIM connects as can read where the directory keeps its changes. [LDAP Connector Delta Import](jim-ldap-connector-delta-import.md) explains each mechanism, and how a Delta Import fails safe.
- **Parallel imports**<br /> Configurable concurrency for OpenLDAP and generic directories, allowing multiple containers and object types to be imported simultaneously.
- **Paged results**<br /> Automatic RFC 2696 Simple Paged Results support for large directories.
- **Configurable search timeout**<br /> Control how long to wait for LDAP search results.
- **Reading values as their attribute's type**<br /> Each value is read as its attribute's data type, in the forms [How values are read](#how-values-are-read) lists.
- **Values JIM cannot read**<br /> A value that does not fit its attribute's type is reported with the entry's distinguished name, never guessed at, as [Values JIM could not import](../configuration/activity-execution-items.md#values-jim-could-not-import) describes.
- **Overlapping Object Types**<br /> An entry carrying more than one selected structural class imports as the most specific of them: with both `user` and `computer` selected, an Active Directory computer imports as `computer`, and on an RFC 4512 directory with both `person` and `inetOrgPerson` selected, an `inetOrgPerson` entry imports as `inetOrgPerson`. Schema discovery records which classes each Object Type inherits from, so this holds whatever order the directory lists an entry's classes in. Object Types discovered before JIM recorded inheritance resolve by the order instead, read from where the entry lists `top` (first on Active Directory and Samba AD, often last in an RFC 4512 directory); refresh the schema of any Connected System with overlapping Object Types selected to record inheritance. Because of this rule, selecting or deselecting an overlapping Object Type changes the Object Type that entries JIM already holds import as; JIM refuses that change by default and keeps the existing objects, as [When an object changes Object Type](../configuration/connected-system-schema.md#when-an-object-changes-object-type) explains.

### Export

- **Create, update, and delete**<br /> Operations on directory objects.
- **Configurable delete behaviour**<br /> Choose between deleting objects outright or disabling them (e.g. via userAccountControl for Active Directory).
- **New Active Directory accounts enabled by their Initial Password**<br /> Active Directory refuses to create an enabled user account that has no password. When the provisioning Synchronisation Rule has an [Initial Password](../configuration/initial-password.md), JIM creates the account disabled and the password's delivery enables it, so a `userAccountControl` Attribute Flow that enables accounts keeps working. See [Enabling new Active Directory accounts](jim-ldap-connector-passwords.md#enabling-new-active-directory-accounts).
- **Configurable concurrency**<br /> Parallel batch export support with 1-64 concurrent LDAP operations; a cancelled export run finishes its current batch.
- **Batched multi-valued modifications**<br /> Large attribute changes (e.g. group membership) are automatically split into configurable batches to avoid exceeding directory server limits.
- **Distinguished names in any case**<br /> A directory treats `DC=CONTOSO,DC=COM` and `dc=contoso,dc=com` as one name, reporting a DN in its stored case. JIM compares DNs, references such as `manager` included, ignoring case: an export is confirmed whichever case your expression writes, and the directory's case is not drift. Changing only the case of the object's name (`cn`, say) renames the entry; a case change elsewhere in the DN is not exported. See [Identifiers a target reports in its own case](../concepts/case-sensitivity.md#identifiers-a-target-reports-in-its-own-case).
- **Values already in place**<br /> Adding a value the directory already holds (a member already in the group) or removing one it no longer holds counts as done, and every other change in the same export is still applied. See [Values already in place](jim-ldap-connector-values-in-use.md#values-already-in-place).
- **Container provisioning**<br /> Optionally create organisational units (OUs) on demand when provisioning objects to new locations.
- **Group placeholder members**<br /> Automatic handling of the `groupOfNames` MUST member constraint for OpenLDAP directories.

### Schema Discovery

- **Automatic RFC 4512 schema parsing**<br /> Object classes and attributes are discovered directly from the directory's subschema subentry.
- **Data types from the directory's schema**<br /> Each attribute's data type comes from its syntax, and cannot be overridden. Active Directory's Enumeration attributes (omSyntax 10), such as `sAMAccountType` and `instanceType`, are whole numbers and are discovered as Number attributes.
- **Structural and auxiliary class support**<br /> Optionally include auxiliary classes in schema discovery.
- **Auxiliary class merging**<br /> Merge an auxiliary class's attributes into a structural Object Type so JIM can import and export them, with JIM composing each entry's `objectClass` itself. See [Auxiliary Object Classes](jim-ldap-connector-auxiliary-classes.md) below.
- **Internal class classification**<br /> Classes the directory keeps for its own configuration or operation are marked internal, and the schema screen puts them out of the way. See [Internal object types](#internal-object-types) below.
- **Partition discovery**<br /> Automatically enumerates naming contexts and organisational units.
- **Hidden partition filtering**<br /> Skip Configuration, Schema, and DNS partitions for improved performance.

### How values are read

| Data type | Read from | Reported, not imported |
|---|---|---|
| Boolean | `TRUE` or `FALSE`, in any case | Anything else, such as `yes` or `1` |
| Number | A whole number with an optional sign that fits 32 bits | Letters, or a number too large for the type |
| Long Number | A whole number with an optional sign that fits 64 bits | Letters, or a number too large for the type |
| DateTime | `GeneralizedTime` (minutes, seconds and a fraction optional; no time zone means UTC), `UTCTime` (years 50 to 99 are 19xx), or ISO 8601 with a time of day | A date that does not exist, text after the time zone, a date with no time, or a date that can be read two ways (below) |
| Guid (Active Directory `objectGUID`) | 16 bytes | Any other length, given as hexadecimal |
| Text, Reference, Binary | As the directory returns them | Never |

Every value of a multi-valued DateTime attribute is imported, such as each of the times in an OpenLDAP password policy's `pwdFailureTime`, to the microsecond. Two values that differ only below 100 nanoseconds cannot be told apart once read, so JIM reports both rather than keeping one. A Boolean holds one value: an entry holding both `TRUE` and `FALSE` has each reported rather than one chosen. Either way the values are listed under **Attribute Values Not Imported** on the object's execution item (see [Values JIM could not import](../configuration/activity-execution-items.md#values-jim-could-not-import)), and the attribute is left as JIM already held it.

A date of ten or twelve digits with a time zone can be either a `GeneralizedTime` or a `UTCTime`. JIM reads it as the one that gives a year from 1950 to 2049. If both do, as `200805121430Z` does (12 May 2008 14:30, or 5 August 2020 12:14:30), JIM does not guess and reports the value.

### Security and Connectivity

- **LDAPS (SSL/TLS)**<br /> Encrypted communication over port 636 (or custom port).
- **Certificate validation**<br /> Always on for LDAPS: the certificate chain, its validity period, and its name are all checked. Certificates added in Admin > Certificates are trusted in addition to the operating system's own trust store.
- **Authentication types**<br /> Simple bind or NTLM. For Active Directory, use a Simple bind over LDAPS: NTLM has not been tested against it, and a Windows Server 2025 domain controller refuses NTLM over an unencrypted connection by default.
- **Automatic retry**<br /> Configurable retry with exponential backoff for transient failures.

### Detected facts

The Connected System's **Details** tab shows a **Detected** strip above the form with the facts JIM has detected about the target directory:

| Fact | Shown when |
|------|------------|
| Directory Type | Always, once detected (Active Directory, Samba AD, OpenLDAP, 389 Directory Server, or Generic) |
| Vendor | The directory reported one |
| DNS Host Name | The directory reported one |
| Paging | Always, once detected (Supported / Not Supported; Samba AD reports Not Supported, see [Supported Directories](#supported-directories)) |
| Pinned Directory Server | A domain controller has been [pinned](jim-ldap-connector-topology.md#domain-controller-discovery-and-pinning) |
| Invocation Id | JIM could read the pinned domain controller's invocationId |

These are read from data JIM already captured during a previous connection; viewing them never opens a new connection to the directory. Before the first successful connection, the strip says nothing has been detected yet. It is read-only: there is nothing here to configure.

Available to automation via `GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/capabilities` and `Get-JIMConnectedSystemCapability -ConnectedSystemId <id>`.

## Connection Settings

### Connectivity

| Setting | Description | Default | Example |
|---------|-------------|---------|---------|
| Host | Hostname or IP address of the directory server. IP address is fastest. | *(required)* | `dc01.corp.local` |
| Preferred Domain Controller | Applies to Active Directory and Samba AD. A specific domain controller FQDN to always connect to. Use the **Discover...** action beside the field to list the forest's domain controllers rather than typing one blind; see [Discovering Domain Controllers](jim-ldap-connector-topology.md#discovering-domain-controllers) below. When left blank, JIM automatically discovers and pins the domain controller it reaches via Host; see [Domain Controller Discovery and Pinning](jim-ldap-connector-topology.md#domain-controller-discovery-and-pinning) below. For LDAPS, use a name present in the domain controller's certificate. | *(blank; auto-discover)* | `dc01.corp.local` |
| Port | Port for the LDAP connection. Use 389 for LDAP or 636 for LDAPS. | `389` | `636` |
| Use Secure Connection (LDAPS)? | Enable LDAPS (SSL/TLS) for encrypted communication. Certificate validation is always applied; see [Certificate validation](jim-ldap-connector-certificates.md#certificate-validation). | `false` | `true` |
| Connection Timeout | Time in seconds to wait before giving up on a connection attempt, including a server that does not answer at all (for example, behind a firewall that drops traffic). Where the host name resolves to several addresses, each address gets this long. | `10` | `30` |
| Global Catalog Server | Applies to Active Directory. The Global Catalog JIM searches when it probes for a value that must be unique across the whole forest (`userPrincipalName`, `servicePrincipalName`, `mail` and `proxyAddresses`), so a value held in another domain is found. JIM connects on port 3268, or 3269 with LDAPS, with this Connected System's credentials. When left blank, JIM uses the domain controller it connects to, if that is a Global Catalog and the forest has more than one domain. For LDAPS, use a name present in the server's certificate. See [Forest-wide attributes and the Global Catalog](jim-ldap-connector-values-in-use.md#forest-wide-attributes-and-the-global-catalog). | *(blank; the connected domain controller in a multi-domain forest)* | `gc01.corp.local` |

### Credentials

| Setting | Description | Example |
|---------|-------------|---------|
| Username | Service account username for connecting to the directory. | `corp\svc-jim-ldap` (Active Directory), `cn=svc-jim,ou=Services,dc=example,dc=com` (OpenLDAP) |
| Password | Service account password (stored encrypted). | *(encrypted)* |
| Authentication Type | Type of authentication: Simple or NTLM. Use Simple for Active Directory; see [Security and Connectivity](#security-and-connectivity). | `Simple` |

### Import Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Search Timeout | Maximum time in seconds to wait for LDAP search results. | `300` (5 minutes) |
| Import Concurrency | Number of parallel LDAP connections for OpenLDAP, 389 Directory Server and generic directory imports. Each connection handles one container and object type combination independently. Not used for Active Directory. | `4` |

### Retry Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Maximum Retries | Maximum retry attempts for transient failures. | `3` |
| Retry Delay (ms) | Initial delay between retries in milliseconds. Uses exponential backoff. | `1000` |

### Schema Discovery

| Setting | Description | Default |
|---------|-------------|---------|
| Include Auxiliary Classes | Include auxiliary object classes alongside structural classes during schema discovery. | `false` |

#### Internal object types

A directory publishes its own machinery in the same schema as the classes you manage. A stock OpenLDAP returns 67 structural classes, of which 27 belong to the server rather than to your directory: the `cn=config` backend's `olc*` classes, the accesslog overlay's `audit*` classes, and the root DSE class.

On 389 Directory Server 3.1, 78 of the 184 structural classes it publishes belong to the server: its `cn=config` and plug-in configuration, replication and changelog classes, Class of Service and role definitions, tombstone and glue entries, and the Netscape console classes. Those are marked internal. `inetOrgPerson` and the classes 389 recommends for users (`nsPerson`, `nsAccount`, `nsOrgPerson`, `nsMemberOf`) are never marked internal.

The Connector marks those Object Types **internal**, and the Schema tab hides them, telling you how many it is holding back and offering **Show internal object types** to see them. Nothing is discarded: every class is still discovered, still stored, and still selectable. An Object Type you have already selected is never hidden, whatever its classification.

The judgement is made from the class's OID rather than its name, because an OID arc is assigned to its vendor and does not change. For 389 Directory Server the vendor arc also carries `inetOrgPerson` and the recommended user classes, so JIM matches an exact list of the classes 389 ships for itself instead of the arc; still an OID, never a name. Classes carrying the RFC 4512 `OBSOLETE` flag are treated the same way, since that is the directory itself declaring them superseded. Classes from the X.500, COSINE and Internet standards arcs, and any schema extensions published under your own organisation's arc, are never marked internal.

Active Directory needs none of this: the Connector already asks the directory to exclude its own hidden and defunct classes when it enumerates them, so what you see is already the classes an administrator manages.

Automation sees the same default. `Get-JIMConnectedSystemObjectType` omits internal Object Types unless you pass `-IncludeInternal`. The REST API always returns every Object Type, each carrying the classification tags the Connector reported and an `isInternal` flag derived from them, so a caller can decide for itself.

### Hierarchy

| Setting | Description | Default |
|---------|-------------|---------|
| Skip Hidden Partitions | Skip Configuration, Schema, and DNS zone partitions when refreshing the hierarchy. Improves performance significantly. | `true` |
| Create containers as needed? | Automatically create OUs when provisioning objects to locations that do not yet exist. | `false` |

A naming context or crossRef partition the service account cannot read (a least-privilege account bound against an OpenLDAP server hosting several suffixes, say) is skipped rather than failing the whole hierarchy import, and named in the Worker log alongside the LDAP result code so you can tell which partition to grant access to. Only every naming context being unreadable fails the import outright.

### Export Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Delete Behaviour | How to handle object deletions: Delete (remove the object) or Disable (set the disable attribute). | `Delete` |
| Disable Attribute | Attribute to set when disabling objects. Only shown, and required, when Delete Behaviour is Disable. | `userAccountControl` |
| Export Concurrency | Maximum number of concurrent LDAP operations during export. Recommended range: 2 to 8. | `4` |
| Modify Batch Size | Maximum number of values per multi-valued attribute modification in a single LDAP request. Lower values improve compatibility; higher values improve throughput, especially for very large groups. Recommended range: 100 to 2000. | `1000` |
| Group Placeholder Member DN | Placeholder DN used for group classes that require at least one member (e.g. groupOfNames). Automatically filtered during import. Only applies to non-AD directories. | `cn=placeholder` |

## Troubleshooting

[Troubleshooting the LDAP Connector](jim-ldap-connector-troubleshooting.md) covers connection, sign-in, import, Delta Import and export failures, each by the message it shows.

## Related

- [LDAP Connector Delta Import](jim-ldap-connector-delta-import.md): how each directory type reports its changes, and the checks a Delta Import makes first.
- [LDAP Connector Values Already in Use](jim-ldap-connector-values-in-use.md): values already in place, and probing for values already in use.
- [LDAP Connector Auxiliary Object Classes](jim-ldap-connector-auxiliary-classes.md): merging auxiliary classes into an Object Type.
- [LDAP Connector Domain Controllers and Containers](jim-ldap-connector-topology.md): domain controllers, forests, referrals and Container Scope.
- [LDAP Connector Certificates](jim-ldap-connector-certificates.md): LDAPS and certificate validation.
- [LDAP Connector Passwords](jim-ldap-connector-passwords.md): setting passwords, and the password policy JIM discovers.
- [LDAP Connector Service Account Permissions](jim-ldap-connector-permissions.md): the rights the service account needs.
