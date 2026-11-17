---
title: Prerequisites
description: "What you need before deploying JIM: container runtime, identity provider, and hardware requirements."
type: reference
audience: implementer
validated: v0.18.0
---

# Prerequisites

JIM needs a container runtime, an OpenID Connect identity provider, a TLS certificate, a PostgreSQL database and a host to run them on. Have each in place before you deploy.

```jim-diagram
archetype: anatomy
title: What a JIM installation needs in place
desc: "Before you deploy JIM, have six things in place. A container runtime: Docker Engine 24.0 or later with Docker Compose v2.24 or later, or Podman 4.4 or later with systemd. An OpenID Connect identity provider, with a client registration for JIM. A TLS certificate for JIM's host name, from your certificate authority or created by the setup script. A PostgreSQL database, either the bundled container or your own server. A host with at least 2 CPU cores, 4 GB of RAM and 20 GB of storage, and more memory as the largest Connected System grows. And the network: only inbound HTTPS, on port 443 or a port you choose, needs to be exposed."
subject:
  kind: object
  label: A JIM installation
  parts:
  - {label: Container runtime, detail: "Docker Engine 24.0+, or Podman 4.4+"}
  - {label: Identity provider, detail: "OpenID Connect, with JIM registered"}
  - {label: TLS certificate, detail: "from your CA, or made by the setup script"}
  - {label: PostgreSQL, detail: "bundled, or your own server"}
  - {label: Host, detail: "at least 2 cores, 4 GB RAM, 20 GB disk"}
  - {label: Network, detail: "only inbound HTTPS exposed"}
```

## :material-docker: Container Runtime

JIM runs in containers, on either of two runtimes. You will need one of:

- **Docker Engine** 24.0 or later, with **Docker Compose** v2.24 or later
- **Podman** 4.4 or later, with systemd: the container engine Red Hat ships and supports on RHEL, including RHEL 9 and 10 (see [Running on Podman](../administration/podman.md))

A production server runs Linux. For evaluation, Docker Desktop also runs JIM on macOS and Windows.

## :material-shield-key-outline: Identity Provider

JIM uses OpenID Connect (OIDC) for Single Sign-On authentication. You will need access to an OIDC-compliant Identity Provider, such as:

- Entra ID
- Keycloak
- AD FS
- Any other OIDC-compliant provider

Before deploying, configure your identity provider with a client registration for JIM. See the [SSO Setup Guide](../administration/sso-setup.md) for detailed instructions.

## :material-certificate-outline: TLS Certificate

JIM serves HTTPS, which browsers need in order to sign in. Use a certificate for JIM's host name from your organisation's certificate authority, or let the setup script create one; creating one needs OpenSSL on the JIM host. See [TLS and Reverse Proxy](../administration/tls-and-reverse-proxy.md).

## :material-memory: Hardware Requirements

| Component | Minimum    | Recommended                                      |
|-----------|------------|--------------------------------------------------|
| CPU       | 2 cores    | 4+ cores                                         |
| RAM       | 4 GB       | 8+ GB                                            |
| Storage   | 20 GB      | 50+ GB (depends on identity data volume)         |

Storage scales with the number of identity objects and the frequency of synchronisation runs (change history, logs, etc.).

### Memory scaling by object count

Memory grows with the size of the largest Connected System, and above all with its largest groups. A Full Import does not set it: the Worker saves what it reads in units of up to 2,000 objects, so an import's memory does not grow with the number of objects it reads. The peak comes in synchronisation and export, which compare and send each group's membership, and in the database, which caches what JIM reads most.

These figures are for the **host machine** (or VM) running JIM (on Docker or Podman); they must cover the operating system, all JIM containers, and the database.

| Connected System Size        | Minimum Host RAM | Recommended Host RAM |
|------------------------------|------------------|----------------------|
| Up to 10,000 objects         | 4 GB             | 8 GB                 |
| 10,000 to 100,000 objects    | 8 GB             | 16 GB                |
| 100,000 to 500,000 objects   | 16 GB            | 24 GB                |
| 500,000 to 1,000,000 objects | 64 GB            | 96 GB                |

!!! info "Large group memberships drive memory more than object count"
    A group's member references take memory as well as the group itself, so a few very large groups can cost more than many small objects. In JIM's validated scenario of 500,000 users and 25,000 groups synchronised between two directories, where the largest groups hold 500,000 and 400,000 members, JIM's four containers peaked at about **10 GB** together, during an export: the database at 6 GB, the Worker at 3.7 GB, and the web and scheduler at under 0.5 GB between them. Each Full Import peaked at under 2.7 GB in the Worker. A deployment of the same object count with only small groups needs considerably less. JIM is also validated with 1,000,000 users and 60,000 groups, two of which hold every user; size a deployment of that scale as the last row shows.

    The database figure follows its own cache size more than the object count: the bundled PostgreSQL takes a quarter of the host's memory for its cache, up to 8 GB (see [Bundled PostgreSQL Memory](../administration/configuration.md#bundled-postgresql-memory)). The minimums above fit those peaks with room for the operating system. The recommended figures leave the Worker room to finish without the Operations page reporting **Memory low**, which it does from 85% in use. The Worker's runtime keeps memory it has finished with while the host has plenty to spare, so a peak shows what it held rather than the least it needs.

!!! note
    These requirements apply to the largest single Connected System. If you have several Connected Systems of 50,000 objects each and run them one after another (not in parallel), size for 50,000, not the sum. Delta Imports hold only what changed, so their memory follows the size of the change, not of the Connected System.

If a host runs short anyway, JIM says so before the operating system steps in: the Operations page reports **Memory low** from 85% in use, and the Worker stops a run that would exhaust its memory, saying why on the run's Activity, rather than being killed part-way through it. See [Troubleshooting Worker Restarts and Memory](../administration/troubleshooting-worker.md).

## :material-lan: Network Requirements

JIM's services communicate internally over a private container network (`jim-network` on Docker, `jim` on Podman). The only port that needs to be exposed externally is the HTTPS port on `jim.web` (container port `8443`), which the installation publishes on host port `443`.

| Direction | Port                                                  | Purpose                                        |
|-----------|-------------------------------------------------------|------------------------------------------------|
| Inbound   | 443 (HTTPS), or the port you chose instead            | Web UI and REST API                            |
| Outbound  | Varies                                                | OIDC provider, LDAP targets, file shares, etc. |

In air-gapped environments, no outbound connectivity is required after initial deployment.

## :material-database-outline: Database

JIM uses PostgreSQL as its database. You have two options:

- **Bundled PostgreSQL:** A PostgreSQL container is included with JIM, on Docker or Podman, for simple deployments. No additional setup is required.
- **External PostgreSQL:** Connect to your existing PostgreSQL server by configuring the `JIM_DB_HOSTNAME` environment variable. Port 5432 is assumed; where your server listens elsewhere, append the port to the hostname (`db.example.org:5433`). Create JIM's user and its database on the server first, as [Before You Install](../administration/deployment.md#before-you-install) shows.
