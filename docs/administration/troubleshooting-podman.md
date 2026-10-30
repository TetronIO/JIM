---
title: Troubleshooting Podman
description: Fix a Podman installation of JIM that cannot be seen, started, reached or kept running, by the message it shows.
type: troubleshooting
audience: operator
validated: v0.17.0
---

# Troubleshooting Podman

This page covers problems particular to running JIM on Podman, each titled by what you see. For one that is not particular to Podman, see [Troubleshooting](troubleshooting.md). The commands are for the default, rootful installation; for a rootless one, see [Rootless commands](podman.md#rootless-commands).

```jim-diagram
archetype: decision
title: Finding the fix for a Podman installation of JIM
desc: "Work down four questions. First, does jim.service start? Check it with systemctl, or for a rootless JIM with jim-systemctl. If a rootless jim.service is not found, remove the XDG folder settings made for every account; if it cannot expose privileged port 443, lower the unprivileged-port threshold. If it starts, does JIM become ready? Look at its containers as the account that runs JIM: sudo podman ps, or jim-podman ps. On Ubuntu, Resource temporarily unavailable or Permission denied in its logs means adding the AppArmor network rule. If it becomes ready, does it stay running? A rootless JIM that stops at log-out or after a reboot needs lingering enabled; on Ubuntu, services that keep restarting need the AppArmor signal rule, then a server restart. If it stays running but other machines cannot reach it, open its port in firewalld."
start: { label: JIM is not working }
questions:
  - { id: starts, label: "Does `jim.service` start?", sub: "rootless: ask `jim-systemctl`" }
  - { id: ready, label: "Does JIM become ready?", sub: "`sudo podman ps` or `jim-podman ps`" }
  - { id: up, label: "Does it stay running?" }
  - { id: reach, label: "Can other machines reach it?" }
outcomes:
  - { id: xdg, label: Remove the XDG folder settings, sub: rootless only }
  - { id: port, label: Lower the port threshold, sub: rootless only }
  - { id: net, label: Add the AppArmor network rule, sub: Ubuntu 24.04 }
  - { id: linger, label: Enable lingering, sub: rootless only }
  - { id: signal, label: Add the AppArmor signal rule, sub: "Ubuntu 24.04, then restart the server" }
  - { id: fw, label: Open the port in firewalld, sub: RHEL and its derivatives }
answers:
  - { from: starts, to: xdg, label: "`not found`" }
  - { from: starts, to: port, label: privileged port 443 }
  - { from: starts, to: ready, label: "yes" }
  - { from: ready, to: net, label: "network errors in its logs" }
  - { from: ready, to: up, label: "yes" }
  - { from: up, to: linger, label: "stops at log-out or reboot" }
  - { from: up, to: signal, label: keeps restarting }
  - { from: up, to: reach, label: "yes" }
  - { from: reach, to: fw, label: "no, only the server itself" }
```

## :material-wrench-outline: `podman ps` shows none of JIM's containers

**What it means.** Each account has its own Podman containers. A rootful JIM's belong to root, so `podman ps` run as yourself sees none of them; a rootless JIM's belong to the `jim` account, so root's Podman sees none either. Nothing is wrong with JIM.

**How to fix.** For a rootful JIM, run Podman as root: `sudo podman ps`. For a rootless one, run it as the `jim` account, with `jim-podman ps` (see [Rootless commands](podman.md#rootless-commands)). Run as root from a folder the account cannot read, such as `/root`, Podman as `jim` fails with `cannot chdir to /root: Permission denied`; the `jim-podman` function starts from the root folder for that reason.

## :material-wrench-outline: `systemctl --user -M jim@` fails with `Transport endpoint is not connected` {#rootless-systemctl-fails}

It may also print `Failed to start transient service unit: Connection reset by peer`, or for `status`, `Failed to get properties`.

**What it means.** JIM v0.16.0 gave `systemctl --user -M jim@` for operating a rootless JIM, in this documentation and at the end of an installation. It reaches the account's systemd manager by a route that fails on a minimal RHEL-family host, such as AlmaLinux 9 installed from its generic cloud image, which lacks the `systemd-container` package. A command that fails this way changes nothing, and JIM carries on as it was. An upgrade that went on past such a failed stop has the new release's files and images in place while JIM still runs, and reports, the old release. Your data is unaffected.

**How to fix.** Run the command with `jim-systemctl` instead (see [Rootless commands](podman.md#rootless-commands)), which reaches the manager through the account's own bus and needs no extra package; for example `jim-systemctl stop jim.service`. To finish such an upgrade, restart the database and then JIM, `jim-systemctl restart jim-database.service` and `jim-systemctl restart jim.service`, and verify it as [Upgrading](upgrading.md#verifying) describes. Installing `systemd-container` from your distribution's repository makes `-M` work too.

## :material-wrench-outline: A rootless JIM stops when you log out, or does not start after a reboot

**What it means.** A rootless JIM runs under its account's own systemd manager, and lingering is not enabled for the account, so that manager, and JIM with it, runs only while the account has a session. The installer enables it; an account set up by hand may lack it. Your data is unaffected.

**How to fix.** As root, `loginctl enable-linger jim`, then start JIM: `jim-systemctl start jim.service`.

## :material-wrench-outline: A rootless `jim.service` is `not found`, or the installer stops over folder settings meant for another account

**What it means.** The `jim` account's systemd manager runs Quadlet and Podman with its own environment, and a setting of `XDG_CONFIG_HOME`, `XDG_DATA_HOME` or `XDG_RUNTIME_DIR` made for every account points them at another account's folders. Quadlet then looks for JIM's units in the wrong folder and generates none, so `jim-systemctl start jim.service` answers `Unit jim.service not found`. The installer checks for this before installing anything, and stops naming each setting. A rootful JIM is unaffected. Nothing has been lost: the units, and any data, are where JIM put them.

**How to fix.** Find the settings (`jim-systemctl show-environment | grep ^XDG_` shows what the manager has), usually in `/etc/environment`, `/etc/environment.d/` or `/etc/security/pam_env.conf`. Remove them, or limit them to the accounts they are meant for, then restart the manager, `sudo systemctl restart user@$(id -u jim).service`, and run the installer again or start JIM: `jim-systemctl start jim.service`.

## :material-wrench-outline: A rootless `jim.service` fails with `rootlessport cannot expose privileged port 443`

The full message continues: `you can add 'net.ipv4.ip_unprivileged_port_start=443' to /etc/sysctl.conf (currently 1024), or choose a larger port number (>= 1024)`.

**What it means.** A rootless container cannot publish a port below the kernel's unprivileged-port threshold. The installer lowers it in `/etc/sysctl.d/90-jim.conf`; that file is missing, or something set the threshold back.

**How to fix.** As root: `echo net.ipv4.ip_unprivileged_port_start=443 > /etc/sysctl.d/90-jim.conf && sysctl --system`, then `jim-systemctl restart jim.service`. Or move JIM to a port of 1024 or above (see [Port Mapping](tls-and-reverse-proxy.md#port-mapping)).

## :material-wrench-outline: On Ubuntu, JIM never becomes ready, and its logs show `Permission denied` or `Resource temporarily unavailable`

**What it means.** On Ubuntu 24.04, Podman's own AppArmor profiles stop a rootful JIM's containers using the network at all. `jim-web`, `jim-worker` and `jim-scheduler` each log `The database is not reachable yet ... Resource temporarily unavailable` (or `Permission denied`), and `sudo dmesg | grep DENIED` shows `profile="crun"` with `class="net"`.

**How to fix.** Allow the network in the profiles' local overrides, as [Firewall, SELinux, AppArmor and NetworkManager](podman.md#firewall-selinux-and-apparmor) shows, then restart JIM: `sudo systemctl restart jim-database.service jim.service`. The installer offers this; you need it by hand only on a host set up without the installer, or where the offer was declined.

## :material-wrench-outline: On Ubuntu, JIM's services keep crashing and restarting

JIM becomes ready, but `jim-web`, `jim-worker` or `jim-scheduler` restart now and then, most often while JIM first prepares its database and after the server starts; a page or a PowerShell command can fail with `The response ended prematurely`.

**What it means.** On Ubuntu 24.04, Podman's own AppArmor profiles stop the processes in a rootful JIM's containers signalling one another. JIM's runtime signals its own threads to reclaim memory, so a service aborts and Podman restarts it. `sudo podman inspect --format '{{.Name}} {{.RestartCount}}' jim-web jim-worker jim-scheduler` counts the restarts, and `sudo journalctl -k | grep DENIED` shows `operation="signal"` with a `peer` ending `//&crun`. A synchronisation running when the worker crashes stops there; the worker fails its Activity when it starts again, and the next run carries on from where JIM's data stands (see [Pausing work first](upgrading.md#pausing-work)).

**How to fix.** Add the signal rule [Firewall, SELinux, AppArmor and NetworkManager](podman.md#firewall-selinux-and-apparmor) shows, then restart the server, which Podman needs to load its profile with the rule. The installer adds the rule; an installation made by the installer of v0.16.0 or earlier needs it adding by hand.

## :material-wrench-outline: A rootful JIM loses its database after a firewalld reload or a restart

JIM's services log `The database is not reachable yet` with `No route to host`, or `Resource temporarily unavailable`, and JIM stays down until you restart it.

**What it means.** Something took a rootful JIM's database off JIM's network. A firewalld reload removes the rules Podman adds for the network, unless `netavark-firewalld-reload.service` is enabled to put them back; on RHEL and its derivatives, NetworkManager can unplug a pod from the network after a restart or reboot. The installer guards against both; an installation made by the installer of v0.16.0 or earlier, or by hand, may not. Your data is unaffected. [Troubleshooting](troubleshooting.md#a-service-logs-the-database-is-not-reachable-yet) shows how to tell which happened.

**How to fix.** Make both host changes [Firewall, SELinux, AppArmor and NetworkManager](podman.md#firewall-selinux-and-apparmor) shows, then restart the database and JIM: `sudo systemctl stop jim.service jim-database.service && sudo systemctl start jim-database.service jim.service`.

## :material-wrench-outline: Other machines cannot reach JIM, but it answers on the server itself

**What it means.** A firewall on the server blocks JIM's port: on RHEL, firewalld, until you allow the port.

**How to fix.** `firewall-cmd --add-service=https && firewall-cmd --permanent --add-service=https` (or `--add-port=<port>/tcp` for another port), which opens it without reloading firewalld: a reload would take a rootful Podman JIM's network away (see [Firewall, SELinux, AppArmor and NetworkManager](podman.md#firewall-selinux-and-apparmor)). If Docker is installed on the same server as a rootful Podman JIM, the default, Docker's firewall rules also drop the traffic; see [Firewall, SELinux, AppArmor and NetworkManager](podman.md#firewall-selinux-and-apparmor).

## :material-lifebuoy: Still stuck?

Collect these before asking for help:

- **The JIM version,** shown at the foot of every portal page and by `Connect-JIM`, or the release you installed.
- **Whether JIM is rootful or rootless,** and the distribution and its version (`cat /etc/os-release`), and Podman's (`podman --version`).
- **The state of JIM's units:** `sudo systemctl status jim.service jim-database.service`, or `jim-systemctl status jim.service jim-database.service` for a rootless JIM.
- **The services' logs from the time of the problem:** `sudo podman logs jim-web`, and likewise `jim-worker`, `jim-scheduler` and `jim-database-postgres`.
- **Any denial the kernel logged,** on Ubuntu: `sudo journalctl -k | grep DENIED`.

Then ask in the [Q&A](https://github.com/TetronIO/JIM/discussions/categories/q-a) category of GitHub Discussions, or open a [GitHub Issue](https://github.com/TetronIO/JIM/issues) for a bug.
