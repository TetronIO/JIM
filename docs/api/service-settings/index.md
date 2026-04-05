---
title: Service Settings
---

# Service Settings

Service Settings control runtime behaviour for JIM, such as SSO configuration, synchronisation options, maintenance mode, and history retention. Each setting has a key (dot notation), a typed value, and a default. Settings can be overridden through the API or admin UI; some are read-only when mirrored from environment variables.

## The Service Setting Object

```json
{
  "key": "ChangeTracking.CsoChanges.Enabled",
  "displayName": "CSO Change Tracking",
  "description": "Controls whether connector space object changes are recorded for audit purposes",
  "category": "Synchronisation",
  "valueType": "Boolean",
  "defaultValue": "true",
  "value": null,
  "effectiveValue": "true",
  "isReadOnly": false,
  "isOverridden": false
}
```

| Field | Type | Description |
|-------|------|-------------|
| `key` | string | Unique setting key using dot notation |
| `displayName` | string | Human-readable name |
| `description` | string, nullable | Description of what this setting controls |
| `category` | string | Grouping category: `SSO`, `Synchronisation`, `Maintenance`, or `History` |
| `valueType` | string | Data type: `String`, `Boolean`, `Integer`, or `TimeSpan` |
| `defaultValue` | string, nullable | The default value |
| `value` | string, nullable | The overridden value, or null if using the default |
| `effectiveValue` | string, nullable | The active value (override if set, otherwise default) |
| `isReadOnly` | boolean | Whether this setting is read-only (mirrored from environment variables) |
| `isOverridden` | boolean | Whether the current value differs from the default |

## Categories

| Category | Description |
|----------|-------------|
| `SSO` | Single sign-on and authentication settings |
| `Synchronisation` | Sync pipeline and change tracking settings |
| `Maintenance` | Maintenance mode and system health settings |
| `History` | Audit history retention and cleanup settings |

## Endpoints

| Endpoint | Description |
|----------|-------------|
| [List Service Settings](list.md) | Get all service settings |
| [Retrieve a Service Setting](retrieve.md) | Get a specific setting by key |
| [Update a Service Setting](update.md) | Change a setting value |
| [Revert a Service Setting](revert.md) | Reset a setting to its default value |
