---
title: Service Settings
---

# Service Settings

Service Settings control runtime behaviour for JIM: instance identity, single sign-on, synchronisation options, maintenance mode, history retention. Each setting has a key (dot notation), a typed value, a default, and an effective value (the override if set, otherwise the default).

> Endpoint reference for this resource is in the [Scalar API reference](../index.md#where-to-find-what). This page covers the model and the operational behaviour.

## Key Concepts

**Default vs override.** Every setting has a built-in default. If you set a value through the API or admin UI, that value takes effect; otherwise the default applies. The `effectiveValue` field tells you which is currently active, and `isOverridden` tells you whether anyone has set an explicit value.

**Read-only settings.** Some settings are mirrored from environment variables (typically the bootstrap settings -- database connection, initial encryption configuration). These are returned by the API for visibility but cannot be changed at runtime; updating them through the API is rejected. Change the underlying environment variable and restart JIM instead. By design, JIM keeps the set of environment-variable-driven settings small and prefers service settings, so this category is small by intent.

**Categories.** Settings are grouped by concern -- `Instance`, `SSO`, `Synchronisation`, `Maintenance`, `History` -- which is mostly a UI grouping. The category does not change semantics.

**Reverting.** Removing the override (i.e. setting `value` back to null so `effectiveValue` reverts to `defaultValue`) is a separate operation from updating, because it conveys intent (return to default) more clearly than an update with a null body would.

**Value types.** Settings are typed (`String`, `Boolean`, `Integer`, `TimeSpan`). The API enforces the type on update.

## Common Workflows

**Looking up a setting:**

1. List service settings to discover the key, current effective value, and whether it's overridden
2. Retrieve the specific setting if you need just one

**Changing a setting:**

1. Update the setting with the new value
2. The change takes effect immediately for new operations; running operations finish under the previous value

**Reverting to default:**

1. Call revert on the setting; `value` returns to null, `effectiveValue` reverts to `defaultValue`

## See also

- [Administration: Configuration Reference](../../administration/configuration.md) -- conceptual overview of which settings exist and what they do
- [PowerShell: Service Settings](../../powershell/service-settings.md) -- cmdlets that wrap these endpoints
