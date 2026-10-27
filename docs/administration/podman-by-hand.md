---
title: Installing on Podman by Hand
description: Install JIM on Podman one step at a time, doing by hand what the installer does, rootful or rootless, for a policy that requires it.
type: how-to
audience: implementer
---

# Installing on Podman by Hand

If your organisation's policy requires every step by hand, these steps do what the [installer](deployment.md#what-the-installer-does) does on Podman. [Running on Podman](podman.md) explains the files they put in place, and the choice between rootful and rootless.

## :material-clipboard-list-outline: Before you begin

- A server that meets the [Requirements](podman.md#requirements) for Podman.
- The files from the release you are installing: those attached to the [release](https://github.com/TetronIO/JIM/releases), or the `podman/` folder of the release bundle, which keeps the three units in `podman/quadlet/`.
- JIM's certificate and its unencrypted private key (see [The Certificate](tls-and-reverse-proxy.md#the-certificate)), and your identity provider's settings for JIM.

## :material-download-outline: Install JIM

Run these steps as root, from the folder holding the files.

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

4. **Store the secrets.** Fill in a copy of `jim-secrets.yaml` (for the bundled PostgreSQL, choose a strong password: the database is created with it on first start), store it, and delete the copy. Then put JIM's certificate and key in `/opt/jim/tls` as `tls.crt` and `tls.key` (see [The Certificate](tls-and-reverse-proxy.md#the-certificate)) and store them too:

    ```bash
    podman kube play - < jim-secrets.yaml && shred -u jim-secrets.yaml

    printf 'apiVersion: v1\nkind: Secret\nmetadata:\n  name: jim-tls\ndata:\n  tls.crt: %s\n  tls.key: %s\n' \
      "$(base64 -w0 /opt/jim/tls/tls.crt)" "$(base64 -w0 /opt/jim/tls/tls.key)" | podman kube play --replace -
    ```

5. **Prepare the host's network.** On RHEL and its derivatives, open the port in firewalld, keep JIM's network through a firewalld reload, and keep NetworkManager off Podman's interfaces; on Ubuntu 24.04, add the AppArmor rules JIM's containers need instead (see [Firewall, SELinux, AppArmor and NetworkManager](podman.md#firewall-selinux-and-apparmor)):

    ```bash
    firewall-cmd --add-service=https && firewall-cmd --permanent --add-service=https
    systemctl enable --now netavark-firewalld-reload.service
    printf '[keyfile]\nunmanaged-devices+=interface-name:veth*;interface-name:podman*\n' > /etc/NetworkManager/conf.d/90-jim-podman.conf
    nmcli general reload conf
    ```

6. **Start JIM**:

    ```bash
    systemctl daemon-reload
    systemctl start jim-database.service jim.service
    ```

## :material-account-outline: Rootless, by hand {#rootless-by-hand}

For a rootless installation, first create the account, enable lingering for it, and let it use port 443. Check `/etc/subuid` and `/etc/subgid` each give the account a range; current distributions add one when the account is created.

```bash
useradd --create-home --comment "JIM (Junctional Identity Manager)" --shell /sbin/nologin jim
grep '^jim:' /etc/subuid /etc/subgid
loginctl enable-linger jim
echo net.ipv4.ip_unprivileged_port_start=443 > /etc/sysctl.d/90-jim.conf && sysctl --system
```

Define the `jim-systemctl` and `jim-podman` functions from [Rootless commands](podman.md#rootless-commands) in the same shell, then follow the steps above, with three differences:

- In step 1, put the units in the account's folder instead of `/etc/containers/systemd/`:

    ```bash
    sudo -u jim mkdir -p /home/jim/.config/containers/systemd
    cp jim.network jim.kube jim-database.kube /home/jim/.config/containers/systemd/
    chown jim: /home/jim/.config/containers/systemd/*
    ```

- Run each `podman` command as `jim-podman`.
- Run each `systemctl` command as `jim-systemctl`.

## :material-check-decagram-outline: Check it worked

1. Wait for JIM, which prepares its database on first start. It is ready when `podman healthcheck run jim-web` succeeds (rootless, `jim-podman healthcheck run jim-web`).
2. Open JIM in a browser and sign in with your identity provider.

## :material-cog-off-outline: Without systemd {#without-systemd}

On a host without systemd, or with a Podman older than 4.4, run the pods directly. JIM runs, but does not start by itself after a reboot, so this is best effort rather than supported:

```bash
podman network create --ignore jim
podman kube play --replace --network jim --configmap /opt/jim/jim-config.yaml /opt/jim/jim-database.yaml
podman kube play --replace --network jim --configmap /opt/jim/jim-config.yaml --publish 443:8443 /opt/jim/jim.yaml
```

The installer does this for you on such a host.

## :material-book-open-variant-outline: Related

- [Running on Podman](podman.md): how JIM runs on Podman, and operating it day to day.
- [Deployment Guide](deployment.md): the installer, certificates and reverse proxies.
- [Deploying with Ansible](deploying-with-ansible.md): the same files, deployed by Red Hat's podman system role.
