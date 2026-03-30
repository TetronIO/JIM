# Devcontainer Identity Provider (Keycloak)

- **Status:** Doing (Phases 1-3 complete)
- **Issue:** [#197](https://github.com/TetronIO/JIM/issues/197)
- **Last Updated:** 2026-03-30
- **Milestone:** v0.9-STABILISATION

## Overview

Integrate a pre-configured Keycloak instance into the devcontainer Docker stack so that developers can sign in to JIM immediately without configuring an external Identity Provider. The Keycloak instance ships with a realm export containing clients, scopes, and test users — zero manual setup required.

## Problem

Developers must currently:

1. Register an application with an external IdP (Entra ID, Keycloak, Okta, etc.)
2. Generate client credentials
3. Configure 6+ environment variables in `.env`
4. Understand OIDC concepts to get values right

This creates a barrier for quick evaluation, onboarding new contributors, and local development. The devcontainer should be self-contained.

## Goals

- Developers can `jim-stack` and sign in to JIM without any IdP configuration
- External IdP configuration remains supported by overriding `.env` values
- PowerShell module (`Connect-JIM`) works against the bundled Keycloak
- No changes to JIM's OIDC code (it is already IdP-agnostic)
- No impact on production deployments

## Non-Goals

- Production Keycloak deployment (customer's responsibility)
- Persistent Keycloak data (H2 ephemeral database is fine — realm re-imports on restart)
- Keycloak customisation UI/themes
- Keycloak as a connected system / connector target

---

## Technical Design

### Keycloak Service

Add Keycloak to `docker-compose.override.yml` (dev-only, not in the production `docker-compose.yml`).

```yaml
jim.keycloak:
  image: quay.io/keycloak/keycloak:26.0
  container_name: JIM.Keycloak
  restart: unless-stopped
  command: start-dev --import-realm --health-enabled=true
  environment:
    KEYCLOAK_ADMIN: admin
    KEYCLOAK_ADMIN_PASSWORD: admin
  ports:
    - "8080:8080"
  volumes:
    - ./.devcontainer/keycloak/jim-realm.json:/opt/keycloak/data/import/jim-realm.json:ro
  healthcheck:
    test: ["CMD-SHELL", "exec 3<>/dev/tcp/127.0.0.1/8080; echo -e 'GET /health/ready HTTP/1.1\\r\\nhost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3; timeout 1 cat <&3 | grep -q '200 OK'"]
    interval: 10s
    timeout: 5s
    retries: 15
    start_period: 30s
  networks:
    - jim-network
```

Key decisions:

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Compose file | `docker-compose.override.yml` | Dev-only; production compose is unaffected |
| Mode | `start-dev` | No TLS or hostname config required |
| Database | H2 (default) | Ephemeral is fine — realm re-imports on restart |
| Realm import | `--import-realm` flag | Scans `/opt/keycloak/data/import/` on startup; skips if realm exists |
| Health check | `/health/ready` via bash TCP | Keycloak image has no `curl`; bash `/dev/tcp` works natively |
| Image pinning | Version tag (e.g. `26.0`) | Consistent with JIM's dependency pinning policy |

### Realm Export

File: `.devcontainer/keycloak/jim-realm.json`

Pre-configured `jim` realm containing:

**Clients:**

| Client ID | Type | Purpose |
|-----------|------|---------|
| `jim-web` | Confidential + PKCE (S256) | JIM web application (auth code flow) |
| `jim-powershell` | Public + PKCE (S256) | PowerShell module (loopback redirect) |

**`jim-web` client configuration:**
- `publicClient: false` (confidential)
- `secret: jim-dev-secret` (known, dev-only)
- `standardFlowEnabled: true`
- `pkce.code.challenge.method: S256`
- Redirect URIs: `http://localhost:5200/*`, `https://localhost:7000/*`
- Web origins: `http://localhost:5200`, `https://localhost:7000`

**`jim-powershell` client configuration:**
- `publicClient: true`
- `standardFlowEnabled: true`
- `pkce.code.challenge.method: S256`
- Redirect URIs: `http://localhost:8400/*` through `http://localhost:8409/*`
- Web origins: `+`

**Client Scopes:**
- `jim-api` scope with audience mapper (`jim-web` audience, added to access token)

**Test Users:**

| Username | Password | Email | Role |
|----------|----------|-------|------|
| `admin` | `admin` | `admin@jim.local` | Initial admin (matches `JIM_SSO_INITIAL_ADMIN`) |
| `user` | `user` | `user@jim.local` | Regular user |

Both users: `enabled: true`, `emailVerified: true`, `temporary: false` (no forced password change).

### Environment Configuration

**`.env.example` updates:**

Add a development defaults block in the SSO section pointing to the bundled Keycloak:

```bash
# Development defaults (bundled Keycloak — override for external IdP)
JIM_SSO_AUTHORITY=http://localhost:8080/realms/jim
JIM_SSO_CLIENT_ID=jim-web
JIM_SSO_SECRET=jim-dev-secret
JIM_SSO_API_SCOPE=jim-api
JIM_SSO_CLAIM_TYPE=sub
JIM_SSO_MV_ATTRIBUTE=Subject Identifier
JIM_SSO_INITIAL_ADMIN=<admin user's sub claim>
```

**`setup.sh` updates:**

When creating `.env` from `.env.example` (and no Codespaces secrets override the SSO values), the file already contains working Keycloak defaults. No additional logic needed — the `.env.example` values just need to work out of the box.

The `JIM_SSO_INITIAL_ADMIN` value requires the `sub` claim of the `admin` user. This is a Keycloak-generated UUID that must match between the realm export and `.env.example`. Two approaches:

1. **Fixed user ID in realm export** — set `"id": "00000000-0000-0000-0000-000000000001"` on the admin user in the realm JSON. Keycloak respects the `id` field on import.
2. **Use a predictable claim** — use `preferred_username` as the claim type instead of `sub`, with value `admin`.

Option 1 is cleaner (keeps `sub` as the claim type, consistent with production guidance).

### Devcontainer Updates

**`devcontainer.json`:**
- Add port `8080` to `forwardPorts` (Keycloak admin console)

### Issuer / Dual-Network Resolution ✅

The `jim.web` container reaches Keycloak via Docker DNS (`jim.keycloak:8080`) for back-channel OIDC discovery, but the browser redirects go to `localhost:8080`. Solved with:

1. **`KC_HOSTNAME_URL=http://localhost:8080`** on the Keycloak container — tokens always claim `http://localhost:8080/realms/jim` as issuer, regardless of how the back-channel reaches Keycloak.
2. **`JIM_SSO_AUTHORITY=http://jim.keycloak:8080/realms/jim`** overridden in `docker-compose.override.yml` for the `jim.web` service — ensures Docker DNS resolution for back-channel.
3. **`JIM_SSO_VALID_ISSUERS=http://localhost:8080/realms/jim`** in `.env.example` — JIM validates the `localhost` issuer claim in tokens.

**For local debugging (Workflow 1):**

When running JIM via F5, it reads `.env` directly where `JIM_SSO_AUTHORITY=http://localhost:8080/realms/jim` — no Docker DNS involved. Start Keycloak standalone with `jim-keycloak`.

---

## Implementation Plan

### Phase 1: Realm Export and Keycloak Service ✅

1. Created `.devcontainer/keycloak/jim-realm.json` — `jim` realm with `jim-web` (confidential + PKCE), `jim-powershell` (public + PKCE), `jim-api` scope with audience mapper, and two test users with fixed UUIDs
2. Added `jim.keycloak` service to `docker-compose.override.yml` — Keycloak 26.0, `start-dev` mode, `--import-realm`, health check, `KC_HOSTNAME_URL` for issuer resolution
3. Added port `8080` to `devcontainer.json` `forwardPorts`
4. Resolved issuer/hostname dual-network issue via `KC_HOSTNAME_URL` + `JIM_SSO_VALID_ISSUERS` (see above)
5. Added `JIM_SSO_AUTHORITY` override in `docker-compose.override.yml` for `jim.web` to use Docker DNS
6. Added `jim.web` `depends_on` Keycloak health check

### Phase 2: Environment Integration ✅

1. Updated `.env.example` with working Keycloak defaults (authority, client, secret, scope, initial admin UUID)
2. Updated `setup.sh` — replaced SSO warning with confirmation message, updated startup instructions
3. Test: fresh devcontainer creation → `jim-stack` → sign in with `admin` / `admin`

### Phase 3: Local Debugging Support ✅

1. Added `jim-keycloak`, `jim-keycloak-stop`, `jim-keycloak-logs` aliases to `jim-aliases.sh`
2. `.env` defaults point to `localhost:8080` — works for both Docker stack and F5 workflows
3. Test: `jim-db && jim-keycloak` → F5 → sign in with `admin` / `admin`

### Phase 4: Documentation

1. Update `docs/SSO_SETUP_GUIDE.md` with a "Development SSO (Bundled Keycloak)" section
2. Update `docs/DEVELOPER_GUIDE.md` workflow sections to mention SSO just works
3. Update `README.md` if the getting-started section references SSO setup

---

## Success Criteria

1. Fresh devcontainer → `jim-stack` → sign in with `admin`/`admin` — zero IdP configuration
2. `Connect-JIM` PowerShell module works against bundled Keycloak
3. Overriding `.env` SSO variables switches to an external IdP (bundled Keycloak ignored)
4. Production `docker-compose.yml` is unaffected (no Keycloak service)
5. Keycloak admin console accessible at `http://localhost:8080` for debugging
6. Both development workflows work (Docker stack and F5 local debugging)

---

## Dependencies

- **Keycloak Docker image**: `quay.io/keycloak/keycloak:26.0` (or latest stable at implementation time)
- No new NuGet packages required
- No changes to JIM's OIDC implementation

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Issuer mismatch (Docker DNS vs localhost) | Token validation fails | Investigate `KC_HOSTNAME` setting; use `JIM_SSO_VALID_ISSUERS` as fallback |
| Keycloak startup time (30-60s) | `jim.web` starts before Keycloak is ready | Health check + `depends_on` with `condition: service_healthy` |
| Image size (~400MB) | Slower first pull | One-time cost; cached after first pull |
| Keycloak version drift | Breaking changes in realm format | Pin to specific version tag; update as part of Dependabot cycle |
