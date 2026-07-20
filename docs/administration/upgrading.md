---
title: Upgrading
---

# Upgrading

Upgrading JIM means replacing the `jim.web`, `jim.worker` and `jim.scheduler` container images with a newer release. Your data stays where it is: the database and the encryption key volume are not modified by the image swap itself, and any pending database upgrades are applied automatically when the new worker starts.

This page covers connected and air-gapped upgrades, what happens during the upgrade window, and how to roll back.

!!! danger "Take a backup first, and make sure it includes the encryption keys"
    A JIM backup is **two artefacts**: the PostgreSQL database *and* the encryption key set (`jim-keys-volume`, or the path in `JIM_ENCRYPTION_KEY_PATH`). A database backup taken without its matching keys is not a recoverable backup; every stored secret (Connected System credentials, the SSO secret, Schedule SQL-step connection strings) becomes permanently undecryptable when it is restored. If an upgrade goes wrong and you fall back to your pre-upgrade backup, you need both halves. See [Backup & Disaster Recovery](backup-recovery.md).

## 📋 Before you upgrade

- [ ] **Read the release notes.** Check the `CHANGELOG.md` for the target release (on the [releases page](https://github.com/TetronIO/JIM/releases), or under `docs/` in an air-gapped bundle) for breaking changes, new configuration variables, and security fixes.
- [ ] **Rehearse in staging** against a copy of production data where practical.
- [ ] **Take a full backup: database plus encryption keys**, as a matched pair, following [Backup & Disaster Recovery](backup-recovery.md). Take it after stopping the services (below) so the two artefacts are consistent with each other.
- [ ] **Disable all Schedules**, and record which ones you disabled. This stops new runs starting under you while you work. See [Pausing work first](#pausing-work).
- [ ] **Let in-flight work finish.** With Schedules disabled, wait for any running Activities to complete before stopping the services. See [Pausing work first](#pausing-work) for what happens if you do not.
- [ ] **Keep the outgoing images.** Do not run `docker image prune` before the new version has been verified; the previous images are your fastest rollback path.
- [ ] **Compare `.env.example` with your `.env`.** New releases can introduce configuration variables. See the [Configuration Reference](configuration.md).

### Pausing work first {#pausing-work}

Bring the system to a standstill in two steps, in this order.

**1. Disable all Schedules.** A Schedule that fires while you are mid-upgrade starts a Run Profile execution you are about to interrupt. Disabling stops the trigger without touching the definition, so nothing is lost. Built-in Schedules can be disabled too; they cannot be deleted, but disabling them is supported.

Do this from **Configuration > Schedules** in the administration UI, or from PowerShell. Capture the list of Schedules that were enabled *before* you disable them, so you can re-enable exactly those afterwards:

```powershell
# Record what is currently enabled, then disable it
$wasEnabled = Get-JIMSchedule | Where-Object { $_.IsEnabled }
$wasEnabled | Select-Object Id, Name | Export-Csv ./schedules-enabled-before-upgrade.csv -NoTypeInformation
$wasEnabled | Disable-JIMSchedule -ChangeReason "Paused for upgrade to 0.14.0"
```

!!! warning "Record the list, do not re-enable everything afterwards"
    Some Schedules are likely to have been deliberately disabled long before the upgrade. If you skip the record and simply enable everything at the end, you will switch those back on and they will start synchronising. Keep the list.

**2. Let running Activities finish.** With no new runs starting, wait for in-flight work to drain. Check **Activities** in the administration UI and wait until nothing is In Progress. Large imports or synchronisation runs can take a while; this wait is the main reason to schedule an upgrade window rather than upgrading on demand.

If you do stop the worker part-way through a Run Profile execution anyway, the work is not silently lost, but the run does not resume where it left off. On restart the worker detects the abandoned task, fails its Activity with an explanatory error, and removes the task from the queue. The failed Activity is a record of the interruption, not a sign of data corruption: JIM's synchronisation state lives in the database, so the next run continues from the current state.

Draining properly therefore avoids noise in your Activity history and a needlessly long first run afterwards, but an interrupted upgrade does not put your data at risk.

## 🔄 Upgrading a connected deployment

1. **Stop the services**, leaving the database running if it is the bundled container:

    ```bash
    docker compose stop jim.web jim.worker jim.scheduler
    ```

2. **Take your backup** (database plus encryption keys) per [Backup & Disaster Recovery](backup-recovery.md).

3. **Pin the new version** in `.env`:

    ```bash
    # Previously JIM_VERSION=0.13.0
    JIM_VERSION=0.14.0
    ```

4. **Pull the new images:**

    ```bash
    docker compose pull
    ```

5. **Start the services**, using the same `-f` overrides and `--profile` flags you normally deploy with (for example `--profile with-db` for the bundled PostgreSQL, and `-f docker-compose.yml -f docker-compose.production.yml` in production):

    ```bash
    docker compose up -d
    ```

6. **Verify**, per [Verifying the upgrade](#verifying) below.

## 📦 Upgrading an air-gapped deployment

The procedure mirrors a first-time air-gapped deployment, minus the initial configuration steps.

1. **Transfer and verify the new release bundle** via your approved process:

    ```bash
    tar -xzf jim-release-0.14.0.tar.gz
    cd jim-release-0.14.0
    sha256sum -c checksums.sha256
    ```

2. **Stop the services** and **take your backup**, as in steps 1 and 2 above.

3. **Load the new images:**

    ```bash
    docker load -i docker-images/jim-web.tar
    docker load -i docker-images/jim-worker.tar
    docker load -i docker-images/jim-scheduler.tar
    ```

4. **Reconcile the compose files.** The bundle ships its own `compose/` directory. Diff it against your deployed copies rather than overwriting them, so local customisations (volumes, ports, reverse-proxy wiring) survive, and check `compose/.env.example` for new variables.

5. **Pin the new version** in `.env` (`JIM_VERSION=0.14.0`) and start the services:

    ```bash
    docker compose up -d
    ```

6. **Verify**, per [Verifying the upgrade](#verifying) below.

## ⚙️ What happens during the upgrade window

Understanding the startup sequence explains why the web interface is briefly unavailable after an upgrade:

- **The worker leads.** `jim.worker` is the first service to initialise. It applies any database upgrades automatically.
- **The web and scheduler wait.** `jim.web` and `jim.scheduler` poll the application's readiness state and do not begin serving until the database upgrade has completed and JIM has left maintenance mode. `jim.web` logs `JIM.Application is not ready yet. Sleeping...` once per second while it waits.
- **Readiness is externally observable.** `GET /api/v1/health/ready` returns `503 Service Unavailable` with `"status": "not_ready"` throughout, then `200 OK` with `"status": "ready"` once JIM is serving.

How long this takes depends on what the release changes in the database. Most upgrades are near-instant; one that rewrites a large table scales with your object count, which is one reason to time the upgrade against a production-sized staging environment first.

!!! warning "If the database upgrade fails"
    The worker logs the full error and JIM stays unready, so the web interface never opens up. Fix the underlying cause (a permissions problem on the database user is the usual culprit) and restart the services. Do not restore a backup unless the upgrade has left the schema in a state you cannot move forwards from.

## ✅ Verifying the upgrade {#verifying}

1. **All services are healthy:**

    ```bash
    docker compose ps
    ```

2. **The new version is running:**

    ```bash
    curl -s https://jim.your-domain.local/api/v1/health/version
    ```

    Or from PowerShell:

    ```powershell
    Get-JIMVersion -Url "https://jim.your-domain.local"
    ```

3. **JIM reports itself ready:**

    ```powershell
    Get-JIMHealth -Url "https://jim.your-domain.local" -Ready
    ```

4. **Secrets still decrypt.** Run an import against a Connected System that authenticates with a stored credential. A successful connection confirms the services found the encryption key volume and can decrypt what is in the database. A "Failed to decrypt credential" error means the key volume did not come across; stop and resolve that before running any synchronisation.

5. **Re-enable the Schedules you disabled.** Only once the checks above pass. Use the list you recorded in [Pausing work first](#pausing-work), so Schedules that were already disabled before the upgrade stay that way:

    ```powershell
    Import-Csv ./schedules-enabled-before-upgrade.csv |
        Enable-JIMSchedule -ChangeReason "Re-enabled after upgrade to 0.14.0"
    ```

    Or re-enable them individually from **Configuration > Schedules** in the administration UI.

6. **Scheduled work resumes.** Confirm the Scheduler is picking up the re-enabled Schedules, that each shows a sensible next run time, and that the next expected run actually fires.

!!! danger "An upgrade is not finished until the Schedules are back on"
    Leaving Schedules disabled is a silent failure mode: JIM appears healthy, the version check passes, and nothing synchronises. Drift accumulates unnoticed until someone reports stale data days later. Make re-enabling them an explicit, checked-off step, not something you intend to do later.

## 🧩 Upgrading the PowerShell module

The JIM PowerShell module versions in step with the product. Upgrade it wherever it is installed so that automation matches the API it is calling.

```powershell
# Connected environments
Update-Module -Name JIM

# Air-gapped: copy from the new release bundle
Copy-Item -Recurse -Force ./powershell/JIM "$env:USERPROFILE\Documents\PowerShell\Modules\"
```

See [PowerShell Module](deployment.md#powershell-module) in the Deployment Guide for the full installation options.

## ↩️ Rolling back

Docker images are immutable, so reverting the application is fast: point `JIM_VERSION` back at the previous release and restart.

```bash
# In .env
JIM_VERSION=0.13.0
```

```bash
docker compose up -d
```

The complication is the database. If the release upgraded the database, the schema is now ahead of the older application version:

- **If the migrations are reversible**, roll the schema back to the migration that was active before the upgrade:

    ```bash
    docker compose exec jim.web dotnet ef migrations list
    docker compose exec jim.web dotnet ef database update <PreviousMigrationName>
    ```

- **If they are not reversible**, restore from your pre-upgrade backup instead: both the database *and* the encryption key set, from the same backup set. This is the scenario the pre-upgrade backup exists for.

!!! tip "Roll back promptly, or not at all"
    A rollback discards everything JIM has written since the upgrade. If the upgraded instance has been synchronising for hours, restoring a pre-upgrade backup rolls the connector space back with it, and the next run will re-evaluate a large amount of drift. Decide quickly, and prefer fixing forwards once real synchronisation work has happened on the new version.

## ✅ Upgrade checklist

- [ ] Release notes reviewed for breaking changes and new configuration.
- [ ] Upgrade rehearsed in staging.
- [ ] Enabled Schedules recorded, then disabled.
- [ ] Running Activities allowed to complete.
- [ ] Services stopped, then database **and** encryption keys backed up as a matched pair.
- [ ] Previous images retained for rollback.
- [ ] `.env` reconciled against the new `.env.example`.
- [ ] New version confirmed via `/api/v1/health/version`.
- [ ] Readiness confirmed via `/api/v1/health/ready`.
- [ ] A Connected System with a stored credential confirmed to connect (proves the keys survived).
- [ ] **Previously-enabled Schedules re-enabled**, and the next run confirmed to fire.
- [ ] PowerShell module upgraded to match.

## Related

- [Backup & Disaster Recovery](backup-recovery.md) -- what to back up, how, and how to restore it.
- [Deployment Guide](deployment.md) -- volumes, compose profiles, production readiness checklist.
- [Configuration Reference](configuration.md) -- environment variables, including `JIM_VERSION`.
- [Troubleshooting](troubleshooting.md) -- diagnosing startup and connectivity problems.
