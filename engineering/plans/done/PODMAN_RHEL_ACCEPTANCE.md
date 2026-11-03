# Podman Support - RHEL Acceptance Run

- **Status:** Done (run by the deployment lab, 2026-11-03; see [Results](#results))
- **Issue:** [#1808](https://github.com/TetronIO/JIM/issues/1808); results recorded by [#2698](https://github.com/TetronIO/JIM/issues/2698)
- **Plan:** [PODMAN_SUPPORT.md](PODMAN_SUPPORT.md) (D5, D12, Phase 4 step 6)

The last open item on #1808. CI's `deployment-boot` installs JIM on Podman, rootful and rootless, on every pull request, but on GitHub's Ubuntu runner: not RHEL, not SELinux-enforcing, no NetworkManager managing the network, and no machine other than the server itself signing in. This run covers what CI cannot, on the platform the work is for.

It passes when, on a fresh RHEL host with SELinux enforcing and firewalld running:

- `setup.sh` installs JIM rootful (the default) and rootless (`--rootless`), from the release bundle with no internet connection, and the Ansible playbook in `docs/administration/deploying-with-ansible.md` deploys it as documented;
- a user on **another machine** signs in over HTTPS, trusting only the installer's certificate authority;
- JIM comes back by itself after a restart, a reboot and a firewalld reload, with its data;
- the kernel denies nothing (no SELinux AVC denials) along the way.

Each failure is either fixed before #1808 closes or filed as its own issue and agreed as not blocking.

## What You Need

- **A JIM release bundle newer than v0.16.0.** v0.16.0 lacks nine Podman fixes since (firewalld reload, NetworkManager, rootless `systemctl` on minimal hosts, PostgreSQL memory sizing among them), several found on RHEL-family hosts; running it would only find them again. Use the next release's `jim-release-X.Y.Z.tar.gz`, or build a bundle from `main` on a Docker host whose image store is containerd: `pwsh ./scripts/Build-ReleaseBundle.ps1 -Version X.Y.Z` (it builds the images and writes `release-output/jim-release-X.Y.Z.tar.gz`).
- **A RHEL 10 virtual machine** for the full run, and **a RHEL 9 one** for route A (the PRD's criterion names both). Each: a minimal install, registered for `dnf`, 4 vCPU and 8 GB of memory (the documented recommendation), 40 GB of disk, SELinux enforcing and firewalld running as installed. Take a snapshot after [Preparing the Host](#preparing-the-host); every route starts from it.
- **A client machine** with a browser, on the same network, that can reach the VM by name (DNS, or an entry in its hosts file).
- **An OIDC identity provider** both the VM and the client can reach, with a client for JIM and the redirect URIs `https://<vm-name>/signin-oidc` and `https://<vm-name>/signout-callback-oidc` registered (see `docs/administration/sso-setup.md`). The development Keycloak realm (`.devcontainer/keycloak/jim-realm.json`) works if it runs somewhere both can reach.
- For route C, **Ansible with the RHEL System Roles**: `sudo dnf install -y ansible-core rhel-system-roles`, on the VM itself or on a control node.

## Preparing the Host

As root on the fresh VM, then snapshot it:

```bash
dnf install -y podman
getenforce                                  # Enforcing
systemctl is-active firewalld               # active
stat -fc %T /sys/fs/cgroup                  # cgroup2fs
rpm -q docker-ce moby-engine                # neither installed
rpm -q systemd-container                    # note the answer: absent on a minimal install, which is the case to test
cat /etc/redhat-release; uname -r; podman version --format '{{.Client.Version}}'
```

Copy the bundle to the VM (`scp jim-release-X.Y.Z.tar.gz root@<vm-name>:`), and the identity provider's details to hand: authority URL, client ID and secret, API scope, and the initial administrator's claim value.

Record the host's details in the [results](#recording-the-results) as you go.

## The Checks

Every route runs these after installing. The commands are rootful; for a rootless JIM, define `jim-systemctl` and `jim-podman` from [Rootless commands](../../../docs/administration/podman.md#rootless-commands) first and use them in place of `systemctl` and `podman`.

1. **Installed and ready.** The installer ends by saying JIM is ready. `systemctl status jim.service jim-database.service` shows both active; `podman ps` lists `jim-web`, `jim-worker`, `jim-scheduler` and `jim-database-postgres` as healthy; `podman healthcheck run jim-web` succeeds.
2. **No denials.** `ausearch -m AVC,USER_AVC -ts boot` finds nothing, and `getenforce` still says Enforcing. Run this again after every later check.
3. **Reachable, and only over HTTPS.** `firewall-cmd --list-services` includes `https` (or your port). From the client, with the installer's CA (`/opt/jim/tls/ca.crt`) imported into the client's trusted roots: `curl --cacert ca.crt https://<vm-name>/api/v1/health/ready` answers, and the browser opens `https://<vm-name>/` with no certificate warning.
4. **Sign-in from the client.** Sign in through the identity provider as the initial administrator and reach the dashboard.
5. **A host folder for the File Connector, under SELinux.** Follow "On Podman" under [File access](../../../docs/connectors/jim-file-connector.md#file-access): a `hostPath` folder (say `/srv/hr-extracts`) labelled with `semanage fcontext -a -t container_file_t ...` and `restorecon`, holding a small CSV. Create a File Connector Connected System reading it, and run a Full Import: the objects import, and check 2 still finds no denials. This is also the data the next checks look for.
6. **Restart.** `systemctl restart jim.service`: JIM is ready again, and the Connected System and its import are still there.
7. **Reboot.** `systemctl reboot`, and do not log in afterwards. From the client, JIM comes back by itself (rootless: through the `jim` account's lingering), with its data.
8. **firewalld reload** (rootful). `firewall-cmd --reload`, then a fresh sign-in and a second Full Import both work. `systemctl is-enabled netavark-firewalld-reload.service` says enabled.
9. **NetworkManager** (rootful). `/etc/NetworkManager/conf.d/90-jim-podman.conf` exists, and `nmcli device` lists JIM's `veth` interfaces as unmanaged, with no "Wired connection" on them, after the restart and the reboot.
10. **Certificate renewal.** `/opt/jim/setup.sh --renew-certificate`: JIM's web service restarts, the browser still trusts it without re-importing anything, and the certificate's new expiry shows (`openssl x509 -in /opt/jim/tls/tls.crt -noout -enddate`).

## The Routes

Restore the snapshot before each.

### A. `setup.sh`, rootful (RHEL 10, then RHEL 9)

```bash
tar -xzf jim-release-X.Y.Z.tar.gz && cd jim-release-X.Y.Z && sudo ./setup.sh
```

Accept the defaults: bundled database, a generated certificate for the VM's name and address, port 443, open the firewall, start now. It should not ask which runtime: Podman is the only one installed. Run all ten checks.

### B. `setup.sh`, rootless and air-gapped (RHEL 10)

Cut the VM off from the internet first, keeping it on the network the client and the identity provider are on: an isolated network on the hypervisor, or, if both are on the VM's own subnet, `sudo ip route del default` (back after a reboot, which check 7 does; delete it again afterwards). Confirm `curl -sS https://github.com` fails.

```bash
cd jim-release-X.Y.Z && sudo ./setup.sh --rootless
```

The installer says it is installing from the bundle with no internet connection needed. Then, besides checks 1 to 7 and 10 (8 and 9 do not apply rootless):

- `id jim` shows the account, `loginctl show-user jim -p Linger` says `Linger=yes`, and `/etc/sysctl.d/90-jim.conf` sets `net.ipv4.ip_unprivileged_port_start=443`.
- `jim-systemctl status jim.service` works on this minimal host, where `systemctl --user -M jim@` fails without `systemd-container`.
- JIM's audit events show one internal address for the client rather than the client's own: expected, and documented, for rootless.

### C. Ansible, rootful then rootless (RHEL 10)

Follow `docs/administration/deploying-with-ansible.md` word for word, as a customer would, with the release's Podman files and the certificate from route A's installer (or your own) in the playbook's `files/` folder. To run it on the VM itself, an inventory of `[jim_servers]` / `localhost ansible_connection=local` will do. Then the page's own "Check it worked", and checks 1 to 4, 6 and 7.

Restore the snapshot, and run it again with the page's [Rootless](../../../docs/administration/deploying-with-ansible.md#rootless) changes.

Anything you had to do that the page does not say is a documentation bug: note it.

### D. Optional: upgrade from v0.16.0 (RHEL 10)

Install v0.16.0 rootful with its own `setup.sh`, sign in and run a Full Import, then upgrade to the new release as [Upgrading on Podman](../../../docs/administration/upgrading-podman.md) describes, including its notes for upgrading from v0.16.0 or earlier. JIM comes back with its data, and checks 8 and 9 pass. Customers already on v0.16.0 will take this path.

### E. Optional: behind Apache httpd

Put Apache httpd in front of route A's JIM as `docs/administration/tls-and-reverse-proxy.md` describes, with the `httpd_can_network_connect` boolean, and sign in through it from the client.

## When a Check Fails

Before restoring the snapshot, keep:

```bash
sudo journalctl -u jim.service -u jim-database.service -b > journal.log
for c in jim-web jim-worker jim-scheduler jim-database-postgres; do sudo podman logs "$c" > "$c.log" 2>&1; done
sudo ausearch -m AVC,USER_AVC -ts boot > avc.log
sudo podman inspect jim-web jim-worker jim-scheduler jim-database-postgres > inspect.json
```

Rootless, use `jim-podman` and `sudo journalctl _UID=$(id -u jim) -b`. `docs/administration/troubleshooting-podman.md` covers the known failures. File anything new as its own issue, as a sub-issue of #1808.

## Recording the Results

Paste this, filled in, into the pull request that closes #1808:

```markdown
| Host | RHEL | Kernel | Podman | SELinux | firewalld | systemd-container | Memory |
|------|------|--------|--------|---------|-----------|-------------------|--------|
| A    | 10.x |        |        | Enforcing | active  | absent / present  | 8 GB   |

| Route | 1 Ready | 2 No AVCs | 3 HTTPS | 4 Sign-in | 5 File Connector | 6 Restart | 7 Reboot | 8 firewalld | 9 NM | 10 Renew |
|-------|---------|-----------|---------|-----------|------------------|-----------|----------|-------------|------|----------|
| A, RHEL 10 |    |           |         |           |                  |           |          |             |      |          |
| A, RHEL 9  |    |           |         |           |                  |           |          |             |      |          |
| B          |    |           |         |           |                  |           |          | n/a         | n/a  |          |
| C rootful  |    |           |         |           | n/a              |           |          |             |      | n/a      |
| C rootless |    |           |         |           | n/a              |           |          | n/a         | n/a  | n/a      |

Deviations from the documentation, and issues filed:
```

The same pull request then:

- ticks the two open Phase 2 criteria in `engineering/prd/done/PRD_PODMAN_SUPPORT.md` (booting on RHEL 9 and 10, and rootless on a real RHEL host);
- marks Phase 4 step 6 of the plan ✅, and moves this file, the plan and the PRD to `done/` with `Status: Done`, updating the links between them;
- says `Closes #1808`.

## Results

Recorded 2026-11-03 ([#2698](https://github.com/TetronIO/JIM/issues/2698)). The run was not made by hand on a RHEL virtual machine, as written above: it is made by the deployment lab (`test/ci/deployment/lab/`, `deploy-lab.yml`), nightly and on every release, so it does not go stale. AlmaLinux 9 stands in for RHEL 9 and CentOS Stream 10 for RHEL 10 (LXD's image server has no AlmaLinux or Rocky Linux 10 image). Every cell is air-gapped, and checks from the lab host, another machine, trusting only the certificate authority JIM's certificate came from.

| Host | OS | Kernel | Podman | SELinux | firewalld | systemd-container | Memory |
|------|----|--------|--------|---------|-----------|-------------------|--------|
| `alma9-podman` | AlmaLinux 9.8 | 5.14.0-687 | 5.8.2 | Enforcing | active | not added | 6 GB |
| `cs10-podman` | CentOS Stream 10 | 6.12.0-279 | 6.1.0 | Enforcing | active | not added | 6 GB |

| Route | 1 Ready | 2 No AVCs | 3 HTTPS | 4 Sign-in | 5 File Connector | 6 Restart | 7 Reboot | 8 firewalld | 9 NM | 10 Renew |
|-------|---------|-----------|---------|-----------|------------------|-----------|----------|-------------|------|----------|
| A, RHEL 10 (`cs10-podrf-ag`) | ✅ | ✅ | ✅ | ✅ | volume only | ✅ | ✅ | not run | not run | not run |
| A, RHEL 9 (`alma9-podrf-ag`, `-cases`, `-soak`) | ✅ | ✅ | ✅ | ✅ | volume only | ✅ (20 in the soak) | ✅ (5 in the soak) | not run | by the soak | ✅ |
| B, RHEL 10 (`cs10-podrl-ag`) | ✅ | ✅ | ✅ | ✅ | volume only | ✅ | ✅ | n/a | n/a | not run |
| B, RHEL 9 (`alma9-podrl-ag`, `-cases`) | ✅ | ✅ | ✅ | ✅ | volume only | ✅ | ✅ | n/a | n/a | ✅ |
| C rootful (`alma9-podrf-ansible`, `cs10-podrf-ansible`) | ✅ | ✅ | ✅ | ✅ | n/a | ✅ | ✅ | ✅ | not run | n/a |
| C rootless (`alma9-podrl-ansible`, `cs10-podrl-ansible`) | ✅ | ✅ | ✅ | ✅ | n/a | ✅ | ✅ | n/a | n/a | n/a |
| D (`alma9-podrf-up-oldest`) | ✅ | ✅ | ✅ | ✅ | n/a | n/a | ✅ | not run | not run | n/a |
| E (Apache httpd) | not run | | | | | | | | | |

- **Check 2** reads the journal (`journalctl -b | grep 'avc: denied'`) rather than `ausearch`: the lab's images, like LXD's, have no auditd, so the kernel logs denials itself. Every AlmaLinux and CentOS Stream cell runs it as it ends, and the Ansible cells also before their reboot.
- **Check 5** is covered only as far as the File Connector volume keeping a file through a restart (`Invoke-DeploymentBoot.ps1`); a labelled `hostPath` folder is not tested.
- **Check 9** is not checked directly; the Soak cell's 20 restarts are what #2009 (the fault it guards) failed.
- **Route D** is also run by `alma9-podrf-up` and `alma9-podrl-up`, from the previous release, with their firewalld reload service and NetworkManager notes.

Deviations from the documentation, all fixed in `docs/administration/deploying-with-ansible.md`:

- An air-gapped server: the podman role downloads every image even when it is already loaded (`podman_pull_image` defaults to true, and the role pulls with `force`), so the page now has `podman_pull_image: false` to uncomment; rootless, the images go into the `jim` account's Podman, so the account must exist before they are loaded.
- The firewall role asks the package manager for firewalld on every run, which fails while a repository it cannot reach is enabled: the page now says so.
- Where the vault file goes was not said: `group_vars/jim_servers/vault.yml`.

Found in the lab rather than in JIM: the CentOS Stream 10 image needed a relabel while permissive, then an enforcing boot of its own, and a timezone (`build-lab-images.sh` in `TetronIO/ci-runners`). Nothing was filed against JIM.
