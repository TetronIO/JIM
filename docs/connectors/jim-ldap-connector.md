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
    - **Active Directory**<br /> Uses USN (Update Sequence Number) change tracking. USNs are only meaningful when read back against the same domain controller that issued them, so JIM also records the domain controller's identity (its invocationId, falling back to its hostname where an invocationId is not available for comparison) and verifies it on every Delta Import before querying for changes. If the pinned domain controller changed since the last run, or was restored from backup, the Delta Import fails fast with an error naming what changed rather than silently skipping or re-importing changes. See [Domain Controller Discovery and Pinning](#domain-controller-discovery-and-pinning) and [Delta import fails with a domain controller mismatch error](#delta-import-fails-with-a-domain-controller-mismatch-error) below.
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
- **Certificate validation**<br /> Always on for LDAPS: the certificate chain, its validity period, and its name are all checked. Certificates added in Admin > Certificates are trusted in addition to the operating system's own trust store.
- **Authentication types**<br /> Simple bind or NTLM authentication.
- **Automatic retry**<br /> Configurable retry with exponential backoff for transient failures.

## Connection Settings

### Connectivity

| Setting | Description | Default | Example |
|---------|-------------|---------|---------|
| Host | Hostname or IP address of the directory server. IP address is fastest. | *(required)* | `dc01.corp.local` |
| Preferred Domain Controller | Applies to Active Directory and Samba AD. A specific domain controller FQDN to always connect to. When left blank, JIM automatically discovers and pins the domain controller it reaches via Host; see [Domain Controller Discovery and Pinning](#domain-controller-discovery-and-pinning) below. For LDAPS, use a name present in the domain controller's certificate. | *(blank; auto-discover)* | `dc01.corp.local` |
| Port | Port for the LDAP connection. Use 389 for LDAP or 636 for LDAPS. | `389` | `636` |
| Use Secure Connection (LDAPS)? | Enable LDAPS (SSL/TLS) for encrypted communication. Certificate validation is always applied; see [Certificate validation](#certificate-validation). | `false` | `true` |
| Connection Timeout | Time in seconds to wait before giving up on a connection attempt. | `10` | `30` |

### Domain Controller Discovery and Pinning

For Active Directory and Samba AD, JIM connects to a single, consistent domain controller rather than reconnecting via whatever the Host setting happens to resolve to each time. This matters for two reasons:

- **Replication consistency.** If Host is a domain name that DNS round-robins across multiple domain controllers, an export could write to one domain controller while the confirming import reads from another before replication catches up, making confirmed objects appear temporarily missing.
- **Delta import correctness.** USNs are scoped to the domain controller that issued them, so a Delta Import against a different domain controller than the one that produced the persisted watermark can silently skip or re-import changes. See [Delta import fails with a domain controller mismatch error](#delta-import-fails-with-a-domain-controller-mismatch-error) below.

**How it works:** leave Preferred Domain Controller blank and JIM auto-discovers. On the first connection, JIM connects via Host, reads the domain controller it reached from the directory's rootDSE, and pins every subsequent connection, within a run and across Run Profile executions, to that same domain controller (by FQDN, not IP; this also keeps LDAPS certificate name validation working, since the pinned name is the one the certificate's SAN needs to match). Setting Preferred Domain Controller to a specific FQDN always takes priority over any pin.

**If the pinned domain controller becomes unavailable:** the Run Profile execution fails outright rather than silently failing over mid-run, and the pin is cleared. The next Run Profile execution resolves via Host again, discovers whichever domain controller answers, and re-pins to it. Because that may be a different domain controller than before, a Full Import is needed to re-establish the Delta Import baseline; see [Delta import fails with a domain controller mismatch error](#delta-import-fails-with-a-domain-controller-mismatch-error).

Pinning only applies to Active Directory and Samba AD; OpenLDAP and other generic directories are unaffected.

### Multi-domain forests

A Connected System manages one domain today. During Partition discovery on Active Directory or Samba AD, JIM lists every domain in the forest, because that is what the directory's crossRef objects expose; it has no way to tell from that list alone which domains the connected domain controller actually holds. A domain controller only ever holds its own domain's naming context and does not chase referrals to serve objects from another domain in the forest.

If you select a Partition for a domain the connected domain controller does not host, the import fails fast with an error naming the Partition and the domain controller, rather than silently returning zero objects. To manage more than one domain, create a separate Connected System per domain, each with its Host setting pointing at that domain's own domain controllers.

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

JIM's own large-scale integration testing (up to 500,000 users, with individual groups of up to 495,000 members) runs OpenLDAP with `sortvals member` enabled, and we recommend it for any deployment where large group memberships are provisioned. Note that `sortvals` only affects entries written after it is enabled; enable it before loading data, or reload existing data (`slapcat`/`slapadd`) afterwards. See the [OpenLDAP tuning guide](https://www.openldap.org/doc/admin26/tuning.html) and the `slapd.conf(5)` man page for details.

## Security Considerations

### Use LDAPS

LDAP traffic is unencrypted by default. In production environments, **always enable LDAPS** (SSL/TLS) to protect credentials and identity data in transit. Set the port to 636 and enable the "Use Secure Connection (LDAPS)?" setting.

### Certificate validation

When LDAPS is enabled, JIM validates the certificate the directory server presents. Three things are checked, and any one of them failing stops the connection before the service account's credentials are sent:

- **Chain**<br /> The certificate must chain to an issuer JIM trusts: either one in the operating system's trust store, or one added in **Admin > Certificates**.
- **Validity period**<br /> An expired or not-yet-valid certificate is rejected, including when its issuer is one you added yourself.
- **Name**<br /> The certificate must have been issued for the value in the Host setting. A certificate for `dc01.corp.local` is not accepted when JIM connects to `10.0.0.5`, or to `dc01`.

There is no per Connected System option to relax any of this.

#### Trusting an internal certificate authority, or a self-signed certificate

Upload the certificate to JIM via **Admin > Certificates**. Both work:

- **An internal certificate authority**<br /> Upload the CA (and any intermediates). Every directory server whose certificate it issued is then trusted.
- **The directory server's own self-signed certificate**<br /> Upload the server certificate itself. Only that certificate is then trusted, which is the tighter option where a directory has no certificate authority behind it.

Certificates added this way are trusted **in addition to** the operating system's trust store, so adding one never stops a publicly-issued or already-trusted certificate from working.

#### When the certificate name does not match the host you connect to

This is the common obstacle: a certificate issued for a fully-qualified name, in an environment where JIM cannot resolve that name, so the Host setting holds an IP address instead. Uploading the certificate does not help, because the name still does not match.

Rather than weakening validation, give JIM's containers a way to resolve the name. In Docker Compose, add a host entry to the `jim.web` and `jim.worker` services:

```yaml
services:
  jim.worker:
    extra_hosts:
      - "dc01.corp.local:10.0.0.5"
```

Set the Host setting to `dc01.corp.local`. The name now resolves inside the container, the certificate matches, and the connection is fully validated. This works when DNS is unavailable or unreliable, because the mapping is static and needs no name server. The alternative is to have the certificate reissued with the name (or IP address) you actually connect to.

!!! warning "Disabling validation entirely"
    OpenLDAP's own `LDAPTLS_REQCERT=never` environment variable is honoured by the LDAP client library JIM's containers use, and switches certificate validation off. It applies to the whole container, so it affects **every** LDAPS Connected System that container serves, and it cannot be scoped to one directory. JIM's development and integration test stacks set it for their throw-away directories. Never set it in production: it exposes the service account's credentials to anyone able to intercept the connection.

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
- Increase the Connection Timeout if the directory server is slow to respond.

!!! tip "LDAPS failures show you the certificate"
    The LDAP client library reports a rejected certificate the same way it reports an unreachable server, so its own message ("The LDAP server is unavailable") tells you nothing. JIM therefore looks at the certificate itself when an LDAPS connection fails, and shows it to you: its subject, the names it was issued for, its issuer, its validity dates and its thumbprint, alongside which check it failed and what to do about it.

    You will see it in two places: on the Connected System's settings when you test the connection, and on the failed Activity when a Run Profile could not connect. The same detail is available to automation on the Activity's `errorDetail` field in the REST API. If the connection failed for a reason that is nothing to do with the certificate, the original error stands unchanged.

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

### Delta import fails with a domain controller mismatch error

Active Directory and Samba AD Delta Imports check that they are still talking to the same domain controller that produced the persisted USN watermark, and fail fast with an error naming the previous and current domain controller (or their invocationId) if not. This is expected, protective behaviour, not a bug: a USN watermark from one domain controller is meaningless against another, and continuing regardless risks silently skipping or re-importing changes.

With [domain controller discovery and pinning](#domain-controller-discovery-and-pinning) in place, the most common cause is the previously pinned domain controller having become unreachable: JIM already cleared the pin and failed that run outright, and the following run resolved via Host, discovered a different domain controller, and re-pinned to it. The other cause is the domain controller having been restored from backup, which is issued a new invocationId even though its hostname is unchanged.

- No action is usually needed beyond running a Full Import: JIM has already re-pinned automatically.
- If you need consistent affinity to one specific domain controller regardless of availability, set Preferred Domain Controller rather than relying on auto-discovery.
- Run a Full Import to re-establish the delta baseline against whichever domain controller JIM connects to next; subsequent Delta Imports then succeed as long as that domain controller keeps answering.

### Export failures

If exports fail with LDAP errors:

- Check the activity log for the specific LDAP error code and message.
- For "insufficient access rights" errors, verify the service account has write permissions on the target container.
- For "constraint violation" errors on multi-valued attributes, try reducing the Modify Batch Size setting.
- For group membership operations, ensure the Group Placeholder Member DN setting is appropriate for your directory.
