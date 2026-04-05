---
title: Prerequisites
description: What you need before deploying JIM: container runtime, identity provider, and hardware requirements.
---

# Prerequisites

Before deploying JIM, ensure you have the following in place.

## Container Runtime

JIM runs as a Docker stack. You will need:

- **Docker Engine** (20.10 or later recommended)
- **Docker Compose v2**

Both Docker Desktop and standalone Docker Engine are supported. JIM runs on Linux, macOS, and Windows hosts.

## Identity Provider

JIM uses OpenID Connect (OIDC) for Single Sign-On authentication. You will need access to an OIDC-compliant Identity Provider, such as:

- Entra ID
- Keycloak
- AD FS
- Any other OIDC-compliant provider

Before deploying, configure your identity provider with a client registration for JIM. See the [SSO Setup Guide](../administration/sso-setup.md) for detailed instructions.

!!! tip "Developer environments"
    If you are using the devcontainer for development, a bundled Keycloak instance is included with pre-configured test users. No external identity provider is needed; sign in with `admin` / `admin`.

## Hardware Requirements

For detailed hardware sizing and production deployment guidance, see the [Deployment Guide](../administration/deployment.md).

As a general guideline, JIM's resource requirements are modest. A small deployment (a few connected systems with thousands of objects) runs comfortably on a single host with 2 CPU cores and 4 GB of RAM.

## Database

JIM uses PostgreSQL as its database. You have two options:

- **Bundled PostgreSQL:** A PostgreSQL container is included in the Docker Compose stack for simple deployments. No additional setup is required.
- **External PostgreSQL:** Connect to your existing PostgreSQL server by configuring the `JIM_DB_HOSTNAME` environment variable.

## For Developers

If you are contributing to JIM or building custom connectors, the development environment is fully self-contained:

- **GitHub Codespaces:** One-click setup with everything pre-configured (.NET 9.0, PostgreSQL, shell aliases, VS Code extensions).
- **Local devcontainer:** Clone the repository and open it in VS Code with the Dev Containers extension. The devcontainer will set up the full environment automatically.

No additional prerequisites are needed when using the devcontainer; it includes all build tools, a PostgreSQL instance, and a bundled Keycloak identity provider for testing.

See the [Developer Guide](../developer/index.md) for the full development setup.
