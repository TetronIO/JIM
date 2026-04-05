# Devcontainer Identity Provider (Keycloak)

- **Status:** Done
- **Issue:** [#197](https://github.com/TetronIO/JIM/issues/197)
- **Last Updated:** 2026-03-30
- **Milestone:** v0.9-STABILISATION

## Overview

Integrate a pre-configured Keycloak instance into the devcontainer Docker stack so that developers can sign in to JIM immediately without configuring an external Identity Provider. The Keycloak instance ships with a realm export containing clients, scopes, and test users; zero manual setup required.

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
- Persistent Keycloak data (H2 ephemeral database is fine; realm re-imports on restart)
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
| Database | H2 (default) | Ephemeral is fine; realm re-imports on restart |
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

**`.env.example` defaults** point to the bundled Keycloak; works out of the box:

```bash
JIM_SSO_AUTHORITY=http://localhost:8181/realms/jim
JIM_SSO_CLIENT_ID=jim-web
JIM_SSO_SECRET=jim-dev-secret
JIM_SSO_API_SCOPE=jim-api
JIM_SSO_CLAIM_TYPE=sub
JIM_SSO_MV_ATTRIBUTE=Subject Identifier
JIM_SSO_INITIAL_ADMIN=00000000-0000-0000-0000-000000000001
JIM_SSO_VALID_ISSUERS=http://localhost:8181/realms/jim,http://jim.keycloak:8080/realms/jim
```

The admin user has a fixed UUID (`00000000-0000-0000-0000-000000000001`) set in the realm export, so the `sub` claim is predictable.

### Port Architecture ✅

Docker-in-Docker proxy ports are not auto-forwarded by VS Code Dev Containers unless present at devcontainer build time. The solution uses a three-hop path:

```
Browser (Mac)          VS Code port forward       socat bridge         Docker port mapping     Keycloak container
localhost:8181    -->  devcontainer:8181     -->  devcontainer:8180  -->  jim.keycloak:8080
```

- **Port 8181**: socat userspace listener (VS Code detects and forwards to host)
- **Port 8180**: Docker host port mapping (`8180:8080` in compose)
- **Port 8080**: Keycloak's internal container port

The `socat` bridge is started automatically by `jim-stack`, `jim-build`, `jim-restart`, and `jim-keycloak` aliases. `socat` is installed by `setup.sh`.

### Issuer / Dual-Network Resolution ✅

The `jim.web` container reaches Keycloak via Docker DNS (`jim.keycloak:8080`) for back-channel OIDC discovery, but browser redirects must go to `localhost:8181`. Solved with:

1. **`JIM_SSO_AUTHORITY=http://jim.keycloak:8080/realms/jim`** overridden in `docker-compose.override.yml`: back-channel uses Docker DNS.
2. **`OnRedirectToIdentityProvider` rewrite** in `Program.cs`: rewrites browser redirects from `jim.keycloak:8080` to the `localhost` issuer found in `JIM_SSO_VALID_ISSUERS`.
3. **`JIM_SSO_VALID_ISSUERS`** accepts both `http://localhost:8181/realms/jim` and `http://jim.keycloak:8080/realms/jim`: tokens may be issued by either hostname.
4. **`ValidIssuers`** set on both OIDC and JWT Bearer `TokenValidationParameters`.

**For local debugging (Workflow 1):**

When running JIM via F5, it reads `.env` directly where `JIM_SSO_AUTHORITY=http://localhost:8181/realms/jim`: no Docker DNS involved. Start Keycloak standalone with `jim-keycloak`.

---

## Implementation Plan

### Phase 1: Realm Export and Keycloak Service ✅

1. Created `.devcontainer/keycloak/jim-realm.json`: `jim` realm with `jim-web` (confidential + PKCE), `jim-powershell` (public + PKCE), `jim-api` scope with audience mapper, built-in OIDC client scopes, and two test users with fixed UUIDs
2. Added `jim.keycloak` service to `docker-compose.override.yml`: Keycloak 26.0, `start-dev` mode, `--import-realm`, health check on port 9000
3. Added port `8181` to `devcontainer.json` `forwardPorts`
4. Added socat bridge (`8181->8180`) to work around VS Code Dev Containers not forwarding Docker-in-Docker proxy ports
5. Resolved issuer/hostname dual-network issue via `OnRedirectToIdentityProvider` rewrite + dual `ValidIssuers`
6. Added `JIM_SSO_AUTHORITY` override in `docker-compose.override.yml` for `jim.web` to use Docker DNS
7. Added `jim.web` `depends_on` Keycloak health check
8. Set `RequireHttpsMetadata=false` conditionally for HTTP authorities

### Phase 2: Environment Integration ✅

1. Updated `.env.example` with working Keycloak defaults (authority, client, secret, scope, initial admin UUID, dual valid issuers)
2. Updated `setup.sh`: replaced SSO warning with confirmation message, updated startup instructions, added socat installation
3. Test: fresh devcontainer creation → `jim-stack` → sign in with `admin` / `admin`

### Phase 3: Local Debugging Support ✅

1. Added `jim-keycloak`, `jim-keycloak-stop`, `jim-keycloak-logs` aliases to `jim-aliases.sh`
2. `.env` defaults point to `localhost:8181`: works for both Docker stack and F5 workflows
3. socat bridge auto-starts from `jim-stack`, `jim-build`, `jim-restart`, `jim-keycloak`
4. Test: `jim-db && jim-keycloak` → F5 → sign in with `admin` / `admin`

### Phase 4: Documentation ✅

1. Updated `docs/SSO_SETUP_GUIDE.md` with "Development (Bundled Keycloak)" section at the top
2. Updated `docs/DEVELOPER_GUIDE.md`: removed SSO prerequisite, added "works out of the box" notes
3. Updated `README.md`: developer prerequisites no longer require external SSO setup
4. Added changelog entry under [Unreleased]

---

## Success Criteria

1. Fresh devcontainer → `jim-stack` → sign in with `admin`/`admin`: zero IdP configuration
2. `Connect-JIM` PowerShell module works against bundled Keycloak
3. Overriding `.env` SSO variables switches to an external IdP (bundled Keycloak ignored)
4. Production `docker-compose.yml` is unaffected (no Keycloak service)
5. Keycloak admin console accessible at `http://localhost:8181` for debugging
6. Both development workflows work (Docker stack and F5 local debugging)

---

## Dependencies

- **Keycloak Docker image**: `quay.io/keycloak/keycloak:26.0` (or latest stable at implementation time)
- No new NuGet packages required
- `socat` package (installed by `setup.sh`)
- Minor OIDC changes in `Program.cs` (HTTP authority support, browser redirect rewrite, dual issuer validation)

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Issuer mismatch (Docker DNS vs localhost) | Token validation fails | `OnRedirectToIdentityProvider` rewrite + dual `ValidIssuers` on both OIDC and JWT Bearer |
| Keycloak startup time (30-60s) | `jim.web` starts before Keycloak is ready | Health check + `depends_on` with `condition: service_healthy` |
| Image size (~400MB) | Slower first pull | One-time cost; cached after first pull |
| Keycloak version drift | Breaking changes in realm format | Pin to specific version tag; update as part of Dependabot cycle |
