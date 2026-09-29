# Podman Support - Implementation Plan

- **Status:** Doing (Phases 1, 2 and 3 complete; Phase 4's check in place, awaiting D6 and the RHEL acceptance run)
- **Created:** 2026-09-25
- **Issue:** [#1808](https://github.com/TetronIO/JIM/issues/1808)
- **PRD:** [PRD_PODMAN_SUPPORT.md](../../prd/doing/PRD_PODMAN_SUPPORT.md) (this plan answers the PRD's open questions in [Decisions](#decisions) and withdraws requirement 12, per D8)

## Overview

JIM gains a second deployment path, alongside Docker, for hosts that run Podman. The images do not change. The path is a small set of Kubernetes-style files run by Podman's built-in `podman kube play`, with Quadlet units so systemd starts JIM at boot. On Podman, JIM runs rootful by default, as on Docker, or rootless under a dedicated `jim` account (D5, as amended in Phase 3). On both runtimes, JIM serves HTTPS out of the box.

The work is in four phases:

1. **Runtime-neutral start-up and cleanup.** Every JIM service waits for the database instead of crashing, the PostgreSQL image name is fully qualified, and the Worker drops two capabilities it never uses. This helps Docker as much as Podman, and the Podman path depends on it.
2. **HTTPS by default, on both runtimes.** JIM serves HTTPS itself with the organisation's certificate or one the installer creates, so a fresh install can be signed into from any machine without first building a reverse proxy.
3. **The Podman path.** Pod files, Quadlet units, secrets, rootful and rootless installation (rootless under a `jim` account), the installer, the release bundle and the documentation, including deployment with Ansible.
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
          the system manager (default), or the systemd user manager of account "jim" (--rootless)
            ├► jim-database.service ─► pod "jim-database" (PostgreSQL, optional)
            └► jim.service ──────────► pod "jim" (web, worker, scheduler)
          both pods on network "jim"; JIM reaches PostgreSQL at host name "jim-database"
          settings: ConfigMap "jim-config"; secrets: Podman secrets "jim-secrets", "jim-tls"

        Both runtimes: HTTPS on host port 443 (container 8443); HTTP 8080 on loopback inside
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
  - HTTPS on container port 8443, published on host port 443, the standard port, so JIM's address and every identity provider registration need no port (`JIM_WEB_PORT` changes it; the installer asks). Chosen in implementation over the originally planned 5200, which put `:5200` in every URL and redirect URI.
  - HTTP on `localhost:8080` inside the container, for the health probes only. It is never published, and binding it to loopback means no other container can reach it either.
  - Development is unchanged: HTTP on `localhost:5200` through the development override, which browsers treat as secure.
- **The certificate, chosen at install:**
  - The organisation's own certificate and key: recommended for production, typically issued by an internal certificate authority such as AD CS or IdM.
  - Otherwise the installer creates a local certificate authority and a server certificate for the host names and addresses the administrator gives it. It saves the certificate authority's certificate for the administrator to distribute to browsers and any proxy. A certificate authority is used rather than a bare self-signed certificate so that renewing the server certificate does not mean distributing trust again.
  - A generated server certificate is valid for one year, and `setup.sh --renew-certificate` issues a new one from the saved certificate authority. JIM already sends HSTS in production, so once browsers trust the certificate authority an expired certificate blocks access outright, with no click-through. The installer therefore prints the expiry date, and the documentation covers renewal.
- **Where the certificate lives:** both runtimes mount the pair at `/run/jim-tls/tls.crt` and `tls.key`, so the Kestrel settings (`ASPNETCORE_Kestrel__Certificates__Default__Path` and `KeyPath`) are identical and need no D7 allowlist entry. *Amended in Phase 3:* planned as `/run/secrets/jim-tls/`, but on RHEL and its derivatives Podman mounts a read-only `/run/secrets` of its own (subscription data, from `mounts.conf`) into every container, so a mount point beneath it cannot be created under a read-only root filesystem. Found booting on CentOS Stream 9; Ubuntu's Podman does not do this.
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

**D5. Rootless (OQ5): the Podman default, under a dedicated `jim` account; rootful is an option.** *Amended in Phase 3: rootful is the default and rootless the option (`setup.sh --rootless`), because rootless networking loses the client's address; see Phase 3.*
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
  - Host ports below 1024 need the `net.ipv4.ip_unprivileged_port_start` setting, and the default port, 443, is one. The rootless installer (run as root) sets it to 443 in `/etc/sysctl.d/`, or the administrator picks a port of 1024 or above. Red Hat's containerized Ansible Automation Platform is believed to lower the same setting for its rootless install; confirm that precedent in Phase 3 before relying on it in the documentation. Rootless Docker has the same limit, and Phase 2's installer already checks for it.
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

**D8. PRD requirement 12 withdrawn: health checks stay as commands in each definition.** *Amended in Phase 3: the Podman probes are liveness checks behind startup probes; see Phase 3.*
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
   - Integration Scenario 001.
6. **Changelog and docs:**
   - Changelog: 🔄 "JIM's services now wait for the database at start-up instead of restarting until it is available."
   - Docs: the troubleshooting page gains the new log lines.

### Phase 2: HTTPS by default, on both runtimes ✅

Runtime-neutral, so it ships on Docker without waiting for the Podman path, and the Podman path inherits it.

1. **Health endpoints exempt from HTTPS redirection:** `UseHttpsRedirectionExceptHealthProbes` in `src/JIM.Web/Middleware/HttpsRedirectionExtensions.cs`, test-first against a pipeline built with production's server addresses (a probe got 307 before the change). Verified at runtime: with the database stopped, the container's own health check command exits 22 and Docker marks `jim.web` unhealthy, then healthy again when the database returns.
2. **Production listening** in `deploy/docker-compose.production.yml`:
   - `ASPNETCORE_URLS=https://+:8443;http://localhost:8080`, and the Kestrel certificate settings pointing at `/run/secrets/jim-tls/`.
   - `ports: "${JIM_WEB_PORT:-443}:8443"`.
   - Compose `secrets:` for the certificate and key. Verified: Compose ignores `uid`, `gid` and `mode` on file-based secrets and bind-mounts the host file with its own owner and mode, as D4 expected.
   - `src/JIM.Web/Dockerfile` exposes 8443. The base compose file and the development override stay on HTTP 8080.
   - Verified: TLS 1.2 and 1.3 accepted and 1.1 refused; HSTS sent; the loopback listener unreachable from other containers; an organisation chain (leaf plus issuing CA, RSA) served in full.
3. **`deploy/setup.sh` certificate step:**
   - Creates a certificate authority and a server certificate with `openssl` (EC P-256, portable to OpenSSL 1.1.1), or installs and checks the organisation's certificate and key (parses, unencrypted, key matches, not expired, warns within 30 days).
   - **Added in implementation:** the generated CA carries name constraints limiting it to the names given (non-critical, so a client that cannot apply them still accepts JIM's certificate). The CA's key sits on the JIM server and administrators are asked to trust the CA in every browser; without constraints, a stolen key could impersonate any site to those browsers. The cost: a new name needs a new CA, distributed again. An IP-only certificate uses the common name `JIM`, because OpenSSL checks a host-name-like common name against the DNS constraint when there is no DNS name.
   - Makes the key belong to UID 1654: `chown` when run as root, otherwise from a throwaway container of the `jim-web` image, which also maps the owner correctly under rootless Docker.
   - `--renew-certificate` issues a new server certificate from the saved CA for the saved names and restarts `jim.web`; verified that the restart serves the new certificate.
   - Asks whether a proxy or load balancer sits in front, and sets `JIM_TRUSTED_PROXIES` from the answer.
   - Found while verifying: every release publishes `.env.example` as `default.env.example` (GitHub renames a leading dot), so the installer had been failing at its download step. Fixed in its own commit.
   - **Added after a review of the deployment engineer's journey** (agreed with the product owner, same PR):
     - The installer asks for the HTTPS port, defaulting to 443, and refuses a port something else holds (`ss`, or a connect test without it) or that rootless Docker cannot publish.
     - One installer for connected and air-gapped installs: run inside an extracted bundle, `setup.sh` copies the bundle's files, loads its images, checks every image the compose files name is present, and starts JIM with `--pull never`. The bundle ships `setup.sh` and a `VERSION` file, and releases publish `setup.sh` as an asset.
     - It installs in `/opt/jim` when run as root, keeps a copy of itself there, waits until `jim.web` is healthy (reporting honestly and exiting 1 if it is not within ten minutes), and makes `.env` readable by its owner only.
     - `--certificate` re-runs the certificate step for an existing installation, which replaces the hand-typed CA recipe and the manual replacement steps; maintenance options act on the installation the script sits in.
   - Found while verifying the air-gapped install on a fresh host with `curl` disabled:
     - Every bundled-database install pointed JIM at `localhost` (the development value in `.env.example`) and never started; the installer now writes `JIM_DB_HOSTNAME=jim.database`, and the by-hand steps say so.
     - The bundle saved PostgreSQL by its digest reference, which writes an archive with no image name; `docker load` then produced an anonymous image the compose file could not find, even on the containerd store. It is now saved by name and tag, and the digest-pinned reference resolves after loading.
4. **Release bundle:** `setup.sh` and `VERSION` at the root, and a short `INSTALL.md` built around running the installer, with a by-hand section kept for organisations whose policy requires it. It is written with LF line endings: `.ps1` files check out with CRLF, and a heredoc copied from a CRLF guide writes carriage returns into the files it creates.
5. **Documentation:**
   - `docs/administration/deployment.md`: installation restructured around the installer (before you install, connected and air-gapped each with the installer or by hand, what the installer does, after installing); "TLS and Reverse Proxy" rewritten around HTTPS by default: the certificate files, the installer's CA and distributing it, renewal, replacement, re-encrypting proxies (nginx and Apache httpd, with the SELinux boolean), trusting the proxy, and the loopback HTTP alternative as a documented override file.
   - `docs/administration/configuration.md`: the certificate settings; troubleshooting, getting started, quick start, prerequisites and security headers updated.
   - Found while verifying: nginx rejects JIM's sign-in response (`upstream sent too big header`, 502) at its default buffer size, because the authentication cookies total about 6 KB; the nginx example now sets `proxy_buffer_size 16k` and `proxy_buffers 8 16k`. This also affected the example documented before this phase.
   - Found while verifying: Kestrel loads `Certificates:Default` even when only HTTP URLs are configured, so the loopback HTTP override also blanks the certificate settings.
6. **Checks before merging:**
   - `dotnet build JIM.sln` and `dotnet test JIM.sln`.
   - A Docker boot with a generated certificate authority, then browser sign-in by server name (Secure cookies, WebSocket over `wss://`).
   - An nginx proxy re-encrypting to JIM (and rejecting JIM's certificate under the wrong CA), with sign-in through it and the client's own address recorded via `JIM_TRUSTED_PROXIES`; and the loopback HTTP alternative with no certificate.
   - `setup.sh` end to end (created and organisation certificates, a proxy, non-root key ownership) and `--renew-certificate`.
   - An Apache httpd proxy re-encrypting to JIM, with sign-in through it.
   - Air-gapped on a fresh host (JIM's images untagged, PostgreSQL's removed, volumes wiped, `curl` replaced by a stub that records calls): the installer from the bundle, then browser sign-in at `https://jim.test` on 443, with no internet access attempted; and the by-hand steps from the generated `INSTALL.md`, run verbatim.
   - The maintenance options from `/opt/jim/setup.sh`: renewal, moving to an organisation chain, new names (new CA, with the redistribution warning), and renewal refused for an organisation certificate.
7. **Changelog:** 🔄 "JIM now serves HTTPS out of the box, with your organisation's certificate or one the setup script creates, so sign-in works from any machine without a reverse proxy."

### Phase 3: The Podman path ✅

Delivered as one PR: the files, the installer, the release and the documentation.

1. **Pod and Quadlet files** in `deploy/podman/`, as tabled above, except `jim-tls.yaml`: the installer, the by-hand steps and the Ansible page each build the TLS Secret document from the certificate files, so a template nobody should edit by hand was left out. `jim-secrets.yaml` uses `stringData`, so an administrator writes plain values. `scripts/Build-PodmanFiles.ps1` renders the image placeholders (Pester-tested).
2. **Database pod** as planned, with the `JIM_DB_LOG_MIN_DURATION` default applied in the entrypoint wrapper.
3. **`deploy/setup.sh`** as planned, with these changes found in implementation:
    - **One installation folder for both runtimes, `/opt/jim`,** rather than D5's `~jim/.config/jim/` for Podman. The documentation, the installer's copy (`sudo /opt/jim/setup.sh --renew-certificate`) and the certificate authority are then in the same place on either runtime, and the CA key stays readable by root only: the `jim` account, which runs the containers, cannot read it. The Quadlet units still go in `~jim/.config/containers/systemd/` (rootful: `/etc/containers/systemd/`) and name the pod files by absolute path. `install.conf` records the runtime, account and port for the maintenance options.
    - Secrets and the TLS pair are played from standard input (`podman kube play -`), so no secret reaches a file or the process list.
    - `systemctl --user -M jim@` works on RHEL 9's systemd 252 without `systemd-container`. `journalctl --user -M jim@` does not (it needs `systemd-machined`), and `journalctl --user-unit` found nothing on the test host, so the documentation uses `podman logs`.
    - Rootless Podman fails outright in a folder the account cannot read, such as `/root` (`cannot chdir to /root`), so every documented command runs Podman as `jim` from the root folder: `jim-podman() { (cd / && sudo -u jim XDG_RUNTIME_DIR=/run/user/$(id -u jim) podman "$@"); }`. `systemd-run -M jim@ --user` was tried and rejected: it expands `$` in arguments.
    - The port check recognises JIM's own pod holding the port on a reinstall; `/etc/sysctl.d` is created if missing.
    - **Both runtimes:** a reinstall keeps the bundled database's password (from `.env`, or `podman secret inspect --showsecret`); with no password to keep and the database volume present, the installer stops and says how to go on. Previously a reinstall generated a new password, which the existing database rejected.
4. **Release** as planned. `Build-ReleaseBundle.ps1` also refuses to build a bundle whose PostgreSQL archive lacks the registry manifest its digest needs (see below).
5. **Documentation:** a new page, `docs/administration/podman.md` (how it runs, rootful or rootless, operating it, health checks, firewall and SELinux, installing by hand, without systemd), linked from the others rather than repeating them; `deploying-with-ansible.md`; and Podman content in deployment, configuration, upgrading, backup, troubleshooting, prerequisites, quick start and the File Connector.

**Found in implementation, and changed:**

- **Rootful is the default; rootless is `--rootless`,** reversing D5. Rootless networking loses the client's address (measured below), so a rootless JIM's security audit events and its rate limiting of unauthenticated requests cannot tell clients apart, which audit requirements in JIM's market do not accept. Rootful keeps it, and matches Docker's posture: the containers still run non-root with every capability dropped. Rootless stays a supported option for policies that require it, with the trade-off documented. Run unprivileged, the installer now stops unless given `--rootless`, rather than quietly installing under the caller's account; a reinstall keeps an installation's mode and account, since its images, secrets and data are in that account's Podman storage, and the installer refuses to move one.
- **Podman 5 restarts a container whose liveness probe fails** (Podman 4.9 and Docker only report it), and there is no pod-file setting to stop it. A readiness probe as liveness would restart `jim-web` throughout maintenance mode or a database outage; a strict `pg_isready` could kill a long crash recovery over and over. So each container has a `startupProbe` (Podman 4 ignores it) allowing any start-up time, and a liveness probe asking only whether the service is hung: `/api/v1/health/live` for `jim-web`, the heartbeat for the worker and scheduler, and any answer at all from PostgreSQL within five minutes.
- **The worker's heartbeat went stale during migrations and cache warming**, which would have let Podman 5 kill a large installation's upgrade part-way. `HealthcheckFile.KeepFreshWhileAsync` keeps it fresh through those steps (TDD). On Docker, the worker now shows healthy during a long upgrade.
- **Podman 4.9 lets `envFrom` replace an explicit `env` value** (the reverse of Kubernetes and Docker Compose), so the settings the pod file sets itself stay out of `jim-config.yaml`, which says so.
- **`emptyDir: {medium: Memory}` is an on-disk named volume on Podman 4.9,** hidden by the tmpfs Podman mounts on `/tmp` for a read-only container; on 5.8 it is a tmpfs. Kept, since `/tmp` would otherwise be read-only on a host that turns `read_only_tmpfs` off.
- **A `secret` volume is a named volume** that Podman fills from the secret at each `kube play`, so a renewed certificate reaches `jim-web` on the next restart. Renewal therefore restarts the whole JIM pod on Podman (the worker and scheduler too); documented.
- **The PostgreSQL archive:** Podman 4.9 and 5.8 resolve the digest-pinned reference after loading the bundle's archive (saved from Docker's containerd store, which keeps the registry manifest). Docker's classic image store does not: it loads the image with no repository digest, so the compose file's reference never resolves and the installer stops at its image check. That predates this work, affects only air-gapped Docker installs on the classic store, and is left for its own change ([#1854](https://github.com/TetronIO/JIM/issues/1854)).

**Verified**, in the cloud sandbox and a CentOS Stream 9 test host (systemd 252, Podman 5.8, firewalld) run as a privileged container:

- Rootful, the default, through the installer from a release bundle with no options: offline image load, secrets, Quadlet units in `/etc/containers/systemd/`, firewalld, start under the system manager, and readiness. Rootless (`--rootless`) the same way, plus the account, subordinate IDs, lingering and port threshold, under the account's systemd manager.
- A reinstall of a rootless installation with no options kept it rootless; the installer refused `--rootless` over a rootful installation, another account over a rootless one, an account without `--rootless`, and an unprivileged run without `--rootless`, each with a message saying what to do.
- Browser sign-in by server name over HTTPS (Secure cookies, `wss://` WebSocket).
- Restart of the host: systemd started the network, database and JIM with nobody logged in, data kept.
- Upgrade by replacing the pod file and restarting; `--renew-certificate` (new certificate served); a reinstall (password and CA kept).
- Backup and restore of the database and keys with the documented commands, and File Connector copies and a rootless host folder mount.
- Without systemd (Podman 4.9): the installer's rootful fallback, renewal, and an external PostgreSQL server.
- Docker regression of the refactored installer: bundle install, reinstall keeping the password, the new certificate path.

**Not verified here:** SELinux enforcing and cgroup v2. The sandbox's hybrid cgroups could run pods under systemd only in the configuration used above (private cgroup namespace, `runc`, cgroup v1), and its kernel has no SELinux. Phase 4's CI job (on a real systemd host with cgroup v2) and the RHEL acceptance run cover them. The Ansible page follows the role's documented example and has not been run (D12).

**Measured, and decided:** rootless networking hides the client's address on both Podman 4.9 (slirp4netns) and 5.8 (pasta): JIM recorded one internal address for a client in another network namespace. Rootful keeps it on both: JIM recorded the client's own address, on 5.8 for a client outside the CentOS Stream 9 host. Security audit events and rate limiting of unauthenticated requests therefore cannot tell rootless clients apart. The product owner chose rootful as the default (see above); the documentation warns against rootless where client addresses matter.

### Phase 4: Proof in CI

1. **`deployment-boot` job** in `.github/workflows/ci.yml` ✅, with one change to the plan: each leg installs JIM from a release bundle built from the commit, with the bundle's own `setup.sh`, offline, rather than rendering the files and playing the secrets by hand. That covers every step planned here, and also the installer and the bundle, which nothing else tested; and it is what a customer runs.
   - Runs on GitHub's Ubuntu 24.04 runners for every event except the nightly schedule; the self-hosted runners are not assumed to have Podman.
   - Builds the three images with `scan-images`' build cache (read-only), then bundles them. `Build-ReleaseBundle.ps1` gains `-SkipImageBuild` and `-SkipArchive` for this, with Pester tests.
   - Switches Docker to the containerd image store, which the bundle's PostgreSQL digest guard needs (#1854), and frees disk for three installations.
   - The test-only Keycloak (`Start-TestIdentityProvider.ps1`) runs the image `docker-compose.override.yml` names, with the development realm, on the host's network at the host's address, so one identity provider serves all three legs and the check has nothing of its own to keep up to date.
2. **Docker leg** ✅ and 3. **Podman legs, rootful and rootless** ✅, all driven by `test/ci/deployment/Invoke-DeploymentBoot.ps1`:
   - The installer creates the rootless leg's account, with lingering, as it does for a customer.
   - Each leg waits for readiness over HTTPS, trusting only the certificate authority the installer created, then for every container's own health check (`podman healthcheck run` on Podman).
   - It writes a marker to the database and to the File Connector volume, stops and starts JIM (Compose down and up; `systemctl stop` and `start` of the units), and checks that JIM came back in new containers with both markers.
   - It saves the containers' inspect output, and on failure their logs, as the job's artifact.
4. **Parity** ✅ `test/ci/deployment/Compare-RuntimeParity.ps1`, with Pester tests for its comparison logic, compares the D7 properties per service. The intended differences are listed in the script with their reasons:
   - variables Podman sets in every container;
   - Compose's own settings, which `env_file` also passes into the containers;
   - the certificate, mounted as two files on Docker and as a secret folder on Podman;
   - the database's default capabilities, where Docker's set is larger;
   - the slow query threshold, which Podman's database command reads from its environment and Compose puts into the command itself.

   **Found on its first run:**
   - `.env.example` set `JIM_SSO_VALID_ISSUERS` to the development Keycloak's issuer, so every Docker installation trusted that issuer too. Fixed in this phase: the value moved into `docker-compose.override.yml`.
   - Docker Compose's `env_file` gave the database container every JIM setting and secret, which it never needed ([#1862](https://github.com/TetronIO/JIM/issues/1862)). Fixed after this phase: the service no longer has `env_file`, and the parity check now fails if JIM's settings come back. `test/ci/deployment/Tests/ComposeFiles.Tests.ps1` also checks the rendered Compose files on every pull request.

   **Found on GitHub's Ubuntu runner (Podman 4.9), and fixed in this phase:**
   - **Rootful JIM on Ubuntu 24.04 had no network at all.** Ubuntu gives `crun` and `podman` AppArmor profiles of their own; a container that sets no-new-privileges cannot leave them for `containers-default`, so AppArmor stacks the two (`containers-default//&crun`), and the stack denies every socket (`failed af match`). JIM reached neither PostgreSQL nor its identity provider, while its health checks stayed green: the worker was deliberately waiting. Rootless Podman, Docker and RHEL are unaffected. `setup.sh` now offers, as it does for the firewall, to add a network rule to each profile's local override in `/etc/apparmor.d/local/`; the container keeps its own profile. Removing the two profiles instead was measured and rejected: it breaks rootless Podman for every account on the host.
   - **`setup.sh` ran the rootless account's Podman with the caller's environment,** and GitHub's sudo keeps `XDG_CONFIG_HOME`, so Podman read the runner's configuration folder and failed. The account's commands now get a clean environment, keeping proxy settings.

   **Found on GitHub's Ubuntu runner, and detected by the installer:** the runner image sets `XDG_CONFIG_HOME` and `XDG_RUNTIME_DIR` for every account in `/etc/environment`, pointing at the runner's own folders. The rootless account's systemd user manager inherits them, so the Quadlet generator looked for JIM's units in the wrong folder and generated none, and Podman used a runtime folder the account does not own. A customer host configured the same way failed the same way, silently. `setup.sh` now reads the account's manager environment before installing anything and stops, naming each setting, where such settings usually live and how to restart the manager, when `XDG_CONFIG_HOME`, `XDG_DATA_HOME` or `XDG_RUNTIME_DIR` is not the account's own (Pester tests in `test/ci/deployment/Tests/SetupScript.Tests.ps1`, which source the installer). It does not override them: they are the host's configuration, and rootful is unaffected. The job removes those lines before the rootless leg.
5. **Required check:** pending. Informational until it has passed ten consecutive runs (D6); then an administrator adds it to the `main` ruleset.
6. **Manual acceptance:** pending. The RHEL virtual machine run from D5, covering `setup.sh` rootless and rootful and the D12 playbook, recorded in the PR that closes #1808.

**Verified before the PR:** the Docker leg in the cloud sandbox (Docker 29, containerd image store), both Podman legs on the CentOS Stream 9 test host (Podman 5.8, systemd), and the parity comparison across all three. The Ubuntu runner (Podman 4.9) is proven by the job itself.

## Success Criteria

- Every acceptance criterion in the PRD is met.
- A default install on either runtime can be signed into from another machine, with no reverse proxy.
- `deployment-boot` passes for Docker, Podman rootful and Podman rootless on every pull request, and has become a required check.
- A fresh RHEL 9 or 10 host with SELinux enforcing and firewalld running installs JIM with `setup.sh --runtime podman`, rootful by default and rootless on request, and with the D12 playbook, and signs in over HTTPS from another machine.
- An air-gapped install needs only the release bundle.

## Benefits

- **Market:** JIM becomes installable in RHEL-standardised, Docker-free estates, in the shape RHEL administrators already expect.
- **First-run experience:** a fresh install on either runtime works from any machine, instead of needing a reverse proxy before anyone can sign in.
- **Resilience on both runtimes:** services no longer crash-loop while the database starts, and an external database outage no longer takes JIM.Web down.
- **Security:**
  - HTTPS by default, with end-to-end encryption through a proxy as the recommended configuration.
  - Rootless on Podman for policies that require it, under an account that owns nothing else.
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
| Rootless port forwarding hides the client's address, so JIM sees one gateway address for everyone. That weakens per-client rate limiting of unauthenticated API calls and makes the logs less useful | Measured in Phase 3: lost rootless on Podman 4.9 and 5.8, kept rootful. Rootful is therefore the default, and the documentation warns against rootless where client addresses matter. Never trust forwarded headers from that gateway address as a workaround: every client would share it, so any client could spoof its address |
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
