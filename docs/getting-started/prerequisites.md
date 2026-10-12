---
title: Prerequisites
description: "What you need before deploying JIM: container runtime, identity provider, and hardware requirements."
type: reference
audience: implementer
validated: v0.16.0
---

# Prerequisites

JIM needs a container runtime, an OpenID Connect identity provider, a TLS certificate, a PostgreSQL database and a host to run them on. Have each in place before you deploy.

## Container Runtime

JIM runs in containers, on either of two runtimes. You will need one of:

- **Docker Engine** 24.0 or later, with **Docker Compose** v2.24 or later
- **Podman** 4.4 or later, with systemd: the container engine Red Hat ships and supports on RHEL, including RHEL 9 and 10 (see [Running on Podman](../administration/podman.md))

A production server runs Linux. For evaluation, Docker Desktop also runs JIM on macOS and Windows.

## Identity Provider

JIM uses OpenID Connect (OIDC) for Single Sign-On authentication. You will need access to an OIDC-compliant Identity Provider, such as:

- Entra ID
- Keycloak
- AD FS
- Any other OIDC-compliant provider

Before deploying, configure your identity provider with a client registration for JIM. See the [SSO Setup Guide](../administration/sso-setup.md) for detailed instructions.

## TLS Certificate

JIM serves HTTPS, which browsers need in order to sign in. Use a certificate for JIM's host name from your organisation's certificate authority, or let the setup script create one; creating one needs OpenSSL on the JIM host. See [TLS and Reverse Proxy](../administration/tls-and-reverse-proxy.md).

## Hardware Requirements

| Component | Minimum    | Recommended                                      |
|-----------|------------|--------------------------------------------------|
| CPU       | 2 cores    | 4+ cores                                         |
| RAM       | 4 GB       | 8+ GB                                            |
| Storage   | 20 GB      | 50+ GB (depends on identity data volume)         |

Storage scales with the number of identity objects and the frequency of synchronisation runs (change history, logs, etc.).

### Memory scaling by object count

During a Full Import, the Worker holds every object it imports from a Connected System in memory, together with its attribute values and the Activity's result for each object. Memory requirements therefore scale linearly with the number of objects in the largest Connected System.

These figures are for the **host machine** (or VM) running JIM (on Docker or Podman); they must cover the operating system, all JIM containers, and the database.

| Connected System Size       | Minimum Host RAM | Recommended Host RAM |
|-----------------------------|------------------|----------------------|
| Up to 10,000 objects        | 4 GB             | 8 GB                 |
| 10,000 to 50,000 objects    | 8 GB             | 16 GB                |
| 50,000 to 100,000 objects   | 20 GB            | 24 GB                |
| 100,000 to 250,000 objects  | 24 GB            | 32 GB                |
| 250,000 to 500,000 objects  | 48 GB            | 64 GB                |

!!! info "Why large imports need significant memory"
    During a Full Import, the Worker holds every imported object and its attributes in memory until it has compared them all (to find duplicates, detect deleted objects, and link objects that refer to each other), and only then saves them. A Full Import of 100,000 objects with 20 attributes each peaks at approximately 2.3 GB of memory in the Worker, and PostgreSQL needs a further 1 to 2 GB while the results are saved. Combined with the web, scheduler, and operating system overhead, total system memory consumption reaches 8 to 10 GB for 100K objects.

!!! note
    These requirements apply to the largest single Full Import. If you have multiple Connected Systems of 50K objects each but import them sequentially (not concurrently), size for 50K, not the sum. Delta Imports process only changed objects and require significantly less memory.

!!! info "Large group memberships drive memory more than object count"
    A group is loaded with its full member list during processing, so a single large group can dominate memory use: a group with 495,000 members loads 495,000 member references. JIM's largest validated scenario (a cross-domain synchronisation between two directories of roughly 500,000 objects each, with groups of up to 495,000 members) peaked at approximately **55 GB total host RAM** with the full stack, database, and both directories resident. Size toward the upper figure in the table when provisioning or synchronising very large groups; a deployment of the same object count with only small groups needs considerably less.

## Network Requirements

JIM's services communicate internally over a private container network (`jim-network` on Docker, `jim` on Podman). The only port that needs to be exposed externally is the HTTPS port on `jim.web` (container port `8443`), which the installation publishes on host port `443`.

| Direction | Port                                                  | Purpose                                        |
|-----------|-------------------------------------------------------|------------------------------------------------|
| Inbound   | 443 (HTTPS), or the port you chose instead            | Web UI and REST API                            |
| Outbound  | Varies                                                | OIDC provider, LDAP targets, file shares, etc. |

In air-gapped environments, no outbound connectivity is required after initial deployment.

## Database

JIM uses PostgreSQL as its database. You have two options:

- **Bundled PostgreSQL:** A PostgreSQL container is included with JIM, on Docker or Podman, for simple deployments. No additional setup is required.
- **External PostgreSQL:** Connect to your existing PostgreSQL server by configuring the `JIM_DB_HOSTNAME` environment variable. Port 5432 is assumed; where your server listens elsewhere, append the port to the hostname (`db.example.org:5433`). Create JIM's user and its database on the server first, as [Before You Install](../administration/deployment.md#before-you-install) shows.

## Building on JIM

Contributing to JIM needs none of the above: the development environment brings its own database and identity provider. The contributor guides in the repository's [engineering documentation](https://github.com/TetronIO/JIM/tree/main/engineering) set it up.
