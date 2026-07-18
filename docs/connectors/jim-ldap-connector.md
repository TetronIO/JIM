# JIM LDAP Connector

## Overview

The JIM LDAP Connector enables bi-directional synchronisation with LDAP-compliant directory services. It supports a wide range of directories and provides full lifecycle management of identity objects -- from importing existing data to provisioning new accounts and groups.

**Capabilities:** Full Import, Delta Import, Export

## Supported Directories

| Directory | Notes |
|-----------|-------|
| **Microsoft Active Directory (AD DS)** | Full support including USN-based delta import, userAccountControl, FILETIME dates, and binary attributes (objectGUID, objectSid) |
| **Active Directory Lightweight Directory Services (AD LDS)** | Full support with AD-specific features |
| **OpenLDAP** | Full support including parallel import, changelog-based delta import, and RFC 4512 schema discovery |
| **389 Directory Server** | Full support including changelog-based delta import |
| **Samba AD** | Full support with Active Directory compatibility |
| **Other RFC 4512-compliant directories** | Supported via generic LDAP mode with automatic directory type detection |

JIM automatically detects the directory type during schema discovery by inspecting the Root DSE and adjusts its behaviour accordingly. No manual directory type configuration is required.

## Features

### Import

- **Full Import**<br /> Reads all objects from selected partitions and object types.
- **Delta Import**<br /> Imports only changes since the last import run.
    - **Active Directory**<br /> Uses USN (Update Sequence Number) change tracking.
    - **OpenLDAP / 389 DS**<br /> Uses the changelog overlay (accesslog).
- **Parallel imports**<br /> Configurable concurrency for OpenLDAP and generic directories, allowing multiple containers and object types to be imported simultaneously.
- **Paged results**<br /> Automatic RFC 2696 Simple Paged Results support for large directories.
- **Configurable search timeout**<br /> Control how long to wait for LDAP search results.

### Export

- **Create, update, and delete**<br /> Operations on directory objects.
- **Configurable delete behaviour**<br /> Choose between deleting objects outright or disabling them (e.g. via userAccountControl for Active Directory).
- **Configurable concurrency**<br /> Parallel batch export support with 1-64 concurrent LDAP operations.
- **Batched multi-valued modifications**<br /> Large attribute changes (e.g. group membership) are automatically split into configurable batches to avoid exceeding directory server limits.
- **Container provisioning**<br /> Optionally create organisational units (OUs) on demand when provisioning objects to new locations.
- **Group placeholder members**<br /> Automatic handling of the `groupOfNames` MUST member constraint for OpenLDAP directories.

### Schema Discovery

- **Automatic RFC 4512 schema parsing**<br /> Object classes and attributes are discovered directly from the directory's subschema subentry.
- **Structural and auxiliary class support**<br /> Optionally include auxiliary classes in schema discovery.
- **Partition discovery**<br /> Automatically enumerates naming contexts and organisational units.
- **Hidden partition filtering**<br /> Skip Configuration, Schema, and DNS partitions for improved performance.

### Security and Connectivity

- **LDAPS (SSL/TLS)**<br /> Encrypted communication over port 636 (or custom port).
- **Certificate validation**<br /> Full validation against system CA store and JIM-managed certificates, with an option to skip validation for testing.
- **Authentication types**<br /> Simple bind or NTLM authentication.
- **Automatic retry**<br /> Configurable retry with exponential backoff for transient failures.

## Connection Settings

### Connectivity

| Setting | Description | Default | Example |
|---------|-------------|---------|---------|
| Host | Hostname or IP address of the directory server. IP address is fastest. | *(required)* | `dc01.corp.local` |
| Port | Port for the LDAP connection. Use 389 for LDAP or 636 for LDAPS. | `389` | `636` |
| Use Secure Connection (LDAPS)? | Enable LDAPS (SSL/TLS) for encrypted communication. | `false` | `true` |
| Certificate Validation | How to validate the server's SSL certificate. Full Validation uses the system CA store plus any certificates added in Admin > Certificates. Only shown, and required, when "Use Secure Connection (LDAPS)?" is enabled. | `Full Validation` | `Skip Validation` |
| Connection Timeout | Time in seconds to wait before giving up on a connection attempt. | `10` | `30` |

### Credentials

| Setting | Description | Example |
|---------|-------------|---------|
| Username | Service account username for connecting to the directory. | `corp\svc-jim-ldap` |
| Password | Service account password (stored encrypted). | *(encrypted)* |
| Authentication Type | Type of authentication: Simple or NTLM. | `Simple` |

### Import Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Search Timeout | Maximum time in seconds to wait for LDAP search results. | `300` (5 minutes) |
| Import Concurrency | Number of parallel LDAP connections for OpenLDAP/generic directory imports. Each connection handles one container and object type combination independently. Not used for Active Directory. | `4` |

### Retry Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Maximum Retries | Maximum retry attempts for transient failures. | `3` |
| Retry Delay (ms) | Initial delay between retries in milliseconds. Uses exponential backoff. | `1000` |

### Schema Discovery

| Setting | Description | Default |
|---------|-------------|---------|
| Include Auxiliary Classes | Include auxiliary object classes alongside structural classes during schema discovery. | `false` |

### Hierarchy

| Setting | Description | Default |
|---------|-------------|---------|
| Skip Hidden Partitions | Skip Configuration, Schema, and DNS zone partitions when refreshing the hierarchy. Improves performance significantly. | `true` |
| Create Containers as Needed | Automatically create OUs when provisioning objects to locations that do not yet exist. | `false` |

### Export Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Delete Behaviour | How to handle object deletions: Delete (remove the object) or Disable (set the disable attribute). | `Delete` |
| Disable Attribute | Attribute to set when disabling objects. Only shown, and required, when Delete Behaviour is Disable. | `userAccountControl` |
| Export Concurrency | Maximum number of concurrent LDAP operations during export. Recommended range: 2--8. | `4` |
| Modify Batch Size | Maximum number of values per multi-valued attribute modification in a single LDAP request. Lower values improve compatibility; higher values improve throughput, especially for very large groups. Recommended range: 100--2000. | `1000` |
| Group Placeholder Member DN | Placeholder DN used for group classes that require at least one member (e.g. groupOfNames). Automatically filtered during import. Only applies to non-AD directories. | `cn=placeholder` |

### Directory Tuning for Large Groups (OpenLDAP)

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

JIM's own large-scale integration testing (up to 1 million users and 500,000-member groups) runs OpenLDAP with `sortvals member` enabled, and we recommend it for any deployment where large group memberships are provisioned. Note that `sortvals` only affects entries written after it is enabled; enable it before loading data, or reload existing data (`slapcat`/`slapadd`) afterwards. See the [OpenLDAP tuning guide](https://www.openldap.org/doc/admin26/tuning.html) and the `slapd.conf(5)` man page for details.

## Security Considerations

### Use LDAPS

LDAP traffic is unencrypted by default. In production environments, **always enable LDAPS** (SSL/TLS) to protect credentials and identity data in transit. Set the port to 636 and enable the "Use Secure Connection (LDAPS)?" setting.

If your directory server uses a certificate issued by an internal certificate authority, upload the CA certificate to JIM via **Admin > Certificates** so that certificate validation can succeed.

!!! warning "Skip Validation"
    The "Skip Validation" certificate option is provided for testing and initial setup only. It disables certificate chain verification, which exposes the connection to man-in-the-middle attacks. Never use this setting in production.

### Service Account Permissions

The LDAP service account used by JIM should follow the principle of least privilege:

- **For import only**<br /> Grant read access to the containers and attributes that JIM needs to import.
- **For export (provisioning)**<br /> Grant create, modify, and delete permissions on the target containers. For Active Directory, this typically means delegated control over the relevant OUs.
- **For container provisioning**<br /> If "Create Containers as Needed" is enabled, the service account must have permission to create organisational units.
- **For delta import**<br /> The service account needs read access to the directory's change tracking mechanism (USN attributes for AD, accesslog for OpenLDAP).

!!! tip "Dedicated service account"
    Always use a dedicated service account for JIM rather than sharing credentials with other applications or using a personal account. This simplifies auditing and ensures that permission changes do not inadvertently affect JIM's operations.

### Network Considerations

- Ensure firewall rules allow traffic from the JIM container to the directory server on the configured port (389 or 636).
- If JIM is running in a container, the directory server must be reachable from the container network. When using Docker Compose, this may require configuring the network mode or adding the directory server to the container's DNS resolution.
- For Active Directory environments, JIM connects to a single domain controller. Consider using a domain controller in the same network segment as JIM to minimise latency.

## Troubleshooting

### Connection failures

If JIM cannot connect to the directory server:

- Verify the hostname or IP address is correct and reachable from the JIM container (`ping` or `nslookup` from within the container).
- Check that the port is correct (389 for LDAP, 636 for LDAPS) and not blocked by a firewall.
- For LDAPS, ensure the server's certificate is trusted -- either by the system CA store or by uploading it to JIM via Admin > Certificates.
- Increase the Connection Timeout if the directory server is slow to respond.

### Authentication failures

If authentication fails with "invalid credentials":

- Verify the username format matches the authentication type. For Simple bind, use a full DN (e.g. `CN=svc-jim,OU=Service Accounts,DC=corp,DC=local`) or UPN (e.g. `svc-jim@corp.local`). For NTLM, use `DOMAIN\username` format.
- Check that the service account password is correct and has not expired.
- Ensure the service account is not locked out or disabled.

### Delta import not detecting changes

If delta imports return no changes when changes are expected:

- **Active Directory**: verify that the service account has read access to the `uSNChanged` attribute.
- **OpenLDAP**: verify that the accesslog overlay is configured and the changelog database is accessible.
- Run a full import to re-baseline, then test delta import again.

### Export failures

If exports fail with LDAP errors:

- Check the activity log for the specific LDAP error code and message.
- For "insufficient access rights" errors, verify the service account has write permissions on the target container.
- For "constraint violation" errors on multi-valued attributes, try reducing the Modify Batch Size setting.
- For group membership operations, ensure the Group Placeholder Member DN setting is appropriate for your directory.
