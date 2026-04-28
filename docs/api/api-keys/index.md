---
title: API Keys
---

# API Keys

API keys provide non-interactive authentication for scripts, automation, and service-to-service integrations. Each key is associated with one or more roles that determine what it can do.

> Endpoint reference for this resource is in the [interactive API reference](../index.md#where-to-find-what). This page covers the model and the operational concerns.

## Key Concepts

**One-time disclosure.** The full secret value of an API key is returned **only once**, in the response to the create call. After that, only the key prefix is visible. JIM stores only a SHA-256 hash of the key; the plaintext is never persisted, and there is no way to recover it.

**Prefix.** Every key begins with `jim_` for easy identification in logs and configuration files, followed by a random secret. Only the prefix is shown after creation.

**Expiry.** Keys can be created with an absolute expiry date or with no expiry. Expired keys are rejected automatically.

**Enabled flag.** A key can be temporarily disabled without deleting it; this revokes its access immediately while preserving its history. Re-enabling restores access.

**Roles.** A key carries the permissions of its assigned roles. Almost all endpoints currently require the Administrator role; future role granularity will let you mint narrower keys.

**Last-used tracking.** JIM records the timestamp and source IP of each successful authentication, which is useful for spotting unused keys and unexpected callers.

## Common Workflows

**Issuing a key for a CI/CD integration:**

1. Create the key with a meaningful name (e.g. "GitHub Actions deploy"), an expiry consistent with your rotation policy, and the role(s) it needs
2. Capture the full key from the create response and store it in your secret manager immediately; you will not be able to retrieve it again
3. Configure the integration to send the key in the `X-Api-Key` header on every request

**Rotating a key:**

1. Create a new key with the same role(s) and a future expiry
2. Roll the new key out to the consuming integration
3. Once you've confirmed the new key is in use (check `lastUsedAt`), delete the old one

**Suspending a key after a suspected leak:**

1. Disable the key immediately (faster than deletion if you might need to inspect history)
2. Investigate
3. Either re-enable, or delete and rotate

## See also

- [Authentication](../authentication.md) -- how keys are presented on requests, plus the alternative JWT Bearer flow
- [PowerShell: API Keys](../../powershell/api-keys.md) -- cmdlets that wrap these endpoints
