---
title: Security Audit Events
---

# Security Audit Events

JIM records authentication activity, interactive sign-in success and failure, and API key authentication failure, in the same [Activity](../configuration/activities.md) system used for every other tracked operation, so there is a single audit log rather than a parallel security store. This is the audit-trail leg of OWASP Top 10 A09 (Security Logging and Monitoring Failures); it complements [rate limiting](../api/rate-limiting.md) and [security headers](security-headers.md) from the same assessment.

## What is recorded

| Event | Initiator | Write shape |
|-------|-----------|-------------|
| Interactive sign-in succeeded | The signed-in user | One Activity per session establishment |
| Interactive sign-in failed | **Anonymous** | Aggregated (see below) |
| API key authentication failed | **Anonymous** | Aggregated (see below) |
| API key authentication succeeded | *n/a* | Not written as an Activity; API key lifecycle (create, update, disable, delete) is already audited via [configuration change history](../configuration/activities.md#configuration-change-history), and each successful call updates the key's last-used timestamp |

Every security audit event carries `Authentication` as its Activity target type, and a client IP address where one was available. Filter the [Activity list](../configuration/activities.md#filtering-the-activity-list) to the **Security** category, or the `Authentication` type, to see these events on their own.

## Aggregation for failures

Failed authentication is the only event class written on behalf of an unauthenticated, attacker-controlled source: a naive one-row-per-failure design would let a key-spraying or credential-stuffing client force database writes at wire speed. To keep the audit log itself from becoming a denial-of-service amplifier, failures are aggregated: **one Activity per (API key prefix, client IP, failure reason) per 15-minute UTC window**, carrying a first-seen timestamp, a last-seen timestamp, and an attempt counter that increments in place as further matching attempts arrive in the same window.

This bounds the worst case to a handful of rows regardless of attack volume, while preserving the count: a 500-attempt spray against one key from one IP in a 15-minute window produces a single Activity with an attempt count of 500, not 500 rows. [Rate limiting](../api/rate-limiting.md) sits in front of these writes as the primary throttle; aggregation is the backstop for whatever gets through.

A failure's reason is one of: `Invalid API key format`, `API key not found`, `API key is disabled`, `API key has expired`, `Authentication error` (API key path), or a short, sanitised category of the interactive sign-in failure (for example `OIDC correlation failed`, `OIDC token expired`). The raw exception or provider error text is never stored: it can be attacker-influenced and is unbounded in length, neither of which belongs in an audit record.

## Retention

Security audit events are governed by their own retention period, the `History.SecurityEventRetentionPeriod` [Service Setting](../configuration/service-settings.md) (default **365 days**, ~1 year), independently of the general history retention period and the (much longer) configuration change retention period. Housekeeping cleanup removes expired security events on its own schedule; the general history cleanup never touches them.

## Client IP capture

An IP address is personal data, so capture is scoped to security-relevant Activities (this feature, plus the pre-existing API key usage tracking) and governed by the retention period above.

If JIM sits behind a reverse proxy or load balancer, the recorded IP is only accurate once JIM trusts the proxy's forwarded headers; see [Rate Limiting: Reverse proxies](../api/rate-limiting.md#reverse-proxies) for the `JIM_TRUSTED_PROXIES` configuration this depends on. Without it, every event records the proxy's own address rather than the real client.

## SIEM integration

Security audit events are queryable through the same [Activities REST API](../../api/reference/) as everything else, filterable by target type, so a SIEM or log shipper can poll for them specifically without a bespoke export mechanism.

**Polling pattern:**

```
GET /api/v1/activities?targetType=Authentication&sortBy=created&sortDirection=desc
```

Repeat-key the `targetType` query parameter to combine with other target types in one query. Track the newest `created` timestamp you have already ingested and pass it back as the lower bound on your next poll to avoid re-fetching unchanged history; because failures aggregate in place, a row you have already seen may reappear with a higher attempt count and a later `lastSeen` if the same window is still accumulating attempts, so key ingestion on the Activity ID and prefer the latest version of a row over discarding it as a duplicate.

Each returned Activity for a failure carries its aggregation window start, first-seen and last-seen timestamps, attempt count, failure reason, API key prefix (never the key itself), and client IP; a sign-in success carries the signed-in user's identity and client IP. See the [interactive API reference](../../api/reference/) for the full response schema.

## See also

- [Activities](../configuration/activities.md) -- the general Activity system this feature is built on
- [Rate Limiting](../api/rate-limiting.md) -- the primary throttle in front of authentication attempts
- [Security Headers](security-headers.md) -- the other application-layer control from the same security assessment
- [Service Settings](../configuration/service-settings.md) -- how to change the retention period
