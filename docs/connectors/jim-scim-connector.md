# JIM SCIM 2.0 Client Connector

## Overview

The JIM SCIM 2.0 Client Connector synchronises identities with any system that exposes a **SCIM 2.0 service provider** interface, as defined by [RFC 7643](https://datatracker.ietf.org/doc/html/rfc7643) (core schema) and [RFC 7644](https://datatracker.ietf.org/doc/html/rfc7644) (protocol).

JIM acts as the **SCIM client**: it connects out to the service provider, discovers what it can do, imports its resources, and exports provisioning changes to it. One standards-based connector therefore reaches every system that speaks SCIM, without a separate connector per product.

!!! note "Direction matters"
    This connector is for systems that **publish** a SCIM interface for JIM to call. The reverse arrangement, where an external system pushes changes into JIM, is a separate capability and is not covered here.

## Supported Service Providers

Any service provider conforming to RFC 7643 and RFC 7644. The connector adapts to what each one actually offers rather than assuming a particular implementation:

- **Schema:** read from the provider's own `/Schemas` and `/ResourceTypes` documents. A provider that publishes neither is handled with the RFC 7643 core `User`, `Group` and Enterprise User schemas.
- **Optional features:** read from `/ServiceProviderConfig`. Where a provider does not advertise filtering, PATCH or entity tags, the connector uses the protocol floors instead, and says so on the Activity.
- **Pagination:** index-based (`startIndex`/`count`, which every provider supports) or cursor-based ([RFC 9865](https://datatracker.ietf.org/doc/html/rfc9865)).

The connector requires no internet access of its own and adds no cloud-service dependency: it talks only to the service provider you point it at, which may be entirely on-premises.

## Features

| Capability | Supported | Notes |
|---|---|---|
| Full Import | ✅ | Walks every resource type you have selected, page by page. |
| Delta Import | ✅ | Asks the provider for what changed since the last completed import. See [Change Detection](#change-detection). |
| Export | ✅ | Creates, updates and deletes resources, including group membership. |
| Paging | ✅ | The Run Profile's Page Size becomes the requested page size. |
| Partitions and Containers | ❌ | SCIM has no partition concept; resource types are Connected System Object Types. |
| Parallel Export | ✅ | Bounded by the provider's own rate limits, which the connector honours. |
| Bulk Operations | ✅ | Optional. Sends exports a batch at a time where the provider advertises `/Bulk`. See [Bulk Operations](#bulk-operations). |

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
| `members`, `groups`, `manager` | unchanged | An attribute carrying a reference stays whole, keeping its plurality, so group membership survives as a reference. |
| `urn:...:enterprise:2.0:User:department` | `enterpriseUser.department` | An extension attribute is prefixed with its schema's name. |

Every resource also carries `id`, `externalId` and the `meta` attributes, which the SCIM specification defines rather than any schema document. `id` is the External ID JIM anchors Connected System Objects on, and is always read-only.

!!! warning "Two entries sharing a canonical type"
    A provider is free to hold two `work` email addresses. `emails.work` holds one, so the first is imported and the run reports a warning naming the attribute. Nothing is silently dropped, but the second value does not reach JIM.

Where discovery had to work around a gap in what the provider publishes (no `/Schemas` document, an attribute definition that could not be fully interpreted), the schema import says so rather than presenting an unqualified success: the warnings appear on the schema screen's refresh summary, and the import's Activity completes with a warning carrying the same detail, so they are visible from the REST API and PowerShell too. Use them to tell a provider gap from a JIM one when an expected attribute is missing.

### Change Detection

SCIM defines no change feed, so a Delta Import asks the provider for the resources modified since the last completed import: `filter=meta.lastModified gt "<watermark>"`. This needs the provider to support filtering.

The **Change Detection** setting controls the choice:

| Value | Behaviour |
|---|---|
| Auto-detect (default) | Filter by last-modified date where the provider advertises filtering; read everything where it does not. |
| Last Modified Filter | Filter regardless of what the provider advertises. Some providers support filtering without saying so. |
| Full Scan | Never filter. Use this where a provider's filtering is unreliable. |

Where a Delta Import cannot filter, it reads every resource instead and reports a warning on the Activity rather than failing. This is deliberate: only a completed import can record a watermark, so failing the first Delta Import after a Connected System is configured would leave it permanently unable to import.

!!! important "Deletions are only detected by a Full Import"
    A deleted SCIM resource simply stops being returned; the protocol offers no way to ask what went. Only a Full Import, which reads everything, can tell that an object has gone. Schedule a Full Import regularly alongside your Delta Imports.

The watermark is recorded only when a run finishes, and is deliberately set a minute behind the point the run started reading. A run that fails or is cancelled part way through therefore leaves the watermark where the last completed import put it, and the next run re-reads a small overlap. Re-reading an unchanged resource is harmless; missing a change would not be.

### Export

| Change | Request |
|---|---|
| Create | `POST` to the resource type's endpoint. The provider assigns the `id`, which JIM records as the External ID. |
| Update | `PATCH`, naming only what changed. |
| Update (provider without PATCH) | The resource is read, the changes are applied to it, and the whole resource is written back with `PUT`. |
| Delete | `DELETE`. A resource that has already gone counts as success. |

Where the provider maintains entity tags, updates carry `If-Match`, so an export cannot silently overwrite a change made in the provider since JIM last read it. A rejected update is reported as a concurrency conflict and reconciled by the next import.

An attribute the provider's schema does not have, or will not accept a write to, fails that object rather than exporting the rest of it: a partial change recorded as applied is worse than one that failed.

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

## Connection Settings

### SCIM Service Provider

| Setting | Required | Description |
|---|---|---|
| Base URL | Yes | The root of the SCIM service, for example `https://example.com/scim/v2`. Point it at the root, not at a resource endpoint such as `/Users`. HTTPS is required, except for loopback addresses when testing locally. |

### Authentication

| Setting | Required | Description |
|---|---|---|
| Authentication Method | Yes | OAuth 2.0 Client Credentials, HTTP Basic, Static Bearer Token, or Custom Header. |
| Token Endpoint URL | With OAuth 2.0 | The token endpoint used to acquire access tokens. |
| Client ID / Client Secret | With OAuth 2.0 | The client credentials. Tokens are cached until shortly before they expire and refreshed automatically. |
| OAuth Scope | No | Optional space-separated scopes to request. |
| Username / Password | With HTTP Basic | The credentials to authenticate with. |
| Bearer Token | With Static Bearer Token | A long-lived token issued by the provider. |
| Authentication Header Name / Value | With Custom Header | For providers using a non-standard header, for example `X-Api-Key`. |

Every secret is encrypted at rest and is never written to a log.

### Transport Security

| Setting | Default | Description |
|---|---|---|
| Certificate Validation | Full Validation | Validates the provider's TLS certificate against the system trust store plus any certificates added in **Admin > Certificates**. Skip Validation accepts whatever the provider presents. |
| Minimum TLS Version | TLS 1.2 | The lowest TLS version accepted. |
| Connection Timeout | 30 seconds | How long to wait for a response. |

#### When a connection test fails on the certificate

A provider using a certificate JIM does not yet trust (an internal certificate authority, or a self-signed certificate) fails the connection test. JIM shows you the certificate the provider presented: its subject, the names it was issued for, its issuer, validity dates and thumbprint, along with which check it failed and what to do about it. The same detail appears on a failed Activity and on the Activity's `errorDetail` field in the REST API.

**Trusting the certificate is the recommended way to unblock this, and JIM offers it on the certificate itself.** Select **Trust this certificate** on the card, and JIM reads the certificate from the provider again, checks it is still the one you were shown, and adds it to the Trusted Certificates store. You never have to obtain the certificate file by other means, which for a hosted service is often the hardest part of the exercise.

You are asked to confirm before anything is added, because this is a security decision. Compare the thumbprint against the one the provider's administrator gives you first. Where the provider sent the authority that issued its certificate, JIM offers that as well and recommends it: trusting the authority survives the provider's own certificate being renewed, whereas trusting the certificate itself has to be repeated each time. A self-signed certificate has no separate authority, so there is only one thing to trust.

Reading the certificate again at the moment you confirm is what makes a change detectable. If the provider is presenting something other than what you were shown, JIM trusts nothing and shows you both thumbprints, which is expected after a renewal and worth investigating otherwise.

**You do not have to wait for a failure.** **Fetch certificate** on the Connected System's settings reads and shows what the provider is presenting before anything has been saved, so configuring a new system is not a cycle of save, fail, come back. Fetching stores nothing; trusting is still the separate, confirmed step.

Note what trusting the certificate does and does not cover. JIM's store answers one question, "do we trust the issuer", so it waives an unknown certificate authority and nothing else. An expired certificate is not a trust gap and is still refused; a certificate issued for a different name is an interception signal, so connect using a name the certificate carries rather than adding it to the store. JIM offers the action only where trusting genuinely fixes the failure, so it does not appear in those cases.

!!! danger "Skip Validation hides certificate changes"
    Skip Validation accepts whatever the provider presents, now and in future, so a certificate that changes, expires or is swapped for an attacker's passes silently. It exists for the cases where nothing else will do (a lab, a provider you cannot get a certificate out of), and it is recorded on the Connected System so an auditor can see it was chosen. Trusting the presented certificate is both safer and now the shorter route: it is one action on the failure itself, and it keeps JIM checking that the certificate has not changed.

To do the same from a script:

```powershell
$reading = Get-JIMConnectedSystemServerCertificate -ConnectedSystemId 42
$reading.certificate | Select-Object subject, issuer, thumbprint, issuerThumbprint

Approve-JIMConnectedSystemServerCertificate -ConnectedSystemId 42 `
    -Thumbprint $reading.certificate.thumbprint `
    -ChangeReason 'Unblocking the HR Cloud connection test.'
```

### Import

| Setting | Default | Description |
|---|---|---|
| Pagination Mode | Auto-detect | Auto-detect starts with index-based paging and switches to cursors if the provider offers one. Choose Cursor-based for large or frequently-changing providers: index-based paging can miss or repeat objects when the data changes during an import. |
| Excluded Attributes | (none) | A comma-separated list of SCIM attributes to ask the provider not to return, for example `photos, x509Certificates`. Useful for large attributes JIM does not need. `id` and `meta` are always requested, because importing cannot work without them. |
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

## Security Considerations

- **HTTPS is required.** Identity data must not travel over cleartext HTTP. Plain HTTP is permitted only for loopback addresses, so that a local test provider can be used during evaluation.
- **Secrets are encrypted at rest** and never logged, sanitised or otherwise.
- **Least privilege.** Give the credential only the SCIM permissions the Run Profiles you have configured need: read for import, write for export.
- **No cloud dependency.** The connector calls only the Base URL you configure, so it works unchanged in an air-gapped deployment against an on-premises provider.

## Troubleshooting

**"It did not answer on any SCIM discovery endpoint"**<br />
The Base URL is probably pointing at a resource endpoint rather than the root of the service. It should be the path that `/ServiceProviderConfig` hangs off.

**Delta Imports read everything every time**<br />
Check the Activity for a warning. Either the provider does not advertise filtering (set Change Detection to Last Modified Filter if you know it supports it anyway), or the provider rejected the filter, or no watermark had been recorded yet, in which case the next Delta Import will be incremental.

**Objects go missing from JIM after a Delta Import**<br />
They should not: deletion detection runs only on a Full Import. If objects are being marked obsolete, the run in question was a Full Import that could not see them.

**Updates fail with a concurrency conflict**<br />
Something changed the resource in the provider between JIM reading it and writing it back. Run an import to pick up the current state; the change will be re-evaluated against it.

**The connection test fails with a certificate error**<br />
JIM shows the certificate the provider presented and which check it failed. An untrusted issuer is fixed by adding the certificate under **Admin > Certificates**; an expired certificate has to be renewed on the provider; a name mismatch means connecting by a name the certificate carries. See [When a connection test fails on the certificate](#when-a-connection-test-fails-on-the-certificate).

**An attribute never receives a value**<br />
Re-import the schema. A provider that has changed what it publishes, or one whose `/Schemas` document went missing (leaving the connector on the RFC 7643 core schemas), will offer a different attribute set.

## Related

- [Connectors](index.md)
- [Connected Systems](../configuration/connected-systems.md)
