---
title: API Keys
---

# API Keys

**API keys** provide non-interactive authentication for scripts, automation, and service-to-service integrations. Each key is associated with one or more [roles](roles.md) that determine what it can do.

## One-time disclosure

The full secret value of an API key is returned **only once**, at the moment the key is created. After that, only the key prefix is visible. JIM stores only a SHA-256 hash of the key; the plaintext is never persisted, and there is no way to recover it.

If you lose the secret, you must delete the key and create a new one.

## Prefix

Every key begins with `jim_` for easy identification in logs and configuration files, followed by a random secret. Only the prefix is shown after creation, so you can identify which key is in use without exposing its secret.

## Expiry

Keys can be created with an absolute expiry date or with no expiry. Expired keys are rejected automatically and need to be replaced.

For production integrations, prefer keys with an expiry that aligns with your rotation policy (e.g. 90 or 180 days), so rotation is forced rather than relying on operational discipline.

## Enabled flag

A key can be temporarily disabled without deleting it. Disabling revokes its access immediately while preserving its history (creation, last use, source IP). Re-enabling restores access. Use this when you want to suspend a key while you investigate, without losing the audit trail.

## Roles

A key carries the permissions of its assigned roles. Almost all endpoints currently require the Administrator role; future role granularity will let you mint narrower keys for specific integrations.

## Last-used tracking

JIM records the timestamp and source IP of each successful authentication. This is useful for spotting unused keys (good rotation candidates) and unexpected callers (potential compromise indicators).

## Common workflows

**Issuing a key for a CI/CD integration:**

1. Create the key with a meaningful name (for example "GitHub Actions deploy"), an expiry consistent with your rotation policy, and the role(s) it needs
2. Capture the full key from the create response and store it in your secret manager immediately; you will not be able to retrieve it again
3. Configure the integration to send the key in the `X-Api-Key` header on every request

**Rotating a key:**

1. Create a new key with the same role(s) and a future expiry
2. Roll the new key out to the consuming integration
3. Once you've confirmed the new key is in use (check `Last Used At`), delete the old one

**Suspending a key after a suspected leak:**

1. Disable the key immediately (faster than deletion if you might need to inspect history)
2. Investigate
3. Either re-enable, or delete and rotate

## Manage API Keys

- **JIM portal**<br /> API Keys area of the admin UI
- **PowerShell**<br /> [API Keys cmdlets](../powershell/api-keys.md) (`Get-JIMApiKey`, `New-JIMApiKey`, `Set-JIMApiKey`, etc.)
- **REST API**<br /> API Keys endpoints in the [interactive API reference](../../api/reference/)

## See also

- [API Authentication](../api/authentication.md) -- how keys are presented on requests, plus the alternative JWT Bearer flow
- [Roles](roles.md) -- the permission grants assigned to a key
