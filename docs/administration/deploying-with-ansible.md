---
title: Deploying with Ansible
---

# Deploying JIM with Ansible

To deploy JIM across RHEL servers with Ansible, use Red Hat's podman system role: `redhat.rhel_system_roles.podman`, from the RHEL System Roles, or its upstream `fedora.linux_system_roles.podman`. The role installs Podman's Quadlet units, stores Podman secrets, opens firewalld ports and, for a rootless account, enables lingering, which is everything [the installer](deployment.md#what-the-installer-does) does on Podman. JIM ships no role or collection of its own: the playbook below deploys the same files as the installer, rootful, as the installer does by default. [Rootless](#rootless) shows what changes to run JIM rootless, as an account named `jim`.

Read [Running JIM on Podman](podman.md) first: it explains the files, and the choice between rootful and rootless.

## Before You Start

- **Ansible** with the RHEL System Roles (`dnf install rhel-system-roles`), or `ansible-galaxy collection install fedora.linux_system_roles`.
- **JIM's Podman files** from the [release](https://github.com/TetronIO/JIM/releases) you are deploying, in the playbook's `files/` folder: `jim.yaml`, `jim-database.yaml` (for the bundled PostgreSQL), `jim.network`, and `jim-config.yaml` with your settings filled in (see the [Configuration Reference](configuration.md#podman)).
- **JIM's certificate** for its name, as `files/tls.crt`, followed by any intermediate CA certificates, and its unencrypted private key.
- **The secrets in Ansible Vault**: the database password, your identity provider's client secret, and the certificate's private key. For the bundled PostgreSQL, choose a strong database password: the database is created with it on first start.
- **For an air-gapped server**, JIM's images loaded into root's Podman (rootless, the `jim` account's) before the play, from the release bundle's `docker-images` folder (see [Installing by Hand](podman.md#installing-by-hand), step 3). The role otherwise downloads them.

## The Playbook

```yaml
- name: Deploy JIM on Podman
  hosts: jim_servers
  become: true

  vars:
    # From Ansible Vault: jim_db_password, jim_sso_secret, jim_tls_key

    podman_secrets:
      - name: jim-secrets
        state: present
        restarts: [jim]
        data: |
          apiVersion: v1
          kind: Secret
          metadata:
            name: jim-secrets
          data:
            JIM_DB_PASSWORD: {{ jim_db_password | b64encode }}
            JIM_SSO_SECRET: {{ jim_sso_secret | b64encode }}
      - name: jim-tls
        state: present
        restarts: [jim]
        data: |
          apiVersion: v1
          kind: Secret
          metadata:
            name: jim-tls
          data:
            tls.crt: {{ lookup('ansible.builtin.file', 'files/tls.crt') | b64encode }}
            tls.key: {{ jim_tls_key | b64encode }}

    # In dependency order. The role copies each file into /etc/containers/systemd/, where the .kube units
    # find the pod and settings files by name, and restarts the units named in "restarts" when a file changes.
    podman_quadlet_specs:
      - file_src: jim.network
      - file_src: jim-config.yaml
        restarts: [jim, jim-database]
      - file_src: jim-database.yaml
        restarts: [jim-database]
      - file_src: jim.yaml
        restarts: [jim]
      - name: jim-database
        type: kube
        file_content: |
          [Unit]
          Description=JIM database (PostgreSQL)

          [Kube]
          Yaml=jim-database.yaml
          ConfigMap=jim-config.yaml
          Network=jim.network

          [Service]
          TimeoutStartSec=900

          [Install]
          WantedBy=default.target
      - name: jim
        type: kube
        file_content: |
          [Unit]
          Description=JIM (Junctional Identity Manager)
          After=jim-database.service

          [Kube]
          Yaml=jim.yaml
          ConfigMap=jim-config.yaml
          Network=jim.network
          PublishPort=443:8443

          [Service]
          TimeoutStartSec=900

          [Install]
          WantedBy=default.target

    podman_firewall:
      - port: 443/tcp
        state: enabled

  roles:
    - redhat.rhel_system_roles.podman
```

With your own PostgreSQL server, leave out `jim-database.yaml` and the `jim-database` unit, and set `JIM_DB_HOSTNAME` in `jim-config.yaml`. For a port other than 443, change `PublishPort=` and the firewall port together.

### Rootless {#rootless}

To run JIM rootless, as an account named `jim`, add the account to the play's `vars`, and create it, with the port threshold rootless containers need for port 443, before the role runs. The role then installs the units in `~jim/.config/containers/systemd/` and enables lingering for the account.

```yaml
  vars:
    podman_run_as_user: jim
    podman_run_as_group: jim
    # ...the rest as above

  pre_tasks:
    # The role needs the account to exist already, with subordinate ID ranges; useradd assigns those on RHEL 9 and
    # 10.
    - name: Create the account that runs JIM
      ansible.builtin.user:
        name: jim
        comment: JIM (Junctional Identity Manager)
        shell: /sbin/nologin
        create_home: true

    # Rootless containers cannot publish a port below 1024 until this threshold is lowered.
    - name: Let rootless containers publish port 443
      ansible.builtin.copy:
        dest: /etc/sysctl.d/90-jim.conf
        content: "net.ipv4.ip_unprivileged_port_start=443\n"
        mode: "0644"
      register: jim_sysctl

    - name: Apply the port threshold
      ansible.builtin.command: sysctl --system
      when: jim_sysctl.changed
```

For a port other than 443, change the port threshold too.

## After the Play

- **Wait for JIM**, which prepares its database on first start: it is ready when `sudo podman healthcheck run jim-web` succeeds (rootless, see [Rootless commands](podman.md#rootless-commands)).
- **Register JIM's redirect URIs** at your identity provider and **distribute your certificate authority**, as the [Deployment Guide](deployment.md#after-installing) describes.
- **Operate JIM** as on any Podman installation (see [Operating JIM](podman.md#operating-jim)).

To change a setting, renew the certificate or change the client secret, update it and run the play again: the role restarts the services that use it. To upgrade, replace `jim.yaml` (and `jim-database.yaml`) with the new release's copies and run the play again; the Worker applies any database changes as JIM starts, as on any installation (see [Upgrading](upgrading.md)).

The `restarts` keys need a recent version of the role. With an older one, restart JIM yourself after a change: `sudo systemctl restart jim.service`, or rootless, `jim-systemctl restart jim.service` (see [Rootless commands](podman.md#rootless-commands)).

!!! note "The bundled database keeps its first password"
    PostgreSQL takes the database password from `jim-secrets` only when it creates the database, on first start. To change it later, change it in PostgreSQL first (`ALTER USER jim WITH PASSWORD '...'`), then in Ansible Vault.
