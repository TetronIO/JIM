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
| `scenarios/<Scenario>.ps1` | Lab host | What each kind of cell does once its VM is up: `Install`, `Upgrade`, `InstallerCases`, `ExternalDatabase` |
| `Invoke-LabUpgradeData.ps1` | Lab host | Gives an upgrade cell's JIM real data, and checks it is all there afterwards |
| `Build-LabBundle.ps1` | Lab host | Builds a bundle from a commit, as the release workflow does, on a throwaway online VM |
| `Cells.psd1` | | The matrix: each cell's image, runtime, network, scenario and memory |
| `Lab.psd1` | | The lab host's fixed parts: LXD profiles, network gateways, identity provider, test directory, database server image, memory reserve |
| `DeploymentLab.psm1` | Lab host | The matrix, scheduling, sign-in and report functions (pure, tested) and the LXD commands |
| `guest/Invoke-LabGuestCommand.ps1` | Each VM | Database queries, restart counts, health, the installation's fingerprint and its database password, for the checks run from the host |
| `Tests/DeploymentLab.Tests.ps1` | CI (any OS) | Pester tests for every pure function, and that `Cells.psd1` is valid |

## The matrix

All cells are air-gapped: their VMs sit on a bridge with no route out, and each cell proves it cannot reach
`github.com` before installing anything. Each combination below is an Install cell (`-ag`) and an Upgrade cell (`-up`);
some are also an InstallerCases cell (`-cases`) or an ExternalDatabase cell (`-extdb`), and one is the Soak cell
(`alma9-podrf-soak`).

| Cells | Operating system | Runtime |
|-------|------------------|---------|
| `u24-docker-ctrd-ag`, `-up`, `-cases`, `-extdb` | Ubuntu 24.04 | Docker, containerd image store |
| `u24-docker-classic-ag`, `-up` | Ubuntu 24.04 | Docker, classic overlay2 image store |
| `u24-podrf-ag`, `-up`, `-cases` | Ubuntu 24.04 | Podman, rootful |
| `u24-podrl-ag`, `-up` | Ubuntu 24.04 | Podman, rootless |
| `alma9-podrf-ag`, `-up`, `-cases`, `-extdb`, `-soak` | AlmaLinux 9 (SELinux enforcing, firewalld, NetworkManager) | Podman, rootful |
| `alma9-podrl-ag`, `-up`, `-cases` | AlmaLinux 9 | Podman, rootless |

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

## What an Upgrade cell checks

An Upgrade cell starts as an Install cell does (a fresh VM, the air gap, the bundle's checksums), then follows the
[Upgrading guide](../../../../docs/administration/upgrading.md). Wherever the guide gives a command, the cell runs that
command, read from the page (`Get-DocumentedCommand.ps1`), so a guide that goes wrong fails the cell; for a rootless
JIM, rewritten with `jim-systemctl` and `jim-podman` as Running on Podman says.

1. **The previous release**, installed with its own installer, with an administrator's own `docker-compose.local.yml`
   beside the installer's on Docker, and **real data**: a Connected System on the LDAP Connector against the lab's test
   directory, with its bind password stored encrypted, a Synchronisation Rule, and Full Import and Full
   Synchronisation run, giving five people in the Metaverse (`Invoke-LabUpgradeData.ps1`, with the JIM PowerShell
   module from that release's bundle).
2. **Before upgrading**: JIM stopped, and the database and encryption keys backed up, by Backup & Disaster Recovery.
3. **The upgrade**: on Docker, the bundle's `setup.sh --upgrade`; on Podman, the guide's steps, with the notes that
   apply when upgrading from v0.16.0 or earlier (PostgreSQL's memory in `jim-config.yaml`; on AlmaLinux, firewalld's
   reload service and NetworkManager; on Ubuntu's rootful Podman, the AppArmor rule and a server restart).
4. **Verified, then again after a reboot**: ready from another machine, the new version reported, the data all there
   and the stored credential still decrypting (a Full Import binds with it), the administrator signing in, the own
   compose file still in use, and PostgreSQL's memory sized to the host.
5. **Rolled back by the guide**: the backup restored into an empty database, the previous files put back, and JIM
   started; then the previous version reported, its data and credential, a sign-in, and nothing written after the
   upgrade left behind. On Docker, the installer must have kept the previous release's PostgreSQL image as
   `postgres:jim-previous`; before starting, every other name is taken off it, as Docker's containerd image store
   leaves it when the new release's image takes over its tag (#2299), and the rolled-back database must run that very
   image. An air-gapped host cannot download it, so this fails the cell whether or not the two releases' images
   differ.

On Docker, before upgrading, the installer must also refuse an upgrade when the administrator says there is no
backup; put everything back after an upgrade from a damaged bundle (an image cut short) fails partway, leaving the
previous release running; and refuse an upgrade over a compose file edited by hand, once the release upgraded from
records its compose files as installed. After upgrading, the same upgrade again must report nothing to upgrade, and the
previous release's installer must refuse to downgrade. Each refusal must leave every file of the installation and every
container exactly as they were.

The release upgraded from is the latest published one, fetched from GitHub once, or `-PreviousBundlePath`. A build from
a commit whose `VERSION` is not newer reports the next patch version, so that the installer takes it as an upgrade.

## What an InstallerCases cell checks

What the installer does with settings it must refuse, with a run stopped partway, and when it is run again over an
installation. Whatever it refuses, or is stopped from doing, must leave the host exactly as it was: every file of the
installation (by checksum) and every JIM container running (by ID and image).

1. **Refused before anything is written**: a malformed `JIM_INFRASTRUCTURE_API_KEY`, and a `JIM_DB_SHARED_BUFFERS`
   larger than the host's memory, each leave nothing in `/opt/jim`.
2. **The install that follows**: ready, a sign-in, a generated database password (not the template's placeholder), the
   infrastructure API key accepted by the API, and `Connect-JIM` (from the bundle) reporting a key JIM rejects.
3. **Run again without an answer**: the installer asks before overwriting the installation, and leaves it alone.
4. **Run again, answering yes**: JIM serves the newly issued certificate at once (#1956), keeping the certificate
   authority, the database, its data and its password.
5. **`JIM_DB_SHARED_BUFFERS` given**: over half the host's memory warns and is used; more than the host has is refused
   on a reinstall, changing nothing and setting nothing aside (#1994), and JIM starts as documented afterwards.
6. **Ctrl+C at a question**: the installer, waiting at a question, is sent SIGINT as a terminal sends it; everything is
   put back, and JIM starts as documented afterwards.
7. **Looking after the certificate**, as TLS and Reverse Proxy says, last because from here the certificate authority
   the cell trusts changes:
   1. `--renew-certificate`, run as the page gives it: JIM serves a new certificate for a year, from the same
      certificate authority and for the same names.
   2. `--certificate` for one more name: the installer creates a new certificate authority and says to distribute it;
      JIM serves the new name, and the old authority no longer vouches for it.
   3. `--certificate` with an organisation's certificate: the lab issues one from a root and an issuing CA of its own,
      given as the certificate followed by the issuing CA's. Trusting the root alone, the cell finds JIM ready and
      serving exactly that certificate, which proves the whole chain is served.
   4. `--renew-certificate` over the organisation's certificate: refused, pointing to `--certificate`, changing nothing.

As every cell does, it ends by checking that nothing crashed quietly.

## What an ExternalDatabase cell checks

1. **A PostgreSQL server of the customer's own**: PostgreSQL 18 from the PostgreSQL project's packages on a VM of its
   own (`DatabaseServer` in `Lab.psd1`), JIM's user and database created by running the SQL from the deployment
   guide's Before You Install as written, then moved onto the cell's air-gapped network.
2. **JIM installed against it** with `setup.sh`: no bundled database runs, JIM creates its tables in the external
   database, it is ready, and the administrator signs in.
3. **A host reboot**: JIM starts by itself, with its data, and the administrator signs in again.
4. **Started again as documented** for a database server of the customer's own (without the bundled database's
   profile): still no bundled database.
5. **Nothing crashed quietly**, as every cell checks.

The database server is a second VM, `jl-<cell>-db`, given the cell's `DatabaseMemoryGiB`, and deleted with the cell's.

## What a Soak cell checks

A fault that strikes one restart in a few passes a single restart most of the time: #2009 (NetworkManager unplugging
rootful Podman pods from their bridge) failed 6 of 20 restarts, so a cell that restarts JIM once passed about two runs
in three. Such a fault need not come from JIM: an operating system's update to NetworkManager, Podman or systemd can
bring one back. The Soak cell, on AlmaLinux with rootful Podman where #2009 was found, repeats what the other cells do
once:

1. **Installed** with `setup.sh`, ready, and the administrator signs in.
2. **Restarted 20 times** as Operating JIM says (`systemctl restart jim.service`): ready again within 300 s every
   time, then the data kept and a sign-in.
3. **The host rebooted 5 times**: JIM starts by itself every time, with its data, then a sign-in.
4. **Nothing crashed quietly**, as every cell checks.

A round that fails saves the VM's journal and container list beside `cell.log`; after three failed rounds a loop stops,
as its check has failed. The cell takes about 7 minutes when JIM is healthy (each failed round adds 300 s), alongside
the others.

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

# The upgrades only, from a particular release
pwsh ./test/ci/deployment/lab/Invoke-DeploymentLab.ps1 -Commit HEAD -Tag upgrade -PreviousBundlePath ~/bundles/jim-release-0.16.0.tar.gz

# Several names or tags: through -Command, as -File passes a list as one string
pwsh -Command "& ./test/ci/deployment/lab/Invoke-DeploymentLab.ps1 -Commit HEAD -Name '*-cases','*-extdb'"

# What would run
pwsh ./test/ci/deployment/lab/Invoke-DeploymentLab.ps1 -Commit HEAD -List
```

Each run writes a folder (default `./deployment-lab-<date and time>`) holding `report.md` and `results.json`, and a
folder per cell with its `cell.log`, the boot check's `boot.log` and output, and the installer's certificate
authority. The runner exits 1 if any cell failed. A kept VM is named `jl-<cell>`; `lxc exec jl-<cell> -- bash` gets a
shell on it, and `lxc delete --force jl-<cell>` removes it. It keeps its memory until then, which the runner does not
count, so a run that keeps several can leave too little for the cells after them.

**How many cells run at once** is decided by memory: each cell's VM is given its `MemoryGiB` (and an ExternalDatabase
cell's database server its `DatabaseMemoryGiB`), and the runner starts cells, largest first, while they fit in the
host's memory less `ReserveGiB` (Lab.psd1). `-BudgetGiB` and
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
