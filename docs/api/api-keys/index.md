---
title: API Keys
---

# API Keys

API keys provide non-interactive authentication for scripts, automation, and service-to-service integrations. The full key is shown only once at creation; after that, only the prefix is available for identification.

## The API Key Object

```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "name": "CI/CD Pipeline",
  "description": "Used by GitHub Actions for automated deployments",
  "keyPrefix": "jim_ak_7",
  "createdAt": "2026-01-15T10:00:00Z",
  "expiresAt": "2026-07-15T10:00:00Z",
  "lastUsedAt": "2026-04-05T08:30:00Z",
  "lastUsedFromIp": "192.168.1.100",
  "isEnabled": true,
  "roles": [
    { "id": 1, "name": "Administrator", "builtIn": true }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `id` | guid | Unique identifier |
| `name` | string | Human-readable name |
| `description` | string, nullable | Optional description |
| `keyPrefix` | string | First characters of the key for identification |
| `createdAt` | datetime | UTC creation timestamp |
| `expiresAt` | datetime, nullable | Expiry date (null = never expires) |
| `lastUsedAt` | datetime, nullable | When the key was last used |
| `lastUsedFromIp` | string, nullable | IP address of last usage |
| `isEnabled` | boolean | Whether the key is currently active |
| `roles` | array | Roles assigned to this key |

## Endpoints

| Endpoint | Description |
|----------|-------------|
| [List API Keys](list.md) | Get all API keys |
| [Retrieve an API Key](retrieve.md) | Get a specific API key by ID |
| [Create an API Key](create.md) | Create a new API key |
| [Update an API Key](update.md) | Update name, roles, expiry, or enabled status |
| [Delete an API Key](delete.md) | Permanently revoke and delete an API key |

!!! warning "Key Security"
    The full API key is returned **only once** at creation. Store it securely; it cannot be retrieved again. If a key is lost, delete it and create a new one. JIM stores only a SHA-256 hash of the key; the plaintext is never persisted.
