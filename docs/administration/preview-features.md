---
title: Preview Features
---

# Preview Features

JIM rolls some features out gradually, behind a Preview feature flag, before they are switched on by default. This page explains what Preview means, where to turn a Preview feature on, and what to expect from it.

## What Preview means

A Preview feature is **supported, with a caveat**: it works, but its behaviour may still change, based on feedback, before it is considered final. Nothing about a Preview feature is experimental in the sense of being unstable or unsupported; it is simply not yet locked in. Every Preview feature is off by default, so turning one on is always a deliberate choice, never something that changes under you on an upgrade.

A feature stops being a Preview feature once JIM considers its behaviour settled. At that point the flag is removed and the feature is simply part of JIM; nothing you configured while it was in Preview needs to change.

## Turning a Preview feature on

Preview features live in the **Preview features** card on the [Service Settings](../configuration/service-settings.md) page in the admin portal. The card is always there, even when JIM currently has no Preview features to offer, so you know where to look once one arrives. Each entry shows the feature's name, a short description of what it does, a switch to turn it on or off, and, once you have changed it, when and by whom.

Wherever a Preview feature's own controls appear elsewhere in the portal (a button, a tab, a new field), they carry a small **Preview** label, so you always know when you are looking at a feature that is still being rolled out.

You can also manage Preview features from PowerShell or the REST API:

- **PowerShell**<br /> [`Get-JIMFeature`, `Enable-JIMFeature`, `Disable-JIMFeature`](../powershell/feature-flags.md)
- **REST API**<br /> the Features endpoints in the [interactive API reference](../../api/reference/)

Turning a Preview feature on or off is recorded in its [configuration change history](../configuration/activities.md#configuration-change-history), the same as any other Service Setting change.

## In development features

Some feature flags exist purely for JIM's own development and its integration test harness, and are never shown to administrators, including in the Preview features card in a production deployment. If you see a reference to an "in development" flag in JIM's release notes or issue tracker, it is not something you can or need to turn on yourself; it becomes a Preview feature, or ships outright, when it is ready.

## See also

- [Service Settings](../configuration/service-settings.md) -- how the underlying settings work, including change history and the read-only/configurable distinction
- [Feature Flags PowerShell cmdlets](../powershell/feature-flags.md) -- `Get-JIMFeature`, `Enable-JIMFeature`, `Disable-JIMFeature`
