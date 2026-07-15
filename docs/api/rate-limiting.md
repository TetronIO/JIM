---
title: Rate Limiting
---

# Rate Limiting

JIM's REST API is protected by configurable rate limiting, so a misbehaving integration, a brute-force credential attempt, or a runaway script cannot exhaust server resources or degrade the service for other clients. This is an application-layer control (OWASP Top 10 A02: Security Misconfiguration); it complements, but does not replace, [brute-force protection and MFA at your identity provider](../administration/sso-setup.md#brute-force-protection-and-mfa).

## What is limited

Only the REST API (paths under `/api/`) is throttled. The Blazor web UI, the SignalR/Blazor Server connection, and static assets are never rate limited.

`/api/v1/health` (and its `/ready`, `/live`, `/version` sub-routes) is exempt: load balancer and orchestrator health probes must never be throttled, and the endpoint is cheap and carries no data.

## How clients are grouped

- **Authenticated requests** are limited per client, identified by a stable ID (the signed-in user's Metaverse Object ID, or the API key's ID), using a **sliding** one-minute window.
- **Unauthenticated requests**, including a request that failed API key authentication, are limited per client IP address, using a **fixed** one-minute window.

Each client's limit is tracked independently: one integration hitting its limit does not affect any other client.

### Infrastructure API keys are exempt

The [infrastructure API key](authentication.md) (the key auto-created from the `JIM_INFRASTRUCTURE_API_KEY` environment variable, shown with an **Infrastructure** badge in the API keys admin page) is **fully exempt** from rate limiting. It is trusted backend automation (CI/CD pipelines, integration tests, bulk configuration) authenticated from a pre-shared bootstrap secret, and it legitimately issues large bursts of requests, for example creating dozens of Synchronisation Rules in one script. Rate limiting exists to blunt untrusted, interactive, or runaway callers, not this trusted principal, so it is never throttled.

Ordinary API keys (those you create in the admin UI) are **not** exempt: they are limited exactly like any other authenticated principal. If a script driven by an ordinary key needs to issue large bursts, either back off on `429` (see below) or, if the key is genuinely for trusted automation, raise `Security.RateLimiting.AuthenticatedRequestsPerMinute`.

## Service Settings

Three built-in [Service Settings](../configuration/service-settings.md) (Security category) control rate limiting, and can be changed at runtime by an Administrator without restarting JIM:

| Key | Display name | Type | Default | Description |
|-----|---------------|------|---------|-------------|
| `Security.RateLimiting.Enabled` | API rate limiting enabled | Boolean | `true` | When `false`, no limiter is applied to any API request. |
| `Security.RateLimiting.AuthenticatedRequestsPerMinute` | Authenticated API requests per minute | Integer | `300` | The per-client limit for authenticated requests. |
| `Security.RateLimiting.UnauthenticatedRequestsPerMinute` | Unauthenticated API requests per minute | Integer | `30` | The per-IP limit for unauthenticated requests. |

!!! info "Settings propagation delay"
    The rate limiter reads these settings from a short-lived cache (TTL: 30 seconds) rather than the database on every request, so it does not add a database round trip to every API call. A change to any of the three settings above takes effect for new requests within 30 seconds, not instantly.

## When a client is rate limited

A request that exceeds its limit receives:

- **HTTP 429 Too Many Requests**
- A **`Retry-After`** header, in seconds, indicating how long to wait before retrying
- A JSON body in JIM's standard API error shape:

```json
{
  "code": "TOO_MANY_REQUESTS",
  "message": "Too many requests. Please retry after the indicated delay.",
  "details": null,
  "validationErrors": null,
  "timestamp": "2026-07-13T12:00:00Z"
}
```

Well-behaved clients should back off for the duration given in `Retry-After` before retrying.

!!! tip "The JIM PowerShell module retries automatically"
    Every cmdlet in the [JIM PowerShell module](../powershell/index.md) honours `429` transparently: it waits for the `Retry-After` interval (falling back to bounded exponential backoff if the header is absent) and retries the request, up to a small fixed budget, before surfacing an error. A script that momentarily exceeds the limit therefore rides out the window and continues rather than failing; run with `-Verbose` to see the back-off messages. If the budget is exhausted, the cmdlet throws a `429` error advising you to retry later or reduce the request rate.

## Reverse proxies

If JIM sits behind a reverse proxy or load balancer, the unauthenticated (per-IP) limit needs to see the real client IP address, not the proxy's. By default JIM does **not** trust any forwarded-header information (`X-Forwarded-For`/`X-Forwarded-Proto`) and uses the connecting socket's address as-is, which is correct when JIM is reached directly.

Set the `JIM_TRUSTED_PROXIES` environment variable to a comma-separated list of trusted proxy IP addresses and/or CIDR networks (for example `10.0.0.1,172.16.0.0/12`) to enable forwarded-header trust from those sources only. See [Configuration Reference](../administration/configuration.md) for details.

## See also

- [Authentication](authentication.md): how to authenticate REST API requests
- [Service Settings](../configuration/service-settings.md): general Service Settings behaviour (defaults, overrides, change history)
- [Configuration Reference](../administration/configuration.md): the `JIM_TRUSTED_PROXIES` environment variable
