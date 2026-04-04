---
title: API Overview
---

# API Overview

JIM provides a comprehensive REST API that enables programmatic access to all identity management operations. The API is served from the same application as the web UI — there is no separate API service to deploy or manage.

## Base URL

The API is available at `/api/` on your JIM instance. For example:

```
https://jim.example.com/api/v1/connected-systems
```

## API Versioning

JIM uses URL path-based versioning. The current API version is **v1**.

All endpoints are prefixed with `/api/v1/`. Future versions will be introduced under `/api/v2/` etc., with previous versions maintained for a deprecation period.

## Authentication

The API supports two authentication methods:

- **JWT Bearer tokens** — obtained via an OIDC authentication flow, suitable for user-driven integrations and applications that already participate in your organisation's single sign-on infrastructure.
- **API keys** — created and managed via the JIM web UI or the PowerShell module, suitable for service-to-service integrations, scripts, and automated workflows.

For full details and usage examples, see the [Authentication](authentication.md) page.

## Interactive Documentation (Swagger UI)

JIM includes an interactive OpenAPI/Swagger UI, available at:

```
https://jim.example.com/api/swagger
```

The Swagger UI provides:

- A browsable list of all available endpoints
- Request and response schema documentation
- The ability to execute API calls directly from the browser (when authenticated)
- OpenAPI specification export for client code generation

!!! tip
    The Swagger UI is the most up-to-date reference for available endpoints and their parameters. It is generated directly from the running application.

## Further Reading

- [Authentication](authentication.md) — configuring and using JWT Bearer tokens and API keys
- [API Endpoints](endpoints.md) — endpoint reference documentation
- [PowerShell Module](../powershell/index.md) — a cross-platform PowerShell module that wraps the API for scripting and automation
