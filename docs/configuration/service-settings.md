---
title: Service Settings
---

# Service Settings

**Service settings** control runtime behaviour for JIM: instance identity, single sign-on, synchronisation options, maintenance mode, and history retention. Each setting has a key (in dot notation), a typed value, and a default.

## Default vs override

Every setting has a built-in default. If you set a value through the admin UI or API, that value takes effect; otherwise the default applies. Two flags help you reason about state at a glance:

- **Effective value**<br /> The value currently in force (override if set, otherwise default).
- **Is overridden**<br /> Whether anyone has set an explicit value for this setting.

## Read-only settings

Some settings are mirrored from environment variables; typically the bootstrap settings such as the database connection and initial encryption configuration. These are returned by the admin surfaces for visibility but cannot be changed at runtime; updating them is rejected. Change the underlying environment variable and restart JIM instead.

By design, JIM keeps the set of environment-variable-driven settings small and prefers service settings, so this read-only category is intentionally narrow.

## Categories

Settings are grouped by concern:

- **Instance**<br /> Service identity (name, ID) for distinguishing JIM deployments.
- **SSO**<br /> Single sign-on and authentication settings.
- **Synchronisation**<br /> Sync pipeline and change tracking settings.
- **Maintenance**<br /> Maintenance mode and system health settings.
- **History**<br /> Audit history retention and cleanup settings.

The category is mostly a UI grouping; it does not change semantics.

## Reverting

Removing the override (resetting the value back to null so the effective value reverts to the default) is a separate operation from updating. It conveys intent (return to default) more clearly than an update with a null body would, and is also useful for backing out a configuration change without needing to remember the original default.

## Value types

Settings are typed: string, boolean, integer, or timespan. The type is enforced on update.

## Common workflows

**Looking up a setting:**

1. Browse the settings list to find the key, the current effective value, and whether it's overridden
2. Drill into the specific setting if you want to see its description, default, and category

**Changing a setting:**

1. Update the setting with the new value
2. The change takes effect immediately for new operations; in-flight operations finish under the previous value

**Reverting to default:**

1. Revert the setting; the override clears, and the effective value returns to the default

## Manage Service Settings

- **JIM portal**<br /> Service Settings area of the admin UI
- **PowerShell**<br /> [Service Settings cmdlets](../powershell/service-settings.md) (`Get-JIMServiceSetting`, `Set-JIMServiceSetting`, etc.)
- **REST API**<br /> Service Settings endpoints in the [interactive API reference](../api/index.md)

## See also

- [Administration: Configuration Reference](../administration/configuration.md) -- detailed reference for the settings that exist and what each one does
