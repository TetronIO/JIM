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

JIM serves HTTPS, which browsers need in order to sign in. Use a certificate for JIM's host name from your organisation's certificate authority, or let the setup script create one; creating one needs OpenSSL on the JIM host. See [TLS and Reverse Proxy](../administration/deployment.md#tls-and-reverse-proxy).

## Hardware Requirements

The minimum is a single host with 2 CPU cores and 4 GB of RAM, enough for a few Connected Systems with thousands of objects. The [Deployment Guide](../administration/deployment.md#hardware-requirements) sizes larger deployments by the number of objects they synchronise.

## Database

JIM uses PostgreSQL as its database. You have two options:

- **Bundled PostgreSQL:** A PostgreSQL container is included with JIM, on Docker or Podman, for simple deployments. No additional setup is required.
- **External PostgreSQL:** Connect to your existing PostgreSQL server by configuring the `JIM_DB_HOSTNAME` environment variable. Port 5432 is assumed; where your server listens elsewhere, append the port to the hostname (`db.example.org:5433`). Create JIM's user and its database on the server first, as [Before You Install](../administration/deployment.md#before-you-install) shows.

## Building on JIM

Contributing to JIM, or building a Connector of your own, needs none of the above: the development environment brings its own database and identity provider. The [Developer Guide](../developer/index.md) sets it up.
