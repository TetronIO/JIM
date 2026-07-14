# Security Audit Events via Activities

- **Status:** Planned
- **Issue:** [#500](https://github.com/TetronIO/JIM/issues/500) (OWASP Top 10:2025 assessment, gap 3: security audit trail)

## Overview

Close the remaining scope of the OWASP A09 "security audit trail" gap by routing authentication events through the existing Activity system, keeping a **single audit log** rather than introducing a parallel store. The configuration change capture system already audits privileged configuration operations (API key CRUD, Role definition and membership, Connected Systems, Synchronisation Rules, Schedules, Service Settings) with versioned snapshots, actor attribution, and long retention; what is missing is authentication telemetry (sign-in success/failure, failed API key attempts) and client origin capture.

Design agreed with the product owner on 2026-07-13. Tamper evidence (hash-chained audit records) is **explicitly deferred**: there is no live compliance driver, and it can be added later scoped to the security and configuration Activity classes without reworking this design.

## Design

### The hard constraint

Failed authentication is the only event class written on behalf of an unauthenticated, attacker-controlled source. A naive one-Activity-per-failure design lets a key-spraying client force database writes at wire speed, making the audit log itself a denial-of-service amplifier and bloating a store customers treat as a compliance record. Everything below is shaped by that constraint.

### Event model

| Event | Source | Initiator attribution | Write shape |
|-------|--------|----------------------|-------------|
| Interactive sign-in succeeded | OIDC `OnTokenValidated` in `JIM.Web` `Program.cs` | The signed-in user (Metaverse Object) | One Activity per session establishment (naturally low volume; cookie sessions mean one event per sign-in, not per request) |
| Interactive sign-in failed | OIDC `OnRemoteFailure` / `OnAuthenticationFailed` | `Anonymous` (new `ActivityInitiatorType` value) | Aggregated (below) |
| API key authentication failed | `ApiKeyAuthenticationHandler` failure paths (bad format, unknown key, disabled, expired) | `Anonymous` | Aggregated (below) |
| API key authentication succeeded | Existing usage tracking (`LastUsed`) | n/a | Unchanged; no Activity per call. Key lifecycle is already audited via configuration change capture |

- New `ActivityTargetType.Authentication` groups these events; a metadata payload carries the failure reason, key prefix (never the key), and client IP.
- New `ActivityInitiatorType.Anonymous` for events with no authenticated principal.

### Aggregation-on-write for failures

One Activity per (key prefix, client IP, failure reason) per 15-minute window, carrying first-seen and last-seen timestamps and an attempt counter that increments in place. Bounded worst-case row rate regardless of attack volume; this is also the shape SIEMs normalise brute-force noise into. Rate limiting (issue #500 delivery plan PR 2) sits in front of these writes as the primary throttle; aggregation is the backstop for what gets through.

### Client IP capture

Nullable client IP fields on Activity, populated for user-, API key-, and Anonymous-initiated Activities from `HttpContext`. Depends on the `ForwardedHeaders` handling landing with the rate limiting work, so recorded IPs are truthful behind a reverse proxy. An IP address is personal data: capture is documented, and the retention class below governs its lifetime.

### Retention

A third retention class, security events, with its own Service Setting (default 1 year; configurable), separate from general history retention and the ~10-year configuration change retention. Cleanup integrates with the existing `ChangeHistoryServer` retention machinery, which already supports class-specific cutoffs.

### SIEM integration

Activities are already queryable via the REST API; add filtering by the new target type so a SIEM can poll security events specifically. Document the pull pattern.

## Implementation phases

1. **Model and migration**: enum values (`ActivityTargetType.Authentication`, `ActivityInitiatorType.Anonymous`), client IP fields, aggregation fields (window start, first/last seen, attempt count).
2. **API key failure capture**: aggregation writer service (single upsert per window bucket), wired into `ApiKeyAuthenticationHandler` failure paths; must never block or fail authentication processing on audit-write failure (log and continue; the auth outcome is primary).
3. **Interactive sign-in events**: OIDC event hooks in `Program.cs`.
4. **Retention**: security-event retention Service Setting (seeded via the audited path per `src/CLAUDE.md` seeding invariants, restored on factory reset), cleanup integration, tests.
5. **API surface and docs**: Activities API filtering by target type, admin documentation for the new events, retention setting, and SIEM pull pattern.
6. **Tests throughout** (red first): aggregation window semantics, initiator attribution, IP capture with and without forwarded headers, retention cutoffs, spray-volume bounding.

## Success criteria

- A failed API key spray of any volume produces a bounded number of Activity rows per window while remaining visible (counts preserved).
- Interactive sign-ins and failures appear in the Activity list with correct attribution and IP.
- Security events are filterable via UI and API, and are deleted by their own retention schedule only.
- No authentication path is slowed or destabilised by audit capture (audit-write failure never fails auth).

## Dependencies

- Rate limiting and `ForwardedHeaders` (issue #500 delivery plan PR 2) should land first: it fronts the failure-event writes and makes captured IPs truthful behind proxies.

## Risks and mitigations

- **Write amplification under spray**: aggregation bounds rows per window; rate limiting bounds attempts. Both must be present before this feature is considered complete.
- **Activity model fit**: authentication events carry no target entity; the Activity schema treats target associations as optional for this target type (precedent: `HistoryRetentionCleanup` and `System` Activities).
- **Privacy**: IPs are personal data; capture is scoped to security-relevant Activities, documented, and retention-bound.

## Deferred

- **Tamper evidence** (hash-chaining of audit records): deferred 2026-07-13, no live compliance driver. If required later, chain only the security and configuration Activity classes; the aggregation design above is compatible (aggregated rows are updated in place within their window, so chaining would seal rows only once their window closes).
