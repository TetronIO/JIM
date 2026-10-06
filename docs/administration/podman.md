---
title: Running on Podman
---

# Running JIM on Podman

JIM runs on Podman as well as Docker, from the same images, with the same settings and the same hardening. Podman is the container engine Red Hat ships and supports on RHEL 8, 9 and 10, so this is the way to run JIM where Docker is not permitted. Podman needs no extra software: no Docker engine, no compose tool.

The [installer](deployment.md#what-the-installer-does) sets everything on this page up for you. Read on to understand what it sets up, to operate JIM day to day, or to install by hand.

## Requirements

- **Podman 4.4 or later, with systemd.** RHEL 9 and 10 ship Podman 5. systemd starts JIM at boot through Podman's Quadlet, which arrived in Podman 4.4. Without systemd JIM still runs, but does not start by itself after a reboot (see [Without systemd](#without-systemd)).
- Everything else in the [Deployment Guide's prerequisites](deployment.md#prerequisites): hardware, OpenSSL, an OIDC identity provider.

## How JIM Runs on Podman

JIM runs as two pods on a network named `jim`:

| Pod            | Containers                                  | systemd unit           |
|----------------|---------------------------------------------|------------------------|
| `jim`          | `jim-web`, `jim-worker`, `jim-scheduler`    | `jim.service`          |
| `jim-database` | `jim-database-postgres` (optional)          | `jim-database.service` |

JIM reaches the bundled PostgreSQL at the host name `jim-database`. With your own PostgreSQL server, there is no database pod and no `jim-database.service`.

The pods are Kubernetes-style YAML files, which Podman runs with its built-in `podman kube play`. They are not Kubernetes manifests: JIM does not support Kubernetes or OpenShift.

| File | What it holds | Yours to edit? |
|------|---------------|----------------|
| `/opt/jim/jim.yaml` | The JIM pod | No: an upgrade replaces it |
| `/opt/jim/jim-database.yaml` | The PostgreSQL pod, and its tuning; its memory settings come from `jim-config.yaml` (see [Bundled PostgreSQL Memory](configuration.md#bundled-postgresql-memory)) | Only to tune PostgreSQL beyond its memory |
| `/opt/jim/jim-config.yaml` | JIM's settings, with the same names as the Docker path's `.env` (see the [Configuration Reference](configuration.md#podman)) | Yes |
| `/opt/jim/tls/` | JIM's certificate and key, and the installer's certificate authority | Through `setup.sh --certificate` |
| `jim.kube`, `jim-database.kube`, `jim.network` | The Quadlet units systemd runs the pods from, including the HTTPS port (`PublishPort=443:8443` in `jim.kube`) | The port only |

The Quadlet units are in `/etc/containers/systemd/`, or in `~jim/.config/containers/systemd/` for a [rootless](#rootful-or-rootless) installation.

JIM's secrets are not in any of these files. The database password, your identity provider's client secret and the optional infrastructure API key are the Podman secret `jim-secrets`; JIM's certificate and key are the Podman secret `jim-tls`. Podman keeps secrets in its secret store, readable only by root (or, rootless, by the account that runs JIM): the same exposure as the Docker path's `.env`, which only root can read.

JIM's data is in named volumes with the same names as on Docker: `jim-db-volume`, `jim-logs-volume`, `jim-keys-volume` and `jim-connector-files-volume`. You also see a volume named `jim-tls`, which Podman fills from the secret of the same name each time JIM starts.

## Rootful or Rootless

By default the installer runs JIM **rootful**: Podman runs as root, as Docker does. JIM's containers still run as a non-root user, with every capability dropped and a read-only root file system, exactly as on Docker.

To run JIM **rootless** instead, pass `--rootless` to the installer. JIM then runs under a dedicated account named `jim`, which the installer creates with no login shell, a range of subordinate user IDs for its containers, and lingering enabled, so that the account's own systemd manager runs JIM with nobody logged in and starts it at boot. Rootless adds a second boundary: a process that escaped a container would land in an account that owns nothing else on the server. Some security policies require it.

Choose on these differences:

| | Rootful (default) | Rootless |
|---|---|---|
| The client address JIM records, for audit events and for rate-limiting unauthenticated requests | ✅ Each client's own address | ❌ One internal address for every client: Podman's rootless port forwarding does not pass on the client's address |
| A process escaping a container | Is JIM's container user, or root if it escapes that too | Is the unprivileged `jim` account |
| Port 443 | Nothing needed | The installer lets unprivileged programs use ports from 443 up (`net.ipv4.ip_unprivileged_port_start=443` in `/etc/sysctl.d/90-jim.conf`) |
| Operating JIM | As root | Through the `jim` account (see [Rootless commands](#rootless-commands)) |
| File Connector folders on the host | Owned by UID `1654`, as on Docker | Owned by a subordinate ID of `jim` (see [File Access](../connectors/jim-file-connector.md#file-access)) |

!!! warning "Rootless JIM cannot tell clients apart"
    Every request to a rootless JIM appears to come from one internal address. Its security audit events then record that address rather than the client's, and unauthenticated API requests from every client share one rate limit. Do not set `JIM_TRUSTED_PROXIES` to that address as a workaround: every client shares it, so any client could then claim any address. If your audit requirements need each client's address, keep the default, rootful.

The choice is made at installation. A reinstall keeps it, since JIM's images, secrets and data are in the Podman storage of the account that runs it; moving an installation between rootful and rootless means installing afresh and restoring a [backup](backup-recovery.md).

## Operating JIM

```bash
# Status, restart, stop and start
sudo systemctl status jim.service
sudo systemctl restart jim.service

# JIM's containers, their logs and health
sudo podman ps
sudo podman logs -f jim-web
sudo podman logs jim-worker
sudo podman healthcheck run jim-web && echo "JIM is ready"
```

Restarting `jim.service` restarts the web, worker and scheduler together; `jim-database.service` runs the bundled PostgreSQL separately. Both start at boot. The installer's copy, `/opt/jim/setup.sh`, looks after the certificate as it does on Docker (see [Renewing the Certificate](deployment.md#renewing-the-certificate)); on Podman, installing a new certificate restarts the whole JIM pod, so do it outside a synchronisation run.

### Rootless commands {#rootless-commands}

A rootless JIM belongs to the `jim` account: its systemd manager runs JIM, and its Podman holds JIM's containers, images, secrets and volumes, which root's Podman does not see. As root, reach them like this:

```bash
# Podman as the jim account, from the root folder: rootless Podman fails in a folder the account cannot read,
# such as your home folder. This function saves typing that out each time.
jim-podman() { (cd / && sudo -u jim XDG_RUNTIME_DIR=/run/user/$(id -u jim) podman "$@"); }

sudo systemctl --user -M jim@ status jim.service
jim-podman ps
jim-podman logs -f jim-web
```

Wherever this documentation runs `podman` as root (`sudo podman`) for a rootful JIM, run `jim-podman` for a rootless one; and wherever it runs `systemctl`, run `systemctl --user -M jim@`.

As the `jim` account, Podman cannot open your files by name, so give it its input and output through your shell's redirections (`<`, `>` and `|`), as this documentation does, rather than as a path. Podman 4's `podman volume export` is the trap: it opens `/dev/stdout` by name even when given no path, so piped or redirected to a file of yours, its archive comes out empty.

### Health checks and restarts

Each container has a health check, which `podman ps` shows. Podman 5 restarts a container whose health check keeps failing, where Docker only reports it, so the checks ask only whether a service has stopped responding:

- **jim-web**<br /> Answers HTTP at all. Maintenance mode, or a database that is unreachable, makes JIM not ready without restarting `jim-web`, which would not help.
- **jim-worker, jim-scheduler**<br /> Refreshed their heartbeat within the last 60 or 120 seconds.
- **jim-database-postgres**<br /> Answered within five minutes, even to refuse a connection while it starts or recovers.

None of these checks starts until its service has finished starting, however long a first start or an upgrade's database changes take, so Podman never restarts a service part-way through starting.

## Firewall, SELinux and AppArmor

- **firewalld**<br /> Blocks JIM's port by default on RHEL. The installer offers to open it; by hand: `firewall-cmd --permanent --add-service=https && firewall-cmd --reload` (or `--add-port=<port>/tcp` for another port).
- **SELinux**<br /> Needs nothing for JIM's own volumes, which Podman labels itself. A host folder you mount for the File Connector needs a label (see [File Access](../connectors/jim-file-connector.md#file-access)), and an Apache httpd reverse proxy needs the `httpd_can_network_connect` boolean (see [Apache httpd Example](deployment.md#apache-httpd-example)).
- **AppArmor, on Ubuntu 24.04**<br /> Ubuntu gives Podman's `crun` and `podman` AppArmor profiles of their own. A rootful container that sets no-new-privileges, as JIM's do, cannot leave them for its own profile, so AppArmor combines the two, and the combination breaks two things:

    - It allows the container no network at all, so JIM cannot reach its database or identity provider.
    - It stops the processes in a container signalling one another. The .NET runtime signals its own threads to reclaim memory, so JIM's services crash, and Podman restarts them, over and over.

    The installer offers to fix both, in AppArmor's places for site changes: a network rule in each profile's local override, and a rule in `/etc/apparmor.d/abstractions/base.d/jim-podman` that lets each profile signal itself when combined with `crun`'s or `podman`'s. Ubuntu already lets every profile signal itself (`signal peer=@{profile_name}` in its base abstraction); the rule extends that to the combined profile, and allows nothing between different profiles. JIM's containers keep their own AppArmor profile. By hand, as root:

    ```bash
    echo 'network,' >> /etc/apparmor.d/local/crun
    echo 'network,' >> /etc/apparmor.d/local/podman
    apparmor_parser -r /etc/apparmor.d/crun /etc/apparmor.d/podman
    mkdir -p /etc/apparmor.d/abstractions/base.d
    echo 'signal peer=@{profile_name}//&{crun,podman},' > /etc/apparmor.d/abstractions/base.d/jim-podman
    ```

    Podman loads its profile for containers when the first container starts after boot, and keeps it until the server restarts, so the signal rule reaches it only then. If a rootful container has run since the server started, as it has wherever JIM already runs, restart the server once you have added the rule. The installer does this part for you where no container is running: it unloads Podman's profile, and the next container to start loads it with the rule.

    A rootless JIM needs neither rule: Podman gives rootless containers no AppArmor profile.

!!! note "With Docker on the same server"
    Docker's firewall rules drop traffic forwarded to other container engines, so other machines cannot reach a rootful Podman JIM, the default, on a server that also runs Docker. Run JIM on one engine per server.

## Installing by Hand {#installing-by-hand}

If your organisation's policy requires every step by hand, these steps do what the installer does. They use the files from a release: those attached to the [release](https://github.com/TetronIO/JIM/releases), or the `podman/` folder of the release bundle, which keeps the three units in `podman/quadlet/`. Run them as root, from the folder holding the files.

1. **Put the files in place.** Leave out `jim-database.kube` if you use your own PostgreSQL server.

    ```bash
    mkdir -p /opt/jim/tls /etc/containers/systemd && chmod 700 /opt/jim/tls
    cp jim.yaml jim-database.yaml jim-config.yaml /opt/jim/
    cp jim.network jim.kube jim-database.kube /etc/containers/systemd/
    ```

    The units expect the pod files in `/opt/jim`; if you put them elsewhere, change `Yaml=` and `ConfigMap=` in each `.kube` file. To use a port other than 443, change `PublishPort=` in `jim.kube`.

2. **Fill in `/opt/jim/jim-config.yaml`**: the identity provider settings, and for your own PostgreSQL server its name in `JIM_DB_HOSTNAME`. For the bundled PostgreSQL on a host with more than 4 GB of memory, size it as the installer would (see [Bundled PostgreSQL Memory](configuration.md#bundled-postgresql-memory)).

3. **Load the images**, from the release bundle's `docker-images` folder, or download them:

    ```bash
    for f in docker-images/*.tar; do podman load < "$f"; done
    # or, connected:
    for image in $(awk '$1 == "image:" { print $2 }' /opt/jim/jim.yaml /opt/jim/jim-database.yaml); do podman pull "$image"; done
    ```

4. **Store the secrets.** Fill in a copy of `jim-secrets.yaml` (for the bundled PostgreSQL, choose a strong password: the database is created with it on first start), store it, and delete the copy. Then put JIM's certificate and key in `/opt/jim/tls` as `tls.crt` and `tls.key` (see [The Certificate](deployment.md#the-certificate)) and store them too:

    ```bash
    podman kube play - < jim-secrets.yaml && shred -u jim-secrets.yaml

    printf 'apiVersion: v1\nkind: Secret\nmetadata:\n  name: jim-tls\ndata:\n  tls.crt: %s\n  tls.key: %s\n' \
      "$(base64 -w0 /opt/jim/tls/tls.crt)" "$(base64 -w0 /opt/jim/tls/tls.key)" | podman kube play --replace -
    ```

5. **Open the port** in firewalld, and on Ubuntu 24.04 add the AppArmor rules JIM's containers need (see [Firewall, SELinux and AppArmor](#firewall-selinux-and-apparmor)):

    ```bash
    firewall-cmd --permanent --add-service=https && firewall-cmd --reload
    ```

6. **Start JIM**, then wait until `podman healthcheck run jim-web` succeeds:

    ```bash
    systemctl daemon-reload
    systemctl start jim-database.service jim.service
    ```

### Rootless, by hand

For a rootless installation, first create the account, enable lingering for it, and let it use port 443. Check `/etc/subuid` and `/etc/subgid` each give the account a range; current distributions add one when the account is created.

```bash
useradd --create-home --comment "JIM (Junctional Identity Manager)" --shell /sbin/nologin jim
grep '^jim:' /etc/subuid /etc/subgid
loginctl enable-linger jim
echo net.ipv4.ip_unprivileged_port_start=443 > /etc/sysctl.d/90-jim.conf && sysctl --system
jim-podman() { (cd / && sudo -u jim XDG_RUNTIME_DIR=/run/user/$(id -u jim) podman "$@"); }
```

Then follow the steps above, with three differences:

- In step 1, put the units in the account's folder instead of `/etc/containers/systemd/`:

    ```bash
    sudo -u jim mkdir -p /home/jim/.config/containers/systemd
    cp jim.network jim.kube jim-database.kube /home/jim/.config/containers/systemd/
    chown jim: /home/jim/.config/containers/systemd/*
    ```

- Run each `podman` command as `jim-podman`.
- Run each `systemctl` command as `systemctl --user -M jim@`.

## Without systemd {#without-systemd}

On a host without systemd, or with a Podman older than 4.4, run the pods directly. JIM runs, but does not start by itself after a reboot, so this is best effort rather than supported:

```bash
podman network create --ignore jim
podman kube play --replace --network jim --configmap /opt/jim/jim-config.yaml /opt/jim/jim-database.yaml
podman kube play --replace --network jim --configmap /opt/jim/jim-config.yaml --publish 443:8443 /opt/jim/jim.yaml
```

The installer does this for you on such a host.

## Related

- [Deployment Guide](deployment.md): installing, certificates, reverse proxies.
- [Deploying with Ansible](deploying-with-ansible.md): the same files, deployed by Red Hat's podman system role.
- [Upgrading](upgrading.md), [Backup & Disaster Recovery](backup-recovery.md) and [Troubleshooting](troubleshooting.md): each covers Podman alongside Docker.
