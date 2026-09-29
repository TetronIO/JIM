# Active Directory lab

The lab is three single-domain-controller Active Directory forests on Windows Server 2025 Datacenter, built by
scripts from installation media, kept as Hyper-V production checkpoints, and reverted between scenarios. It exists so
JIM's LDAP Connector meets a real Windows domain controller before a release, instead of only Samba AD. The design and
the reasons are in [`engineering/prd/doing/PRD_ACTIVE_DIRECTORY_LAB.md`](../../../engineering/prd/doing/PRD_ACTIVE_DIRECTORY_LAB.md).

| VM name      | Forest         | Domain controller  | Base DN              | Recycle Bin |
|--------------|----------------|--------------------|----------------------|-------------|
| `dc-primary` | PANOPLY.LOCAL  | dc1.panoply.local  | DC=panoply,DC=local  | on          |
| `dc-source`  | RESURGAM.LOCAL | dc1.resurgam.local | DC=resurgam,DC=local | off         |
| `dc-target`  | GENTIAN.LOCAL  | dc1.gentian.local  | DC=gentian,DC=local  | off         |

Each forest is at the Windows Server 2016 functional level. Every domain controller carries the same directory content
as the Samba images (`test/integration/docker/samba-ad-prebuilt/post-provision.sh`): the OUs `Corp`, `Users` and
`Groups` under it, `TestUsers`, `TestGroups` and `Services`; the `svc-jim` account in the `JIM Connectors` group; the
delegation from `jim-ad-delegation.acl`, applied unchanged; read over Deleted Objects; and an LDAPS certificate with the
same subject and names. It differs from Samba where Windows Server 2025 differs: LDAP signing is enforced (everything
talks LDAPS on 636) and the domain password policy keeps its defaults (complexity on).

## What is in this folder

| Path | Runs on | What it does |
|------|---------|--------------|
| `host/New-LabDomainController.ps1` | Hyper-V host | Builds a domain controller from the ISO, unattended, and takes the `baseline` checkpoint |
| `host/Restore-LabDomainController.ps1` | Hyper-V host | Reverts to a named checkpoint, starts the VM, waits for the guest and LDAPS |
| `host/Checkpoint-LabDomainController.ps1` | Hyper-V host | Takes a production checkpoint (`-Replace` to overwrite one) |
| `host/Get-LabDomainController.ps1` | Hyper-V host | State, checkpoints and guest OS build (`-AsJson` for the runner) |
| `host/Remove-LabDomainController.ps1` | Hyper-V host | Stops and removes the VM, its checkpoints and its disk (needs `-Force`) |
| `host/Grant-LabDelegation.ps1` | Hyper-V host | Applies the delegation to a container created at run time (`-Tombstones` for Deleted Objects) |
| `host/Invoke-LabRebuild.ps1` | Hyper-V host | One phase of the monthly rebuild (`Prune`, `Build`, `Stage`, `Promote`, `Rollback`, `Status`): builds a candidate set beside the live one and swaps names only when the suite passes; see [Monthly rebuild](#monthly-rebuild) |
| `host/Install-LabScripts.ps1` | Hyper-V host or the repository clone | Copies the host and guest scripts into `C:\jim-ad-lab` in the layout above; see [Deploying to the host](#deploying-to-the-host) |
| `guest/autounattend.xml` | Guest (via ISO) | Template for the unattended install; rendered by the module |
| `guest/Initialize-LabDomainController.ps1` | Guest | The build phases: `Prepare`, `Promote`, `Configure`, `Verify` |
| `guest/LabDomainController.psm1` | Both | Pure functions (tested) and the Windows-only helpers |
| `LabDomainController.Tests.ps1` | CI (any OS) | Pester tests for every pure function, the rebuild planning included |
| `LabDnsForwarder.Tests.ps1` | CI (any OS) | Pester tests for the optional DNS forwarder of the build scripts |
| `Install-LabScripts.Tests.ps1` | CI (any OS) | Pester tests for the deployment layout and the settings example |

The scheduled rebuild after Patch Tuesday (PRD functional requirement 8) is `host/Invoke-LabRebuild.ps1`, driven by
`ad-lab-rebuild.yml`; see [Monthly rebuild](#monthly-rebuild).

## Host prerequisites

The hypervisor host needs, once:

1. **Windows Server Datacenter with the Hyper-V role, activated.** Guests then activate through Automatic Virtual
   Machine Activation (AVMA) with the generic Windows Server 2025 Datacenter key in the unattend file; no per-VM licence
   is involved. AVMA needs the host activated and the guest's Data Exchange integration service enabled (the build does
   that). Evaluation editions are not used: an evaluation domain controller cannot be converted and its timer runs in
   real time regardless of checkpoint reverts. The build refuses Evaluation media.
2. **An Internal virtual switch called `Lab`** (not External, and not Private), which the domain controllers and the
   runner VM's second adapter attach to. An Internal switch gives the host an adapter of its own on the lab network and
   has no uplink, so nothing routes out: the domain controllers reach only the host and each other, and the host does not
   forward. The host is the only NTP-synchronised machine they can reach, so it serves NTP on that adapter (UDP 123,
   that interface only, and no SSH); and it needs a path to them for diagnostics and for
   `Restore-LabDomainController.ps1`'s TCP check when no password is available. The runner VM is the only machine that
   bridges the CI network and the lab. Nothing in the lab trusts, or is trusted by, any real domain.
3. **OpenSSH Server, with a dedicated `jim-lab` account** that is a member of the local **Hyper-V Administrators**
   group, and **PowerShell 7 (`pwsh`)** installed. The runner connects as this account with a key; it is the only door
   into the control plane, and it holds no hypervisor credentials on the runner.
4. **NTP, from the host.** The domain controllers take their time from the host's own address on the `Lab` switch (pass
   it to the build as `-NtpServer`); the host follows an upstream source, the one the runner VM follows too, and serves
   it on the `Lab` adapter only. A domain controller reverted to a checkpoint boots with the host's clock and stays
   within the runner's 5 second window.
5. **Non-evaluation Windows Server 2025 media** (an ISO), and optionally the latest cumulative update (a `.msu`).

Preparing the host is the ci-runners repository's job (its `docs/hyperv-host.md`). Then deploy the scripts with
`Install-LabScripts.ps1`, described in "Deploying to the host" below: the runner calls
`<JIM_AD_LAB_SCRIPT_ROOT>\<script>.ps1` (default `C:\jim-ad-lab`), so the host scripts sit in that folder beside `guest`
and `delegation`. The scripts also run straight from a repository checkout (`test\integration\ad-lab\host`), which is how
to try them first. Redeploy after any change to the lab code; the delegation file in particular is the same file the
Samba image and the connector documentation use, so a change there needs redeploying too.

Two secrets are needed, the Administrator password (the same in all three forests) and the `svc-jim` password. Pass them
to the build as `SecureString` parameters, or set the environment variables `JIM_AD_LAB_ADMIN_PASSWORD` and
`JIM_AD_LAB_JIM_PASSWORD`. They are never written to disk or logged, apart from the encoded Administrator password in the
unattend ISO, which is deleted as soon as the install finishes. The control-plane scripts the runner calls
(`Restore`, `Get`, `Grant-LabDelegation`) read `JIM_AD_LAB_ADMIN_PASSWORD` from the environment and never accept it as a
parameter, because a command line is visible to other processes and to SSH logs. Set it for the `jim-lab` account (a
user-level variable, so sshd sessions for that account load it), not machine-wide.

## Deploying to the host

Preparing the host itself is not JIM's job. Hyper-V, OpenSSH, the `jim-lab` account and its key, the `CI` and `Lab`
switches and the Linux runner virtual machine are set up from the TetronIO/ci-runners repository, whose
`docs/hyperv-host.md` is a phase-by-phase runbook (written for an agent working on the host with the operator watching);
phases 1 to 4 of it come first. This section is the JIM-specific part: getting these scripts onto that host and building
the three domain controllers.

**1. Deploy the scripts.** Clone this repository on the host (the runbook uses `C:\src\JIM`) and, from an elevated
PowerShell 7 session, run:

```powershell
cd C:\src\JIM
.\test\integration\ad-lab\host\Install-LabScripts.ps1 -CreateSettings
```

`Install-LabScripts.ps1` copies the host scripts flat into `C:\jim-ad-lab` (or `-Destination`), the `guest` folder beside
them and the delegation file into `delegation`, which is the layout `JIM_AD_LAB_SCRIPT_ROOT` points the runner at. It is
idempotent, prints `new`, `updated` or `unchanged` per file, never deletes anything, and does not deploy itself. It also
writes `settings.example.json`, and with `-CreateSettings` creates `settings.json` from it when there is none; it refuses
(before copying anything) if `settings.json` already exists, and never touches that file otherwise. Run it again after any
change to the lab code, and after pulling a new release: the scripts on the host are a copy, not the checkout.

**2. Fill in `settings.json`.** Every value in the example is a placeholder. `Invoke-LabRebuild.ps1` reads this file, and
the first build below is easiest when it is driven from the same values:

| Key | Meaning |
|-----|---------|
| `isoPath` | The non-evaluation Windows Server 2025 ISO |
| `cumulativeUpdateDirectory` | Where the newest cumulative update `.msu` is kept (the rebuild picks the latest) |
| `vhdDirectory` | Where the virtual machines' disks live |
| `switchName` | The Internal `Lab` switch the domain controllers attach to (not the external `CI` switch) |
| `ntpServer` | The host's own address on the `Lab` switch (for example `10.99.0.1`): the only NTP-synchronised machine the domain controllers can reach |
| `domainControllers` | Per virtual machine (`dc-primary`, `dc-source`, `dc-target`): `domain`, `ipAddress`, `prefixLength`, `gateway` (also the host's address on `Lab`, which leads nowhere by design: the switch has no uplink), `enableRecycleBin` |

There is no `dnsForwarder`: the lab network has no uplink, so a domain controller has nothing to forward DNS to and
resolves through itself.

The addresses must agree with what the runner virtual machine is told, `JIM_AD_LAB_PRIMARY_ADDRESS`, `_SOURCE_ADDRESS` and
`_TARGET_ADDRESS`, which the runbook writes into the runner's `.env`. Neither file holds a password.

**3. The two passwords** are user-level environment variables of the `jim-lab` account, set once by the operator:
`JIM_AD_LAB_ADMIN_PASSWORD` and `JIM_AD_LAB_JIM_PASSWORD`. The runbook's `New-LabControlAccount.ps1 -SetSecrets` prompts
for both as secure strings and writes them there, so neither appears on a command line or in a file that could be
committed. They are the same passwords for all three forests.

**4. Build the domain controllers, one at a time, in this order:** `dc-primary`, `dc-source`, `dc-target`. Each takes
40 to 60 minutes. The build script keeps its explicit parameters, so this maps the settings onto them for one virtual
machine; set `$name` to each in turn and check it (step 5) before starting the next:

```powershell
$name = 'dc-primary'    # then dc-source, then dc-target
$settings = Get-Content C:\jim-ad-lab\settings.json -Raw | ConvertFrom-Json
$dc = $settings.domainControllers.$name
$admin = Read-Host 'Administrator password' -AsSecureString
$jim = Read-Host 'svc-jim password' -AsSecureString

$arguments = @{
    Name = $name; Domain = $dc.domain; IsoPath = $settings.isoPath; VhdDirectory = $settings.vhdDirectory
    SwitchName = $settings.switchName; IPAddress = $dc.ipAddress; PrefixLength = $dc.prefixLength; Gateway = $dc.gateway
    NtpServer = $settings.ntpServer
    AdministratorPassword = $admin; ServiceAccountPassword = $jim
}
if ($dc.enableRecycleBin) { $arguments.EnableRecycleBin = $true }
$update = Get-ChildItem $settings.cumulativeUpdateDirectory -Filter *.msu -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if ($update) { $arguments.CumulativeUpdatePath = $update.FullName }

C:\jim-ad-lab\New-LabDomainController.ps1 @arguments
```

Re-running it after a failure is safe (see "Building a domain controller" below); an unattended run over SSH is not
what this is for, because the passwords are prompted for.

**5. Verify each one** before the next build:

```powershell
C:\jim-ad-lab\Get-LabDomainController.ps1 -Name dc-primary
```

It should report the virtual machine `Running`, a `baseline` checkpoint, checkpoints `ProductionOnly` and a guest OS build
(a Windows Server 2025 build, 26100 or later). The last proof is the production path itself, run from the runner virtual
machine (or from anywhere holding the `jim-lab` key), because it exercises the account's rights, its environment
variable and PowerShell Direct together:

```text
ssh jim-lab@<host> pwsh -NoProfile -NonInteractive -File C:\jim-ad-lab\Get-LabDomainController.ps1 -Name dc-primary -AsJson
```

The output is one JSON object whose `guest` member is not `null`. A `null` guest with the virtual machine running means
PowerShell Direct did not work for that account, or `JIM_AD_LAB_ADMIN_PASSWORD` is missing from its environment; the
ci-runners runbook says what to check.

## Building a domain controller

Each build takes roughly 40 to 60 minutes: install, promotion, configuration, verification, checkpoint. Run it in an
elevated PowerShell 7 session on the host, once per domain controller. Substitute your own paths and addresses. The
examples take `10.99.0.1` as the host's address on the `Lab` switch: it is both the time source and the gateway (which
leads nowhere), and there is no `-DnsForwarder`, because the network has no uplink.

```powershell
$admin = Read-Host 'Administrator password' -AsSecureString
$jim   = Read-Host 'svc-jim password' -AsSecureString

# dc-primary: PANOPLY.LOCAL, Recycle Bin on
.\New-LabDomainController.ps1 -Name dc-primary -Domain PANOPLY.LOCAL -IsoPath D:\iso\ws2025.iso -VhdDirectory D:\vm `
    -SwitchName Lab -IPAddress 10.99.0.11 -PrefixLength 24 -Gateway 10.99.0.1 `
    -NtpServer 10.99.0.1 -AdministratorPassword $admin -ServiceAccountPassword $jim -EnableRecycleBin

# dc-source: RESURGAM.LOCAL, Recycle Bin off
.\New-LabDomainController.ps1 -Name dc-source -Domain RESURGAM.LOCAL -IsoPath D:\iso\ws2025.iso -VhdDirectory D:\vm `
    -SwitchName Lab -IPAddress 10.99.0.12 -PrefixLength 24 -Gateway 10.99.0.1 `
    -NtpServer 10.99.0.1 -AdministratorPassword $admin -ServiceAccountPassword $jim

# dc-target: GENTIAN.LOCAL, Recycle Bin off
.\New-LabDomainController.ps1 -Name dc-target -Domain GENTIAN.LOCAL -IsoPath D:\iso\ws2025.iso -VhdDirectory D:\vm `
    -SwitchName Lab -IPAddress 10.99.0.13 -PrefixLength 24 -Gateway 10.99.0.1 `
    -NtpServer 10.99.0.1 -AdministratorPassword $admin -ServiceAccountPassword $jim
```

Useful extras: `-CumulativeUpdatePath <msu>` installs a cumulative update between promotion and configuration;
`-ExtraCertificateNames <names>` adds DNS names to the LDAPS certificate (beyond the FQDN, the short name and the
domain); `-ComputerName` changes the host name from `dc1`; `-MemoryStartupBytes`, `-ProcessorCount` and `-VhdSizeBytes`
size the VM; `-SkipBaselineCheckpoint` builds without checkpointing.

What the build does, in order: creates a Generation 2 VM (UEFI, Secure Boot, static memory, production checkpoints only);
builds a second small ISO holding a rendered `autounattend.xml` (pure PowerShell, IMAPI2, no extra tool); installs Windows
Server 2025 Datacenter (Desktop Experience) unattended with the AVMA key, then ejects the media and deletes that ISO;
copies the guest files into `C:\jim-ad-lab` in the guest; runs `Initialize-LabDomainController.ps1` phase by phase over
PowerShell Direct, restarting between phases (`Prepare` rename, static IP and roles; `Promote` new forest; optional
cumulative update; `Configure` directory content, delegation, LDAPS certificate, Recycle Bin, Windows Update off, w32time;
`Verify` a read-back of all of it plus the same probes `post-provision.sh` runs as `svc-jim` over LDAPS); and finally takes
the `baseline` checkpoint. There is no guest listener and no OpenSSH in the guests: PowerShell Direct is the only control
path and needs no network.

Re-running the same command after a failure is safe. If the VM already exists the create step is skipped, and each guest
phase checks whether it has already happened (role installed, domain exists, OU exists, certificate with that subject
exists) and does nothing if so. To start again from nothing, run `Remove-LabDomainController.ps1 -Name <vm> -Force` first.

The guest phases can be run by hand through PowerShell Direct when investigating, for example
`Invoke-Command -VMName dc-primary -Credential $cred -ScriptBlock { & C:\jim-ad-lab\Initialize-LabDomainController.ps1 -Phase Verify ... }`;
run `Get-Help .\guest\Initialize-LabDomainController.ps1 -Full` for the parameters.

## Checkpoint lifecycle

Every state worth returning to is a Hyper-V **production** checkpoint (`ProductionOnly`, which fails rather than falling
back to a standard checkpoint with saved memory). Reverting one boots the guest fresh, sets its clock from the host, and
the domain controller renews its invocationId, exactly as a restore from backup does (Microsoft's virtualised domain
controller "safe restore"). That is intended: the next Delta Import fails fast naming the invocationId change, and a Full
Import re-establishes the baseline.

| Checkpoint | Taken by | When |
|------------|----------|------|
| `baseline` | `New-LabDomainController.ps1` | After the build and its verification |
| `populated-<template>-<hash>` | The runner, through `Checkpoint-LabDomainController.ps1` | After population; template lower-case, hash 16 lower-case hex characters covering the populate scripts, as the Samba snapshot label does |

Names are built by `Get-LabCheckpointName` in the module and validated by the control-plane scripts, which refuse any
other name. The runner rejects a populated checkpoint whose hash does not match the current scripts and rebuilds it.
Reverts are unbounded (each consumes a RID pool), and the age of a checkpoint set is bounded by the monthly rebuild, far
short of the 180-day tombstone lifetime.

The control-plane scripts only ever touch VMs this lab created: the build writes a marker and the forest metadata into
the VM's Notes, and every other script refuses a VM without it.

## How the runner calls the control plane

The runner VM (label `ad-lab`) never holds hypervisor credentials. It runs, over OpenSSH as `jim-lab`:

```text
ssh -p <port> [-i <key>] jim-lab@<host> pwsh -NoProfile -NonInteractive -File "<root>\<script>.ps1" <arguments>
```

through `Invoke-LabControl -Script <name>.ps1 -Arguments <string[]>`, which returns standard output and throws on a
non-zero exit code with standard error in the message. Every script exits non-zero on failure and prints JSON on
standard output only where the runner needs data (`Get-LabDomainController.ps1 -AsJson`).

| Runner step | Script | Notes |
|-------------|--------|-------|
| Reset between scenarios | `Restore-LabDomainController.ps1 -Name <vm> -Checkpoint <name>` | Waits for the guest (PowerShell Direct) and LDAPS on 636; fails if the checkpoint does not exist |
| After population | `Checkpoint-LabDomainController.ps1 -Name <vm> -Checkpoint populated-<template>-<hash> -Replace` | |
| Record the OS build in run metadata | `Get-LabDomainController.ps1 -Name <vm> -AsJson` | `{ name, state, generation, checkpointType, uptimeSeconds, checkpoints: [{ name, createdUtc }], guest: { productName, displayVersion, currentBuild, ubr, buildString } or null }` |
| A scenario creates a container at run time | `Grant-LabDelegation.ps1 -Name <vm> -ContainerDn <dn>` | The Windows counterpart of `jim-delegate.sh`; idempotent; one output line per outcome. Runs the same guest function the build's `Configure` phase uses |

The host-side variables the runner reads are `JIM_AD_LAB_CONTROL_HOST`, `JIM_AD_LAB_CONTROL_USER` (default `jim-lab`),
`JIM_AD_LAB_CONTROL_KEY`, `JIM_AD_LAB_CONTROL_PORT` (default `22`) and `JIM_AD_LAB_SCRIPT_ROOT` (default `C:\jim-ad-lab`),
alongside the per-instance addresses, host names, VM names and the two passwords listed in the PRD's harness section.

## Monthly rebuild

The lab is patched by rebuild and never in place: the domain controllers do not run Windows Update, and once a month
they are built again from the Windows Server media plus the newest cumulative update. `ad-lab-rebuild.yml` does it the
Wednesday after Patch Tuesday, on the lab runner, by driving `host/Invoke-LabRebuild.ps1` over the control plane one
phase at a time. It builds a candidate set beside the live one, runs the whole suite against the candidates once, and
only if that passes lets the nightly adopt them. Because the guest OS build is recorded with every run, a failure after
a rebuild is attributable to the Windows update or to JIM.

**When.** GitHub's cron treats day-of-month and day-of-week as OR, so the workflow fires every Wednesday at 03:00 UTC
and its first job lets the rebuild proceed only when the UTC day of the month is 9 to 15 (the day after the second
Tuesday). On any other Wednesday it ends with a notice and touches nothing. `workflow_dispatch` applies the same rule
unless `force` is set; `fresh` replaces a candidate that already exists instead of reusing a recent one. It shares the
nightly's concurrency group, so the two never overlap, and a run is never cancelled by a newer one.

**The phases.** Each is idempotent and prints one JSON document with `-AsJson`; run again after a failure, it converges.

| Phase | What it does |
|-------|--------------|
| `Prune` | Removes any `dc-<name>-prev` demoted 7 or more days ago. Runs first, so the old set's disk is free before three new ones are built |
| `Build` | Per domain controller: stops the live VM, builds `dc-<name>-candidate` from `settings.json` with `New-LabDomainController.ps1` (including the newest `.msu` in `cumulativeUpdateDirectory`, or the media as it is when there is none), takes `baseline`. A finished candidate under a day old is reused, an unfinished one resumed, an older one (a leftover from a red run) replaced |
| `Stage` | Stops the live VMs and starts the candidates from `baseline`, waiting until each answers on LDAPS |
| (suite) | The workflow runs `Run-IntegrationTests.ps1 -Scenario All -Template Medium -DirectoryType ActiveDirectory` and the connector probes with `JIM_AD_LAB_PRIMARY_VM`, `_SOURCE_VM` and `_TARGET_VM` set to the candidates for that run only. Nothing else in the configuration changes |
| `Promote` | Green: renames each live VM `dc-<name>-prev` (stamping `demoted=<UTC time>` in its Notes) and each candidate `dc-<name>`. Every candidate is checked before any is renamed, and an interrupted promotion finishes when run again |
| `Rollback` | Not green: stops the candidates, leaves them in place for diagnosis, and starts the live VMs again from `baseline`. Also puts the live VM back if a promotion was interrupted after it was demoted |
| `Status` | Read-only: which VMs exist, their state, and the live guest OS build |

**The live domain controllers are off from `Build` until `Promote` or `Rollback`.** A candidate is built with the live
one's own address, because the checkpoints carry it and it cannot be changed afterwards, and two running domain
controllers cannot share an address. That is why the shared concurrency group matters, and why a run that dies mid-way
needs the recovery below.

**Sets and disk.** The live set is always `dc-primary`, `dc-source` and `dc-target`, so the nightly's configuration never
changes; a candidate is `dc-<name>-candidate` and the previous set `dc-<name>-prev`. Each rebuild's disks go in their own
folder, `<vhdDirectory>\rebuild-yyyyMMdd\<vm>\`, because a promoted candidate keeps the folder it was built in and the next
candidate has the same name. `Remove-LabDomainController.ps1` only deletes a VM's folder when it carries the VM's name,
which a renamed VM's does not, so the rebuild clears such folders itself (only beneath `vhdDirectory`, only when named for
a lab VM, and never one that still holds a disk). Plan disk for up to three sets of three dynamically expanding disks
(live, previous and candidate; 80 GB maximum each, far less in use).

**The cumulative update.** `cumulativeUpdateDirectory` in `settings.json` should hold only the update wanted: the newest
`.msu` by modification time is installed, so a servicing stack update saved later would win.

**Run one by hand** (from the checkout on the host, or over SSH as the runner does):

```powershell
C:\jim-ad-lab\Invoke-LabRebuild.ps1 -Phase Status
C:\jim-ad-lab\Invoke-LabRebuild.ps1 -Phase Build -Role dc-primary   # one domain controller; -Role also takes a list
```

It reads `settings.json` beside it (`-SettingsPath` to point elsewhere; only `Build` reads more than the names, so a
rollback is never blocked by a settings file that is wrong for building), logs to `%ProgramData%\jim-ad-lab\logs`, and
needs the two passwords in the `jim-lab` account's environment like the other control-plane scripts. It runs only what
Hyper-V Administrators may run: no elevation.

**Recovery.** If a run is cancelled, the runner dies or the host restarts part way, the live set may still be off and the
candidates running. On the host, as `jim-lab`:

```powershell
C:\jim-ad-lab\Invoke-LabRebuild.ps1 -Phase Status     # what exists and in what state
C:\jim-ad-lab\Invoke-LabRebuild.ps1 -Phase Rollback   # live set back in service, candidates stopped
```

Then dispatch `ad-lab.yml` to confirm, and `ad-lab-rebuild.yml` with `force` to try again (add `fresh` to build new
candidates rather than reuse the ones left behind). If a `Promote` failed part way, run `Promote` again rather than
`Rollback`: it finishes the swap, whereas a rollback restores each domain controller's own live VM and could leave the
three forests on different builds.

**Not in scope.** The rebuild never touches the runner VM or the hypervisor host. Patching the host is a manual,
announced event because it restarts every virtual machine: do it when no lab run is in progress, then run `Status` and
dispatch `ad-lab.yml`.

**Tested where it can be.** The decisions (VM names, the settings mapping, which build action, the promote and rollback
plans, the 7-day rule, the demotion stamp, the cumulative update choice) are pure functions in the module, and the phases
run over an in-memory adapter in `LabDomainController.Tests.ps1`, including interrupted promotions and a failed build.
The Hyper-V calls behind the adapter are in `Invoke-LabRebuild.ps1` and are proven by the first real rebuild.

## Testing the lab code

The decisions that can be made without Hyper-V are pure functions in `guest/LabDomainController.psm1`, and are tested on
any platform, including Linux CI: SDDL rendering from the `.acl` file (compared against the exact shell pipeline
`jim-delegate.sh` uses, where bash is available), DACL merging, checkpoint naming, unattend rendering, certificate name
lists, OS build parsing, the argument strings for the guest phases and the JSON status shape. From the repository root:

```powershell
Invoke-Pester -Path ./test/integration/ad-lab -Output Detailed
Invoke-ScriptAnalyzer -Path ./test/integration/ad-lab -Recurse -Severity Warning,Error
```

The Hyper-V and Active Directory paths cannot run in CI; they are proven by building a domain controller, whose `Verify`
phase fails the build if anything is wrong.
