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
| `guest/autounattend.xml` | Guest (via ISO) | Template for the unattended install; rendered by the module |
| `guest/Initialize-LabDomainController.ps1` | Guest | The build phases: `Prepare`, `Promote`, `Configure`, `Verify` |
| `guest/LabDomainController.psm1` | Both | Pure functions (tested) and the Windows-only helpers |
| `LabDomainController.Tests.ps1` | CI (any OS) | Pester tests for every pure function |

`Invoke-LabRebuild.ps1`, the scheduled rebuild after Patch Tuesday (PRD functional requirement 8), is a later phase and
is not here yet. Until it lands, a rebuild is `Remove-LabDomainController.ps1 -Force` followed by
`New-LabDomainController.ps1` with the newest media and cumulative update.

## Host prerequisites

The hypervisor host needs, once:

1. **Windows Server Datacenter with the Hyper-V role, activated.** Guests then activate through Automatic Virtual
   Machine Activation (AVMA) with the generic Windows Server 2025 Datacenter key in the unattend file; no per-VM licence
   is involved. AVMA needs the host activated and the guest's Data Exchange integration service enabled (the build does
   that). Evaluation editions are not used: an evaluation domain controller cannot be converted and its timer runs in
   real time regardless of checkpoint reverts. The build refuses Evaluation media.
2. **An external virtual switch on the CI VLAN**, so the domain controllers are reachable from the runner VM and from
   nothing else. Nothing in the lab trusts, or is trusted by, any real domain.
3. **OpenSSH Server, with a dedicated `jim-lab` account** that is a member of the local **Hyper-V Administrators**
   group, and **PowerShell 7 (`pwsh`)** installed. The runner connects as this account with a key; it is the only door
   into the control plane, and it holds no hypervisor credentials on the runner.
4. **NTP.** Point the host at the same time source the runner uses (and pass it to the build as `-NtpServer`), so a
   domain controller reverted to a checkpoint boots with the host's clock and stays within the runner's 5 second window.
5. **Non-evaluation Windows Server 2025 media** (an ISO), and optionally the latest cumulative update (a `.msu`).

Then deploy the scripts. The runner calls `<JIM_AD_LAB_SCRIPT_ROOT>\<script>.ps1` (default `C:\jim-ad-lab`), so the host
scripts sit in the root, beside the `guest` folder and the delegation file. From the repository root:

```powershell
$root = 'C:\jim-ad-lab'
New-Item -ItemType Directory -Force -Path $root, "$root\delegation" | Out-Null
Copy-Item test\integration\ad-lab\host\*.ps1 $root -Force
Copy-Item test\integration\ad-lab\guest $root -Recurse -Force
Copy-Item test\integration\docker\samba-ad-prebuilt\delegation\jim-ad-delegation.acl "$root\delegation" -Force
```

The scripts also run straight from a repository checkout (`test\integration\ad-lab\host`), which is how to try them first.
Redeploy after any change to the lab code; the delegation file in particular is the same file the Samba image and the
connector documentation use, so a change there needs redeploying too.

Two secrets are needed, the Administrator password (the same in all three forests) and the `svc-jim` password. Pass them
to the build as `SecureString` parameters, or set the environment variables `JIM_AD_LAB_ADMIN_PASSWORD` and
`JIM_AD_LAB_JIM_PASSWORD`. They are never written to disk or logged, apart from the encoded Administrator password in the
unattend ISO, which is deleted as soon as the install finishes. The control-plane scripts the runner calls
(`Restore`, `Get`, `Grant-LabDelegation`) read `JIM_AD_LAB_ADMIN_PASSWORD` from the environment and never accept it as a
parameter, because a command line is visible to other processes and to SSH logs. Set it for the `jim-lab` account (a
user-level variable, so sshd sessions for that account load it), not machine-wide.

## Building a domain controller

Each build takes roughly 40 to 60 minutes: install, promotion, configuration, verification, checkpoint. Run it in an
elevated PowerShell 7 session on the host, once per domain controller. Substitute your own paths, switch, addresses and
time source.

```powershell
$admin = Read-Host 'Administrator password' -AsSecureString
$jim   = Read-Host 'svc-jim password' -AsSecureString

# dc-primary: PANOPLY.LOCAL, Recycle Bin on
.\New-LabDomainController.ps1 -Name dc-primary -Domain PANOPLY.LOCAL -IsoPath D:\iso\ws2025.iso -VhdDirectory D:\vm `
    -SwitchName CI-VLAN -IPAddress 10.20.30.41 -PrefixLength 24 -Gateway 10.20.30.1 -DnsForwarder 10.20.30.2 `
    -NtpServer 10.20.30.2 -AdministratorPassword $admin -ServiceAccountPassword $jim -EnableRecycleBin

# dc-source: RESURGAM.LOCAL, Recycle Bin off
.\New-LabDomainController.ps1 -Name dc-source -Domain RESURGAM.LOCAL -IsoPath D:\iso\ws2025.iso -VhdDirectory D:\vm `
    -SwitchName CI-VLAN -IPAddress 10.20.30.42 -PrefixLength 24 -Gateway 10.20.30.1 -DnsForwarder 10.20.30.2 `
    -NtpServer 10.20.30.2 -AdministratorPassword $admin -ServiceAccountPassword $jim

# dc-target: GENTIAN.LOCAL, Recycle Bin off
.\New-LabDomainController.ps1 -Name dc-target -Domain GENTIAN.LOCAL -IsoPath D:\iso\ws2025.iso -VhdDirectory D:\vm `
    -SwitchName CI-VLAN -IPAddress 10.20.30.43 -PrefixLength 24 -Gateway 10.20.30.1 -DnsForwarder 10.20.30.2 `
    -NtpServer 10.20.30.2 -AdministratorPassword $admin -ServiceAccountPassword $jim
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
