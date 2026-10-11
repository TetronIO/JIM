# Deployment lab

The deployment lab installs a JIM release bundle the way a customer does, on a fresh virtual machine for every
combination of operating system, container runtime and network that JIM supports, and checks each one. It exists
because the `deployment-boot` job in CI can only install on GitHub's Ubuntu runners, with internet access: it cannot
show what happens on Red Hat Enterprise Linux with SELinux enforcing, on a host with no route to the internet, or
across a host reboot. Issue #1876 is where the lab was designed and first run.

The lab host is a dedicated Linux machine running LXD. Its setup (the storage pool, the two networks, the identity
provider, the test directory and the VM images) lives in `TetronIO/ci-runners`, under `scripts/deploy-lab/`, with its
runbook in `docs/deploy-lab.md`; this folder holds the tests that run on it.

## What is in this folder

| Path | Runs on | What it does |
|------|---------|--------------|
| `Invoke-DeploymentLab.ps1` | Lab host | Runs the matrix, or part of it, against one bundle, as many cells at once as the host's memory holds, and writes `report.md` |
| `Invoke-LabCell.ps1` | Lab host | Runs one cell on its own VM; the runner starts one per cell |
| `Build-LabBundle.ps1` | Lab host | Builds a bundle from a commit, as the release workflow does, on a throwaway online VM |
| `Cells.psd1` | | The matrix: each cell's image, runtime, network, scenario and memory |
| `Lab.psd1` | | The lab host's fixed parts: LXD profiles, network gateways, identity provider, memory reserve |
| `DeploymentLab.psm1` | Lab host | The matrix, scheduling, sign-in and report functions (pure, tested) and the LXD commands |
| `guest/Invoke-LabGuestCommand.ps1` | Each VM | Database queries, restart counts and health for the checks run from the host |
| `Tests/DeploymentLab.Tests.ps1` | CI (any OS) | Pester tests for every pure function, and that `Cells.psd1` is valid |

## The matrix

All cells are air-gapped: their VMs sit on a bridge with no route out, and each cell proves it cannot reach
`github.com` before installing anything.

| Cell | Operating system | Runtime |
|------|------------------|---------|
| `u24-docker-ctrd-ag` | Ubuntu 24.04 | Docker, containerd image store |
| `u24-docker-classic-ag` | Ubuntu 24.04 | Docker, classic overlay2 image store |
| `u24-podrf-ag` | Ubuntu 24.04 | Podman, rootful |
| `u24-podrl-ag` | Ubuntu 24.04 | Podman, rootless |
| `alma9-podrf-ag` | AlmaLinux 9 (SELinux enforcing, firewalld, NetworkManager) | Podman, rootful |
| `alma9-podrl-ag` | AlmaLinux 9 | Podman, rootless |

AlmaLinux stands in for Red Hat Enterprise Linux 9. The images are each distribution's cloud image with only the
container runtime added, so they are as bare as a customer's new host.

## What an Install cell checks

1. **A fresh VM**, and on the air-gapped network, proof that it has no route out.
2. **The bundle**, checked as the installation guide tells a customer to: the archive against its published
   `.sha256`, and every file against the bundle's `checksums.sha256`.
3. **The deployment-boot check, on the cell's operating system.** The runner carries PowerShell, the checks in
   `test/ci/deployment/` and the documentation into the VM and runs `Invoke-DeploymentBoot.ps1` there, exactly as CI
   runs it, with the certificate issued for the lab's host name and the installer's firewall offer accepted. That
   covers the installer itself, readiness, every container's health check, the infrastructure API key, data kept
   through a stop and start, PostgreSQL sized to the host, the documented key backup and restore, the documented
   rollback, and no crashes. CI and the lab can therefore never check different things.
4. **From another machine** (the lab host): JIM answers ready over HTTPS trusting only the installer's certificate
   authority, which also proves the firewall was opened; the initial administrator signs in through the identity
   provider, followed through every redirect and form post as a browser does; and JIM holds exactly one
   Administrator.
5. **A host reboot**: JIM starts by itself, with the data written before the reboot, and the administrator signs in
   again.
6. **Nothing crashed quietly**: no container has restarted, and the kernel has logged no fault or AppArmor denial of a
   container since the reboot. A crashing service is restarted and soon looks healthy again, so readiness alone
   cannot show it (#1953).

## Running it

On the lab host, from a clone of this repository, with PowerShell 7 (the runner fetches the same version of PowerShell
for the VMs once, into `~/.cache/jim-deployment-lab`):

```bash
# Every cell, against a release's bundle (its .sha256 beside it)
pwsh ./test/ci/deployment/lab/Invoke-DeploymentLab.ps1 -BundlePath ~/bundles/jim-release-0.17.0.tar.gz

# Build the bundle from a commit first (a release candidate, say), and test that
pwsh ./test/ci/deployment/lab/Invoke-DeploymentLab.ps1 -Commit HEAD

# Some of the cells: by name (wildcards) or by tag; keep a failed cell's VM to look at
pwsh ./test/ci/deployment/lab/Invoke-DeploymentLab.ps1 -Commit HEAD -Tag rhel -KeepVm OnFailure

# What would run
pwsh ./test/ci/deployment/lab/Invoke-DeploymentLab.ps1 -Commit HEAD -List
```

Each run writes a folder (default `./deployment-lab-<date and time>`) holding `report.md` and `results.json`, and a
folder per cell with its `cell.log`, the boot check's `boot.log` and output, and the installer's certificate
authority. The runner exits 1 if any cell failed. A kept VM is named `jl-<cell>`; `lxc exec jl-<cell> -- bash` gets a
shell on it, and `lxc delete --force jl-<cell>` removes it.

**How many cells run at once** is decided by memory: each cell's VM is given its `MemoryGiB`, and the runner starts
cells, largest first, while they fit in the host's memory less `ReserveGiB` (Lab.psd1). `-BudgetGiB` and
`-MaxParallel` override that.

## Adding a cell

Add a line to `Cells.psd1`. A new image must also be built on the lab host (`build-lab-images.sh` in
`TetronIO/ci-runners`) and added to the matrix's `Images`. The Pester tests check that every cell is valid, so a
mistake fails CI rather than a lab run.

## Things worth knowing

- **lxc must never be given an open standard input.** `lxc init` and `lxc launch` read instance YAML from a standard
  input that is not a terminal, and wait for ever on one that never closes, as a CI runner's or an agent's does.
  `Invoke-LabLxc` closes it for every lxc command, and the runner starts each cell with it closed.
- **AlmaLinux's cloud image has no `libicu`**, which PowerShell needs for culture data, so the VMs run PowerShell with
  `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` rather than installing anything a customer's host would not have.
- **Podman's health check for `jim-web` is liveness only**, so every container can be healthy before JIM is ready;
  the host's checks poll readiness for up to five minutes rather than asking once.
