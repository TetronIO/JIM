---
title: Backup & Disaster Recovery
---

# Backup & Disaster Recovery

JIM stores two pieces of state that must be backed up **together as a matched pair**: the PostgreSQL database and the encryption key set. Backing up one without the other leaves you unable to recover.

!!! danger "The database alone is not a recoverable backup"
    Sensitive values in the database (Connected System credentials, the SSO secret, Schedule SQL-step connection strings) are encrypted at rest. They can only be decrypted with the encryption keys that were in use when they were written. Restore a database backup onto a host that does not have the matching keys and **every stored secret becomes permanently undecryptable**. The rest of the data restores fine, but JIM cannot connect to any Connected System until an administrator re-enters every affected secret by hand.

## 🔐 What to back up

| Item | Where it lives | Backup source |
|------|----------------|---------------|
| **Database** | PostgreSQL (`jim.database` bundled container, or your external server) | `pg_dump` / volume snapshot, or your existing DBA tooling |
| **Encryption keys** | `jim-keys-volume` Docker volume, mounted at `/data/keys`; or the path in `JIM_ENCRYPTION_KEY_PATH` | Copy the whole key directory |

These two are a pair. Every backup schedule that captures the database must capture the key set at the same cadence, and a restore must bring back both.

### Why the keys matter

JIM encrypts credentials with [ASP.NET Core Data Protection](https://learn.microsoft.com/aspnet/core/security/data-protection/introduction) (AES-256-GCM). The `jim.web`, `jim.worker`, and `jim.scheduler` services all share the same key set so they can decrypt each other's data. Key facts that shape your backup strategy:

- **Keys are not stored in the database.** They are files on the key volume, deliberately kept separate from the ciphertext they protect.
- **Keys are not themselves encrypted at rest.** This is the correct design for a self-contained, air-gappable product with no external key-management dependency, but it makes the key volume as sensitive as the database. Protect and store its backups accordingly (see [Securing key backups](#securing-key-backups)).
- **The whole key set must be backed up, not just the active key.** Key rotation generates a new active key but retains the previous ones for decryption. A backup that captured only the current key would fail to decrypt older values. Always back up the entire key directory.

## 🗄️ Taking a backup

The commands below assume the bundled PostgreSQL container and the default volume names. Confirm your actual volume name with `docker volume ls | grep keys` (Compose may prefix it with the project name).

### 1. Back up the database

Bundled PostgreSQL:

```bash
docker exec jim.database pg_dump -U jim -d jim -Fc -f /tmp/jim.dump
docker cp jim.database:/tmp/jim.dump ./jim-db-2026-07-09.dump
docker exec jim.database rm /tmp/jim.dump
```

External PostgreSQL: use your existing database backup tooling against the JIM database.

### 2. Back up the encryption keys

```bash
docker run --rm \
  -v jim-keys-volume:/keys:ro \
  -v "$(pwd)":/backup \
  alpine tar czf /backup/jim-keys-2026-07-09.tar.gz -C /keys .
```

If you set `JIM_ENCRYPTION_KEY_PATH` to a bind-mounted host directory instead of using the managed volume, simply back up that directory.

### 3. Store both artefacts together

Keep the database dump and the key archive from the same point in time as a single labelled backup set, so a restore never mixes a database with a mismatched key set.

## ♻️ Restoring {#restoring}

Restore both artefacts from the **same backup set**, then start the services.

1. **Restore the encryption keys first** (or at least before starting `jim.web`/`jim.worker`/`jim.scheduler`), so the services find their keys on first boot:

    ```bash
    docker run --rm \
      -v jim-keys-volume:/keys \
      -v "$(pwd)":/backup \
      alpine sh -c "rm -rf /keys/* && tar xzf /backup/jim-keys-2026-07-09.tar.gz -C /keys"
    ```

2. **Restore the database** from the matching dump (bundled example):

    ```bash
    docker cp ./jim-db-2026-07-09.dump jim.database:/tmp/jim.dump
    docker exec jim.database pg_restore -U jim -d jim --clean --if-exists /tmp/jim.dump
    docker exec jim.database rm /tmp/jim.dump
    ```

3. **Start the stack** and verify:

    ```bash
    docker compose up -d
    ```

4. **Confirm secrets decrypt.** Open a Connected System that uses a password (for example an LDAP Connector) and run an import, or trigger a synchronisation run. Successful connection confirms the keys and database match. A decryption error in the logs ("Failed to decrypt credential") means the key set does not match the database; restore the correct keys before proceeding.

!!! tip "Test your restore"
    A backup you have never restored is a hypothesis, not a backup. Periodically rehearse a full restore (database plus keys) into a scratch environment and confirm a Connected System still connects.

## 🔒 Securing key backups {#securing-key-backups}

Because the keys are unencrypted, anyone with both the database backup and the key backup can decrypt every stored secret. Treat key backups with at least the same care as database backups:

- Store them on encrypted media or encrypt the archive at rest.
- Restrict access to the same principals who may access database backups.
- Prefer keeping the two artefacts in the same protected location so they are governed by one access policy, rather than scattering them.

## If the keys are lost

If you have a valid database backup but no matching keys, the non-secret data is fully recoverable but the encrypted values are not. Recovery means:

1. Restore the database as normal.
2. Re-enter every Connected System secret (service-account passwords, and any other encrypted setting) via the administration UI.
3. Re-set the SSO secret and any other encrypted [Service Settings](../configuration/service-settings.md).
4. Re-enter any Schedule SQL-step connection strings.

There is no way to recover the original secret values without the keys; this is by design.

## ✅ Checklist

- [ ] Database backup scheduled and tested.
- [ ] Encryption key set (`jim-keys-volume` / `JIM_ENCRYPTION_KEY_PATH`) backed up at the same cadence as the database.
- [ ] Database and key backups stored together as a labelled, matched pair.
- [ ] Key backups protected with the same access controls as database backups.
- [ ] Full restore (database plus keys) rehearsed in a scratch environment, with a Connected System confirmed to reconnect.

## Related

- [Deployment Guide](deployment.md) -- volumes, production readiness checklist, air-gapped checklist.
- [Configuration Reference](configuration.md) -- the `JIM_ENCRYPTION_KEY_PATH` variable.
- [Service Settings](../configuration/service-settings.md) -- encrypted settings and how secret changes are recorded.
