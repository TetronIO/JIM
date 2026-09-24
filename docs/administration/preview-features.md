---
title: Preview Features
---

# Preview Features

JIM rolls some features out gradually, behind a Preview feature flag, before they are switched on by default. This page explains what Preview means, where to turn a Preview feature on, and what to expect from it.

## What Preview means

A Preview feature is **supported, with a caveat**: it works, but its behaviour may still change, based on feedback, before it is considered final. Nothing about a Preview feature is experimental in the sense of being unstable or unsupported; it is simply not yet locked in. Every Preview feature is off by default, so turning one on is always a deliberate choice, never something that changes under you on an upgrade.

A feature stops being a Preview feature once JIM considers its behaviour settled. At that point the flag is removed and the feature is simply part of JIM; nothing you configured while it was in Preview needs to change.

## Turning a Preview feature on

Preview features are ordinary rows on the [Service Settings](../configuration/service-settings.md) page in the admin portal, under the **Preview Features** category, alongside JIM's other settings. Each row shows the feature's name (carrying a small **Preview** chip), a description, its current value (on or off) and, once you have changed it, when and by whom. Edit and revert it exactly as you would any other setting: use the pencil action to turn it on or off, and the revert action to turn it back off. Use the toolbar's **Show preview features only** checkbox to filter the table down to just these rows.

Wherever a Preview feature's own controls appear elsewhere in the portal (a button, a tab, a new field), they carry the same small **Preview** chip, so you always know when you are looking at a feature that is still being rolled out.

You can also manage Preview features from PowerShell or the REST API:

- **PowerShell**<br /> [`Get-JIMFeature`, `Enable-JIMFeature`, `Disable-JIMFeature`](../powershell/feature-flags.md)
- **REST API**<br /> the Features endpoints in the [interactive API reference](../../api/reference/)

Turning a Preview feature on or off is recorded in its [configuration change history](../configuration/activities.md#configuration-change-history), the same as any other Service Setting change.

## In development features

Some feature flags exist purely for JIM's own development and its integration test harness, and are never shown to administrators: their Service Settings row never appears in a production deployment, whatever category you filter to. If you see a reference to an "in development" flag in JIM's release notes or issue tracker, it is not something you can or need to turn on yourself; it becomes a Preview feature, or ships outright, when it is ready.

## See also

- [Service Settings](../configuration/service-settings.md) -- how the underlying settings work, including change history and the read-only/configurable distinction
- [Feature Flags PowerShell cmdlets](../powershell/feature-flags.md) -- `Get-JIMFeature`, `Enable-JIMFeature`, `Disable-JIMFeature`
