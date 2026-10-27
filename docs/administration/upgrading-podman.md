---
title: Upgrading on Podman
description: Upgrade JIM on Podman to a newer release, connected or air-gapped, rootful or rootless, and roll it back if you must.
type: how-to
audience: operator
validated: v0.16.0
---

# Upgrading on Podman

On Podman, an upgrade replaces the pod files, which name the release's images, and restarts JIM. Do the steps in [Before you upgrade](upgrading.md#before-you-upgrade) first. The commands are for the default, rootful installation, run as root; for a rootless one, see [Rootless commands](podman.md#rootless-commands).

## :material-update: Upgrade JIM

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

    Upgrading from v0.16.0 or earlier with the bundled PostgreSQL: its memory settings now come from `jim-config.yaml`, and without them it uses defaults for a 4 GB host, where the earlier pod file asked for 8 GB of `shared_buffers`. On a larger host, add them to `jim-config.yaml`, sized as the installer would (see [Bundled PostgreSQL Memory](configuration.md#bundled-postgresql-memory)).

    Upgrading a rootful JIM from v0.16.0 or earlier, make the host changes [Firewall, SELinux, AppArmor and NetworkManager](podman.md#firewall-selinux-and-apparmor) shows:

    - **firewalld:** enable `netavark-firewalld-reload.service`, or a firewalld reload leaves JIM unable to reach its database until you restart JIM.
    - **RHEL or a derivative:** tell NetworkManager to leave Podman's interfaces alone, or a restart or reboot can leave JIM's database unplugged from its network, with JIM's services logging `No route to host`.
    - **Ubuntu 24.04:** add the AppArmor signal rule, without which JIM's services crash and restart, and in step 4 restart the server instead of the two services. Podman loads the rule only when the server starts.

4. **Start JIM**, restarting the database first so it runs the release's PostgreSQL image:

    ```bash
    systemctl restart jim-database.service
    systemctl start jim.service
    ```

## :material-check-decagram-outline: Check it worked

Check it as [Check it worked](upgrading.md#verifying) on Upgrading describes, with `systemctl status jim-database.service jim.service` in place of `docker compose ps`, and re-enable the Schedules you disabled.

## :material-backup-restore: Rolling back {#rolling-back}

Stop JIM, restore the pre-upgrade backup as [Rolling back](upgrading.md#rolling-back) describes (it explains why both halves go back together), put `jim.yaml.previous` and `jim-database.yaml.previous` back in place of the new files, and start JIM again.

## :material-book-open-variant-outline: Related

- [Upgrading](upgrading.md): what to do before you upgrade, what happens as JIM starts, how to check it worked, and rolling back.
- [Running on Podman](podman.md): the pod files, and rootful or rootless.
- [Backup & Disaster Recovery](backup-recovery.md): what to back up, how, and how to restore it.
