---
title: Upgrading
---

# Upgrading

Upgrading JIM means replacing the `jim.web`, `jim.worker` and `jim.scheduler` container images with a newer release. Your data stays where it is: the database and the encryption key volume are not modified by the image swap itself, and any pending database upgrades are applied automatically when the new worker starts.

This page covers connected and air-gapped upgrades, what happens during the upgrade window, and how to roll back.

!!! danger "Take a backup first, and make sure it includes the encryption keys"
    A JIM backup is **two artefacts**: the PostgreSQL database *and* the encryption key set (`jim-keys-volume`, or the path in `JIM_ENCRYPTION_KEY_PATH`). A database backup taken without its matching keys is not a recoverable backup; every stored secret (Connected System credentials, the SSO secret, Schedule SQL-step connection strings) becomes permanently undecryptable when it is restored. If an upgrade goes wrong and you fall back to your pre-upgrade backup, you need both halves. See [Backup & Disaster Recovery](backup-recovery.md).

## 📋 Before you upgrade {#before-you-upgrade}

- [ ] **Read the release notes.** Check the `CHANGELOG.md` for the target release (on the [releases page](https://github.com/TetronIO/JIM/releases), or under `docs/` in an air-gapped bundle) for breaking changes, new configuration variables, and security fixes.
- [ ] **Rehearse in staging** against a copy of production data where practical.
- [ ] **Take a full backup: database plus encryption keys**, as a matched pair, following [Backup & Disaster Recovery](backup-recovery.md). Take it after stopping the services (below) so the two artefacts are consistent with each other.
- [ ] **Disable all Schedules**, and record which ones you disabled. This stops new runs starting under you while you work. See [Pausing work first](#pausing-work).
- [ ] **Let in-flight work finish.** With Schedules disabled, wait for any running Activities to complete before stopping the services. See [Pausing work first](#pausing-work) for what happens if you do not.
- [ ] **Keep the outgoing images.** Do not run `docker image prune` before the new version has been verified; the previous images are your fastest rollback path.
- [ ] **Compare `.env.example` with your `.env`.** New releases can introduce configuration variables. See the [Configuration Reference](configuration.md).
- [ ] **Upgrading from v0.15 or earlier on Docker: put JIM's certificate in place first.** From v0.16.0, `docker-compose.production.yml` serves HTTPS itself, on host port 443 by default, and needs the certificate and its key at `tls/tls.crt` and `tls/tls.key` beside the compose files (see [The Certificate](deployment.md#the-certificate)). Until both exist, `docker compose up` stops with an error naming the missing file and JIM does not start; your data is untouched, so putting the files in place and running it again completes the upgrade. If a reverse proxy on the same host terminates TLS for JIM, use [Plain HTTP for a Proxy on the Same Host](deployment.md#plain-http-for-a-proxy-on-the-same-host) instead, which needs no certificate.

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

## ⬆️ Upgrading with the installer {#with-the-installer}

On Docker, the installer upgrades an installation it made, connected or air-gapped, keeping its settings. Do the steps in [Before you upgrade](#before-you-upgrade) first, then:

=== "Connected"

    ```bash
    # Upgrades to the latest release
    curl -fsSL https://junctional.io/get | sudo bash -s -- --upgrade
    ```

=== "Air-gapped"

    ```bash
    # In the new release's extracted bundle; upgrades to that release
    sha256sum -c checksums.sha256
    sudo ./setup.sh --upgrade
    ```

    Check the bundle when you download it, before carrying it to the server: see [Checking the Download](deployment.md#checking-the-download).

It upgrades `/opt/jim`, or the folder `JIM_INSTALL_DIR` names. In order, it:

1. Stops if the new release is not newer than the one JIM runs, and asks you to confirm the database and encryption keys are backed up (set `JIM_SETUP_BACKUP_CONFIRMED=true` to answer in advance; a "no" cancels the upgrade).
2. Stops if `docker-compose.yml` or `docker-compose.production.yml` has been edited since it installed them, because it replaces them. Put changes of your own in a compose file of your own beside them, such as `docker-compose.local.yml`, and start JIM once with it added (`-f docker-compose.local.yml`): the upgrade starts JIM with every compose file it was last started with. Nothing changes when it stops at this step or the one before. It records the files it installs in `compose-files.sha256`; an installation made before it kept that record is upgraded with a warning instead.
3. Installs the release's compose files, keeping the previous ones and `.env` beside them as `docker-compose.yml.previous`, `docker-compose.production.yml.previous` and `.env.previous`, sets `JIM_VERSION`, and saves the release's `.env.example` beside `.env`, naming any setting it adds that your `.env` does not mention.
4. Loads the release's images from the bundle, or downloads them, while the current version keeps running. With the bundled PostgreSQL, it moves JIM onto the release's PostgreSQL image, checking it against the ID the bundle records where Docker's classic image store needs `JIM_DB_IMAGE`.
5. Restarts JIM on the new version, which applies any database upgrade as it starts, and waits until JIM is ready. It keeps the release's copy of `setup.sh` in the installation.

Anything that fails before the restart puts the previous compose files and `.env` back, leaving JIM running as it was. Then carry on at [Verifying the upgrade](#verifying).

## 🔄 Upgrading a connected deployment

The same upgrade by hand. On Podman, see [Upgrading on Podman](#upgrading-on-podman).

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

The same upgrade as [the installer's](#with-the-installer), by hand. The procedure mirrors a first-time air-gapped deployment, minus the initial configuration steps.

1. **Check, transfer and verify the new release bundle** via your approved process. Check the download first, where you downloaded it with its checksum file (see [Checking the Download](deployment.md#checking-the-download)), then extract and verify it on the JIM server:

    ```bash
    # Where you downloaded it, before transferring it
    sha256sum -c jim-release-0.14.0.tar.gz.sha256

    # On the JIM server
    tar -xzf jim-release-0.14.0.tar.gz
    cd jim-release-0.14.0
    sha256sum -c checksums.sha256
    ```

2. **Stop the services** and **take your backup**, as in steps 1 and 2 above.

3. **Load the new images**, PostgreSQL's included: a release can move the bundled database to a newer PostgreSQL 18 image, and its compose file then names that image, which only the bundle can supply.

    ```bash
    for f in docker-images/*.tar; do docker load -i "$f"; done
    ```

4. **Reconcile the compose files.** The bundle ships its own `compose/` directory. Diff it against your deployed copies rather than overwriting them, so local customisations (volumes, ports, reverse-proxy wiring) survive, and check `compose/.env.example` for new variables. Copy the bundle's `setup.sh` over the installation's copy (`/opt/jim/setup.sh` by default), so that looking after JIM uses the new release's installer.

    If your `.env` sets `JIM_DB_IMAGE` (the installer sets it on Docker's classic image store, which cannot find the bundled PostgreSQL by its pinned digest), point it at the new bundle's PostgreSQL image, after checking that image's ID is the one the bundle records. Otherwise JIM carries on running the previous release's PostgreSQL.

    ```bash
    # In the extracted bundle
    image=$(docker load -i docker-images/postgres-18.tar | sed -n 's/^Loaded image: //p')
    id=$(docker image inspect -f '{{.Id}}' "$image")
    grep -qxF "$id" docker-images/postgres-18.image-ids \
      && sed -i "s|^JIM_DB_IMAGE=.*|JIM_DB_IMAGE=$id|" /opt/jim/.env \
      || echo "Not the PostgreSQL image this bundle ships: check it with sha256sum -c checksums.sha256"
    ```

5. **Pin the new version** in `.env` (`JIM_VERSION=0.14.0`) and start the services, using the same `-f` files and `--profile` flags you deployed with:

    ```bash
    docker compose -f docker-compose.yml -f docker-compose.production.yml \
      --profile with-db up -d --pull never
    ```

    `--pull never` makes Docker report an image the bundle did not load, rather than try the internet.

6. **Verify**, per [Verifying the upgrade](#verifying) below.

## 🦭 Upgrading on Podman {#upgrading-on-podman}

On Podman, an upgrade replaces the pod files, which name the release's images, and restarts JIM. The commands are for the default, rootful installation, run as root; for a rootless one, see [Rootless commands](podman.md#rootless-commands).

1. **Stop JIM**, leaving the bundled database running, and **take your backup** per [Backup & Disaster Recovery](backup-recovery.md):

    ```bash
    systemctl stop jim.service
    ```

2. **Keep the current pod files**, for rolling back:

    ```bash
    cd /opt/jim
    cp jim.yaml jim.yaml.previous && cp jim-database.yaml jim-database.yaml.previous
    ```

3. **Put the new release's files in place, and its images.** Connected, download the pod files from the release and pull the images they name:

    ```bash
    cd /opt/jim
    for f in jim.yaml jim-database.yaml setup.sh; do
      curl -fsSLO "https://github.com/TetronIO/JIM/releases/download/v0.14.0/$f"
    done
    chmod 755 setup.sh
    for image in $(awk '$1 == "image:" { print $2 }' jim.yaml jim-database.yaml); do podman pull "$image"; done
    ```

    Air-gapped, from the extracted bundle, load the images and copy the files:

    ```bash
    for f in docker-images/*.tar; do podman load < "$f"; done
    cp podman/jim.yaml podman/jim-database.yaml setup.sh /opt/jim/
    ```

    If you tuned PostgreSQL in `jim-database.yaml`, carry your settings into the new copy rather than losing them. Compare the release's `jim-config.yaml` with yours for new settings.

4. **Start JIM**, restarting the database first so it runs the release's PostgreSQL image:

    ```bash
    systemctl restart jim-database.service
    systemctl start jim.service
    ```

5. **Verify**, per [Verifying the upgrade](#verifying) below.

To roll back on Podman, stop JIM, restore the pre-upgrade backup (see [Rolling back](#rolling-back)), put `jim.yaml.previous` and `jim-database.yaml.previous` back in place of the new files, and start JIM again.

## ⚙️ What happens during the upgrade window

Understanding the startup sequence explains why the web interface is briefly unavailable after an upgrade:

- **The worker leads.** `jim.worker` is the first service to initialise. It applies any database upgrades automatically.
- **Built-in configuration catches up.** Immediately after the database upgrade, the worker brings your instance's built-in configuration into line with what the release ships, creating anything it does not already hold: Metaverse Object Types and Attributes, Predefined Searches, Connectors, Example Data Sets, built-in Schedules and Roles, and Service Settings. Anything already present is left exactly as it is, including settings you have changed, so the pass records nothing on an instance that is already up to date. Where it does create something, it appears in the change history as a **System Initialisation** Activity with each new object beneath it. This takes well under a second on a converged instance.
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

## ↩️ Rolling back {#rolling-back}

Rolling back means putting **both** halves of JIM back to their pre-upgrade state: the application version, and the database it runs against. A release can upgrade the database, leaving the schema ahead of what the older version understands, so reverting the images alone is not a rollback. This is what the pre-upgrade backup is for.

1. **Stop the services:**

    ```bash
    docker compose stop jim.web jim.worker jim.scheduler
    ```

2. **Restore your pre-upgrade backup**, database and encryption keys together from the same backup set, following the restore procedure in [Backup & Disaster Recovery](backup-recovery.md#restoring).

3. **Point `JIM_VERSION` back** at the previous release in `.env`:

    ```bash
    JIM_VERSION=0.13.0
    ```

    After an upgrade by the installer, put its `.previous` files back instead, which also returns the compose files and any `JIM_DB_IMAGE` to the previous release's:

    ```bash
    cd /opt/jim
    for f in docker-compose.yml docker-compose.production.yml .env; do cp -p "$f.previous" "$f"; done
    ```

4. **Start the services:**

    ```bash
    docker compose up -d
    ```

5. **Verify** as you would after an upgrade, per [Verifying the upgrade](#verifying), and re-enable your Schedules.

!!! danger "Do not run an older JIM against an upgraded database"
    Starting the previous version without restoring the database leaves the older application reading a schema built for the newer one. Always restore the backup first. If you have no usable pre-upgrade backup, do not roll back at all: stay on the new version and resolve the problem there, because an older JIM against a newer schema fails in subtle ways rather than refusing to start.

!!! tip "Roll back promptly, or not at all"
    A rollback discards everything JIM has written since the upgrade. If the upgraded instance has been synchronising for hours, restoring the pre-upgrade backup rolls the connector space back with it, and the next run will re-evaluate a large amount of drift. Decide quickly, and prefer fixing forwards once real synchronisation work has happened on the new version.

## ✅ Upgrade checklist

- [ ] Release notes reviewed for breaking changes and new configuration.
- [ ] Upgrade rehearsed in staging.
- [ ] Enabled Schedules recorded, then disabled.
- [ ] Running Activities allowed to complete.
- [ ] Services stopped, then database **and** encryption keys backed up as a matched pair.
- [ ] Previous images retained for rollback.
- [ ] `.env` reconciled against the new `.env.example` (the installer's upgrade names new settings for you), or on Podman `jim-config.yaml` against the release's copy.
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
