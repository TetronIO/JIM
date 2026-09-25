# Podman Support - Implementation Plan

- **Status:** Planned
- **Created:** 2026-09-25
- **Issue:** [#1808](https://github.com/TetronIO/JIM/issues/1808)
- **PRD:** [PRD_PODMAN_SUPPORT.md](../prd/PRD_PODMAN_SUPPORT.md) (this plan answers the PRD's open questions in [Decisions](#decisions) and withdraws requirement 12, per D8)

## Overview

JIM gains a second deployment path, alongside Docker, for hosts that run Podman. The images do not change. The path is a small set of Kubernetes-style files run by Podman's built-in `podman kube play`, with Quadlet units so systemd starts JIM at boot. The Docker path keeps working as it does today.

The work is in three phases:

1. **Runtime-neutral start-up and cleanup.** Every JIM service waits for the database instead of crashing, the PostgreSQL image name is fully qualified, and the Worker drops two capabilities it never uses. This helps Docker as much as Podman, and the Podman path depends on it.
2. **The Podman path.** Pod files, Quadlet units, secrets, HTTPS, the installer, the release bundle and the documentation.
3. **Proof.** A CI job that boots both paths from freshly built images on every pull request, and compares what each runtime actually runs.

## Business Value

Red Hat ships and supports Podman, not Docker, on RHEL 8, 9 and 10. Regulated organisations standardised on RHEL often will not install Docker on a server, and they are a core part of JIM's market. Today JIM cannot be installed in their estates at all.

Adding the runtime now, before JIM has an installed base, means no deployment has to be migrated and no compatibility promise is at stake.

## Technical Architecture

### Current state

- **One install path.** `deploy/setup.sh`, the manual compose steps and the air-gapped bundle all end in `docker compose -f docker-compose.yml -f docker-compose.production.yml up -d`.
- **The Docker daemon supervises.** It orders start-up through `depends_on: condition: service_healthy`, runs the health checks, and restarts containers after a reboot (`restart: unless-stopped`).
- **The services lean on that ordering:**
  - The Worker calls `InitialiseDatabaseAsync()` once (`src/JIM.Worker/Worker.cs:118`) and exits if PostgreSQL is not yet accepting connections.
  - JIM.Web's readiness loop (`CompleteJimApplicationBootstrapAsync` in `src/JIM.Web/Program.cs`) calls `IsApplicationReadyAsync()` with no error handling, so an unreachable database crashes the host.
  - The Scheduler already catches the failure and keeps waiting.

### Target: two runtimes, one set of images

```
                      ┌──────────────────── release (CI) ───────────────────┐
                      │ images (unchanged)   compose files   deploy/podman/  │
                      └─────────┬──────────────────┬──────────────┬─────────┘
                                │                  │              │
               setup.sh / INSTALL.md picks the path by runtime    │
                                │                  │              │
        Docker host ────────────┘                  │              │
          docker compose ... up -d (as today)      │              │
                                                   │              │
        Podman host ───────────────────────────────┘──────────────┘
          systemd ─► jim-database.service ─► pod "jim-database" (PostgreSQL, optional)
                  └► jim.service ──────────► pod "jim" (web, worker, scheduler)
          both pods on network "jim"; JIM reaches PostgreSQL at host name "jim-database"
          settings: ConfigMap "jim-config"; secrets: Podman secrets "jim-secrets", "jim-tls"
```

**Files in `deploy/podman/`:**

| File | Kind | Purpose |
|------|------|---------|
| `jim.yaml` | Pod | `jim.web`, `jim.worker`, `jim.scheduler`. Image names and tags are placeholders rendered at release |
| `jim-database.yaml` | Pod | Optional bundled PostgreSQL, with the same tuning as `docker-compose.yml` |
| `jim-config.yaml` | ConfigMap template | The same non-secret keys as `.env.example` |
| `jim-secrets.yaml` | Secret template | `JIM_DB_PASSWORD`, `JIM_SSO_SECRET`, optional `JIM_INFRASTRUCTURE_API_KEY`. Played once into Podman's secret store, then deleted |
| `jim-tls.yaml` | Secret template | `tls.crt` and `tls.key`, mounted into `jim.web` as a secret volume |
| `quadlet/jim.network` | Quadlet network | The `jim` network both pods join |
| `quadlet/jim-database.kube` | Quadlet kube unit | Runs `jim-database.yaml`; installed only when the bundled database is used |
| `quadlet/jim.kube` | Quadlet kube unit | Runs `jim.yaml` with `ConfigMap=jim-config.yaml` |

**Hardening parity.** Every compose setting has a pod equivalent:

| `docker-compose.yml` | Pod file |
|----------------------|----------|
| `read_only: true` | `securityContext.readOnlyRootFilesystem: true` |
| `cap_drop: [ALL]` | `securityContext.capabilities.drop: [ALL]` |
| `security_opt: no-new-privileges` | `securityContext.allowPrivilegeEscalation: false` |
| `tmpfs: /tmp` | One `emptyDir: {medium: Memory}` per container at `/tmp`, never shared (see D10) |
| image `USER app` (1654) | Unchanged; the image sets it |
| named volumes | `persistentVolumeClaim`s of the same names, so ownership is copied from the image on first use |
| `healthcheck:` | `livenessProbe.exec` with the same command (see D8) |
| `restart:` | `restartPolicy: Always` in the pod; restart at boot comes from the Quadlet unit |

**Behaviour already verified on Podman 4.9.3** (2026-09-24 and 2026-09-25, cloud sandbox, rootful):

- `podman kube play` ran web, worker, scheduler and PostgreSQL from a pod file with no compose tool and no systemd, with no restarts and no error log lines.
- Browser sign-in succeeded over HTTPS by server name, with Kestrel serving a PEM certificate.
- A `kind: Secret` document played with `podman kube play` becomes a Podman secret that `env.valueFrom.secretKeyRef` resolves.
- `envFrom.configMapRef` with `--configmap` delivers values containing spaces intact, without quote marks.
- Pods on a shared network resolve each other by pod name (`jim-database`) and by container name.
- A `livenessProbe.exec` command reaches Podman's health check unaltered, `$` and `%` included.
- A `secret` volume mounts `tls.crt` and `tls.key` readable by UID 1654 under a read-only root filesystem and all capabilities dropped.
- Named volumes take UID 1654 ownership and are writable.
- The `io.podman.annotations.shm-size/<container>` annotation is **not** honoured: PostgreSQL's `/dev/shm` stays at 64 MB (see D9).

## Decisions

These answer the PRD's open questions (OQ). Each was chosen on the evidence above; alternatives are recorded so they need not be re-argued.

**D1. Secrets (OQ1): Kubernetes `Secret` documents, played once into Podman's secret store.**
- **How it works:**
  - `setup.sh`, or the administrator following the guide, fills in `jim-secrets.yaml`, runs `podman kube play jim-secrets.yaml`, then deletes the file.
  - The pod file refers to values by name through `secretKeyRef`, so no secret value is ever in the pod file.
  - The same mechanism carries the TLS key pair (D4).
- **Storage:** Podman keeps secrets in its store, readable only by root, or by the owning user when rootless. That is the same exposure class as `.env` today, and it is documented as such.
- **Rejected:**
  - Values inline in the pod file: that fails requirement 8.
  - `podman secret create` with raw values: `secretKeyRef` needs a key, which a raw secret does not have.
  - A generated environment file: Quadlet cannot pass one to `kube play`.

**D2. Configuration (OQ2): a ConfigMap with exactly the `.env` key names.**
- `jim-config.yaml` carries the same non-secret keys as `.env.example`, so `docs/administration/configuration.md` still documents one set of settings. It says the Docker path reads them from `.env` and the Podman path from `jim-config.yaml`.
- `setup.sh` writes the ConfigMap from the same questions it already asks.
- A ConfigMap carries values literally, so the quoting pitfall found in testing cannot occur.
- **Rejected:** converting `.env` at start-up. That needs tooling on the host and reintroduces the quoting pitfall.

**D3. Optional database (OQ3): its own pod, joined to JIM by the `jim` network.**
- JIM always sets `JIM_DB_HOSTNAME`: `jim-database` for the bundled database, the external server's name otherwise. The JIM pod file is identical either way, and the bundled database is simply a second, optional pod and unit, much as the `with-db` profile is on Docker.
- The PRD expected the database to stay in the JIM pod to keep `localhost` networking. Testing showed pod-name resolution works, which makes a separate pod simpler.
- **Rejected:**
  - Two JIM pod files, with and without the database: they would drift.
  - A database container inside the JIM pod: it could not be made optional without editing the file.

**D4. HTTPS (OQ4): JIM serves HTTPS itself by default on Podman, and a reverse proxy remains supported.**
- **Default:**
  - Kestrel listens on `https://+:8443` using the `jim-tls` secret, which is published on the host port (default 5200).
  - It also listens on `http://+:8080` inside the pod for health probes only; that port is not published.
  - `setup.sh` accepts the administrator's certificate and key. If none is given, it generates a self-signed pair with `openssl` and warns that browsers will not trust it.
- **Behind a reverse proxy:** the administrator publishes 8080 on loopback instead and sets `JIM_TRUSTED_PROXIES` (added in #1816).
- **Docker parity:** the same Kestrel certificate settings are documented for Docker as an alternative to a proxy, with no code difference between runtimes.
- **Code change:** health endpoints must be exempt from HTTPS redirection (Phase 2.2). Otherwise a probe on port 8080 is answered with a redirect, which `curl -f` counts as success, so a failing service would report healthy.

**D5. Rootless (OQ5): in scope at launch, proven in CI, and confirmed by hand on RHEL.**
- CI boots the Podman path rootful and rootless on GitHub's Ubuntu runner.
- Acceptance also needs one manual run on a RHEL 9 or 10 virtual machine with SELinux enforcing, rootful and rootless, because GitHub's runner is neither RHEL nor SELinux-enforcing.

**D6. Required check (OQ6): not required at first, required once it has proven reliable.**
- The new `deployment-boot` check starts as informational. Once it has passed on ten consecutive runs, it is added to the `main` ruleset's required checks.
- Changing the ruleset is an administrator action.

**D7. Drift (OQ7): compare what the two runtimes actually run.**
- After booting both paths, the CI job inspects each running JIM container on both runtimes (`docker inspect`, `podman inspect`). It compares, per service:
  - environment variable names;
  - mount destinations;
  - read-only root filesystem;
  - dropped capabilities;
  - no-new-privileges;
  - user.
- Intended differences are held in an allowlist in the script: the value of `JIM_DB_HOSTNAME`, the port bindings, the Podman-only TLS settings.
- This tests what runs rather than parsing two YAML dialects, and needs no new dependency.
- **Rejected:** generating the pod files from the compose files. The generator would be a third thing to maintain, and administrators read these files directly, so hand-written files serve them better.

**D8. PRD requirement 12 withdrawn: health checks stay as commands in each definition.**
- The requirement assumed Quadlet `.container` units, where systemd treats `%` and `$` specially. In the pod design the probe is a YAML array that systemd never parses; tested, it reaches Podman unaltered.
- Moving the checks into scripts would add shell scripts to the images against the repository's scripting rule, for no remaining benefit.
- CI runs `podman healthcheck run` on every container, so a broken probe fails the check.

**D9. PostgreSQL shared memory: `dynamic_shared_memory_type=mmap` in the database pod.**
- Podman ignores the shm-size annotation, and the pod specification has no per-container shared-memory size.
- PostgreSQL uses `/dev/shm` only for dynamic shared memory (parallel query workers); `shared_buffers` does not use it.
- `mmap` moves dynamic shared memory into the data directory, removing the 64 MB ceiling.
- The Docker path keeps its `shm_size`. The difference is on the D7 allowlist.

**D10. Each container gets its own in-memory `/tmp`.** Sharing one broke start-up in testing, and it would also mix up the Worker's and Scheduler's `/tmp/healthcheck` heartbeat files.

**D11. Image references are rendered, never duplicated.**
- The pod files carry placeholders for the registry, the JIM version and the PostgreSQL image.
- `scripts/Build-ReleaseBundle.ps1`, the release workflow and the CI job fill them in. The PostgreSQL value is read from `docker-compose.yml`, which stays the single source of truth that Dependabot maintains.
- Otherwise Dependabot's `docker` ecosystem, which scans Kubernetes YAML, would start maintaining a second PostgreSQL digest.

## Implementation Phases

### Phase 1: Runtime-neutral start-up and cleanup

The first commit moves the PRD and this plan to `doing/` with `Status: Doing`.

1. **Wait for the database** (TDD, test-first):
   - Add `JimApplication.WaitForDatabaseAsync(TimeSpan budget, CancellationToken)`, backed by a new repository method `CanConnectAsync()`.
   - It retries with an increasing delay (1, 2, 4 and 8 seconds, then every 15 seconds), logging one Information line per attempt with the attempt number and elapsed time.
   - When the budget runs out (five minutes by default) it fails with a single clear Fatal line, and the supervisor restarts the service.
   - The delay is injected so tests control time without a new dependency.
   - Unit tests cover:
     - connecting at once;
     - connecting after several failures, checking the log lines and the delay sequence;
     - the budget running out;
     - cancellation.
   - Only the connection failures it can recover from are caught (`NpgsqlException`, `SocketException`, `TimeoutException`), never a general exception.
2. **Call it at every start-up:**
   - The Worker, before `InitialiseDatabaseAsync()`.
   - JIM.Web, before its readiness loop.
   - The Scheduler, before its readiness loop, replacing today's `catch (Exception)` path for the unreachable-database case.
3. **Fully qualified PostgreSQL image:**
   - `docker.io/library/postgres:18.6@sha256:…` in `docker-compose.yml`.
   - Update the regex in `scripts/Build-ReleaseBundle.ps1:65` to match.
   - Confirm Dependabot's `docker-compose` ecosystem still tracks the digest.
4. **Worker capabilities:**
   - Remove `SYS_ADMIN` and `DAC_READ_SEARCH` from `jim.worker` in `docker-compose.yml`; no code in `src/` mounts anything.
   - Prove it with the integration scenarios that use the File Connector's `/connector-files` volume.
5. **Checks before merging:**
   - `dotnet build JIM.sln` and `dotnet test JIM.sln`.
   - Boot the Docker stack with PostgreSQL started last: services wait, then start with no restarts.
   - Integration Scenario 1.
6. **Changelog and docs:**
   - Changelog: 🔄 "JIM's services now wait for the database at start-up instead of restarting until it is available."
   - Docs: the troubleshooting page gains the new log lines.

### Phase 2: The Podman path

Delivered as one PR, or two if review size demands: files and HTTPS, then installer, release and docs.

1. **Pod and Quadlet files** in `deploy/podman/`, as tabled above, each with a header comment saying what it is for and what an administrator may edit. They are hand-written YAML using only fields that Podman 4.4 supports.
2. **HTTPS:**
   - Kestrel certificate settings (`ASPNETCORE_Kestrel__Certificates__Default__Path` and `KeyPath`) point at the `jim-tls` secret volume.
   - Health endpoints (`/api/v1/health/*`) become exempt from HTTPS redirection in `src/JIM.Web/Program.cs`, test-first against the redirect behaviour.
   - Both runtimes' probes are checked to report unhealthy when readiness fails.
3. **Database pod:**
   - The same tuning arguments as the production compose file, plus `dynamic_shared_memory_type=mmap` and `LANG=C.UTF-8`.
   - The same log-directory entrypoint wrapper.
   - The `jim-db` and `jim-logs` claims.
4. **`deploy/setup.sh`:**
   - Detect Docker and Podman, and add `--runtime docker|podman`. When both are present it asks rather than guessing.
   - The Podman branch:
     1. Checks the Podman version, systemd and, for rootless installs, lingering.
     2. Asks the existing questions and writes `jim-config.yaml`.
     3. Plays the secrets and TLS documents, then removes the files.
     4. Installs the units into `/etc/containers/systemd/`, or `~/.config/containers/systemd/` when rootless, with the configuration under `/etc/jim/` or `~/.config/jim/`.
     5. Runs `systemctl daemon-reload` and starts `jim.service`.
     6. Waits for `/api/v1/health/ready` and prints the address.
   - `setup.sh` stays bash: it is the existing customer-facing installer, and PowerShell cannot be assumed on a customer server.
5. **Release:**
   - `.github/workflows/release.yml` publishes the rendered Podman files as release assets alongside the compose files.
   - `scripts/Build-ReleaseBundle.ps1` adds a `podman/` folder to the bundle, and the generated `INSTALL.md` gains Podman steps using the same image tarballs.
6. **Documentation**, per the PRD's Documentation Impact table:
   - Docker and Podman content tabs (`pymdownx.tabbed` is already enabled) in deployment, upgrading, backup and troubleshooting.
   - The Podman secrets and TLS model in configuration.
   - Rootless and SELinux guidance for File Connector bind mounts, including a Podman volume backed by a CIFS share as the rootful alternative to a host bind mount.
   - The two-runtime architecture in `engineering/DEVELOPER_GUIDE.md`.
7. **Checks before merging:**
   - A rootful boot in the cloud sandbox, and a rootless boot on a host that supports it.
   - Remote HTTPS sign-in by server name.
   - A restart of the unit with data kept.
   - Upgrade by replacing the pod file and restarting.
8. **Changelog:** ✨ "JIM can now be deployed with Podman, rootful or rootless, with no Docker or other extra software, including air-gapped."

### Phase 3: Proof in CI

1. **New `deployment-boot` job** in `.github/workflows/ci.yml`:
   - Runs on GitHub's Ubuntu 24.04 runners for every event; the self-hosted runners are not assumed to have Podman.
   - Builds the three images once and loads them into both runtimes.
   - Uses a test-only Keycloak with the development realm (`test/ci/deployment/`) as the identity provider.
2. **Docker leg:** boots the production compose files and waits for readiness.
3. **Podman legs, rootful and rootless:**
   - Render the pod files with the CI tag, play the secrets, install the Quadlet units, start `jim.service`, and wait for readiness over HTTPS.
   - Run `podman healthcheck run` on each container.
   - Stop and start the unit, then confirm a marker written before the restart is still there.
4. **Parity:** `test/ci/deployment/Compare-RuntimeParity.ps1` (PowerShell, with Pester tests for its comparison logic) runs the D7 comparison and fails with a named difference.
5. **Required check:** made required per D6.
6. **Manual acceptance:** the RHEL virtual machine run from D5, recorded in the PR that closes #1808.

## Success Criteria

- Every acceptance criterion in the PRD is met.
- `deployment-boot` passes for Docker, Podman rootful and Podman rootless on every pull request, and has become a required check.
- A fresh RHEL 9 or 10 host with SELinux enforcing installs JIM with `setup.sh --runtime podman`, rootful and rootless, and signs in over HTTPS from another machine.
- An air-gapped install needs only the release bundle.

## Benefits

- **Market:** JIM becomes installable in RHEL-standardised, Docker-free estates.
- **Resilience on both runtimes:** services no longer crash-loop while the database starts, and an external database outage no longer takes JIM.Web down.
- **Security:**
  - The Worker loses two unused capabilities.
  - Rootless deployment becomes possible.
  - Health probes can no longer pass on a redirect when HTTPS is configured.
- **Confidence:** for the first time a CI check boots the production deployment of either runtime, so deployment regressions surface before release rather than at a customer.

## Dependencies

- #1805 (merged): port 8080 and the bundled database locale.
- #1816 (merged): the HTTPS requirement documented, and `JIM_TRUSTED_PROXIES` for reverse proxies.
- GitHub-hosted Ubuntu 24.04 runners with Podman 4.9 and systemd.
- A RHEL 9 or 10 virtual machine with SELinux enforcing, for D5's manual acceptance. The maintainer provides it.
- No new NuGet packages or other third-party dependencies.

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Pod-file features differ between Podman 4.4 and 5.x | Use only fields verified above; CI covers 4.9 and the RHEL run covers 5.x |
| The two deployment definitions drift | D7 runtime parity check on every pull request |
| Probes pass on an HTTPS redirect | Health endpoints exempt from redirection, test-first (Phase 2.2) |
| PostgreSQL parallel queries starved of shared memory | D9 `mmap`; an integration scenario against the Podman database pod before release |
| Secrets at rest in Podman's secret store | Same exposure class as `.env`, and documented. Podman's alternative secret drivers are noted as a hardening option |
| SELinux blocks a File Connector bind mount | Documented relabelling guidance, plus the CIFS-backed Podman volume alternative; covered in the RHEL run |
| `setup.sh` grows complex | The Podman branch in its own functions, reusing the existing prompts; each path exercised by the CI boot legs |
| CI time grows | The new job runs in parallel with the existing ones; images built once per job |

**Out of scope, noted for later:**
- Removing `cifs-utils` from the Worker image, which also appears unused.
- Podman auto-update.
- Kubernetes or OpenShift deployment.
