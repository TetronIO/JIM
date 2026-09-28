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
| `/opt/jim/jim-database.yaml` | The PostgreSQL pod, and its tuning | Only to tune PostgreSQL |
| `/opt/jim/jim-config.yaml` | JIM's settings, with the same names as the Docker path's `.env` (see the [Configuration Reference](configuration.md#podman)) | Yes |
| `/opt/jim/tls/` | JIM's certificate and key, and the installer's certificate authority | Through `setup.sh --certificate` |
| `jim.kube`, `jim-database.kube`, `jim.network` | The Quadlet units systemd runs the pods from, including the HTTPS port (`PublishPort=443:8443` in `jim.kube`) | The port only |

The Quadlet units are in `~jim/.config/containers/systemd/` for a rootless installation, and in `/etc/containers/systemd/` for a rootful one.

JIM's secrets are not in any of these files. The database password, your identity provider's client secret and the optional infrastructure API key are the Podman secret `jim-secrets`; JIM's certificate and key are the Podman secret `jim-tls`. Podman keeps secrets in its secret store, readable only by the account that runs JIM (or root, rootful): the same exposure as the Docker path's `.env`, which only root can read.

JIM's data is in named volumes with the same names as on Docker: `jim-db-volume`, `jim-logs-volume`, `jim-keys-volume` and `jim-connector-files-volume`. You also see a volume named `jim-tls`, which Podman fills from the secret of the same name each time JIM starts.

## Rootless or Rootful

By default the installer runs JIM **rootless**, under a dedicated account named `jim` that it creates. This is how Red Hat's own containerized products install, and what security policies increasingly require: JIM's containers already run as a non-root user with every capability dropped, and rootless adds a second boundary, so that a process escaping a container lands in an account that owns nothing else on the server.

The installer creates the account with no login shell, gives it a range of subordinate user IDs for its containers, and enables lingering for it, so that its own systemd manager runs JIM with nobody logged in and starts it at boot.

To run JIM as root instead, pass `--rootful` to the installer. Choose between them on these differences:

| | Rootless (default) | Rootful |
|---|---|---|
| A process escaping a container | Is the unprivileged `jim` account | Is root, or JIM's container user |
| The client address JIM records, for audit events and for rate-limiting unauthenticated requests | ❌ One internal address for every client: Podman's rootless port forwarding does not pass on the client's address | ✅ Each client's own address |
| Port 443 | The installer lets unprivileged programs use ports from 443 up (`net.ipv4.ip_unprivileged_port_start=443` in `/etc/sysctl.d/90-jim.conf`) | Nothing needed |
| Operating JIM | Through the `jim` account (see [Operating JIM](#operating-jim)) | As root |
| File Connector folders on the host | Owned by a subordinate ID of `jim` (see [File Access](../connectors/jim-file-connector.md#file-access)) | Owned by UID `1654` |

!!! warning "Rootless JIM cannot tell clients apart"
    Every request to a rootless JIM appears to come from one internal address. Its security audit events then record that address rather than the client's, and unauthenticated API requests from every client share one rate limit. Do not set `JIM_TRUSTED_PROXIES` to that address as a workaround: every client shares it, so any client could then claim any address. If your audit requirements need each client's address, run JIM rootful.

## Operating JIM

As root, for a rootless installation:

```bash
# Status, restart, stop and start
sudo systemctl --user -M jim@ status jim.service
sudo systemctl --user -M jim@ restart jim.service

# Podman commands run as the jim account, from the root folder: rootless Podman fails in a folder the account
# cannot read, such as your home folder. This function saves typing that out each time.
jim-podman() { (cd / && sudo -u jim XDG_RUNTIME_DIR=/run/user/$(id -u jim) podman "$@"); }

jim-podman ps
jim-podman logs -f jim-web
jim-podman logs jim-worker
jim-podman healthcheck run jim-web && echo "JIM is ready"
```

For a rootful installation, drop `--user -M jim@` and run `podman` as root.

Restarting `jim.service` restarts the web, worker and scheduler together; `jim-database.service` runs the bundled PostgreSQL separately. Both start at boot. The installer's copy, `/opt/jim/setup.sh`, looks after the certificate as it does on Docker (see [Renewing the Certificate](deployment.md#renewing-the-certificate)); on Podman, installing a new certificate restarts the whole JIM pod, so do it outside a synchronisation run.

### Health checks and restarts

Each container has a health check, which `podman ps` shows. Podman 5 restarts a container whose health check keeps failing, where Docker only reports it, so the checks ask only whether a service has stopped responding:

- **jim-web**<br /> Answers HTTP at all. Maintenance mode, or a database that is unreachable, makes JIM not ready without restarting `jim-web`, which would not help.
- **jim-worker, jim-scheduler**<br /> Refreshed their heartbeat within the last 60 or 120 seconds.
- **jim-database-postgres**<br /> Answered within five minutes, even to refuse a connection while it starts or recovers.

None of these checks starts until its service has finished starting, however long a first start or an upgrade's database changes take, so Podman never restarts a service part-way through starting.

## Firewall and SELinux

- **firewalld**<br /> Blocks JIM's port by default on RHEL. The installer offers to open it; by hand: `firewall-cmd --permanent --add-service=https && firewall-cmd --reload` (or `--add-port=<port>/tcp` for another port).
- **SELinux**<br /> Needs nothing for JIM's own volumes, which Podman labels itself. A host folder you mount for the File Connector needs a label (see [File Access](../connectors/jim-file-connector.md#file-access)), and an Apache httpd reverse proxy needs the `httpd_can_network_connect` boolean (see [Apache httpd Example](deployment.md#apache-httpd-example)).

!!! note "With Docker on the same server"
    Docker's firewall rules drop traffic forwarded to other container engines, so other machines cannot reach a rootful Podman JIM on a server that also runs Docker. Run JIM on one engine per server.

## Installing by Hand {#installing-by-hand}

If your organisation's policy requires every step by hand, these steps do what the installer does, rootless under the `jim` account. They use the files from a release: those attached to the [release](https://github.com/TetronIO/JIM/releases), or the `podman/` folder of the release bundle, which keeps the three units in `podman/quadlet/`. Run them from the folder holding the files.

As root:

1. **Create the account** and enable lingering. Check `/etc/subuid` and `/etc/subgid` each give it a range; current distributions add one when the account is created.

    ```bash
    useradd --create-home --comment "JIM (Junctional Identity Manager)" --shell /sbin/nologin jim
    grep '^jim:' /etc/subuid /etc/subgid
    loginctl enable-linger jim
    jim-podman() { (cd / && sudo -u jim XDG_RUNTIME_DIR=/run/user/$(id -u jim) podman "$@"); }
    ```

2. **Put the files in place.** Leave out `jim-database.kube` if you use your own PostgreSQL server.

    ```bash
    mkdir -p /opt/jim/tls && chmod 700 /opt/jim/tls
    cp jim.yaml jim-database.yaml jim-config.yaml /opt/jim/
    sudo -u jim mkdir -p /home/jim/.config/containers/systemd
    cp jim.network jim.kube jim-database.kube /home/jim/.config/containers/systemd/
    chown jim: /home/jim/.config/containers/systemd/*
    ```

    The units expect the pod files in `/opt/jim`; if you put them elsewhere, change `Yaml=` and `ConfigMap=` in each `.kube` file. To use a port other than 443, change `PublishPort=` in `jim.kube`.

3. **Fill in `/opt/jim/jim-config.yaml`**: the identity provider settings, and for your own PostgreSQL server its name in `JIM_DB_HOSTNAME`.

4. **Load the images**, from the release bundle's `docker-images` folder, or download them:

    ```bash
    for f in docker-images/*.tar; do jim-podman load < "$f"; done
    # or, connected:
    for image in $(awk '$1 == "image:" { print $2 }' /opt/jim/jim.yaml /opt/jim/jim-database.yaml); do jim-podman pull "$image"; done
    ```

5. **Store the secrets.** Fill in a copy of `jim-secrets.yaml` (for the bundled PostgreSQL, choose a strong password: the database is created with it on first start), store it, and delete the copy. Then put JIM's certificate and key in `/opt/jim/tls` as `tls.crt` and `tls.key` (see [The Certificate](deployment.md#the-certificate)) and store them too:

    ```bash
    jim-podman kube play - < jim-secrets.yaml && shred -u jim-secrets.yaml

    printf 'apiVersion: v1\nkind: Secret\nmetadata:\n  name: jim-tls\ndata:\n  tls.crt: %s\n  tls.key: %s\n' \
      "$(base64 -w0 /opt/jim/tls/tls.crt)" "$(base64 -w0 /opt/jim/tls/tls.key)" | jim-podman kube play --replace -
    ```

6. **Allow port 443**, which rootless containers cannot use by default, and open it in firewalld:

    ```bash
    echo net.ipv4.ip_unprivileged_port_start=443 > /etc/sysctl.d/90-jim.conf && sysctl --system
    firewall-cmd --permanent --add-service=https && firewall-cmd --reload
    ```

7. **Start JIM**, then wait until `jim-podman healthcheck run jim-web` succeeds:

    ```bash
    systemctl --user -M jim@ daemon-reload
    systemctl --user -M jim@ start jim-database.service jim.service
    ```

For a rootful installation, skip step 1 and the port setting in step 6, put the units in `/etc/containers/systemd/`, run `podman` as root, and use `systemctl` without `--user -M jim@`.

## Without systemd {#without-systemd}

On a host without systemd, or with a Podman older than 4.4, run the pods directly. JIM runs, but does not start by itself after a reboot, so this is best effort rather than supported:

```bash
podman network create --ignore jim
podman kube play --replace --network jim --configmap /opt/jim/jim-config.yaml /opt/jim/jim-database.yaml
podman kube play --replace --network jim --configmap /opt/jim/jim-config.yaml --publish 443:8443 /opt/jim/jim.yaml
```

The installer does this for you on such a host, running JIM rootful.

## Related

- [Deployment Guide](deployment.md): installing, certificates, reverse proxies.
- [Deploying with Ansible](deploying-with-ansible.md): the same files, deployed by Red Hat's podman system role.
- [Upgrading](upgrading.md), [Backup & Disaster Recovery](backup-recovery.md) and [Troubleshooting](troubleshooting.md): each covers Podman alongside Docker.
