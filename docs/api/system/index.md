---
title: System
---

# System

System endpoints expose health checks, application version, OIDC client discovery configuration, and information about the currently authenticated user. Most of these endpoints are anonymous: deliberately so, because they are designed to be called by load balancers, container orchestrators, and unauthenticated client bootstraps.

> Endpoint reference for this resource (status payloads, OIDC config fields, user-info schema) is in the [Scalar API reference](../index.md#where-to-find-what). This page covers what each endpoint is for and when to use it.

## Key Concepts

**Health, readiness, liveness.** Three separate endpoints, each meaningful to a different consumer:

- **Health** -- the simplest signal: "the application is running". Use when all you need is a binary up/down.
- **Readiness** -- "the application is up *and* ready to accept requests". Verifies database connectivity and checks maintenance mode. Use this as the readiness probe for orchestrators (Kubernetes, ECS, etc.) and load balancers; this is the right endpoint for routing decisions.
- **Liveness** -- "the process itself is alive and not deadlocked". Use as a Kubernetes liveness probe to detect a wedged process that needs to be restarted.

The distinction matters: a ready/healthy check that fails should take a node out of rotation; a liveness check that fails should restart it.

**Version.** Returns the deployed JIM version. Useful for compatibility checks in client libraries, deployment pipelines, and support tooling.

**Auth Config.** Returns the OIDC discovery configuration the JIM web UI and any custom client integrations need to initiate the authorisation code + PKCE flow against your configured identity provider. This endpoint is anonymous so that clients can bootstrap without needing to be authenticated first.

**User Info.** Returns information about the currently authenticated principal -- their roles, whether they hold an admin role, and (for OIDC users) the metaverse object they are mapped to. Authenticated but unauthorised callers (e.g. an OIDC user with no matching metaverse identity) get a structured response explaining the situation rather than a hard error.

## Anonymous vs authenticated

| Endpoint | Auth required |
|----------|---------------|
| Health | No |
| Readiness | No |
| Liveness | No |
| Version | No |
| Auth Config | No |
| User Info | Yes (any authenticated principal; no role required) |

The anonymous endpoints expose nothing sensitive; they are safe to surface to a load balancer or to your monitoring infrastructure.

## See also

- [Authentication](../authentication.md) -- how to obtain credentials for authenticated calls
- [Administration: SSO Setup](../../administration/sso-setup.md) -- configuring the OIDC provider that auth-config advertises
- [PowerShell: System](../../powershell/system.md) -- cmdlets that wrap these endpoints
