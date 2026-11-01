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

A fourth VM, `adfs-primary`, is an AD FS server joined to PANOPLY.LOCAL (`adfs1.panoply.local`, federation service
`https://adfs.panoply.local/adfs`), so JIM's single sign-on is tested against a real AD FS before a release (#2355). See
[The AD FS server](#the-ad-fs-server).

`exchange-primary` is an Exchange Server SE Mailbox server joined to PANOPLY.LOCAL (`ex1.panoply.local`), so JIM's
Exchange recipient provisioning (#2021) and GAL sync (#944) meet a real Exchange server. A second, `exchange-source` in
RESURGAM.LOCAL, follows for two-way GAL sync. See [The Exchange servers](#the-exchange-servers).

Each forest is at the Windows Server 2016 functional level. Every domain controller carries the same directory content
as the Samba images (`test/integration/docker/samba-ad-prebuilt/post-provision.sh`): the OUs `Corp`, `Users` and
`Groups` under it, `TestUsers`, `TestGroups` and `Services`; the `svc-jim` account in the `JIM Connectors` group; the
delegation from `jim-ad-delegation.acl`, applied unchanged; read over Deleted Objects; and an LDAPS certificate with the
same subject and names. It differs from Samba where Windows Server 2025 differs: LDAP signing is enforced (everything
talks LDAPS on 636) and the domain password policy is at the Windows defaults (complexity on, minimum length 7,
history 24, minimum age 1 day, maximum age 42 days), which `Configure` sets itself rather than waiting for Group Policy to
write them (#2412). One registry value departs from the Windows default: `OldPasswordAllowedPeriod` is 0, so a previous
password stops working as soon as it changes rather than for 5 minutes after, as on Samba (#2287). `Configure` sets it,
`Verify` checks it, and `Restore-LabDomainController.ps1` applies it again after every checkpoint revert.

Each forest also carries the **full Exchange organisation**, as an Exchange customer's forest does: Exchange's schema
(all of its classes and attributes, `extensionAttribute1`-`15` among them), an organisation named after the forest
(`PANOPLY`, `RESURGAM`, `GENTIAN`) and the domain prepared, made by Exchange Setup itself (`/PrepareSchema`,
`/PrepareAD`, `/PrepareAllDomains`), the only supported way. The domain controller's build does this; an Exchange
server added later joins the organisation that is already there and never prepares a forest itself. The Samba images carry
only a hand-written `extensionAttribute1`-`15`, so scenarios that need more of Exchange's schema run on this lab only.
Products that extend a forest are listed in `settings.json` `directoryExtensions`; Exchange is the first, and another
(Skype for Business, say) is added as a new entry and a handler in the `Extend` phase.

## What is in this folder

| Path | Runs on | What it does |
|------|---------|--------------|
| `host/New-LabDomainController.ps1` | Hyper-V host | Builds a domain controller from the ISO, unattended, and takes the `baseline` checkpoint |
| `host/Restore-LabDomainController.ps1` | Hyper-V host | Reverts to a named checkpoint, starts the VM, waits for the guest and LDAPS (HTTPS on the AD FS server) |
| `host/Checkpoint-LabDomainController.ps1` | Hyper-V host | Takes a production checkpoint (`-Replace` to overwrite one) |
| `host/Get-LabDomainController.ps1` | Hyper-V host | State, checkpoints and guest OS build (`-AsJson` for the runner) |
| `host/Remove-LabDomainController.ps1` | Hyper-V host | Stops and removes the VM, its checkpoints and its disk (needs `-Force`) |
| `host/Grant-LabDelegation.ps1` | Hyper-V host | Applies the delegation to a container created at run time (`-Tombstones` for Deleted Objects) |
| `host/Invoke-LabRebuild.ps1` | Hyper-V host | One phase of the monthly rebuild (`Prune`, `Build`, `Stage`, `Promote`, `Rollback`, `Status`): builds a candidate set beside the live one and swaps names only when the suite passes; see [Monthly rebuild](#monthly-rebuild) |
| `host/Install-LabScripts.ps1` | Hyper-V host or the repository clone | Copies the host and guest scripts into `C:\jim-ad-lab` in the layout above, and does what `jim-lab` is refused (Host access); see [Deploying to the host](#deploying-to-the-host) |
| `guest/autounattend.xml` | Guest (via ISO) | Template for the unattended install; rendered by the module |
| `guest/Initialize-LabDomainController.ps1` | Guest | The build phases: `License`, `Prepare`, `Promote`, `Extend`, `Configure`, `Verify` |
| `guest/LabDomainController.psm1` | Both | Pure functions (tested) and the Windows-only helpers |
| `host/New-LabFederationServer.ps1` | Hyper-V host | Builds the AD FS server, a member of a domain controller's forest, and takes the `baseline` checkpoint of both; see [The AD FS server](#the-ad-fs-server) |
| `guest/Initialize-LabFederationServer.ps1` | Guest | The AD FS server's build phases: `License`, `Prepare`, `Join`, `Farm`, `Verify` |
| `guest/LabFederationServer.psm1` | Both | The AD FS server's pure functions (tested) |
| `LabFederationServer.Tests.ps1` | CI (any OS) | Pester tests for `LabFederationServer.psm1` |
| `host/New-LabExchangeServer.ps1` | Hyper-V host | Builds an Exchange server, a member of a domain controller's forest, and takes the `baseline` checkpoint of both; see [The Exchange servers](#the-exchange-servers) |
| `guest/Initialize-LabExchangeServer.ps1` | Guest | An Exchange server's build phases: `License`, `Prepare`, `Join`, `Prerequisites`, `Install`, `Configure`, `Verify` |
| `guest/LabExchangeServer.psm1` | Both | The Exchange servers' pure functions (tested) |
| `LabExchangeServer.Tests.ps1` | CI (any OS) | Pester tests for `LabExchangeServer.psm1` |
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
   that). The host itself must be a full, activated edition. No domain controller is ever left on an evaluation edition:
   one cannot be converted once promoted, and its timer runs in real time regardless of checkpoint reverts. Evaluation
   *media* is fine, though: see item 5.
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
5. **Windows Server 2025 media** (an ISO), and optionally the latest cumulative update (a `.msu`). Full media is used
   as it is. The free evaluation ISO from Microsoft's Evaluation Center works too: the build installs its Datacenter
   Evaluation image without a product key, and the guest's `License` phase converts it to full Datacenter with the AVMA
   key before promotion (`DISM /Set-Edition`, one restart), after which it activates through AVMA like any other guest.
   That is the only point at which the conversion is supported, so `Promote` refuses an evaluation edition and `Verify`
   checks the result is `ServerDatacenter`.
6. **Exchange Server media** for the Exchange organisation: the Exchange Server SE ISO (`ExchangeServerSE-x64.iso`,
   a free download from the Microsoft Download Center). The domain controllers' builds run Setup's preparation steps from
   it, and the Exchange servers install from the same media. No product key is used (an Exchange server runs
   unlicensed, as a 180-day trial, which each successful monthly rebuild starts again; a server that no rebuild has
   replaced for 180 days must be rebuilt by hand), but Setup's licence
   terms are accepted. The schema version the forests
   get is read from the media (`Setup\Data\SchemaVersion.ldf`), so newer media needs no code change. Microsoft requires
   a Windows Server 2025 schema master to have the November 2025 update (build 26100.7171) or later before Exchange
   extends the schema; the `Extend` phase refuses an older one.
7. **The Exchange prerequisites that are not on its media**, for the Exchange servers only, downloaded once into one
   folder (`settings.json` `exchangePrerequisiteDirectory`) under these names, because the lab network has no route out:

   | File | Download | What it is |
   |------|----------|------------|
   | `vcredist_2012_x64.exe` | <https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x64.exe> | Visual C++ 2012 Update 4 runtime (x64) |
   | `vcredist_2013_x64.exe` | <https://aka.ms/highdpimfc2013x64enu> | Visual C++ 2013 runtime (x64) |
   | `rewrite_amd64_en-US.msi` | <https://download.microsoft.com/download/1/2/8/128E2E22-C1B9-44A4-BE2A-5859ED1D4592/rewrite_amd64_en-US.msi> | IIS URL Rewrite Module 2.1 (x64) |

   These are the ones Microsoft lists for a Mailbox server
   ([Exchange Server 2019 and SE prerequisites](https://learn.microsoft.com/exchange/plan-and-deploy/prerequisites)) and
   `Get-LabExchangePrerequisite` names; the fourth, UCMA 4.0, is on the Exchange media, and .NET Framework 4.8.1 is part
   of Windows Server 2025. The build refuses any file without a valid Microsoft Corporation signature.

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
them, the delegation file into `delegation` and the JIM PowerShell module into `module\<digest>\JIM` (it registers JIM
on the AD FS server), which is the layout `JIM_AD_LAB_SCRIPT_ROOT` points the runner at. It is idempotent, prints `new`,
`updated`, `unchanged` or `removed` per file, and does not deploy itself. Each version of the module keeps its own
folder, named by its digest (`Get-LabModuleDigest`), so a run lasting hours still finds its commit's module after a newer
main is deployed beside it; a version not deployed for 7 days (`-ModuleRetentionDays`) is deleted. It deletes nothing
else, except a file in the module being deployed that the checkout's module no longer has, because the registration
refuses a module copy that is not exactly the commit's. It also
writes `settings.example.json`, and with `-CreateSettings` creates `settings.json` from it when there is none; it refuses
(before copying anything) if `settings.json` already exists, and never touches that file otherwise. Run it again after any
change to the lab code, and after pulling a new release: the scripts on the host are a copy, not the checkout.

**Keep them current.** The AD FS registration refuses a copy of the JIM PowerShell module that is not the commit under
test, so a merge to `src/JIM.PowerShell` fails the AD FS scenarios (Scenario 030 in `jim-ad-lab` among them) until the
host is redeployed. Register the scheduled task that does it hourly, once, from an elevated PowerShell 7 session:

```powershell
C:\jim-ad-lab\Register-LabScriptsUpdateTask.ps1
```

It runs `Update-LabScripts.ps1` as SYSTEM every hour (`\JIM\Update AD lab scripts`): it brings its own clone of main,
`C:\jim-ad-lab-source`, to `origin/main` (reset hard and cleaned, so it must never be a working checkout) and runs
`Install-LabScripts.ps1` from it, logging to `C:\jim-ad-lab\logs\update-lab-scripts.log`. The repository is public, so
it needs no credentials. A run dispatched within the hour of a module change can still find no copy of its module, and
says so. A merge during a run does no harm: the run keeps using its own commit's copy.

**Host access.** The rebuild runs as `jim-lab`, which is a Hyper-V administrator and nothing more, and three things it
needs are refused to such an account. So `Install-LabScripts.ps1` does them, once `settings.json` exists and only when
elevated (the hourly task, as SYSTEM, always is), and reports each as `Granted`, `AlreadyGranted`, `Recorded` or
`AlreadyRecorded`:

| What | Why `jim-lab` cannot | What host setup does |
|------|----------------------|----------------------|
| Read the image list of the Windows media | `Mount-DiskImage` and `Get-WindowsImage` need an administrator ("Access denied", "requires elevation") | Records the image to install in `media.json` beside `settings.json`, once per version of the file (its length and time). The builds read it from there; an elevated build without a record reads the media itself, and `jim-lab` without one is refused with a message saying so |
| Let a VM read the media it is given | VMMS grants that on the ISO as its caller, who must be allowed to change the ISO's ACL (VMMS event 20790) | Grants read on `isoPath` and each `directoryExtensions` ISO to the capability SID Hyper-V uses for it (`vmWorkerProcess`, `Get-LabMediaCapabilitySid`) |
| Delete a removed VM's files | The VMs an administrator built are not its own, and VMMS creates each VM's `Snapshots` folder as SYSTEM | Lets `jim-lab` modify each lab VM's folder beneath `vhdDirectory` and each `rebuild-yyyyMMdd` folder (`Get-LabControlFolder`), never `vhdDirectory` itself, which may hold the runner's VM. The rebuild also gives itself the same on the folder it creates |

So after building the domain controllers by hand (step 4), either run `Install-LabScripts.ps1` again or wait for the
hourly task before the first rebuild. A step that fails makes the script exit 1, after everything else is done. If a
VM's files still cannot be deleted, `Remove-LabDomainController.ps1` and the rebuild name what they left behind.

**2. Fill in `settings.json`.** Every value in the example is a placeholder. `Invoke-LabRebuild.ps1` reads this file, and
the first build below is easiest when it is driven from the same values:

| Key | Meaning |
|-----|---------|
| `isoPath` | The Windows Server 2025 ISO (full or evaluation media; see host prerequisite 5) |
| `cumulativeUpdateDirectory` | Where the newest cumulative update `.msu` is kept (the rebuild picks the latest) |
| `vhdDirectory` | Where the virtual machines' disks live |
| `switchName` | The Internal `Lab` switch the domain controllers attach to (not the external `CI` switch) |
| `ntpServer` | The host's own address on the `Lab` switch (for example `10.99.0.1`): the only NTP-synchronised machine the domain controllers can reach |
| `directoryExtensions` | The products extending every forest, in the order applied: `[{ "product": "Exchange", "isoPath": "<Exchange Server SE ISO>" }]`. Empty or absent builds plain forests |
| `domainControllers` | Per virtual machine (`dc-primary`, `dc-source`, `dc-target`): `domain`, `ipAddress`, `prefixLength`, `gateway` (also the host's address on `Lab`, which leads nowhere by design: the switch has no uplink), `enableRecycleBin` |
| `federationServers` | Per AD FS server (`adfs-primary`): `domainController` (the VM whose forest it joins; its domain and address come from there), `ipAddress`, `prefixLength`, `gateway`, and optionally `computerName` (default `adfs1`) and `serviceName` (default `adfs`) |
| `exchangeServers` | Per Exchange server (`exchange-primary`): `domainController` (the VM whose forest it joins; its domain and address come from there), `ipAddress`, `prefixLength`, `gateway`, and optionally `computerName` (default `ex1`). It installs from the `directoryExtensions` Exchange media |
| `exchangePrerequisiteDirectory` | The folder holding the Exchange prerequisites that are not on its media (host prerequisite 7) |

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
foreach ($extension in @($settings.directoryExtensions)) { if ($extension.product -eq 'Exchange') { $arguments.ExchangeIsoPath = $extension.isoPath } }

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
`-ExchangeIsoPath <iso>` adds the Exchange organisation (settings.json `directoryExtensions`);
`-ExtraCertificateNames <names>` adds DNS names to the LDAPS certificate (beyond the FQDN, the short name and the
domain); `-ComputerName` changes the host name from `dc1`; `-MemoryStartupBytes`, `-ProcessorCount` and `-VhdSizeBytes`
size the VM; `-SkipBaselineCheckpoint` builds without checkpointing.

What the build does, in order: creates a Generation 2 VM (UEFI, Secure Boot, static memory, production checkpoints only);
builds a second small ISO holding a rendered `autounattend.xml` (pure PowerShell, IMAPI2, no extra tool); installs Windows
Server 2025 Datacenter (Desktop Experience) unattended with the AVMA key (no key for evaluation media), then ejects the
media and deletes that ISO; copies the guest files into `C:\jim-ad-lab` in the guest; runs
`Initialize-LabDomainController.ps1` phase by phase over PowerShell Direct, restarting between phases (`License` converts
an evaluation edition to full Datacenter, and does nothing on full media; `Prepare` rename, static IP and roles; `Promote` new forest; optional
cumulative update; `Extend` with each directory extension's media attached for that phase only, Exchange Setup
`/PrepareSchema`, `/PrepareAD` and `/PrepareAllDomains`, skipping what is already done; `Configure` directory content, delegation, LDAPS certificate, Recycle Bin, Windows Update off, w32time;
`Verify` a read-back of all of it plus the same probes `post-provision.sh` runs as `svc-jim` over LDAPS); and finally takes
the `baseline` checkpoint. There is no guest listener and no OpenSSH in the guests: PowerShell Direct is the only control
path and needs no network.

Re-running the same command after a failure is safe. If the VM already exists the create step is skipped, and each guest
phase checks whether it has already happened (role installed, domain exists, OU exists, certificate with that subject
exists) and does nothing if so. To start again from nothing, run `Remove-LabDomainController.ps1 -Name <vm> -Force` first.

The guest phases can be run by hand through PowerShell Direct when investigating, for example
`Invoke-Command -VMName dc-primary -Credential $cred -ScriptBlock { & C:\jim-ad-lab\Initialize-LabDomainController.ps1 -Phase Verify ... }`;
run `Get-Help .\guest\Initialize-LabDomainController.ps1 -Full` for the parameters.

## The AD FS server

`adfs-primary` is a Windows Server 2025 member server running AD FS, joined to PANOPLY.LOCAL, so JIM's single sign-on
is tested end to end against a real AD FS (#2355). It is built like a domain controller (the same media, unattended
install and AVMA licensing) by `host/New-LabFederationServer.ps1`, which drives `guest/Initialize-LabFederationServer.ps1`
through these phases:

| Phase | What it does |
|-------|--------------|
| `License` | As for the domain controllers: converts evaluation media to full Datacenter |
| `Prepare` | Renames the computer `adfs1`, sets its static address, resolves through `dc-primary`, switches automatic Windows Update off, and stops the machine account password changing (below) |
| `Join` | Joins PANOPLY.LOCAL as its Administrator |
| (on `dc-primary`) | Creates the KDS root key (back-dated ten hours, as Microsoft documents for a single-domain-controller test forest), the group managed service account `gmsa-adfs` that only `adfs1$` may use, and the DNS record `adfs.panoply.local` |
| `Farm` | Installs AD FS, creates a self-signed TLS certificate for `adfs.panoply.local` (exported as `adfs-primary-ca.cer`, as the LDAPS certificates are), creates the farm on the Windows Internal Database running as the gMSA, and sets sign-in to forms everywhere, because a headless browser outside the domain cannot complete Windows Integrated Authentication |
| `Verify` | Membership and secure channel, the service and its account, then the OpenID Connect discovery document and key set fetched over HTTPS by the service name, as JIM will. It also reports the `access_token_issuer` AD FS publishes, which #2355 needs to know |

Nothing about JIM is configured here: the Application Group that registers JIM is created on every run, after the
revert, by the registration command JIM ships for administrators (`Register-JIMIdentityProvider`, #2364), so the lab
tests exactly what customers run (see "How a run uses it" below).

**It depends on `dc-primary`.** Its computer account, the gMSA and the KDS root key live in `dc-primary`'s directory,
which is reverted to its checkpoints all the time. So the two are a pair:

- Building a new AD FS server first reverts `dc-primary` to `baseline`, so no test data is captured. When the server is
  built, the script takes `baseline` of both, so the accounts are in `dc-primary`'s baseline from then on, and removes
  `dc-primary`'s `populated-*` checkpoints, which were taken without them (the runner takes them again).
- **Their baselines are a matching pair.** `dc-primary` is reverted before every scenario, and its baseline already
  holds the AD FS accounts, so the AD FS server keeps working without being reverted itself. It is reverted, after
  `dc-primary`, only before the AD FS checks. A revert costs about two minutes, because AD FS is a delayed-start
  service, and the LDAP scenarios never touch AD FS.
- The AD FS server never changes its machine account password (`DisablePasswordChange=1`). A password changed after the
  baseline would match neither checkpoint and break the secure channel. The gMSA needs nothing similar: its password is
  derived from the KDS root key and the time, not stored.
- **The monthly rebuild handles the pair**: it builds the AD FS candidate after, and against, the `dc-primary`
  candidate, rebuilds it whenever that domain controller candidate is newer, and stages, promotes and rolls back each
  domain controller before the AD FS server that depends on it. See [Monthly rebuild](#monthly-rebuild).

Build it after `dc-primary`, with nothing else running against the lab, from the values in `settings.json`:

```powershell
$settings = Get-Content C:\jim-ad-lab\settings.json -Raw | ConvertFrom-Json
Import-Module C:\jim-ad-lab\guest\LabFederationServer.psm1
$adfs = ConvertFrom-LabFederationSetting -Settings $settings -Name 'adfs-primary'
$admin = Read-Host 'Administrator password' -AsSecureString

$arguments = @{
    Name = $adfs.Name; DomainControllerName = $adfs.DomainControllerName; IsoPath = $settings.isoPath
    VhdDirectory = $settings.vhdDirectory; SwitchName = $settings.switchName
    IPAddress = $adfs.IPAddress; PrefixLength = $adfs.PrefixLength; Gateway = $adfs.Gateway
    ComputerName = $adfs.ComputerName; ServiceName = $adfs.ServiceName; AdministratorPassword = $admin
}
$update = Get-ChildItem $settings.cumulativeUpdateDirectory -Filter *.msu -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if ($update) { $arguments.CumulativeUpdatePath = $update.FullName }

C:\jim-ad-lab\New-LabFederationServer.ps1 @arguments
```

It takes 30 to 45 minutes and is safe to re-run after a failure. It costs 4 GB of memory while running.

**How a run uses it.** Only the scenarios that sign in through AD FS use the AD FS server (Scenarios 030 and 031;
`$script:AdLabFederationScenarios` in `test/integration/utils/ActiveDirectoryLab-Helpers.ps1`). For those, the runner:

1. reverts `dc-primary`, then `adfs-primary`, to `baseline`;
2. starts JIM with `docker-compose.ad-lab-adfs.yml` as well, which makes `https://adfs.panoply.local/adfs` its identity
   provider and maps `adfs.panoply.local` to `JIM_AD_LAB_ADFS_PRIMARY_ADDRESS` inside `jim.web`;
3. reads the AD FS server's HTTPS certificate off the wire into `test/integration/ad-lab-certs/adfs-primary.pem`, and
   waits until it publishes its discovery document, verified against that certificate and the service name (AD FS
   answers 503 for a minute or two after the machine starts);
4. sets JIM up to sign people in through it, as a customer does before turning AD FS sign-in on (Step 4e, #2441):
   - registers JIM on it with `Register-LabIdentityProvider.ps1` on the host, which runs JIM's own
     `Register-JIMIdentityProvider -Provider ADFS` against the AD FS server over PowerShell Direct, with JIM's address as
     the sign-in checks reach it, `https://jim.web:8443` (`Get-ActiveDirectoryLabJimUrl`). The registration is made from
     the commit's copy of the JIM PowerShell module that `Install-LabScripts.ps1` put on the host,
     `module\<digest>\JIM`, and refuses to run unless that copy's content gives the commit's digest
     (`Get-LabModuleDigest`): **after any change to `src/JIM.PowerShell`, the host needs that version deployed before
     the run** (the hourly update does it for main; for another branch run `Install-LabScripts.ps1` from its checkout),
     or the AD FS scenarios fail, saying so. The same
     script then turns CORS on for that address, with the commands the AD FS SSO guide gives administrators, so the API
     reference's Try It can sign in (#2594). The script runs from the host's copy too, so a change to it reaches the
     lab only once the host is redeployed;
   - creates the account that signs in as JIM's initial administrator, `jim-sso-admin` in `dc-primary`'s `CN=Users`,
     with a password drawn for the run (never the domain Administrator, whose password the control plane depends on);
   - recreates `jim.web` with all of it, through its own process environment: the client IDs, the API scope, `oid`
     matched to the built-in `Object GUID` attribute, and `jim-sso-admin`'s objectGUID as `JIM_SSO_INITIAL_ADMIN`.
     JIM first starts on `.env`'s `sub`, because the registration and the account only exist once the AD FS server
     and `dc-primary` answer.

   The portal's client secret and the administrator's password are new on every run and die at the next revert. Each
   is masked in the log at once and handed to the workflow's scrub step through `GITHUB_ENV`, which removes it from
   every artefact; neither is written to any other file.

**The AD FS sign-in checks** are Scenario 031, which `ad-lab.yml` runs in a step of its own after the LDAP scenarios
and reports as the commit status `jim-ad-lab-adfs` (`Test-ScenarioRunsOnItsOwn` keeps it out of `-Scenario All`). It
trusts AD FS's certificate, creates an ordinary employee in `OU=Users,OU=Corp` and synchronises Active Directory into
JIM (`Setup-Scenario-031.ps1`), then signs people in from the LDAP toolbox container, the lab's stand-in for an
administrator's workstation: it resolves `jim.web` and the AD FS server's name, trusts their certificates, and has
PowerShell (`AdfsSignIn-Helpers.ps1`, `AdfsSignInClient.psm1`). There is no browser: the client follows JIM's redirect
to AD FS, fills in AD FS's forms sign-in page, posts the `form_post` reply back to JIM, and stands in for the browser
`Connect-JIM` opens. It checks the portal sign-in, the initial administrator provisioned from `oid`, a bearer token on
`/api`, `Connect-JIM` through PKCE, the same person from both, the employee renamed in Active Directory, the
employee's `User`-only access, and the API reference's Try It: the client, endpoints and scope its page and OpenAPI
document give, PKCE back to `/api/reference/` (where Scalar redirects the page), the code exchanged with JIM's origin (which AD FS must allow), and its
token on `/api`.
The runner does not give JIM the certificate: the scenario trusts it the way an administrator would, with
`Add-JIMCertificate -FromIdentityProvider`. It reads the certificate off the wire rather than copying
`adfs-primary-ca.cer` from the guest, the same way it reads the domain controllers'.

The run needs the variable `JIM_AD_LAB_ADFS_PRIMARY_ADDRESS` (10.99.0.21 in the example settings) in the `jim-ad-lab`
environment. `JIM_AD_LAB_ADFS_PRIMARY_HOST` (default `adfs.panoply.local`) and `JIM_AD_LAB_ADFS_PRIMARY_VM` (default
`adfs-primary`) are optional.

## The Exchange servers

`exchange-primary` is a Windows Server 2025 member server running Exchange Server SE with the Mailbox role, joined to
PANOPLY.LOCAL as `ex1.panoply.local`, so JIM's Exchange recipient provisioning (#2021) and GAL sync (#944) meet a real
Exchange server (#2412). `exchange-source`, the same in RESURGAM.LOCAL, follows as a settings change for two-way GAL
sync. GENTIAN.LOCAL stays without one. Each is built like the AD FS server (the same media, unattended install and AVMA
licensing) by `host/New-LabExchangeServer.ps1`, which drives `guest/Initialize-LabExchangeServer.ps1` through these
phases:

| Phase | What it does |
|-------|--------------|
| `License` | As for the domain controllers: converts evaluation media to full Datacenter |
| `Prepare` | Renames the computer `ex1`, sets its static address, resolves through its domain controller, switches automatic Windows Update off, stops the machine account password changing (as on the AD FS server), sets Remote Registry to Automatic (Exchange requires it) and fixes the page file at a quarter of memory (Exchange's system requirements) |
| `Join` | Joins the forest as its Administrator |
| `Prerequisites` | Installs the Windows features Microsoft lists for a Mailbox server, then the Visual C++ 2012 and 2013 runtimes and IIS URL Rewrite (copied in by the host after checking their signatures) and UCMA 4.0 (from the Exchange media, attached for this phase and `Install` only) |
| `Install` | Runs Exchange Setup unattended (`/Mode:Install /Roles:Mailbox`), joining the forest's existing organisation (`PANOPLY`) and naming the first database `MDB01`. It first refuses a forest whose Exchange schema is not this media's, or whose organisation is not the forest's own: the domain controller's build prepares forests, not this one |
| `Configure` | Through Exchange Remote PowerShell: the second database, `MDB02`, excluded from automatic mailbox distribution so #2021 can prove Exchange honours that; `svc-jim` in **Recipient Management**; the GAL sync contacts OU, `OU=GALSync,OU=Corp`; and Exchange's own certificate for `ex1.panoply.local`, exported as `exchange-primary-ca.cer` (as the LDAPS certificates are) and trusted on the server. The `/PowerShell` virtual directory keeps its default authentication (Kerberos), because that is what JIM must handle |
| `Verify` | Everything above read back, Exchange's services (`Test-ServiceHealth`), then the cmdlets JIM will drive: a mailbox enabled on a throwaway user (landing in `MDB01`, the only database open to distribution) and removed with `-Permanent`, a mail contact enabled in `OU=GALSync` and removed, no disconnected mailbox left behind, a new Remote PowerShell session over HTTP with Kerberos at `http://ex1.panoply.local/PowerShell/` (Microsoft's documented connection: Kerberos encrypts the payload), and a Kerberos challenge from `https://ex1.panoply.local/PowerShell/` with a trusted certificate. It does not open a WinRM session over HTTPS: with Exchange's default `/PowerShell` settings the Windows WinRM client loops on 401 and 200 there and never connects; which transport JIM uses is #2021's question. It reports how long after boot Remote PowerShell answered and the services were healthy |

Nothing about JIM is configured here, beyond `svc-jim`'s role group: #2021 sets JIM up the way customers will.

**It is a pair with its domain controller**, like the AD FS server, but more tightly. Exchange keeps its configuration
and every recipient in the directory, and the mailboxes on the server, so reverting one without the other would leave
mailboxes whose users no longer exist:

- Building a new Exchange server first reverts its domain controller to `baseline`. If that baseline already holds an
  Exchange server object of the same name, from an earlier build, it stops there: Setup cannot install a new server
  over it, so the domain controller is rebuilt first.
- When the server is built, the script shuts it down (turning it off if the guest has not stopped within 10 minutes)
  and takes `baseline` of both, the server first, so nothing Exchange writes to the directory after its own checkpoint
  is in the domain controller's. If the domain controller's checkpoint fails, the server's new one is removed, so no
  half-matched pair is left. It then removes the domain controller's `populated-*` checkpoints (the runner takes them
  again).
- The two are reverted together, each to its own baseline, before any scenario that creates or changes recipients in
  that forest. LDAP scenarios that never touch Exchange leave the Exchange server off and are unaffected by it.
- It never changes its machine account password (`DisablePasswordChange=1`), for the same reason as the AD FS server.

Build it after its domain controller, with nothing else running against the lab, from the values in `settings.json`:

```powershell
$settings = Get-Content C:\jim-ad-lab\settings.json -Raw | ConvertFrom-Json
Import-Module C:\jim-ad-lab\guest\LabExchangeServer.psm1
$exchange = ConvertFrom-LabExchangeSetting -Settings $settings -Name 'exchange-primary'
$admin = Read-Host 'Administrator password' -AsSecureString

$arguments = @{
    Name = $exchange.Name; DomainControllerName = $exchange.DomainControllerName; IsoPath = $settings.isoPath
    ExchangeIsoPath = $exchange.ExchangeIsoPath; PrerequisiteDirectory = $exchange.PrerequisiteDirectory
    VhdDirectory = $settings.vhdDirectory; SwitchName = $settings.switchName
    IPAddress = $exchange.IPAddress; PrefixLength = $exchange.PrefixLength; Gateway = $exchange.Gateway
    ComputerName = $exchange.ComputerName; AdministratorPassword = $admin
}
$update = Get-ChildItem $settings.cumulativeUpdateDirectory -Filter *.msu -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if ($update) { $arguments.CumulativeUpdatePath = $update.FullName }

C:\jim-ad-lab\New-LabExchangeServer.ps1 @arguments
```

It is safe to re-run after a failure; an interrupted Exchange Setup resumes. When it has finished it shuts the server
down (`-LeaveRunning` keeps it up), because the lab starts it only for the scenarios that use Exchange.

**Its cost.** Each server takes 12 GB of static memory (Microsoft does not support Dynamic Memory for Exchange), 4
vCPUs and a 150 GB dynamically expanding disk. The host has 64 GB and 8 logical processors, so one Exchange server fits
beside everything else; with both running, `dc-target` and `adfs-primary` are stopped. CPU is the tighter limit: the
VMs already have twice as many vCPUs as the host has logical processors, so the build reports how long each phase took
and the host's CPU use during `Install` and `Verify`, and longer run times are accepted until the host's CPU is
upgraded.

The [monthly rebuild](#monthly-rebuild) rebuilds it with its domain controller.

**Not yet wired in** (#2412): the runner does not yet start the Exchange servers for Exchange scenarios, revert them
with their domain controllers, or refuse to start them when the host's memory is short.

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

## The runner VM

The runner VM (`jim-ad-lab-runner`, Ubuntu, GitHub runner `<host>-jim-ad-lab`) is built and configured from the
`TetronIO/ci-runners` repository (`docs/hyperv-host.md`), not from this folder.

One part of that configuration matters to every run here: **`needrestart` must never restart the runner, Docker or
containerd.** Ubuntu's daily upgrade runs `needrestart`, which restarts services whose libraries changed. On 23 Oct 2026
it restarted the runner's service at 06:43, in the middle of the nightly, and the job was cancelled ("The runner has
received a shutdown signal"). ci-runners now writes the exclusion for every runner it sets up, and moves the lab VM's
daily upgrade to the afternoon, out of the nightly's way.

Every lab run checks for the exclusion (`Check the runner is exempt from automatic service restarts` in `ad-lab.yml`),
warns if it is missing, and logs the upgrade timer's schedule.

## How the runner calls the control plane

The runner VM (label `jim-ad-lab`) never holds hypervisor credentials. It runs, over OpenSSH as `jim-lab`:

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
| An AD FS scenario registers JIM | `Register-LabIdentityProvider.ps1 -Name <vm> -Url <jim url> -ModuleDigest <digest>` | Refuses unless the host's copy of the JIM PowerShell module is the commit's; prints one JSON object, which holds the portal's new client secret (see "How a run uses it") |

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

**When.** GitHub's cron treats day-of-month and day-of-week as OR, so the workflow fires every Wednesday at 03:37 UTC
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

**The forests are built side by side.** The workflow builds each VM in its own control-plane call and runs one chain
per domain controller at the same time (`Get-LabRebuildBuildChain`): `dc-source` and `dc-target` alone, and
`dc-primary` followed by the member servers that join it, in turn, since each takes that domain controller's baseline
again. A chain that fails stops there; the others finish, and the step then fails, so the run rolls back. The Build
phase takes about two hours instead of three. Each such call logs to a file of its own,
`%ProgramData%\jim-ad-lab\logs\Invoke-LabRebuild-yyyyMMdd-<vm>.log`.

**The AD FS server is rebuilt with them** when `settings.json` lists it under `federationServers`. It comes after its
domain controller in every phase:
- **Build** stops the live `adfs-primary` and builds `adfs-primary-candidate` with `New-LabFederationServer.ps1`, joined
  to `dc-primary-candidate`. That build retakes `dc-primary-candidate`'s baseline, so the accounts are in it.
- Build refuses to start before `dc-primary-candidate` exists. It rebuilds an AD FS candidate older than that domain
  controller candidate, and never resumes a part-built one.
- **Stage, Promote and Rollback** handle it after `dc-primary`, so its domain controller is always up first.
- The workflow reads the list of VMs from `Status`, so it builds whatever the host's settings define.

**The Exchange server is rebuilt with them too** when `settings.json` lists it under `exchangeServers`. It comes last,
after the AD FS server:
- **Build** builds `exchange-primary-candidate` with `New-LabExchangeServer.ps1`, joined to `dc-primary-candidate`, from
  the Exchange media in `directoryExtensions` (the media that candidate's forest was prepared with) and the
  prerequisites in `exchangePrerequisiteDirectory`. That build retakes `dc-primary-candidate`'s baseline, so the server
  and its recipients are in it. It takes about 65 minutes.
- The same rules as the AD FS server apply: Build refuses to start before `dc-primary-candidate` exists, rebuilds an
  Exchange candidate older than it, and never resumes a part-built one.
- **A finished Exchange candidate is never rebuilt on its own** (`-Fresh`, or a leftover older than a day). The domain
  controller candidate's baseline now holds its Exchange server object, and a new server of the same name cannot be
  installed against it. Build refuses, and says to build `dc-primary` again first; the Exchange candidate is then older
  than it, and is replaced.
- **Stage and Rollback revert it to `baseline` and leave it off**, as the live server is at rest; the runner starts it
  only for the scenarios that use Exchange. Promote renames it like the others.
- **Stage and Promote refuse a mismatched pair** before touching anything: an AD FS or Exchange candidate built before
  its domain controller candidate (when that domain controller was built again but the server was not) is reported,
  and has to be built again first.

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
Hyper-V Administrators may run: no elevation. What such an account is refused, host setup does beforehand (Host access,
under [Deploying to the host](#deploying-to-the-host)).

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
