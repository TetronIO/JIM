# OWASP Top 10:2025 Assessment

- **Status:** Doing (remediation in progress; see [issue #500](https://github.com/TetronIO/JIM/issues/500) for current per-gap status)
- **Issue:** [#500](https://github.com/TetronIO/JIM/issues/500)
- **Assessed:** 2026-04-09
- **Standard:** [OWASP Top 10:2025](https://owasp.org/Top10/2025/)

## Overview

This document assesses JIM against each category in the OWASP Top 10:2025. JIM is deployed in high-trust/assurance environments (healthcare, financial services, government), so all gaps identified here should be treated seriously even if the practical risk is moderate.

**Overall result:** JIM is in strong shape. The fundamentals (authentication, access control, cryptography, injection prevention, exception handling) are solid and well-implemented. Five gaps were identified; none represent critical vulnerabilities, but all should be remediated to maintain the security posture expected by JIM's target deployment environments.

## Assessment Results

### A01:2025; Broken Access Control

**Rating: Strong**

All API controllers enforce role-based authorisation via `[Authorize]` attributes. Blazor pages are secured with role requirements. Anonymous access is deliberate and limited to health checks and OAuth discovery.

| Control | Evidence |
|---------|----------|
| API controllers require `[Authorize(Roles = "Administrator")]` | `SecurityController.cs:19`, `ApiKeysController.cs:25` |
| Blazor admin pages require Administrator role | All `/admin/*` pages verified |
| Index page requires `[Authorize(Roles = "User")]` | `Index.razor:2` |
| `[AllowAnonymous]` used only where necessary | `HealthController.cs:19` (load balancers), `AuthController.cs:37` (OAuth discovery) |
| API requests return HTTP 401 (not redirect) | `Program.cs:252-256` |

**Gaps:** None.

---

### A02:2025; Security Misconfiguration

**Rating: Good, with gaps**

Core security configuration is sound: HSTS, HTTPS redirection, restricted development tooling. Two gaps exist.

| Control | Evidence |
|---------|----------|
| HSTS enabled in production | `Program.cs:398` |
| HTTPS redirection enabled | `Program.cs:414` |
| OpenAPI spec pre-generated at build time and served as a static document (no runtime OpenAPI generator in production); Scalar API reference available in every environment for authenticated users | `src/JIM.Web/wwwroot/api/openapi/v1.json`, `Program.cs` |
| Stack traces suppressed in production | `GlobalExceptionHandler.cs:32` |
| DbContext pooling prevents connection exhaustion | `Program.cs:76-80` |

**Gap 1: No rate limiting (High priority) - ✅ Remediated**

No `UseRateLimiter()` middleware is configured. Authentication endpoints (`/api/auth`, `/api/apikeys`) are exposed to brute-force attacks with no throttle. This is the highest-priority gap in the assessment.

Remediated: `Microsoft.AspNetCore.RateLimiting` (built-in; no new dependency) now protects the whole REST API with per-client partitioning (authenticated principal or client IP), HTTP 429 with `Retry-After`, and `ForwardedHeaders` handling (`JIM_TRUSTED_PROXIES`) so per-IP partitioning is correct behind a reverse proxy. Limits are runtime-tunable via Service Settings. See [Rate Limiting](../../../docs/api/rate-limiting.md).

**Gap 2: No Content Security Policy header (Medium priority)**

No explicit CSP header is defined. Blazor Server uses inline scripts/styles which complicates CSP, but a policy should still be defined to mitigate XSS risks.

---

### A03:2025; Software Supply Chain Failures

**Rating: Good, with gap**

Framework and packages are current. One structural gap exists around dependency pinning.

| Control | Evidence |
|---------|----------|
| .NET 10 (latest supported release) | All `.csproj` files target `net10.0` |
| NuGet packages appear current | e.g. `Microsoft.AspNetCore.Authentication.*` v10.0.4 |
| Third-party dependency governance documented | `CLAUDE.md` requires approval before adding any NuGet package |

**Gap 1: No `packages.lock.json` (Medium priority)**

NuGet packages are not cryptographically pinned. `dotnet restore` can resolve different transitive dependency versions across environments, which undermines reproducible builds and supply chain integrity.

**Gap 2: DynamicExpresso review needed (Low priority)**

`DynamicExpresso.Core` v2.19.3 is used for expression evaluation in `JIM.Application`. If user-controlled input can flow into the expression interpreter, this becomes a code injection vector. The input paths need to be reviewed and documented to confirm this is safe.

---

### A04:2025; Cryptographic Failures

**Rating: Strong**

Encryption at rest uses AES-256-GCM (FIPS-approved). Cryptographically secure RNG is used for API key generation. No weak algorithms found.

| Control | Evidence |
|---------|----------|
| AES-256-GCM for credentials at rest | `CredentialProtectionService.cs` with versioned prefix `$JIM$v1$` |
| `RandomNumberGenerator.Create()` for API keys | `ApiKeyAuthenticationHandler.cs:159-171` (256-bit entropy) |
| Data Protection key files chmod 700 on Linux | `DataProtectionHelper.cs:95-96` |
| Key management with env var, Docker volume, and OS fallback | `DataProtectionHelper.cs:20-76` |
| No weak algorithms (MD5, SHA1, DES) | Full codebase search confirmed |

**Note:** API keys are hashed with SHA256 (`ApiKeyAuthenticationHandler.cs:179`). This is acceptable because the input is a 256-bit random value (no rainbow table risk), but the rationale should be documented.

**Gaps:** None.

---

### A05:2025; Injection

**Rating: Strong**

All database access uses parameterised queries (EF Core LINQ or typed `NpgsqlParameter` objects). LDAP uses the type-safe `DirectoryServices.Protocols` API. Log injection is mitigated via mandatory `LogSanitiser.Sanitise()`.

| Control | Evidence |
|---------|----------|
| EF Core LINQ throughout | Standard pattern across all repositories |
| Raw SQL uses typed `NpgsqlParameter` objects | `BulkSqlHelpers.cs:18-25`, `SyncRepository.CsOperations.cs:313` |
| Type-safe LDAP API (no filter string concatenation) | `LdapConnectorImport.cs` uses `SearchRequest` |
| Log injection (CWE-117) prevention | `LogSanitiser.cs`, enforced in `GlobalExceptionHandler.cs:42,46` |

**Note:** See A03 Gap 2 regarding DynamicExpresso. If user input reaches the expression interpreter, this becomes an injection risk under A05 as well.

**Gaps:** None (pending DynamicExpresso review).

---

### A06:2025; Insecure Design

**Rating: Good**

JIM's architecture reflects security-conscious design: the metaverse pattern enforces all identity changes flow through a central model, N-tier boundaries are enforced, and the system is designed for air-gapped deployment.

| Control | Evidence |
|---------|----------|
| Metaverse pattern prevents direct system-to-system writes | Core architecture principle |
| N-tier layer boundaries enforced | UI/API calls `JimApplication` only, never repositories |
| Air-gapped deployment (no cloud dependencies) | Design principle documented in `CLAUDE.md` |
| Fail-fast on missing configuration | `Program.cs:536-557` validates all required config at startup |
| Versioned credential encryption (supports algorithm migration) | `$JIM$v1$` prefix in `CredentialProtectionService.cs` |

**Gaps:** None.

---

### A07:2025; Authentication Failures

**Rating: Strong**

OIDC with Authorization Code + PKCE is the primary authentication mechanism. Full token validation is implemented. API key authentication includes expiry, usage tracking, and secure storage.

| Control | Evidence |
|---------|----------|
| OIDC with Authorization Code + PKCE | `Program.cs:168-278` |
| Full token validation (issuer, audience, lifetime, signing key) | `Program.cs:268-274` |
| Session lifetime bound to IdP token | `UseTokenLifetime = true` at `Program.cs:171` |
| Triple auth scheme (Cookie + JWT Bearer + API Key) | `Program.cs:146-167` with header-based forwarding |
| API keys: prefix validation, expiry, usage tracking, SHA256 hash | `ApiKeyAuthenticationHandler.cs` |

**Gaps:** None.

---

### A08:2025; Software or Data Integrity Failures

**Rating: Good, with gap**

Data Protection keys are version-prefixed and purpose-isolated. The main gap is shared with A03: no `packages.lock.json` for reproducible builds.

| Control | Evidence |
|---------|----------|
| Versioned Data Protection with purpose isolation | `CredentialProtectionService.cs:18,24` |
| Blazor serves static assets from the app (no external CDN) | Default Blazor Server hosting |

**Gap:** No `packages.lock.json` (same as A03 Gap 1). Build artefacts are not reproducibly tied to specific dependency versions.

---

### A09:2025; Security Logging and Alerting Failures

**Rating: Good, with gap**

Structured logging is comprehensive: Serilog with JSON output, rolling log files, log injection prevention, and sync error reporting via RPEIs. The gap is in security-specific audit and alerting.

| Control | Evidence |
|---------|----------|
| Structured logging via Serilog with JSON output | `Program.cs:478-526` |
| Rolling log files (100 max, 50MB each) | `Program.cs:519-520` |
| Log injection prevention (CWE-117) | `LogSanitiser.cs` enforced across codebase |
| Sync errors logged via RPEI/Activities | Core sync operation pattern |

**Gap: No security audit trail (Medium priority)**

There is no dedicated security audit log for privileged operations such as API key creation/deletion, role changes, configuration modifications, or authentication failures. For healthcare/government deployments, a tamper-evident audit trail is a compliance expectation. Current application logs mix operational and security events, making it difficult to isolate security-relevant actions for review or SIEM integration.

---

### A10:2025; Mishandling of Exceptional Conditions

**Rating: Strong**

A global exception handler catches all unhandled exceptions. Production responses are generic (no internal detail leaked). Sync operations fail fast on errors rather than continuing with corrupted state.

| Control | Evidence |
|---------|----------|
| Global exception handler | `GlobalExceptionHandler.cs` |
| Production: generic error messages only | `GlobalExceptionHandler.cs:97` |
| Development: full detail (correct behaviour) | `GlobalExceptionHandler.cs:32` |
| Transient DB errors return 503 + Retry-After | `GlobalExceptionHandler.cs:55-65` |
| Sync errors fail entire activity (no silent corruption) | `UnhandledError` RPEI items fail activity |

**Gaps:** None.

---

## Summary Matrix

| # | Category | Rating | Gaps | Priority |
|---|----------|--------|------|----------|
| A01 | Broken Access Control | Strong | None | - |
| A02 | Security Misconfiguration | Good | ~~Rate limiting~~ (remediated), CSP header | ~~High~~, Medium |
| A03 | Software Supply Chain | Good | `packages.lock.json`, DynamicExpresso review | Medium, Low |
| A04 | Cryptographic Failures | Strong | None | - |
| A05 | Injection | Strong | None (pending DynamicExpresso) | - |
| A06 | Insecure Design | Good | None | - |
| A07 | Authentication Failures | Strong | None | - |
| A08 | Software/Data Integrity | Good | `packages.lock.json` (shared with A03) | Medium |
| A09 | Security Logging & Alerting | Good | Security audit trail | Medium |
| A10 | Exceptional Conditions | Strong | None | - |

## Remediation Recommendations

### 1. Rate Limiting Middleware (High) - ✅ Remediated

**OWASP:** A02
**Risk:** Authentication endpoints exposed to brute-force attacks.

**Option A: ASP.NET Core built-in rate limiter (Recommended)**
- Use `Microsoft.AspNetCore.RateLimiting` (built-in, no new dependency)
- Apply fixed-window or sliding-window policies to `/api/auth` and `/api/apikeys` endpoints
- Configure per-IP limits with appropriate thresholds (e.g. 10 requests/minute for auth)
- Return HTTP 429 with `Retry-After` header

**Option B: Reverse proxy rate limiting**
- Defer to deployment infrastructure (nginx, HAProxy, or cloud load balancer)
- Less control, but zero application code changes
- Not viable for air-gapped deployments without a reverse proxy

**Recommendation:** Option A. It is built-in, requires no new dependency, and works in all deployment topologies including air-gapped.

**Effort:** Low

**Implemented:** Option A, extended to the whole REST API rather than just `/api/auth`/`/api/apikeys` (any endpoint, not just the two named here, is a viable target for API key spraying). Authenticated requests are partitioned per principal (sliding window); unauthenticated requests, including failed API key attempts, per client IP (fixed window). Limits are Service Settings, not hardcoded, per product decision. `/api/health` is excluded (orchestrator probes).

---

### 2. NuGet Package Lock File (Medium)

**OWASP:** A03, A08
**Risk:** Non-reproducible builds; transitive dependency substitution.

**Remediation:**
1. Run `dotnet restore --use-lock-file` to generate `packages.lock.json` for each project
2. Commit all lock files
3. Add `<RestoreLockedMode>true</RestoreLockedMode>` to `Directory.Build.props` or CI build scripts
4. CI builds use `dotnet restore --locked-mode` to fail if lock files are out of date

**Effort:** Low

**Status: Implemented.** `packages.lock.json` is now committed for every project, and `Directory.Build.props` forces `RestoreLockedMode` whenever `CI=true`, with explicit `--locked-mode` / `/p:RestoreLockedMode=true` in CI, the release workflow, and all three production container image builds. Because Dependabot does not reliably regenerate lock files across project references when it bumps a NuGet version, a new `regenerate-nuget-lock-files` workflow watches Dependabot's NuGet branches and pushes a signed regeneration commit automatically. See `engineering/DEPENDENCY_PINNING.md` for the full policy.

---

### 3. Security Audit Trail (Medium)

**OWASP:** A09
**Risk:** Cannot isolate security events for review or compliance evidence.

**Option A: Dedicated audit log table (Recommended)**
- Create an `AuditLog` table with: Timestamp, Actor (user/API key), Action, Resource, Outcome, IP Address
- Log security-relevant actions: API key CRUD, authentication success/failure, configuration changes, role changes
- Expose via API for SIEM integration (`GET /api/v1/audit-logs` with filtering)
- Retention policy configurable via admin UI

**Option B: Structured log enrichment**
- Add a `SecurityEvent` property to Serilog log entries for security-relevant actions
- Use Serilog filtering to route security events to a separate sink (file, database, or external)
- Lower effort but less queryable and harder to make tamper-evident

**Recommendation:** Option A. A dedicated table is queryable, exportable, and can be made tamper-evident. It also serves as the foundation for a future admin UI audit viewer.

**Effort:** Medium

---

### 4. Content Security Policy Header (Medium)

**OWASP:** A02
**Risk:** No defence-in-depth against XSS via injected scripts.

**Option A: Nonce-based CSP (Recommended)**
- Blazor Server requires inline scripts for SignalR bootstrap
- Use a per-request nonce injected into both the CSP header and the `<script>` tags
- ASP.NET Core middleware generates the nonce and sets the header
- Policy: `default-src 'self'; script-src 'self' 'nonce-{value}'; style-src 'self' 'unsafe-inline'; connect-src 'self' wss:`

**Option B: Hash-based CSP**
- Pre-compute SHA256 hashes of known inline scripts
- Less flexible (hashes must be updated when Blazor framework updates change inline scripts)

**Option C: `unsafe-inline` with restrictive defaults**
- Set `script-src 'self' 'unsafe-inline'` as a starting point
- Weaker than nonce-based, but still better than no CSP at all
- Can be tightened later

**Recommendation:** Start with Option C as a quick win, then migrate to Option A. Blazor Server's inline script requirements make nonce-based CSP non-trivial; a staged approach avoids breaking the UI.

**Effort:** Low (Option C), Medium (Option A)

---

### 5. DynamicExpresso Input Path Review (Low)

**OWASP:** A03, A05
**Risk:** If user-controlled input reaches the expression evaluator, it becomes a code injection vector.

**Remediation:**
1. Trace all call sites of `DynamicExpresso` in `JIM.Application`
2. Document which inputs are user-controlled vs. admin-configured vs. system-generated
3. If user input can reach the interpreter:
   - Implement an allowlist of permitted functions/operators
   - Sandbox the expression context (DynamicExpresso supports restricting available types)
   - Add input length limits
4. Document the findings regardless of outcome

**Effort:** Low (review), Medium (if sandboxing is needed)
