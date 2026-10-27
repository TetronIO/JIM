---
title: Security Audit Events
description: The sign-in and API key authentication events JIM records in its Activity log, how long it keeps them, and how to send them to a SIEM.
type: reference
audience: security-assessor
---

# Security Audit Events

JIM records authentication activity, interactive sign-in success and failure, and failed API authentication with an API key or an SSO access token, in the same [Activity](../configuration/activities.md) system used for every other tracked operation, so there is a single audit log rather than a parallel security store. This is the audit-trail leg of OWASP Top 10 A09 (Security Logging and Monitoring Failures); it complements [rate limiting](../api/rate-limiting.md) and [security headers](security-headers.md) from the same assessment.

## :material-clipboard-text-clock-outline: What is recorded

| Event | Initiator | Write shape |
|-------|-----------|-------------|
| Interactive sign-in succeeded | The signed-in user | One Activity per session establishment |
| Interactive sign-in failed | **Anonymous** | Aggregated (see below) |
| API key authentication failed | **Anonymous** | Aggregated (see below) |
| Access token authentication failed | **Anonymous** | Aggregated (see below) |
| API key authentication succeeded | *n/a* | Not written as an Activity; API key lifecycle (create, update, disable, delete) is already audited via [Configuration Change History](../configuration/activities.md#configuration-change-history), and each successful call updates the key's last-used timestamp |

Every security audit event carries `Authentication` as its Activity target type, and a client IP address where one was available. Filter the [Activity list](../configuration/activities.md#filtering-the-activity-list) to the **Security** category, or the `Authentication` type, to see these events on their own.

## :material-sigma: Aggregation for failures

Failed authentication is the only event class written on behalf of an unauthenticated, attacker-controlled source: a naive one-row-per-failure design would let a key-spraying or credential-stuffing client force database writes at wire speed. To keep the audit log itself from becoming a denial-of-service amplifier, failures are aggregated: **one Activity per (API key prefix, client IP, failure reason) per 15-minute UTC window**, carrying a first-seen timestamp, a last-seen timestamp, and an attempt counter that increments in place as further matching attempts arrive in the same window.

Aggregation bounds how many Activities an attack produces while preserving the count: a 500-attempt spray against one key from one IP in a 15-minute window produces a single Activity with an attempt count of 500, not 500 Activities. It does not bound the work: every failed attempt still updates its Activity, and each client IP gets an Activity of its own. For an unknown API key, the prefix recorded is the start of whatever the caller sent, so a caller who varies it also gets an Activity per prefix.

Authentication happens before [rate limiting](../api/rate-limiting.md), and rate limiting applies only to the API, so it does not throttle these writes. To limit a single source's request rate across all of JIM, do it at the reverse proxy or load balancer in front of it.

A failure's reason is a short, sanitised category:

| Event | Reasons |
|-------|---------|
| API key authentication failed | `API key not found`, `API key is disabled`, `API key has expired`, `Authentication error` |
| Access token authentication failed | `Access token expired`, `Access token not yet valid`, `Access token lifetime invalid`, `Access token signature invalid`, `Access token signing key not found`, `Access token signing key invalid`, `Access token algorithm not allowed`, `Access token issuer invalid`, `Access token audience invalid`, `Access token type invalid`, `Access token replayed`, `Access token malformed`, `Access token decryption failed`, `Access token validation failed` |
| Interactive sign-in failed | For example `OIDC correlation failed`, `OIDC token expired`, `OIDC access denied` |

The raw exception or provider error text is never stored: it can be attacker-influenced and is unbounded in length, neither of which belongs in an audit record. Nor is the credential itself: an API key failure records only the key's identifying prefix, and an access token failure records none of the token.

JIM recognises an API key by its `jim_ak_` prefix, so a bearer token without that prefix is treated as an SSO access token. A key whose `jim_ak_` prefix is damaged therefore shows up as `Access token malformed`; a key with an intact prefix but a wrong or truncated remainder shows up as `API key not found`, with the first characters it was sent as its prefix.

An access token is recorded only when the token itself is at fault. When a valid token's user cannot be resolved, or the identity provider cannot be reached to check signatures, JIM reports a server error instead: these are faults to fix, not refused credentials.

## :material-timer-sand: Retention

Security audit events are governed by their own retention period, the `History.SecurityEventRetentionPeriod` [Service Setting](../configuration/service-settings.md) (default **365 days**, ~1 year), independently of the general history retention period and the (much longer) configuration change retention period. Housekeeping cleanup removes expired security events on its own schedule; the general history cleanup never touches them.

## :material-ip-network-outline: Client IP capture

An IP address is personal data, so capture is scoped to security-relevant Activities (this feature, plus the pre-existing API key usage tracking) and governed by the retention period above.

If JIM sits behind a reverse proxy or load balancer, the recorded IP is only accurate once JIM trusts the proxy's forwarded headers; see [Rate Limiting: Reverse proxies](../api/rate-limiting.md#reverse-proxies) for the `JIM_TRUSTED_PROXIES` configuration this depends on. Without it, every event records the proxy's own address rather than the real client.

## :material-bell-ring-outline: SIEM integration

Security audit events are queryable through the same [Activities REST API](../../api/reference/) as everything else, filterable by target type, so a SIEM or log shipper can poll for them specifically without a bespoke export mechanism.

**Polling pattern:**

```http
GET /api/v1/activities?targetType=Authentication&sortBy=created&sortDirection=desc
```

Repeat-key the `targetType` query parameter to combine with other target types in one query.

A failure's Activity keeps counting attempts in place until its 15-minute window ends, so polling only for Activities created since your last poll would miss attempts added to one you have already ingested. Instead, on each poll pass `createdFrom` as the start of the 15-minute UTC window your previous poll ran in (a previous poll at 10:37 means `createdFrom=…T10:30:00Z`), and upsert what comes back by Activity `id`, keeping the latest `attemptCount` and `lastSeen`. An Activity whose `aggregationWindowStart` is more than 15 minutes before the current time no longer changes.

Each returned Activity for a failure carries its aggregation window start, first-seen and last-seen timestamps, attempt count, failure reason, API key prefix (never the key itself), and client IP; a sign-in success carries the signed-in user's identity and client IP. See the [interactive API reference](../../api/reference/) for the full response schema.

## :material-book-open-variant-outline: See also

- [Activities](../configuration/activities.md): the general Activity system this feature is built on
- [Rate Limiting](../api/rate-limiting.md): throttles API requests once they are authenticated; it does not limit failed attempts
- [Security Headers](security-headers.md): the other application-layer control from the same security assessment
- [Service Settings](../configuration/service-settings.md): how to change the retention period
