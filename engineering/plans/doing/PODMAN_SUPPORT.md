# Podman Support - Implementation Plan

- **Status:** Doing (Phase 1 complete)
- **Created:** 2026-09-25
- **Issue:** [#1808](https://github.com/TetronIO/JIM/issues/1808)
- **PRD:** [PRD_PODMAN_SUPPORT.md](../../prd/doing/PRD_PODMAN_SUPPORT.md) (this plan answers the PRD's open questions in [Decisions](#decisions) and withdraws requirement 12, per D8)

## Overview

JIM gains a second deployment path, alongside Docker, for hosts that run Podman. The images do not change. The path is a small set of Kubernetes-style files run by Podman's built-in `podman kube play`, with Quadlet units so systemd starts JIM at boot. On Podman, JIM runs rootless by default under a dedicated `jim` account. On both runtimes, JIM serves HTTPS out of the box.

The work is in four phases:

1. **Runtime-neutral start-up and cleanup.** Every JIM service waits for the database instead of crashing, the PostgreSQL image name is fully qualified, and the Worker drops two capabilities it never uses. This helps Docker as much as Podman, and the Podman path depends on it.
2. **HTTPS by default, on both runtimes.** JIM serves HTTPS itself with the organisation's certificate or one the installer creates, so a fresh install can be signed into from any machine without first building a reverse proxy.
3. **The Podman path.** Pod files, Quadlet units, secrets, rootless installation under a `jim` account, the installer, the release bundle and the documentation, including deployment with Ansible.
4. **Proof.** A CI job that boots both paths from freshly built images on every pull request, and compares what each runtime actually runs.

## Business Value

Red Hat ships and supports Podman, not Docker, on RHEL 8, 9 and 10. Regulated organisations standardised on RHEL often will not install Docker on a server, and they are a core part of JIM's market. Today JIM cannot be installed in their estates at all.

Adding the runtime now, before JIM has an installed base, means no deployment has to be migrated and no compatibility promise is at stake.

## Deployment Scenarios

The decisions below follow how organisations in JIM's market deploy containerised vendor software on RHEL. The reference point is Red Hat's own multi-container product, Ansible Automation Platform, whose containerized installation is what a RHEL administrator will compare JIM's against.

| # | Scenario | What organisations do | What JIM provides |
|---|----------|-----------------------|-------------------|
| 1 | First install by an operations team | Run the vendor's installer on a RHEL server. Red Hat's product installs rootless under a non-root account, with lingering so it keeps running after logout and starts at boot | `setup.sh` does the same (D5) |
| 2 | Fleet deployment | Ansible, using Red Hat's podman system role, which deploys Kubernetes YAML through Quadlet, creates secrets, and runs rootless as a named user | The same pod files, and a documented example playbook (D12) |
| 3 | Air-gapped | Transfer a bundle, load the images, install offline | The same release bundle for both runtimes (Phase 3) |
| 4 | First HTTPS | Red Hat's installer generates its own certificate authority and server certificates, so the product serves HTTPS from its first start | JIM does the same, on both runtimes (D4) |
| 5 | Production HTTPS | Replace the generated certificate with one from the organisation's internal certificate authority (typically AD CS or IdM), and/or put an existing load balancer or reverse proxy in front. On RHEL that proxy is usually Apache httpd, which needs the `httpd_can_network_connect` SELinux boolean to proxy | Bring-your-own certificates, and documented proxy configurations (D4) |
| 6 | Security policy | Rootless required: increasingly the norm, and Red Hat's own default | Rootless is the Podman default (D5) |

References:
- [Ansible Automation Platform 2.6: containerized installation](https://docs.redhat.com/en/documentation/red_hat_ansible_automation_platform/2.6/html-single/containerized_installation/index) (rootless under a non-root user, installer-generated certificate authority, custom certificates)
- [RHEL 9: managing containers with the podman RHEL system role](https://docs.redhat.com/en/documentation/red_hat_enterprise_linux/9/html/automating_system_administration_by_using_rhel_system_roles/managing-containers-by-using-the-podman-rhel-system-role_automating-system-administration-by-using-rhel-system-roles)
- [Automating Podman with RHEL system roles](https://www.redhat.com/en/blog/automating-podman-rhel-system-roles)

## Technical Architecture

### Current state

- **One install path.** `deploy/setup.sh`, the manual compose steps and the air-gapped bundle all end in `docker compose -f docker-compose.yml -f docker-compose.production.yml up -d`.
- **The Docker daemon supervises.** It orders start-up through `depends_on: condition: service_healthy`, runs the health checks, and restarts containers after a reboot (`restart: unless-stopped`).
- **The services lean on that ordering:**
  - The Worker calls `InitialiseDatabaseAsync()` once (`src/JIM.Worker/Worker.cs:118`) and exits if PostgreSQL is not yet accepting connections.
  - JIM.Web's readiness loop (`CompleteJimApplicationBootstrapAsync` in `src/JIM.Web/Program.cs`) calls `IsApplicationReadyAsync()` with no error handling, so an unreachable database crashes the host.
  - The Scheduler already catches the failure and keeps waiting.
- **HTTP only.** Production publishes JIM.Web's HTTP port 8080. Sign-in from any machine other than the JIM host needs HTTPS (#1816), which today means the administrator building a reverse proxy first.

### Target: two runtimes, one set of images

```
                      ┌──────────────────── release (CI) ───────────────────┐
                      │ images (unchanged)   compose files   deploy/podman/  │
                      └─────────┬──────────────────┬──────────────┬─────────┘
                                │                  │              │
               setup.sh / INSTALL.md picks the path by runtime    │
                                │                  │              │
        Docker host ────────────┘                  │              │
          docker compose ... up -d                 │              │
                                                   │              │
        Podman host ───────────────────────────────┘──────────────┘
          systemd user manager of account "jim" (default), or the system manager (--rootful)
            ├► jim-database.service ─► pod "jim-database" (PostgreSQL, optional)
            └► jim.service ──────────► pod "jim" (web, worker, scheduler)
          both pods on network "jim"; JIM reaches PostgreSQL at host name "jim-database"
          settings: ConfigMap "jim-config"; secrets: Podman secrets "jim-secrets", "jim-tls"

        Both runtimes: HTTPS on host port 5200 (container 8443); HTTP 8080 on loopback inside
        the container, for health probes only
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

These answer the PRD's open questions (OQ). Each was chosen on the evidence above and the [Deployment Scenarios](#deployment-scenarios); alternatives are recorded so they need not be re-argued.

**D1. Secrets (OQ1): Kubernetes `Secret` documents, played once into Podman's secret store.**
- **How it works:**
  - `setup.sh`, or the administrator following the guide, fills in `jim-secrets.yaml`, runs `podman kube play jim-secrets.yaml`, then deletes the file.
  - The pod file refers to values by name through `secretKeyRef`, so no secret value is ever in the pod file.
  - The same mechanism carries the TLS key pair (D4).
- **Storage:** Podman keeps secrets in its store, readable only by root, or by the owning account when rootless. That is the same exposure class as `.env` today, and it is documented as such.
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

**D4. HTTPS (OQ4): JIM serves HTTPS itself by default, on both runtimes.**
- **Why:** it is how Red Hat's own containerized product installs (scenario 4), and #1816 established that sign-in from another machine needs HTTPS. A default that leaves remote sign-in broken until the administrator builds a proxy is the wrong default on either runtime.
- **Listening, in production:**
  - HTTPS on container port 8443, published on host port 5200 (`JIM_WEB_PORT` still changes it).
  - HTTP on `localhost:8080` inside the container, for the health probes only. It is never published, and binding it to loopback means no other container can reach it either.
  - Development is unchanged: HTTP on `localhost:5200` through the development override, which browsers treat as secure.
- **The certificate, chosen at install:**
  - The organisation's own certificate and key: recommended for production, typically issued by an internal certificate authority such as AD CS or IdM.
  - Otherwise the installer creates a local certificate authority and a server certificate for the host names and addresses the administrator gives it. It saves the certificate authority's certificate for the administrator to distribute to browsers and any proxy. A certificate authority is used rather than a bare self-signed certificate so that renewing the server certificate does not mean distributing trust again.
  - A generated server certificate is valid for one year, and `setup.sh --renew-certificate` issues a new one from the saved certificate authority. JIM already sends HSTS in production, so once browsers trust the certificate authority an expired certificate blocks access outright, with no click-through. The installer therefore prints the expiry date, and the documentation covers renewal.
- **Where the certificate lives:** both runtimes mount the pair at `/run/secrets/jim-tls/tls.crt` and `tls.key`, so the Kestrel settings (`ASPNETCORE_Kestrel__Certificates__Default__Path` and `KeyPath`) are identical and need no D7 allowlist entry.
  - Docker: compose `secrets:` from files in the install directory, with an absolute `target`. Compose mounts file-based secrets as bind mounts, so the key file must be readable by UID 1654; the installer sets its owner to 1654 and its mode to 0400. Verified in Phase 2.
  - Podman: the `jim-tls` secret (D1), mounted as a secret volume.
- **Behind a load balancer or reverse proxy:**
  - Recommended: the proxy connects to JIM's HTTPS port and trusts JIM's certificate, so traffic is encrypted end to end, which regulated environments commonly require.
  - Supported alternative: a proxy on the same host terminates TLS and forwards to JIM's HTTP port, published on loopback only. This is the configuration #1816 documented; it stays in the documentation as a manual option, not an installer choice.
  - Either way the installer asks one question, whether a proxy or load balancer sits in front and at what address, and sets `JIM_TRUSTED_PROXIES` from the answer.
  - The documentation gives Apache httpd (RHEL's default) and nginx examples, including RHEL's `httpd_can_network_connect` SELinux boolean.
- **Code change:** health endpoints must be exempt from HTTPS redirection (Phase 2). Otherwise a probe on port 8080 is answered with a redirect, which `curl -f` counts as success, so a failing service would report healthy.
- **Rejected:**
  - HTTPS on Podman only: Docker installs would stay unreachable from other machines without a proxy, and D7 would need a TLS difference on its allowlist for no reason.
  - A reverse proxy as the default: it is a second product for the administrator to install, configure and patch, and a small or air-gapped server often has none.
  - A bare self-signed server certificate: every renewal would mean distributing trust again.

**D5. Rootless (OQ5): the Podman default, under a dedicated `jim` account; rootful is an option.**
- **Why:** Red Hat's own containerized product installs rootless under a non-root account (scenario 1), and security teams in JIM's market increasingly require it (scenario 6). JIM's containers already run non-root with all capabilities dropped, so rootful Podman roughly matches today's Docker posture. Rootless adds protection on the host: a container escape lands in an unprivileged account rather than root.
- **Install, with `setup.sh` run as root:**
  1. Creates the `jim` account as a regular account with no login shell. A `--system` account is not used, because it gets no subordinate ID ranges. The installer adds `/etc/subuid` and `/etc/subgid` ranges if they are missing.
  2. Enables lingering for the account, so JIM runs without anyone logged in and starts at boot.
  3. Installs the Quadlet units into `~jim/.config/containers/systemd/` (supported since Podman 4.4) and the configuration into `~jim/.config/jim/`.
  4. Drives the account's user manager with `systemctl --user -M jim@`, which needs no password or login session for the account (systemd 248 or later, which every supported host has).
- **Without root:** an administrator can run `setup.sh` as their own account for a rootless install under that account, provided lingering is enabled for it. The installer checks, and says what to ask the host's administrators for if it is not.
- **Rootful:** `setup.sh --rootful` installs into `/etc/containers/systemd/` and `/etc/jim/` instead.
- **Consequences, all documented:**
  - Operating JIM uses `systemctl --user -M jim@ status jim.service` and `journalctl _SYSTEMD_USER_UNIT=jim.service`. The documentation gives an operations cheat sheet.
  - Volumes and secrets live under the `jim` account's home directory. The backup documentation says where, and exports volumes with `podman volume export` run as that account.
  - Host ports below 1024 need a proxy or the `net.ipv4.ip_unprivileged_port_start` setting. The default port 5200 is unaffected.
  - File Connector bind mounts see container UIDs mapped into the account's subordinate range, as PRD requirement 18 covers.
- **Testing:** CI boots the Podman path rootful and rootless on GitHub's Ubuntu runner, the rootless leg under a dedicated account as the installer creates it. Acceptance also needs one manual run on a RHEL 9 or 10 virtual machine with SELinux enforcing and firewalld running, rootless and rootful, covering the Ansible route (D12), because GitHub's runner is neither RHEL nor SELinux-enforcing.
- **Rejected:** rootful as the default. It gives up the protection the target customers ask for, in exchange for convenience the installer can provide instead.

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
- Intended differences are held in an allowlist in the script: the value of `JIM_DB_HOSTNAME`, the port bindings, and PostgreSQL's shared-memory setting (D9).
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

**D12. Ansible: a documented route using Red Hat's podman system role, with the same files.**
- Larger RHEL estates deploy with Ansible (scenario 2). Red Hat's podman system role (`redhat.rhel_system_roles.podman`, upstream `fedora.linux_system_roles.podman`) installs Kubernetes YAML as Quadlet units, runs rootless as a named user, and opens firewalld ports.
- JIM ships no role or collection of its own. A documentation page gives an example playbook that deploys the release's Podman files rootless as `jim`, equivalent to `setup.sh`. Secrets go through the same Secret document as D1, templated from Ansible Vault and played like any other kube specification.
- The playbook is verified by hand in the RHEL acceptance run (D5), not in CI: testing it in CI would add the role's collection as a dependency, which needs approval under the dependency policy. Revisit if the page drifts.
- **Rejected:** a JIM Ansible collection. It would be a second installer to maintain, for customers who already have Red Hat's role.

## Implementation Phases

### Phase 1: Runtime-neutral start-up and cleanup ✅

The first commit moves the PRD and this plan to `doing/` with `Status: Doing`.

1. **Wait for the database** (TDD, test-first):
   - Add `JimApplication.WaitForDatabaseAsync(TimeSpan budget, whileWaiting, CancellationToken)` (implemented in `DatabaseStartupWait`), backed by a new repository method `TryConnectAsync()`. `whileWaiting` lets a host keep its container health heartbeat fresh during the wait (`HealthcheckFile`, which also replaces the hosts' bare `catch { }` heartbeat writes).
   - `TryConnectAsync()` opens its own Npgsql connection and names the server and the socket's own reason on failure. A server that accepts the credentials but has no JIM database yet counts as connected, because JIM.Worker's migration creates it.
   - It retries with an increasing delay (1, 2, 4 and 8 seconds, then every 15 seconds), logging one Information line per attempt with the attempt number and elapsed time.
   - When the budget runs out (five minutes by default) it fails with a single clear Fatal line, and the supervisor restarts the service.
   - The delay is injected so tests control time without a new dependency.
   - Unit tests cover:
     - connecting at once;
     - connecting after several failures, checking the log lines and the delay sequence;
     - the budget running out;
     - cancellation.
   - Only failures that waiting can fix are retried (`NpgsqlException.IsTransient`: refused, unresolvable, timed out, "starting up"). Anything else, such as rejected credentials, is thrown at once, so the service stops with the real error instead of waiting it out.
2. **Call it at every start-up:**
   - The Worker, before `InitialiseDatabaseAsync()`.
   - JIM.Web, before its readiness loop.
   - The Scheduler, before its readiness loop, replacing today's `catch (Exception)` path for the unreachable-database case.
   - The Worker's Password Delivery Service checks `JimApplication.IsDatabaseReachableAsync()` quietly before each readiness poll, leaving the reporting to the main loop's wait; without it, the data layer logged an error every two seconds while the database was down.
   - All three hosts run through `HostRunner`, which exits 1 when a background service failed. Found while verifying this phase: a host stopped by a failing background service returned normally, so the process exited 0 after its Fatal line, and a supervisor or monitor saw a clean stop.
3. **Fully qualified PostgreSQL image:**
   - `docker.io/library/postgres:18.6@sha256:…` in `docker-compose.yml`.
   - Update the regex in `scripts/Build-ReleaseBundle.ps1:65` to match.
   - Confirm Dependabot's `docker-compose` ecosystem still tracks the digest. Its Docker parser drops a `docker.io` registry and treats the image as Docker Hub, and the explicit `library/` avoids its known short-name gap; confirm on its first run after merge.
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

### Phase 2: HTTPS by default, on both runtimes

Runtime-neutral, so it ships on Docker without waiting for the Podman path, and the Podman path inherits it.

1. **Health endpoints exempt from HTTPS redirection** in `src/JIM.Web/Program.cs`, test-first against the redirect behaviour. Both runtimes' probes are checked to report unhealthy when readiness fails.
2. **Production listening** in `deploy/docker-compose.production.yml`:
   - `ASPNETCORE_URLS=https://+:8443;http://localhost:8080`, and the Kestrel certificate settings pointing at `/run/secrets/jim-tls/`.
   - `ports: "${JIM_WEB_PORT:-5200}:8443"`.
   - Compose `secrets:` for the certificate and key.
   - `src/JIM.Web/Dockerfile` also exposes 8443. The base compose file and the development override stay on HTTP 8080.
3. **`deploy/setup.sh` certificate step:**
   - Asks for the organisation's certificate and key, or generates a certificate authority and server certificate with `openssl`, for the host names and addresses the administrator gives (defaulting to the host's fully qualified name and primary address).
   - Sets the key file's owner and mode for UID 1654, and prints the certificate authority's location and the server certificate's expiry date.
   - `--renew-certificate` issues a new server certificate from the saved certificate authority and restarts JIM.Web.
   - Asks whether a proxy or load balancer sits in front, and sets `JIM_TRUSTED_PROXIES` from the answer.
4. **Release bundle:** the generated `INSTALL.md` gains the certificate steps as `openssl` commands, for air-gapped installs done by hand.
5. **Documentation:**
   - `docs/administration/deployment.md`: the "TLS and Reverse Proxy" section is rewritten around HTTPS by default. It covers bring-your-own certificates, renewal, a proxy re-encrypting to JIM (recommended), and the loopback HTTP alternative from #1816, with Apache httpd and nginx examples and the SELinux boolean. The security checklist's "TLS configured via reverse proxy" line changes to match.
   - `docs/administration/configuration.md`: the certificate settings.
6. **Checks before merging:**
   - `dotnet build JIM.sln` and `dotnet test JIM.sln`.
   - A Docker boot with a generated certificate authority, then remote sign-in by server name.
   - An nginx proxy re-encrypting to JIM, and the loopback HTTP alternative.
7. **Changelog:** 🔄 "JIM now serves HTTPS out of the box, with your organisation's certificate or one the installer creates, so sign-in works from any machine without first setting up a reverse proxy."

### Phase 3: The Podman path

Delivered as one PR, or two if review size demands: files, then installer, release and docs.

1. **Pod and Quadlet files** in `deploy/podman/`, as tabled above, each with a header comment saying what it is for and what an administrator may edit. They are hand-written YAML using only fields that Podman 4.4 supports. `jim.web` mounts the `jim-tls` secret at `/run/secrets/jim-tls/` and uses the same listening settings as Phase 2.
2. **Database pod:**
   - The same tuning arguments as the production compose file, plus `dynamic_shared_memory_type=mmap` and `LANG=C.UTF-8`.
   - The same log-directory entrypoint wrapper.
   - The `jim-db` and `jim-logs` claims.
3. **`deploy/setup.sh`:**
   - Detect Docker and Podman, and add `--runtime docker|podman`. When both are present it asks rather than guessing.
   - The Podman branch:
     1. Checks the Podman version and systemd.
     2. Creates the `jim` account and its subordinate ID ranges and enables lingering (D5), unless `--rootful` is given or it is run without root.
     3. Asks the existing questions and writes `jim-config.yaml`.
     4. Runs the Phase 2 certificate step, then plays the secrets and TLS documents as the account that runs JIM and removes the files.
     5. Installs the units and configuration (D5 gives the locations).
     6. If firewalld is running, offers to open the web port.
     7. Reloads and starts `jim.service` through the right systemd manager.
     8. Waits for `/api/v1/health/ready` over HTTPS and prints the address, the certificate authority's location, and the operations cheat sheet's commands.
   - `setup.sh` stays bash: it is the existing customer-facing installer, and PowerShell cannot be assumed on a customer server.
4. **Release:**
   - `.github/workflows/release.yml` publishes the rendered Podman files as release assets alongside the compose files.
   - `scripts/Build-ReleaseBundle.ps1` adds a `podman/` folder to the bundle, and the generated `INSTALL.md` gains Podman steps using the same image tarballs, rootless by default.
5. **Documentation**, per the PRD's Documentation Impact table:
   - Docker and Podman content tabs (`pymdownx.tabbed` is already enabled) in deployment, upgrading, backup and troubleshooting.
   - The Podman secrets model in configuration.
   - Operating a rootless install: the `jim` account, `systemctl --user -M jim@`, logs, where volumes live, and backup.
   - Rootless and SELinux guidance for File Connector bind mounts, including a Podman volume backed by a CIFS share as the rootful alternative to a host bind mount.
   - A new page, `docs/administration/deploying-with-ansible.md`, with the D12 example playbook, added to the site navigation.
   - The two-runtime architecture in `engineering/DEVELOPER_GUIDE.md`.
6. **Checks before merging:**
   - A rootful boot in the cloud sandbox, and a rootless boot under a dedicated account on a host that supports it.
   - Remote HTTPS sign-in by server name.
   - A restart of the unit with data kept.
   - Upgrade by replacing the pod file and restarting.
   - The client address JIM logs for a remote request under rootless networking (see Risks).
   - An air-gapped load of the bundle's PostgreSQL image, on Podman and on both Docker image stores (classic and containerd), resolves the digest-pinned reference without trying to pull. Found in Phase 1: the containerd store keeps the digest through `docker save` and `docker load`; the classic store and Podman are unverified.
7. **Changelog:** ✨ "JIM can now be deployed with Podman, rootless by default, with no Docker or other extra software, including air-gapped."

### Phase 4: Proof in CI

1. **New `deployment-boot` job** in `.github/workflows/ci.yml`:
   - Runs on GitHub's Ubuntu 24.04 runners for every event; the self-hosted runners are not assumed to have Podman.
   - Builds the three images once and loads them into both runtimes.
   - Uses a test-only Keycloak with the development realm (`test/ci/deployment/`) as the identity provider.
2. **Docker leg:** boots the production compose files with a generated certificate and waits for readiness over HTTPS.
3. **Podman legs, rootful and rootless:**
   - The rootless leg creates a dedicated account with lingering, as `setup.sh` does.
   - Render the pod files with the CI tag, play the secrets, install the Quadlet units, start `jim.service`, and wait for readiness over HTTPS.
   - Run `podman healthcheck run` on each container.
   - Stop and start the unit, then confirm a marker written before the restart is still there.
4. **Parity:** `test/ci/deployment/Compare-RuntimeParity.ps1` (PowerShell, with Pester tests for its comparison logic) runs the D7 comparison and fails with a named difference.
5. **Required check:** made required per D6.
6. **Manual acceptance:** the RHEL virtual machine run from D5, covering `setup.sh` rootless and rootful and the D12 playbook, recorded in the PR that closes #1808.

## Success Criteria

- Every acceptance criterion in the PRD is met.
- A default install on either runtime can be signed into from another machine, with no reverse proxy.
- `deployment-boot` passes for Docker, Podman rootful and Podman rootless on every pull request, and has become a required check.
- A fresh RHEL 9 or 10 host with SELinux enforcing and firewalld running installs JIM with `setup.sh --runtime podman`, rootless by default and rootful on request, and with the D12 playbook, and signs in over HTTPS from another machine.
- An air-gapped install needs only the release bundle.

## Benefits

- **Market:** JIM becomes installable in RHEL-standardised, Docker-free estates, in the shape RHEL administrators already expect.
- **First-run experience:** a fresh install on either runtime works from any machine, instead of needing a reverse proxy before anyone can sign in.
- **Resilience on both runtimes:** services no longer crash-loop while the database starts, and an external database outage no longer takes JIM.Web down.
- **Security:**
  - HTTPS by default, with end-to-end encryption through a proxy as the recommended configuration.
  - Rootless by default on Podman, under an account that owns nothing else.
  - The Worker loses two unused capabilities.
  - Health probes can no longer pass on a redirect.
- **Confidence:** for the first time a CI check boots the production deployment of either runtime, so deployment regressions surface before release rather than at a customer.

## Dependencies

- #1805 (merged): port 8080 and the bundled database locale.
- #1816 (merged): the HTTPS requirement documented, and `JIM_TRUSTED_PROXIES` for reverse proxies.
- GitHub-hosted Ubuntu 24.04 runners with Podman 4.9 and systemd.
- `openssl` on the host, for generated certificates; it is part of every supported distribution's base install.
- A RHEL 9 or 10 virtual machine with SELinux enforcing, for D5's manual acceptance. The maintainer provides it.
- No new NuGet packages or other third-party dependencies. Ansible and Red Hat's podman system role are the customer's, used only in the manual acceptance run.

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Pod-file features differ between Podman 4.4 and 5.x | Use only fields verified above; CI covers 4.9 and the RHEL run covers 5.x |
| The two deployment definitions drift | D7 runtime parity check on every pull request |
| Probes pass on an HTTPS redirect | Health endpoints exempt from redirection, test-first (Phase 2) |
| A generated certificate expires, and HSTS then blocks access with no click-through | One-year validity, expiry printed at install, `setup.sh --renew-certificate`, and renewal documented |
| Rootless port forwarding hides the client's address, so JIM sees one gateway address for everyone. That weakens per-client rate limiting of unauthenticated API calls and makes the logs less useful | Measured in Phase 3 on Podman 4.9 and 5.x before release. If the address is lost, use a networking mode that keeps it. Never trust forwarded headers from that gateway address as a workaround: every client would share it, so any client could spoof its address |
| Docker mounts file-based compose secrets as bind mounts, so the key's host permissions decide whether UID 1654 can read it | The installer sets owner and mode; verified in Phase 2 |
| PostgreSQL parallel queries starved of shared memory | D9 `mmap`; an integration scenario against the Podman database pod before release |
| Secrets at rest in Podman's secret store | Same exposure class as `.env`, and documented. Podman's alternative secret drivers are noted as a hardening option |
| SELinux blocks a File Connector bind mount, or rootless container storage | Documented relabelling guidance, plus the CIFS-backed Podman volume alternative; the `jim` account uses a standard home directory so SELinux's home-directory labelling applies; covered in the RHEL run |
| firewalld blocks the web port on RHEL | `setup.sh` offers to open it; the D12 playbook opens it through the role |
| `setup.sh` grows complex | The certificate step and the Podman branch in their own functions, reusing the existing prompts; each path exercised by the CI boot legs |
| CI time grows | The new job runs in parallel with the existing ones; images built once per job |

**Out of scope, noted for later:**
- Removing `cifs-utils` from the Worker image, which also appears unused.
- Warning administrators in the portal before JIM's certificate expires.
- Podman auto-update.
- Kubernetes or OpenShift deployment.
