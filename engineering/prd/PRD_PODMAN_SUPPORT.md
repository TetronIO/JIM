# Podman Support

- **Status:** Planned
- **Created:** 2026-09-24
- **Author:** Jay
- **Issue:** #1808

## Problem Statement

JIM can only be deployed with Docker. Every install path (the `setup.sh` script, the manual compose steps and the air-gapped release bundle) ends in `docker compose` handing JIM's containers to the Docker daemon, and `setup.sh` stops outright when Docker is missing.

That excludes a large part of the market JIM targets. Red Hat removed Docker from RHEL 8 and does not support it on RHEL 9 or 10; Podman is the container engine Red Hat ships and supports. Docker does publish its own packages for RHEL, but its install guide starts by removing Podman, and the result falls outside the customer's Red Hat support. Regulated organisations standardised on RHEL (healthcare, finance, government, the sectors JIM is aimed at) commonly will not accept that on a server. For them, JIM is not installable today. A consultancy customer has already hit this: JIM would not run in their Podman environment.

JIM's images are not the obstacle. They already run on Podman unchanged. What does not carry over is the supervision the Docker daemon provides implicitly: starting containers in dependency order, running health checks, and restarting containers after a reboot. Podman has no daemon; under Podman those jobs belong to systemd, and JIM ships nothing that tells systemd how to run it.

JIM is pre-release with no installed base, so this is the cheapest point at which to add a second runtime: there is no deployment to migrate and no compatibility promise to preserve.

## Goals

- An administrator can install, run, upgrade and back up JIM on a Linux host with Podman 4.4 or later and systemd, using only Podman: no Docker engine, no compose tool, no other additional software.
- The Podman path works air-gapped, from the same release bundle and the same image tarballs as the Docker path.
- On a host with systemd, JIM restarts automatically after a reboot under Podman.
- JIM runs with the same hardening on Podman as on Docker (non-root, read-only root filesystem, all capabilities dropped, no privilege escalation), rootful and rootless.
- Both deployment paths are booted from freshly built images by CI on every pull request, so a change that breaks either path fails before merge.
- The public documentation covers both runtimes for deployment, configuration, upgrading, backup and troubleshooting.

## Non-Goals

- Kubernetes or OpenShift deployment. The Podman definition is Kubernetes-style YAML, but it is not a supported Kubernetes manifest and must not be presented as one.
- Running JIM's compose files through Podman's compose wrappers (`podman compose`, podman-compose) as a supported route. They may work, but they need extra software that Red Hat does not ship or support, and podman-compose does not parse the current production compose file.
- Podman Desktop on macOS or Windows as a production platform.
- Different images per runtime. The images stay runtime-neutral.
- A bundled identity provider for production use. Customers bring their own, as on Docker.
- Demo-specific kits or tooling.
- Making Podman the primary or default runtime. Docker remains the primary path; this adds a second one.

## User Stories

1. As an administrator of a RHEL estate that does not permit Docker, I want to install and run JIM with Podman alone, so that I can deploy it without leaving Red Hat support or my organisation's container policy.
2. As an administrator on an air-gapped network, I want the release bundle to contain everything a Podman install needs, so that no internet access and no additional software are required.
3. As a security officer, I want JIM to run rootless under Podman with the same hardening as on Docker, so that the host has no root-equivalent container daemon.
4. As an operator, I want JIM to come back by itself after a host reboot on Podman, so that a patching window does not leave identity synchronisation stopped.
5. As a JIM maintainer, I want CI to boot both deployment paths on every change, so that the Docker and Podman definitions cannot drift apart silently.

## Requirements

### Functional Requirements

**Deployment definition**

1. JIM must ship a Podman deployment definition in `deploy/podman/`: a Kubernetes-style pod file, run by Podman's built-in `podman kube play`, describing `jim.web`, `jim.worker`, `jim.scheduler` and the optional bundled PostgreSQL. See [Design decision](#design-decision).
2. The pod file must apply the same security settings as `docker-compose.yml`: non-root user, read-only root filesystem, all capabilities dropped, no privilege escalation, and a separate in-memory `/tmp` for each container. Containers must not share `/tmp`: the Worker and Scheduler both write their health heartbeat to `/tmp/healthcheck`.
3. The same persistent data as the Docker path must live in named Podman volumes: database, logs, encryption keys and connector files, with the image's ownership (UID 1654) applied when a volume is first created.
4. A Quadlet `.kube` unit must run the pod file under systemd, so that JIM starts at boot. It must work rootful (`/etc/containers/systemd/`) and rootless (`~/.config/containers/systemd/`, with lingering enabled so JIM keeps running after the administrator logs out).
5. On a host without Quadlet (Podman older than 4.4) or without systemd, `podman kube play` must still start JIM; only restart after a reboot is lost. This is documented as best effort, not supported.
6. JIM's web port must be configurable, defaulting to host port 5200 as on the Docker path.

**Configuration and secrets**

7. The Podman path must use the same configuration variables as `.env`, documented once in `docs/administration/configuration.md`. An administrator must not have to learn a second set of settings.
8. Secret values (database password, SSO client secret, infrastructure API key) must not have to be written in plain text into the pod file. See [Open Questions](#open-questions) for the mechanism.
9. Values quoted in `.env` style (`JIM_SSO_MV_ATTRIBUTE="Subject Identifier"`) must reach JIM without the quotes, however the configuration is supplied.
10. The bundled PostgreSQL must be optional, as the `with-db` profile is on Docker. An external PostgreSQL server must be fully supported.

**Start-up behaviour (runtime-neutral)**

11. Every JIM service must tolerate its dependencies being unavailable at start-up, rather than relying on the supervisor to order start-up by health. A pod starts all its containers at once. Specifically, the Worker must wait for PostgreSQL with a bounded retry and a clear log line per attempt, instead of exiting. `jim.web` and `jim.scheduler` already wait for the Worker's readiness.
12. The worker and scheduler heartbeat health checks must move from shell one-liners in `docker-compose.yml` into scripts inside the images. The one-liners are full of `%` and `$`, which systemd and Quadlet treat as specifiers, and one definition in the image then serves both runtimes.

**Remote access**

13. The Podman path must support browser access from other machines, which requires HTTPS. In Production mode JIM's sign-in correlation cookies are `Secure` by design (`src/JIM.Web/Program.cs`), so over plain HTTP from a server name the browser drops them and sign-in loops. (Browsers treat `localhost` as secure, which hides this in local testing.) Both of these must be supported and documented:
    - JIM serving HTTPS itself from a certificate file, through Kestrel's certificate settings.
    - A TLS-terminating reverse proxy in front of JIM, as the Docker documentation already describes.

**Installation, upgrade and backup**

14. The release bundle must include the Podman files. Images must load with `podman load` from the existing tarballs, and the pod file must not pull images when they are already present.
15. `setup.sh` must detect Docker or Podman. On a Podman host it installs the Podman definition (the `.kube` unit where systemd and Quadlet are available, otherwise it runs `podman kube play`) and prints the address JIM is reachable on. Where both runtimes are present, the administrator chooses; the script must not silently pick one.
16. Upgrading on Podman must be documented and must preserve volumes: load or pull the new images, update the version, restart the unit (or replay the pod). Database upgrades apply automatically, as on Docker.
17. Backup and restore on Podman must be documented, keeping the database and the encryption keys volume as a matched pair, as `docs/administration/backup-recovery.md` requires for Docker.
18. File Connector guidance must cover rootless Podman: bind-mounted host paths appear under a mapped UID, so the current advice to `chown 1654:1654` on the host is wrong there (`:U` or `podman unshare chown` instead), and SELinux hosts need `:Z` or `:z` labels.

**Runtime-neutral cleanup**

19. Every image reference must be fully qualified (`docker.io/library/postgres:…`). Podman refuses unqualified names unless the host defines an alias, and `scripts/Build-ReleaseBundle.ps1` must be updated to match the new form.
20. The Worker's `SYS_ADMIN` and `DAC_READ_SEARCH` capabilities must be removed if confirmed unused. No code in `src/` mounts anything, and rootless containers cannot mount CIFS in any case, so these grant privilege for nothing.

**Verification**

21. CI must boot the Podman path on every pull request from the images the run has just built, on a host with systemd, through the `.kube` unit, and wait for `/api/v1/health/ready` to return 200. The Docker path must be booted the same way, so both definitions are proven by the same check.

### Non-Functional Requirements

- **Security parity:** no setting on the Podman path may be weaker than its Docker equivalent.
- **Air-gap:** no step may require internet access once the bundle is on the host.
- **No additional host software** beyond Podman and systemd.
- **No new NuGet packages or other third-party dependencies.**
- **Start-up time** comparable to Docker. In testing, JIM reported ready 40 to 75 seconds after `podman kube play` on a fresh database.

## Examples and Scenarios

### Scenario 1: Air-gapped install on RHEL 9, rootful

**Given**: a RHEL 9 server with Podman and systemd, no internet access, no Docker, and the release bundle transferred to it
**When**: the administrator loads the image tarballs with `podman load`, fills in the configuration, installs the `.kube` unit into `/etc/containers/systemd/`, and starts it with `systemctl start`
**Then**: JIM's four services start, `https://<server>:5200/api/v1/health/ready` returns 200, and sign-in through the organisation's identity provider works from a workstation browser

### Scenario 2: Connected install with setup.sh on a Podman-only host

**Given**: a Fedora or RHEL host with Podman but no Docker
**When**: the administrator runs `setup.sh`
**Then**: the script detects Podman, asks the same configuration questions as on Docker, installs and starts the Podman definition, and prints JIM's address, instead of stopping with "Docker is required"

### Scenario 3: Reboot

**Given**: JIM running from the `.kube` unit
**When**: the host is rebooted
**Then**: systemd starts JIM again with no administrator action, and the database, keys and logs are intact

### Scenario 4: Rootless install

**Given**: a service account with no root rights, with lingering enabled
**When**: the unit is installed into `~/.config/containers/systemd/` and started with `systemctl --user`
**Then**: JIM runs with the same hardening as rootful, keeps running after the account logs out, and restarts after a reboot

### Scenario 5: Upgrade

**Given**: JIM 0.16.0 running on Podman
**When**: the administrator loads the 0.17.0 images, updates the version and restarts the unit
**Then**: the Worker applies any database upgrades and JIM serves 0.17.0, with all data preserved

### Scenario 6: Older Podman (best effort)

**Given**: a Debian 12 host with Podman 4.3 (no Quadlet)
**When**: the administrator runs `podman kube play` on the pod file
**Then**: JIM starts and works, and the documentation states that it will not restart after a reboot on this host

### Scenario 7: Drift caught in CI

**Given**: a pull request that adds a required setting to `docker-compose.yml` but not to the Podman definition
**When**: CI runs
**Then**: the Podman boot check fails and names the service that did not become ready

## Constraints

- Must work air-gapped and on-premises only; no cloud services.
- Must not require software on the host beyond Podman and systemd.
- The images stay shared across both runtimes.
- The supported minimum is Podman 4.4 on a Linux host with systemd. Below that is best effort.
- British English throughout, per the repository conventions.

## Affected Areas

| Area | Impact |
|------|--------|
| Deployment files | New `deploy/podman/` (pod file, `.kube` unit, configuration and secret templates); `docker-compose.yml` image names and health checks |
| `deploy/setup.sh` | Runtime detection and a Podman install branch |
| Worker | Start-up retry while PostgreSQL is unavailable (`src/JIM.Worker/Worker.cs`, which calls `InitialiseDatabaseAsync` once today) |
| Images | Health-check scripts in the Worker and Scheduler images; possibly Web |
| Release | `scripts/Build-ReleaseBundle.ps1` ships the Podman files and matches the new PostgreSQL image reference |
| CI | New boot check for both deployment paths in `.github/workflows/ci.yml` |
| Database, API, UI | None |

## Documentation Impact

| Doc | Change |
|------|--------|
| `docs/administration/deployment.md` | Docker and Podman variants of every install path; HTTPS requirement for remote access; Podman prerequisites and supported versions |
| `docs/getting-started/prerequisites.md`, quick start | Podman as a supported runtime |
| `docs/administration/configuration.md` | How the same settings are supplied on Podman; secrets handling |
| `docs/administration/upgrading.md` | Podman upgrade and rollback procedure |
| `docs/administration/backup-recovery.md` | Podman volume backup and restore |
| `docs/administration/troubleshooting.md` | Podman-specific diagnostics (`podman pod`, `journalctl --user`, lingering) |
| `docs/connectors/jim-file-connector.md` | Rootless UID mapping and SELinux labels for bind mounts |
| `engineering/DEVELOPER_GUIDE.md` | Deployment architecture: two runtimes, one set of images |

## Dependencies

- #1805 (merged): `jim.web` pinned to port 8080 and the bundled database initialised with `C.UTF-8`. Both are prerequisites; port 80 cannot be bound by a non-root, capability-free process under Podman.

## Open Questions

1. **Secrets mechanism:** Podman secrets (`podman secret create`) referenced from the pod file, a Kubernetes `Secret` document played alongside it, or a generated file with restricted permissions?
2. **Configuration supply:** a Kubernetes `ConfigMap` passed with `podman kube play --configmap` (and Quadlet `ConfigMap=`), or `setup.sh` generating it from the `.env` an administrator already knows?
3. **Optional database:** one pod file with PostgreSQL removed for external-database installs, or two pod files? A separate database pod would lose the shared-`localhost` networking, so it should stay in the same pod when bundled.
4. **Default HTTPS approach:** should the Podman path serve HTTPS from JIM itself by default, with a reverse proxy as the alternative, or keep the Docker path's stance (reverse proxy recommended) for consistency?
5. **Rootless at launch:** support rootful and rootless together, or rootful first? Rootless could not be tested in the cloud sandbox and needs a real RHEL host.
6. **Required check:** should the CI boot check become a required status check in the `main` ruleset?
7. **Drift beyond start-up:** is a boot check enough, or should a test also compare the settings the two definitions pass to each service? A boot check misses a new optional setting.

## Acceptance Criteria

### Phase 1: runtime-neutral cleanup

- [ ] All image references fully qualified; the release bundle builds with the new PostgreSQL reference
- [ ] Worker waits for PostgreSQL at start-up with a bounded retry and logged attempts, covered by tests
- [ ] Worker `SYS_ADMIN` and `DAC_READ_SEARCH` removed, or their use documented if one is found
- [ ] Heartbeat health checks run from scripts inside the images; the Docker stack stays healthy

### Phase 2: Podman path

- [ ] `deploy/podman/` pod file and `.kube` unit boot JIM rootful on RHEL 9 and 10, with the same hardening as Docker
- [ ] JIM restarts after a reboot under the `.kube` unit
- [ ] Rootless install verified on a real RHEL host (or explicitly deferred per Open Question 5)
- [ ] Secrets are not stored in plain text in the pod file
- [ ] Bundled and external PostgreSQL both work
- [ ] HTTPS remote sign-in works with a certificate file and behind a reverse proxy
- [ ] `setup.sh` installs on a Podman-only host and refuses to guess on a host with both runtimes
- [ ] The release bundle contains the Podman files; an air-gapped install needs nothing else

### Phase 3: verification and documentation

- [ ] CI boots both paths from freshly built images on every pull request and waits for readiness
- [ ] Every page in the Documentation Impact table updated
- [ ] Changelog entry for Podman support

## Design decision

Four ways to run JIM on Podman were assessed:

| Option | Extra host software | Restart after reboot | Verdict |
|--------|--------------------|----------------------|---------|
| Compose files over Podman's Docker-compatible socket | Docker's compose tool (not shipped or supported by Red Hat) | Only with `restart: always`; `podman-restart.service` skips `unless-stopped` containers | Rejected: unsupported software on RHEL, and a carry-in for air-gapped hosts |
| podman-compose | podman-compose from EPEL | As above | Rejected: crashes on the production compose file today, and lags the Compose specification |
| One Quadlet `.container` unit per service (about nine files) | None | Yes | Viable, but a larger second definition, and health-gated ordering (`Notify=healthy`) needs Podman 5 |
| **Pod file run by `podman kube play`, plus one Quadlet `.kube` unit** | **None** | **Yes, with systemd** | **Chosen** |

The pod file was chosen because:

- It needs nothing beyond Podman, rootful or rootless.
- It still runs on hosts without Quadlet or systemd.
- Its services share `localhost` inside the pod, which simplifies database and sign-in wiring.
- The second definition is one file rather than nine.

Start-up order comes from JIM itself (requirement 11), not from the supervisor, so the design has no dependency on Podman 5.

## Additional Context

Evidence, gathered on 2026-09-24 in the cloud sandbox with Podman 4.9.3 (rootful), podman-compose 1.0.6 and 1.6.0, and JIM images built from `main`:

- **Works unchanged:** the images (non-root, read-only root, capabilities dropped), named-volume ownership on first use, dotted container hostnames, and `podman load` of the bundle tarballs with pinned digests intact.
- **`podman kube play` ran the full stack** (Web, Worker, Scheduler, PostgreSQL, plus a Keycloak standing in for a customer identity provider) from a single pod file, with no compose tool and no systemd:
  - Ready within 40 to 75 seconds.
  - No container restarts.
  - No Error or Fatal lines in any JIM service log.
  - Named volumes owned by UID 1654 and writable.
- **Browser sign-in:**
  - It loops over plain HTTP from a server name (requirement 13).
  - It succeeded over HTTPS in a single pass, with JIM serving a PEM certificate through `ASPNETCORE_Kestrel__Certificates__Default__Path` and `__KeyPath`, and Keycloak serving HTTPS from the same certificate.
- **Two pod-file pitfalls found and fixed during testing:**
  - A `/tmp` shared between containers failed at start-up, because Podman copies existing content into an in-memory mount, and it would also collide the heartbeat files.
  - `.env`-style quoted values carried their quotes into JIM, which rejected the SSO attribute name.
- **Compose-route findings:**
  - podman-compose crashes on `build: !reset null` in `deploy/docker-compose.production.yml`.
  - Unqualified `postgres:18.6` is refused where the host defines no short-name alias.
  - Health checks never run without systemd timers, so `service_healthy` start-up gating hangs.
- **Rootless** could not be tested: the sandbox has no `/dev/net/tun`.

Platform facts:

- Quadlet is part of Podman from 4.4; Red Hat introduced it as a technology preview in RHEL 9.2, and recent RHEL 9 releases and RHEL 10 ship Podman 5.
- Debian 12 ships Podman 4.3, which predates Quadlet.
- RHEL 8 offers Quadlet only as a technology preview.
- GitHub-hosted Ubuntu 24.04 runners ship Podman 4.9 with systemd, which is enough for the CI check in requirement 21.

References:

- [Red Hat: Building, running, and managing containers (RHEL 9)](https://docs.redhat.com/en/documentation/red_hat_enterprise_linux/9/html-single/building_running_and_managing_containers/index)
- [Docker: Install Docker Engine on RHEL](https://docs.docker.com/engine/install/rhel/)
- [podman-systemd.unit (Quadlet) reference](https://docs.podman.io/en/latest/markdown/podman-systemd.unit.5.html)
- [podman-kube-play reference](https://docs.podman.io/en/latest/markdown/podman-kube-play.1.html)
- [podman#17851: `podman-restart.service` skips `unless-stopped` containers](https://github.com/containers/podman/issues/17851)
- [Stack Overflow Developer Survey 2025](https://survey.stackoverflow.co/2025/technology): 10.9% of professional developers used Podman in the past year, against 73.8% for Docker
