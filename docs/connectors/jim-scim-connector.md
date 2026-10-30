---
title: JIM SCIM 2.0 Client Connector
description: Synchronise identities with any system that publishes a SCIM 2.0 service provider interface, and the settings the Connector takes.
type: object
audience: administrator
---

# JIM SCIM 2.0 Client Connector

The JIM SCIM 2.0 Client Connector synchronises identities with any system that exposes a **SCIM 2.0 service provider** interface, as defined by [RFC 7643](https://datatracker.ietf.org/doc/html/rfc7643) (core schema) and [RFC 7644](https://datatracker.ietf.org/doc/html/rfc7644) (protocol).

JIM acts as the **SCIM client**: it connects out to the service provider, discovers what it can do, imports its resources, and exports provisioning changes to it. One standards-based connector therefore reaches every system that speaks SCIM, without a separate connector per product.

```jim-diagram
archetype: map
title: What JIM calls, as the SCIM client
desc: "JIM, as the SCIM client, makes every call; the service provider only answers. JIM reads the provider's features from /ServiceProviderConfig, and its schema from /Schemas and /ResourceTypes, which become the Connected System's Object Types. It imports the provider's resources and exports changes to them. With OAuth 2.0 Client Credentials, JIM first gets an access token from the token endpoint. Each part links to its section of this page."
nodes:
  - { id: features, label: "Provider's features", link: "#supported-service-providers", row: 0, col: 1 }
  - { id: token, label: OAuth 2.0 token endpoint, link: "#authentication", row: 1, col: 0 }
  - { id: jim, label: "JIM, the SCIM client", row: 1, col: 1 }
  - { id: resources, label: "Provider's resources", link: "#features", row: 1, col: 2 }
  - { id: schema, label: "Provider's schema", link: "#schema-discovery", row: 2, col: 1 }
edges:
  - { from: jim, to: features, label: "`/ServiceProviderConfig`" }
  - { from: jim, to: token, label: gets a token }
  - { from: jim, to: resources, label: imports and exports }
  - { from: jim, to: schema, label: "`/Schemas`, `/ResourceTypes`" }
```

!!! note "Direction matters"
    This connector is for systems that **publish** a SCIM interface for JIM to call. For the reverse arrangement, where an identity provider pushes Users and Groups to JIM, use the [JIM SCIM 2.0 Service Provider Connector](jim-scim-service-provider-connector.md).

## :material-cloud-outline: Supported Service Providers

Any service provider conforming to RFC 7643 and RFC 7644. The connector adapts to what each one actually offers rather than assuming a particular implementation:

- **Schema:** read from the provider's own `/Schemas` and `/ResourceTypes` documents. A provider that publishes neither is handled with the RFC 7643 core `User`, `Group` and Enterprise User schemas.
- **Optional features:** read from `/ServiceProviderConfig`. Where a provider does not advertise filtering, PATCH or entity tags, the connector uses the protocol floors instead, and says so on the Activity.
- **Pagination:** index-based (`startIndex`/`count`, which every provider supports) or cursor-based ([RFC 9865](https://datatracker.ietf.org/doc/html/rfc9865)).

The connector requires no internet access of its own and adds no cloud-service dependency: it talks only to the service provider you point it at, which may be entirely on-premises.

Keycloak's built-in SCIM API needs a particular setup, which [Connect the SCIM Connector to Keycloak](jim-scim-connector-keycloak.md) walks through.

## :material-star-four-points-outline: Features

| Capability | Supported | Notes |
|---|---|---|
| Full Import | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } | Walks every resource type you have selected, page by page. A provider that returns an empty page while its `totalResults` says more remain fails the run, rather than having the resources it did not return treated as deleted. |
| Delta Import | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } | Asks the provider for what changed since the last completed import. See [Change Detection](#change-detection). |
| Export | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } | Creates, updates and deletes resources, including group membership. |
| Paging | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } | The Run Profile's Page Size becomes the requested page size. |
| Partitions and Containers | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } | SCIM has no partition concept; resource types are Connected System Object Types. |
| Parallel Export | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } | Bounded by the provider's own rate limits, which the connector honours. |
| Bulk Operations | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } | Optional. Sends exports a batch at a time where the provider advertises `/Bulk`. See [Bulk Operations](#bulk-operations). |
| Probe | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } | Asks the provider whether a generated value is already in use before JIM chooses it. See [Probing for values already in use](#probing-for-values-already-in-use). |

### Schema Discovery

Importing the schema reads the provider's discovery documents and presents its resource types as Connected System Object Types.

SCIM's nested and multi-valued attributes are flattened into the flat attributes JIM's Attribute Flows target:

| SCIM attribute | JIM attribute | Why |
|---|---|---|
| `userName` | `userName` | A simple attribute is carried across unchanged. |
| `name.givenName` | `name.givenName` | A complex attribute becomes one attribute per sub-attribute. |
| `emails` (type `work`) | `emails.work` | A multi-valued attribute with canonical types becomes one single-valued attribute per type, so a flow has a definite target. |
| `emails` (marked primary) | `emails.primary` | The entry the provider flags as primary is addressable in its own right. |
| `addresses` (type `work`) | `addresses.work.streetAddress`, ... | An address has no single value, so the slot is cut per sub-attribute. |
| `roles`, `entitlements`, `x509Certificates` (no canonical types) | `roles.value`, `roles.type`, `roles.display` | Each sub-attribute becomes a multi-valued attribute. Only `roles.value` can be exported: each value is one entry, so exporting a value adds an entry and taking one away removes that entry alone, leaving the provider's other entries untouched. `roles.type` and `roles.display` are read-only, because once separated from their entries JIM cannot tell which entry each belongs to. The Boolean `primary` sub-attribute is left out: separated from its entry, it would say only that some entry is primary and some is not, and a Boolean attribute holds one value. |
| A multi-valued `dateTime` attribute | unchanged | Every element of the array is imported, to the microsecond. |
| `members`, `groups`, `urn:...:enterprise:2.0:User:manager` | `members`, `groups`, `enterpriseUser.manager` | An attribute carrying a reference stays whole, keeping its plurality, so group membership survives as a reference. |
| `profileUrl`, `photos` (type `photo`) | `profileUrl`, `photos.photo` | A URL to something outside SCIM is text. Only a reference to a SCIM resource type, such as a User or a Group, becomes a reference JIM resolves to another object. |
| `urn:...:enterprise:2.0:User:department` | `enterpriseUser.department` | An extension attribute is prefixed with its schema's name. |

Every resource also carries `id`, `externalId` and the `meta` attributes, which the SCIM specification defines rather than any schema document. `id` is the External ID JIM anchors Connected System Objects on, and is always read-only.

A Connected System whose schema was imported before JIM left these `primary` sub-attributes out reports them as no longer reported at its next [schema refresh](../configuration/connected-system-schema.md#refreshing-the-schema), which names any Attribute Flow mapping that reads one.

Likewise, a Connected System whose schema was imported before JIM typed URLs as text reports `profileUrl` and the `photos` attributes as changed from Reference to Text at its next schema refresh. Run a Full Import afterwards: it replaces the values those attributes held as unresolved references with the URLs as text.

!!! warning "Two entries sharing a canonical type"
    A provider is free to hold two `work` email addresses. `emails.work` holds one, so the first is imported and the run reports a warning naming the attribute. Nothing is silently dropped, but the second value does not reach JIM.

Where discovery had to work around a gap in what the provider publishes (no `/Schemas` document, an attribute definition that could not be fully interpreted, a core attribute such as `members` published with nothing inside it, which JIM fills in from RFC 7643), the schema import says so rather than presenting an unqualified success: the warnings appear on the schema screen's refresh summary, and the import's Activity completes with a warning carrying the same detail, so they are visible from the REST API and PowerShell too. Use them to tell a provider gap from a JIM one when an expected attribute is missing.

### Change Detection

SCIM defines no change feed, so a Delta Import asks the provider for the resources modified since the last completed import: `filter=meta.lastModified gt "<watermark>"`. This needs the provider to support filtering.

The **Change Detection** setting controls the choice:

| Value | Behaviour |
|---|---|
| Auto-detect (default) | Filter by last-modified date where the provider advertises filtering; read everything where it does not. |
| Last Modified Filter | Filter regardless of what the provider advertises. Some providers support filtering without saying so. |
| Full Scan | Never filter. Use this where a provider's filtering is unreliable. |

Against Keycloak, which advertises filtering but does not update the last-modified date for most changes, a Delta Import reads every resource whichever of the first two is chosen, and says why on the Activity: a filter would succeed having missed those changes. JIM recognises Keycloak from the `/realms/<realm>/scim/v2` path, in the Base URL or in where Keycloak says its configuration lives.

Where a Delta Import cannot filter, it reads every resource instead and reports a warning on the Activity rather than failing. This is deliberate: only a completed import can record a watermark, so failing the first Delta Import after a Connected System is configured would leave it permanently unable to import.

!!! important "Deletions are only detected by a Full Import"
    A deleted SCIM resource stops being returned; the protocol offers no way to ask what went. Only a Full Import, which reads everything, can tell that an object has gone. Schedule a Full Import regularly alongside your Delta Imports.

The watermark is recorded only when a run finishes, and is deliberately set a minute behind the point the run started reading. A run that fails or is cancelled part way through therefore leaves the watermark where the last completed import put it, and the next run re-reads a small overlap. Re-reading an unchanged resource is harmless; missing a change would not be.

### Export

| Change | Request |
|---|---|
| Create | `POST` to the resource type's endpoint. The provider assigns the `id`, which JIM records as the External ID. Where the provider supports PATCH, a group's members (and any other multi-valued reference) are left out of the `POST` and added with a `PATCH` once the group exists; some providers refuse a group created with members. If adding them fails, the create still succeeds with its `id`, the failure is logged, and the members are sent again as an update after the next import finds the group without them. |
| Update | `PATCH`, naming only what changed. Values added to one multi-valued attribute travel as one operation, and no request carries more than 100 operations; a provider that refuses a request as carrying too many is sent half as many at a time. A change needing several requests that fails partway is reported as failed, and the next import shows what the provider holds. |
| Update (provider without PATCH) | The resource is read, the changes are applied to it, and the whole resource is written back with `PUT`. |
| Delete | `DELETE`. A resource that has already gone counts as success. |

Where the provider maintains entity tags, updates carry `If-Match`, so an export cannot silently overwrite a change made in the provider since JIM last read it. A rejected update is reported as a concurrency conflict and reconciled by the next import. The tag sent is the `meta.version` JIM last imported, so schema import selects `meta.version` on every Object Type and keeps it selected; its Selected switch is disabled. Where the provider does not support entity tags, `meta.version` is yours to select or not.

A `dateTime` value is written in UTC, with a fraction of a second only where it has one. A provider's schema cannot say how finely it keeps dates, so schema discovery gives every `dateTime` attribute a [Date resolution](../configuration/connected-system-attributes.md#date-resolution) of one second, and JIM exports and compares dates to the second. Where a provider keeps milliseconds, change the resolution on the Schema tab or with `Set-JIMConnectedSystemAttribute -DateTimeResolution`. Removing one date of a multi-valued attribute finds it as an instant, however the provider writes it.

An attribute the provider's schema does not have, or will not accept a write to, fails that object rather than exporting the rest of it: a partial change recorded as applied is worse than one that failed.

#### Passwords

SCIM's User `password` is write-only: a provider accepts it and never returns it. JIM never exports a password through an Attribute Flow, so the attribute is left out of the Connected System's schema, and a Synchronisation Rule cannot flow a value to it. Any attribute a provider's schema marks write-only, or never returned, is treated the same way: the Connector refuses to send it and fails that object, without sending anything. JIM delivers passwords only through [Password Synchronisation](../configuration/connected-system-passwords.md#password-synchronisation), which the SCIM Connector does not support yet, so JIM cannot set passwords on a SCIM service provider today. Set them in the provider, or have users sign in through a federated identity provider.

!!! note "Referenced objects are exported first"
    RFC 7644 makes the client responsible for creating dependencies before the things that refer to them. JIM orders exports accordingly, and where a provider still rejects a change because something it references is missing, that is reported as a dependency-ordering problem and retried once the dependency lands.

### Bulk Operations

By default each change is its own HTTP request. Where a provider advertises SCIM's `/Bulk` endpoint, turning on **Use Bulk Operations** sends them a batch at a time instead, which is considerably faster over a high-latency connection: an export of ten thousand objects becomes tens of requests rather than ten thousand.

It is off by default, and worth understanding why before turning it on. Per-object export is always correct. Bulk moves responsibility for reporting each outcome to the provider, and a provider whose implementation reports them inaccurately would have JIM record changes as applied that were not, which surfaces later as drift nobody can explain. `/Bulk` is also the least consistently implemented part of SCIM. Turn it on once you have seen an export succeed against your provider, and check the Activity afterwards.

What JIM does with it:

- **The provider's advertised limits are respected, and a provider that under-states them is adapted to.** Batches stay within the `maxOperations` and `maxPayloadSize` the provider publishes; a change too large for any batch is sent on its own instead. Where the provider advertises bulk without stating a limit, JIM batches 100 operations at a time. A provider that then refuses a batch as too large has enforced a limit it did not publish, and since it refuses before applying anything, JIM halves the batch and retries rather than failing the changes.
- **One bad object never abandons the rest.** JIM does not set `failOnErrors`, so the provider is asked to process everything regardless, exactly as the per-object path behaves.
- **Outcomes are matched to changes, never counted off.** A bulk response is not required to list operations in the order they were sent. JIM correlates each outcome to the change that produced it.
- **An operation the provider does not report on is treated as failed.** A provider that stops early says nothing about what it never reached, and a change JIM cannot confirm was applied is never recorded as exported. It stays pending and is reported on the Activity.
- **A provider that advertises `/Bulk` and does not serve it is survived, not failed.** JIM falls back to one request per object for the rest of the run and warns.
- **A bulk request that fails after being sent fails its changes rather than resending them.** How far the provider got is unknowable, and resending a create that did apply would duplicate the resource. Those changes stay pending; the next import establishes what actually landed before they are retried.

!!! note "Bulk changes throughput, not behaviour"
    The same request bodies, entity tags, dependency ordering and error classification apply either way. If an export is failing, turning bulk off will not change the outcome; it will only change how many requests it takes to reach it.

### Rate Limits and Throttling

Providers commonly rate-limit. The connector:

- retries transient failures with exponential backoff and jitter;
- honours `Retry-After` on a `429` or `503`;
- pauses proactively when a provider's `RateLimit-*` headers say its allowance is nearly spent.

Throttling is recorded in the logs and never fails a run; it is not reported on the Activity.

### Probing for values already in use

When a synchronisation generates a value that is exported unchanged to an attribute of this provider, such as an account name sent to `userName` or an address sent to `emails.work`, JIM asks the provider whether the value is already in use before choosing it. This catches an account JIM has not imported yet: one created in the provider since the last import, by another process or by hand. How generated values are checked, and what is and is not probed, is in [Checking availability in target systems](../configuration/generated-values-in-target-systems.md#checking-availability-in-target-systems).

- **One connection per synchronisation**<br /> The connection is made with this Connected System's own settings, credential and certificates, the first time the synchronisation needs it, and closed when the run ends. JIM reads the provider's discovery documents once when it connects, to learn each resource type's endpoint and attributes.
- **One search per object**<br /> The object's candidate values (up to ten) and a control value JIM already holds for the attribute go in one filtered `GET` to the resource type's endpoint, for example `/Users?filter=userName eq "jbloggs" or userName eq "jbloggs1"`. Each value is written as a quoted string with its quotes and backslashes escaped, so a value can never change what the filter asks. A control value that does not come back means the credential cannot see the attribute, and JIM stops trusting the probe for that attribute for the rest of the run.
- **Case is ignored wherever the provider can ignore it**<br /> As at every uniqueness check, `JBloggs` in the provider stops JIM choosing `jbloggs`. Most attributes, `userName`, `emails` and `displayName` included, are not case exact, so the provider matches them without regard to case. An attribute the provider declares case exact (`externalId` in the core schema) is compared exactly, because a SCIM filter cannot ask otherwise: a value differing only in case is not found, though nor would the provider refuse it, and JIM's own records of the accounts it imports still ignore case.
- **An address is searched across every entry**<br /> A typed slot such as `emails.work` is searched as `emails.value`, so an address held as another account's home or other email is found too.
- **Undetermined is not a failure**<br /> A provider that cannot be reached, refuses the credential (`401` or `403`) or does not answer in time stops JIM probing it for the rest of the run. A provider that will not filter on an attribute (`400` with `invalidFilter`, or `501`), returns resources holding none of the values asked for (a filter it did not apply), or reports more matches than it returned stops JIM probing that attribute only. Each is reported once on the synchronisation's Activity as a warning naming the reason, and the run continues with JIM's own records.

## :material-lan-connect: Connection Settings

### SCIM Service Provider

| Setting | Required | Description |
|---|---|---|
| Base URL | Yes | The root of the SCIM service, for example `https://example.com/scim/v2`. Point it at the root, not at a resource endpoint such as `/Users`. HTTPS is required, except for loopback addresses when testing locally. |

### Authentication

| Setting | Required | Description |
|---|---|---|
| Authentication Method | Yes | OAuth 2.0 Client Credentials, HTTP Basic, Static Bearer Token, or Custom Header. |
| Token Endpoint URL | With OAuth 2.0 | The token endpoint used to acquire access tokens. HTTPS is required, except for loopback addresses when testing locally, because the Client Secret is sent to this address. If the authorisation server uses a private certificate authority, add its certificate in **Admin > Certificates**. |
| Client ID | With OAuth 2.0 | The client identifier issued by the provider. |
| Client Secret | With OAuth 2.0 | The client secret issued with it. Tokens are cached until shortly before they expire and refreshed automatically. |
| OAuth Scope | No | Optional space-separated scopes to request. |
| Username | With HTTP Basic | The user to authenticate as. |
| Password | With HTTP Basic | That user's password. |
| Bearer Token | With Static Bearer Token | A long-lived token issued by the provider. |
| Authentication Header Name | With Custom Header | For providers using a non-standard header, the header's name, for example `X-Api-Key`. |
| Authentication Header Value | With Custom Header | The value to send in that header. |

Every secret is encrypted at rest and is never written to a log.

### Transport Security

| Setting | Default | Description |
|---|---|---|
| Certificate Validation | Full Validation | Validates the provider's TLS certificate against the system trust store plus any certificates added in **Admin > Certificates**. Skip Validation accepts whatever the provider presents. |
| Minimum TLS Version | TLS 1.2 | The lowest TLS version accepted. |
| Connection Timeout | 30 seconds | How long to wait for a response. |

#### When a connection test fails on the certificate

A provider using a certificate JIM does not yet trust (an internal certificate authority, or a self-signed certificate) fails the connection test. JIM shows you the certificate the provider presented: its subject, the names it was issued for, its issuer, validity dates and thumbprint, along with which check it failed and what to do about it. The same detail appears on a failed Activity and on the Activity's `errorDetail` field in the REST API.

**Trusting the certificate is the recommended way to unblock this, and JIM offers it on the certificate itself.** The card shows the certificate chain JIM found, from the root down to the provider's own certificate, and where each came from. Select **Choose what to trust** (or **Trust this certificate**, where JIM found only the provider's own), and JIM reads the chain from the provider again, checks your choice is still in it, and adds it to the Trusted Certificates store. You never have to obtain the certificate file by other means, which for a hosted service is often the hardest part of the exercise.

You are asked to confirm before anything is added, because this is a security decision. Compare the thumbprint against the one the provider's administrator gives you first. Every certificate in the chain makes the connection work; they differ in how long the decision lasts. JIM recommends the root, which survives the renewal of every certificate beneath it, whereas trusting the provider's own certificate has to be repeated each time it is renewed. A self-signed certificate has no separate authority, so there is only one thing to trust.

Reading the certificate again at the moment you confirm is what makes a change detectable. If the provider is presenting something other than what you were shown, JIM trusts nothing and shows you both thumbprints, which is expected after a renewal and worth investigating otherwise.

**You do not have to wait for a failure.** **Fetch certificate** on the Connected System's settings reads and shows what the provider is presenting before anything has been saved, so configuring a new system is not a cycle of save, fail, come back. Fetching stores nothing; trusting is still the separate, confirmed step.

Note what trusting the certificate does and does not cover. JIM's store answers one question, "do we trust the issuer", so it waives an unknown certificate authority and nothing else. An expired certificate is not a trust gap and is still refused; a certificate issued for a different name is an interception signal, so connect using a name the certificate carries rather than adding it to the store. JIM offers the action only where trusting genuinely fixes the failure, so it does not appear in those cases.

!!! danger "Skip Validation hides certificate changes"
    Skip Validation accepts whatever the provider presents, now and in future, so a certificate that changes, expires or is swapped for an attacker's passes silently. It exists for the cases where nothing else will do (a lab, a provider you cannot get a certificate out of), and it is recorded on the Connected System so an auditor can see it was chosen. Trusting the presented certificate is both safer and now the shorter route: it is one action on the failure itself, and it keeps JIM checking that the certificate has not changed.

To do the same from a script:

```powershell
$reading = Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42
$reading.certificate.chain | Format-Table subject, source, downloadedFrom

Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 `
    -Thumbprint ($reading.certificate.rootThumbprint ?? $reading.certificate.thumbprint) `
    -ChangeReason 'Unblocking the HR Cloud connection test.'
```

### Import

| Setting | Default | Description |
|---|---|---|
| Pagination Mode | Auto-detect | Auto-detect starts with index-based paging and switches to cursors if the provider offers one. Choose Cursor-based for large or frequently-changing providers: index-based paging can miss or repeat objects when the data changes during an import. |
| Excluded Attributes | (none) | A comma-separated list of SCIM attributes to ask the provider not to return, for example `photos, x509Certificates`. Useful for large attributes JIM does not need. `id` and `meta` are always requested, because importing cannot work without them. Where a selected attribute is one the provider returns only when asked for it by name (`returned: request` in its schema, as Keycloak's Group `members` is), JIM instead names every selected attribute it wants, leaving out the ones listed here. |
| Change Detection | Auto-detect | See [Change Detection](#change-detection). |

### Retry Settings

| Setting | Default | Description |
|---|---|---|
| Maximum Retries | 3 | Retry attempts for transient failures. |
| Retry Delay (ms) | 1000 | The initial backoff delay. Backoff is exponential with jitter, and `Retry-After` always takes precedence. |

### Export Settings

| Setting | Default | Description |
|---|---|---|
| Use Bulk Operations | Off | Send exports in batches through the provider's `/Bulk` endpoint instead of one request per object. Only used where the provider advertises bulk support, and only within the limits it states. See [Bulk Operations](#bulk-operations). |

## :material-shield-lock-outline: Security Considerations

- **HTTPS is required** for both the Base URL and, with OAuth 2.0, the Token Endpoint URL. Identity data and the Client Secret must not travel over cleartext HTTP. Plain HTTP is permitted only for loopback addresses, so that a local test provider can be used during evaluation. JIM refuses any other address when you save the settings, before it tests the connection, and again when a Run Profile starts.
- **Secrets are encrypted at rest** and never logged, sanitised or otherwise.
- **Least privilege.** Give the credential only the SCIM permissions the Run Profiles you have configured need: read for import, write for export. A Connected System a generated value is exported to also needs read access with filtering across the resource types it probes, including for a credential that only exports; see [Probing for values already in use](#probing-for-values-already-in-use).
- **No cloud dependency.** The connector calls only the Base URL you configure and, with OAuth 2.0, the Token Endpoint URL, so it works unchanged in an air-gapped deployment against an on-premises provider.

## :material-wrench-outline: Troubleshooting

**"It did not answer on any SCIM discovery endpoint"**<br />
The Base URL is probably pointing at a resource endpoint rather than the root of the service. It should be the path that `/ServiceProviderConfig` hangs off.

**Delta Imports read everything every time**<br />
Check the Activity for a warning. Either the provider does not advertise filtering (set Change Detection to Last Modified Filter if you know it supports it anyway), or the provider rejected the filter, or the provider is Keycloak (whose last-modified dates cannot be filtered on safely; set Change Detection to Full Scan), or no watermark had been recorded yet, in which case the next Delta Import will be incremental.

**An import fails with "returned an empty page ... while its totalResults says"**<br />
The provider counted resources it then did not return. JIM stops rather than finish a Full Import that would treat the missing resources as deleted. Check that the credential can read every resource of the type, and whether the provider counts resources it hides.

**Exports to some objects fail with 403 while others succeed**<br />
The provider protects those objects; Keycloak, for one, refuses any change to an account or group holding an administrative role. Scope them out of the Synchronisation Rule. Where every export fails with 403, the credential lacks the permission instead.

**Objects go missing from JIM after a Delta Import**<br />
They should not: deletion detection runs only on a Full Import. If objects are being marked obsolete, the run in question was a Full Import that could not see them.

**Updates fail with a concurrency conflict**<br />
Something changed the resource in the provider between JIM reading it and writing it back. Run an import to pick up the current state; the change will be re-evaluated against it.

**The connection test fails with a certificate error**<br />
JIM shows the certificate the provider presented and which check it failed. An untrusted issuer is fixed by trusting the certificate with **Choose what to trust** (or **Trust this certificate**) on the certificate card; an expired certificate has to be renewed on the provider; a name mismatch means connecting by a name the certificate carries. See [When a connection test fails on the certificate](#when-a-connection-test-fails-on-the-certificate).

**A synchronisation warns that JIM couldn't probe the provider**<br />
The warning names the reason. A refused credential needs read access to the resource type; a refused filter means the provider will not search that attribute, so JIM relies on its own records for it (and on [Collision Remediation](../configuration/generated-values-in-target-systems.md#when-a-target-rejects-a-value) should the provider then refuse a value); a provider that returned resources holding none of the values asked for is not applying the filter it advertises. See [Probing for values already in use](#probing-for-values-already-in-use).

**An attribute never receives a value**<br />
Re-import the schema. A provider that has changed what it publishes, or one whose `/Schemas` document went missing (leaving the connector on the RFC 7643 core schemas), will offer a different attribute set.

## :material-book-open-variant-outline: Related

- [Connect the SCIM Connector to Keycloak](jim-scim-connector-keycloak.md)
- [Connectors](index.md)
- [Connected Systems](../configuration/connected-systems.md)
