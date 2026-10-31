# Exchange Remote PowerShell Transport Spike

- **Status:** Doing (Phases 0, 1a and 2 complete; Phases 1b and 3 wait for an Exchange window on the AD lab)
- **Created:** 2026-10-21
- **Issue:** [#2021](https://github.com/TetronIO/JIM/issues/2021)

## Overview

Exchange recipient provisioning (#2021) depends on JIM calling Exchange Server cmdlets (`Enable-Mailbox`, `Set-Mailbox`, `Enable-MailContact` and so on) from the Linux Worker container. On-premises Exchange exposes recipient management only through Remote PowerShell: WinRM (MS-WSMV) carrying the PowerShell Remoting Protocol (MS-PSRP) to the `Microsoft.Exchange` endpoint at `/PowerShell`. #2021 targets Exchange SE (lab-tested), 2019 CU15 (supported) and 2016 (best effort). There is no REST admin API on-premises: EWS manages mailbox content, not recipients, and the Exchange Online Admin API is cloud-only. PowerShell 7 on Linux has no supported WSMan client.

**Decided after Phase 0 (2026-10-21): option A2.**
- JIM hosts `System.Management.Automation` 7.6.x (Microsoft) for PSRP, runspaces and serialisation, through PowerShell's public custom transport API. Dependency governance is approved.
- JIM writes only the WinRM transport and Kerberos authentication. The default is **HTTP with Kerberos message encryption**, as Microsoft documents for Exchange; HTTPS is optional (decided 2026-10-30, see Decisions).

This spike proves that architecture end to end and measures its cost. It is time-boxed to **two weeks of effort**. It ends with a go / no-go decision recorded in this document, not with a shippable feature.

## Business Value

- **Removes the single largest risk in #2021 before any feature work starts.** If the transport is not viable, the design changes (to a Windows-side relay or SSH to a management host), and every later phase depends on that answer.
- **Keeps JIM a single set of Linux containers.** No Windows component is needed, which matters to air-gapped buyers and follows the self-contained design principle.
- **Produces a reusable building block.** The same transport could later reach other Windows-only management surfaces without touching the LDAP connector.

## Questions the spike must answer

| # | Question | Evidence that answers it |
|---|---|---|
| Q0 | Is there a mature managed .NET client we could adopt instead? | **Answered in Phase 0:** no, but PowerShell's custom transport API removes the need to write PSRP. See Spike Results. |
| Q1 | Can the Worker container authenticate to an `HTTP/<host>` SPN with Kerberos using .NET `NegotiateAuthentication`? It must work from a username and password (no keytab), with the `libgssapi-krb5-2` already in the image and a `krb5.conf` that JIM generates (see "Kerberos configuration" below). | A passing authentication from the Worker image, using only a generated `krb5.conf` and an in-memory credential cache. First against the sandbox's Samba KDC (Phase 1a), then the lab DC. |
| Q2 | Does PowerShell's PSRP run over our transport with Kerberos authentication **and Kerberos message encryption over HTTP** (`multipart/encrypted`, `application/HTTP-Kerberos-session-encrypted`)? Encryption goes through P/Invoke to `gss_wrap_iov` and `gss_unwrap_iov` in the image's MIT `libgssapi_krb5`, because `NegotiateAuthentication` has no IOV equivalent. Can it open a remote runspace, run one command with bound parameters, and return output and error records? | `Get-Date`, `Get-Service -Name WinRM` and a deliberately failing command, first against the sandbox stand-in (Phase 1a), then the lab DC's default `Microsoft.PowerShell` endpoint on 5985. The transport must refuse a session that would not be encrypted. |
| Q3 | Does the optional HTTPS path work where the server accepts it (TLS 1.2 minimum, certificate validation on, Kerberos authentication through `NegotiateAuthentication`)? Why did a Kerberos session over HTTPS to a newly installed Exchange SE loop on 401 and 200 in the lab ([#2021 comment](https://github.com/TetronIO/JIM/issues/2021#issuecomment-6520532342))? | Against the lab DC's 5986 listener, then `exchange-primary` from the Worker image. At minimum, the transport must detect a 401/200 loop and fail fast with a clear error rather than retry for hours. |
| Q4 | Does the same client work against Exchange's `Microsoft.Exchange` endpoint, including Exchange's restricted (no-language) runspace and its serialisation payload signing (on by default since the November 2023 SU)? | Against the lab Exchange SE server, each with `-DomainController`: `Get-Recipient`, `Get-MailboxDatabase`, `Enable-Mailbox` (with and without `-Database`, and with `-Shared`), `Set-Mailbox -Type Shared`, `Enable-MailUser`, `Enable-MailContact`, `Enable-DistributionGroup`, and `Disable-Mailbox`. |
| Q5 | What do the failures look like? | Captured error records, each mapped to a proposed RPEI error type and to retry or no-retry, for: object not found (replication lag), already enabled, non-universal group passed to `Enable-DistributionGroup`, insufficient RBAC, bad credentials, an untrusted Exchange certificate, clock skew, and Exchange PowerShell throttling (`PowerShellMaxConcurrency`). |
| Q6 | What does it cost at run time? | Session set-up time, per-cmdlet latency, whether one runspace can serve a whole export batch, safe concurrency against Exchange's default throttling policy, and the Worker image size increase from hosting PowerShell. |

## Technical Architecture

### Current state

- There is no WinRM, PSRP or Exchange code in JIM, and no reference to `System.Management.Automation`.
- The Worker image ships `libgssapi-krb5-2` (`src/JIM.Worker/Dockerfile`). Today that's only for PostgreSQL and LDAP.
- The LDAP connector does not use Kerberos or SASL. The AD lab PRD (`engineering/prd/doing/PRD_ACTIVE_DIRECTORY_LAB.md`) lists that as a non-goal, so Kerberos from the container is new ground for this spike.
- The AD lab provides Windows Server 2025 domain controllers with Exchange schema preparation only. The Exchange servers come with #2412.

### Spike layout

The prototype is written where the real library would live, so that a "go" keeps the code rather than rewriting it:

```
src/Tetron.PowerShellRemoting/            # standalone library, no JIM.* references
  Authentication/                         # NegotiateAuthentication wrapper (Kerberos), krb5.conf generation
  Encryption/                             # gss_wrap_iov / gss_unwrap_iov P/Invoke, HTTP-Kerberos-session-encrypted framing
  WsMan/                                  # SOAP envelopes: Create/Command/Send/Receive/Signal/Delete
  Transport/                              # RunspaceConnectionInfo + ClientSessionTransportManagerBase over WsMan
  RemoteSessionFactory.cs                 # public entry point: opens a remote Runspace over the transport
test/Tetron.PowerShellRemoting.Tests/     # unit tests on recorded fixtures, boundary test
test/integration/remoting-harness/        # Phase 1a: Samba KDC + pwsh -s stand-in WinRM server (Docker)
```

PowerShell supplies everything above the transport, so the public surface is PowerShell's own:

```csharp
using var runspace = await RemoteSessionFactory.OpenAsync(new RemoteSessionOptions
{
    ConnectionUri = new Uri("http://ex1.panoply.local/PowerShell/"),   // Kerberos-encrypted payload, as Microsoft documents
    ConfigurationName = "Microsoft.Exchange",
    Credential = credential,                      // Kerberos via NegotiateAuthentication
    MessageEncryption = MessageEncryption.Required  // never an unencrypted session
}, cancellationToken);

using var ps = PowerShell.Create(runspace);
ps.AddCommand("Enable-Mailbox")
  .AddParameter("Identity", objectGuid.ToString())
  .AddParameter("Shared")
  .AddParameter("DomainController", "dc1.panoply.local");
var output = await ps.InvokeAsync();               // PSObjects; ps.Streams.Error holds error records
```

Commands are always a command name plus bound parameters, never script text. This matches Exchange's restricted endpoint and rules out injection by construction. The library only ever creates remote runspaces, never a local one, and the Worker image sets `POWERSHELL_TELEMETRY_OPTOUT=1`.

### Kerberos configuration (decided: JIM generates `krb5.conf`)

Operators do not mount a `krb5.conf`. JIM generates one from the settings of every Connected System that uses Kerberos. This follows the "admin UI over environment variables" principle, and means a new Connected System works without touching the container. The spike proves the approach; #2021 builds the settings UI for it.

- **Inputs per Connected System:** the realm, and optionally explicit KDCs (default: DNS SRV lookup). The realm is derived from the service account's UPN suffix where possible.
- **One file per Worker process, holding every realm.** `KRB5_CONFIG` is process-wide, so the Worker writes a single file with one `[realms]` entry and `[domain_realm]` mappings per configured realm, and points `KRB5_CONFIG` at it before any GSSAPI call. The spike confirms that MIT Kerberos picks up a rewritten file without a restart (its profile library checks the file's modification time), so adding a Connected System needs no Worker restart.
- **Hardened defaults:** AES encryption types only (no RC4 or DES), `dns_canonicalize_hostname = false` and `rdns = false` (the SPN is built from the configured host name, never from reverse DNS), and `forwardable = false`.
- **No tickets or secrets on disk.** The file holds no secrets and is written owner-only (`0600`) to a private directory. The credential cache is in memory (`KRB5CCNAME=MEMORY:`), and credentials come from the encrypted Connected System settings through `NegotiateAuthentication` with an explicit `NetworkCredential`.
- **Clock skew is the most likely operator fault.** The spike records what the failure looks like, so "Test connection" can name it plainly.

### Out of scope for the spike

- Any LDAP connector, JIM model, UI, REST API or PowerShell module changes. The Exchange cmdlet façade, the recipient types, GAL sync and the staged Exchange step are #2021's first-release phase.
- NTLM, CredSSP, Basic and certificate authentication.
- HTTPS beyond Q3's checks (it is optional, not the default).
- PowerShell host calls (`Read-Host` and so on). The transport rejects them.
- Disconnected or reconnected sessions, and file copy.

## Implementation Phases

### Phase 0: Prior-art survey (day 1) ✅

- Answer Q0. If a candidate passes, present the comparison and stop for the user's dependency decision before writing protocol code.
- Read MS-WSMV, MS-PSRP and the WinRM message encryption details. Note which sections the minimal subset needs.

### Phase 1a: Sandbox harness (days 2 to 4) ✅

Runs entirely in a Claude Code on the web sandbox, and is set up to run in CI. What it cannot run is a Windows host, so this phase proves our code against an independent implementation, not Windows' behaviour. Everything lives in `test/integration/remoting-harness/`, started by `Start-RemotingHarness.ps1`.

- **KDC:** Samba AD (the public `diegogslomp/samba-ad-dc` image, digest-pinned) as a self-contained `HARNESS.TEST` realm, with a client account `svc-winrm` and an `HTTP/winrm-harness.harness.test` service principal whose keytab is exported for the stand-in.
- **Stand-in WinRM server:** a Python server (`winrm-server/winrm_server.py`).
  - Kerberos and WinRM message encryption through **pyspnego** (decision 6), an implementation independent of ours, so the framing is checked against code already proven against Windows.
  - The WS-Management shell operations, with the PSRP payload passed to and from `pwsh -s`, PowerShell's own out-of-process server.
  - An HTTPS listener with a harness test CA for Q3.
- **Client:** the transport under hosted `System.Management.Automation` 7.6.7, with a generated `krb5.conf` pointing at the harness realm (Q1, Q2). It ran natively on the sandbox's Ubuntu 24.04, which has the same MIT `libgssapi-krb5-2` as the Worker image, not yet inside the Worker image itself.
- **Known limit:** our client and the stand-in's framing and WS-Management code share an author, so they can share a misreading of MS-WSMV. pyspnego covers the Kerberos layer independently; Phase 1b on a real Windows host is what proves the rest.

### Phase 1b: Real Windows WinRM (days 5 to 6)

Runs on the lab's Linux runner VM, which reaches the lab network.
- Repeat Q1 and Q2 against `dc1.panoply.local`'s WinRM over HTTP (5985) with message encryption, from the Worker image, then Q3 over HTTPS (5986).
- Prove the framing against Windows' real implementation: AES256 payloads of several sizes, including ones that span WinRM's 16 KB encryption chunks.
- Capture the real WinRM exchanges as recorded fixtures for the unit tests.

### Phase 2: Hardening probes (day 7) ✅

- Cancellation, operation timeouts, and runspace reuse across many commands (Q6).
- Logging without secrets.
- Error mapping from PowerShell error records into structured results ready for RPEIs (Q5, Windows-side cases).

### Phase 3: Exchange (days 8 to 9)

**Prerequisite:** the lab's Exchange Server SE mailbox server, `exchange-primary` (`ex1.panoply.local`), built by [#2412](https://github.com/TetronIO/JIM/issues/2412).

- Run Q4's cmdlets against `Microsoft.Exchange` over HTTP with Kerberos message encryption, including with serialisation payload signing on.
- Answer Q3 against Exchange: whether HTTPS works from the Worker image, and if not, that the transport fails fast with a clear error.
- Collect Q5's Exchange-side failure catalogue and Q6's timings.

### Phase 4: Decision and write-up (day 10)

- Record the result of every question under "Spike Results" below.
- Make a go / no-go call against the criteria below, and update #2021: revised estimates, the Kerberos settings the Connected System must expose, and the error types.
- On "go", the prototype stays in place, gains the boundary test, and becomes the basis of #2021's first-release phase. The Phase 1a harness stays as a CI test. On "no-go", the prototype is removed in the same PR that records the decision.

## Success Criteria

The spike is a **go** for option A2 when all of these hold:

1. Q1 and Q2 pass from the Worker container image, in the sandbox harness and against the lab DC.
2. Q4 passes against the lab Exchange SE server for at least `Get-Recipient`, `Enable-Mailbox`, `Set-Mailbox -Type Shared` and `Enable-MailContact`, including with serialisation payload signing on.
3. The estimated effort to harden the prototype into a production library is no more than **three further weeks**. That covers tests, error mapping, logging without secrets, cancellation and session reuse; it's down from four now that PSRP is Microsoft's.
4. No finding requires weakening JIM's security baseline beyond the one documented exception: Kerberos only, Kerberos AES message encryption on HTTP or TLS 1.2+ on HTTPS (never neither), certificate validation on for HTTPS, and no secrets in logs.

It is a **no-go** in any of these cases:
- PowerShell's custom transport cannot carry Exchange's endpoint.
- Serialisation payload signing blocks the hosted client.
- Kerberos message encryption cannot be made to interoperate with Windows from the Worker image.
- The hardening estimate exceeds the threshold.

In that case, the write-up compares option A1 (own PSRP), option B (Windows-side relay) and option C (SSH to a Windows management host) with the evidence gathered.

## Benefits

- A firm transport decision before UI, model or connector work is committed.
- A failure catalogue (Q5) that directly shapes #2021's RPEI types and retry rules.
- Known Kerberos configuration requirements, which the Connected System settings page and documentation will need either way.
- A CI-runnable remoting test harness that needs no Windows host.

## Dependencies

- **NuGet (approved 2026-10-21):** `System.Management.Automation` 7.6.x and its transitive dependencies, pinned per `engineering/DEPENDENCY_PINNING.md`. It is added to the remoting library only, and reaches the Worker through the LDAP connector.
- **Sandbox:** Docker, the Samba AD image and `pwsh`, all already provisioned by the SessionStart hook.
- **Lab:** Windows Server 2025 DC WinRM listeners (enabled by default on domain controllers; an HTTPS listener may need configuring as part of #2412's lab work). `exchange-primary` from [#2412](https://github.com/TetronIO/JIM/issues/2412) for Phase 3. The spike needs only that one; #2412's second server is for #2021's GAL sync tests.
- **Runtime:** `libgssapi-krb5-2` in the Worker image (already present).
- **Network:** the lab network is reachable only from the lab's Linux runner VM, so Phases 1b to 3 run there. Phase 1a runs in the sandbox and in CI.
- **Specifications:** MS-WSMV (Microsoft Open Specifications). PSWSMan (MIT) and pypsrp (MIT) are used to compare behaviour. Anything derived from them is attributed in `third-party-notices`.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Kerberos configuration inside the container is fragile (realm, KDC discovery, clock skew, SPN) | Make it a first-class finding of Q1, starting in the sandbox. Plan a "Test connection" action with plain-language diagnostics for #2021. |
| PowerShell's custom transport API is sparsely documented and could change between PowerShell releases | PSWSMan relies on the same public API. Pin `System.Management.Automation` and cover the transport with the Phase 1a harness in CI, so an upgrade that breaks it fails the build. |
| Hosting PowerShell enlarges the Worker image and its scan surface | Measure the size increase (Q6). Every transitive package passes the normal Trivy and pinning process. Allow remote runspaces only, never local ones. |
| PowerShell's major version is tied to .NET's | A .NET upgrade includes the matching `System.Management.Automation` upgrade; the CI harness catches breakage. |
| Exchange serialisation payload signing rejects the hosted client | Test it explicitly in Phase 3. If it blocks, it is a no-go trigger, not something to work around. |
| Native GSSAPI interop is ours to get right (buffer handling, IOV layout, MIT versus Heimdal differences) | MIT only, as shipped in the Worker image. Follow PSWSMan's proven IOV approach. Fuzz the framing parser. Test against real Windows in Phase 1b and record fixtures. |
| Kerberos message encryption is not TLS, which JIM's baseline otherwise requires | A documented, scoped exception in `engineering/COMPLIANCE_MAPPING.md` (SC-8). The transport refuses an unencrypted session and never falls back to one. |
| The stand-in server agrees with our own misreading of MS-WSMV | Phase 1b against real Windows is mandatory before the decision. Its recorded exchanges become unit fixtures. |
| The lab Exchange server (#2412) is not ready for Phase 3 | Phases 0 to 2 still answer most of the risk. Phase 3 slips until Exchange is in the lab; the decision waits for it. |
| Microsoft changes or retires on-premises Remote PowerShell | The cmdlet façade (command plus parameters) has the same shape as the Exchange Online Admin API, so a future REST transport could sit behind the same interface. |

## Decisions

1. **Exchange in the lab:** the AD lab gains two Exchange Server SE mailbox servers, `exchange-primary` in PANOPLY.LOCAL and `exchange-source` in RESURGAM.LOCAL ([#2412](https://github.com/TetronIO/JIM/issues/2412); #2021 is blocked by it). The spike uses `exchange-primary`; the second server exists for #2021's two-way GAL sync tests.
2. **Kerberos configuration:** JIM generates `krb5.conf` from Connected System settings; operators never mount one. See "Kerberos configuration" above.
3. **Client architecture (2026-10-21):** option A2. JIM hosts `System.Management.Automation` 7.6.x and writes only the WinRM transport and Kerberos authentication. The dependency is approved under dependency governance.
4. **Transport security (2026-10-30, replacing the 2026-10-21 HTTPS-first decision):**
   - **The default is HTTP with Kerberos message encryption;** HTTPS is optional.
   - **Why:** building `exchange-primary`, a Kerberos session over HTTPS looped on 401/200 for hours while HTTP connected at once ([#2021 comment](https://github.com/TetronIO/JIM/issues/2021#issuecomment-6520532342)). Microsoft documents HTTP for Exchange Remote PowerShell, and changing the `/PowerShell` virtual directory is unsupported without its support team.
   - **What it costs:** `gss_wrap_iov` interop is now required, not a reserve.
   - **Security:** approved as a documented exception to TLS in transit (`engineering/COMPLIANCE_MAPPING.md`, SC-8).
5. **Sandbox harness (2026-10-21):** Phase 1a is added, using a Samba KDC and a `pwsh -s` stand-in WinRM server, so most transport work is built and debugged without the lab.
6. **Stand-in server on pyspnego (2026-10-30):** the harness's stand-in WinRM server uses pyspnego for Kerberos and WinRM message encryption, so our client is tested against an independent implementation rather than against itself. These are test-only dependencies, approved under dependency governance and never shipped:
   - pyspnego 0.12.4 and krb5 0.10.0 from PyPI. krb5 publishes no Linux wheels, so it is compiled from hash-pinned source with hash-pinned Cython and setuptools in a build stage.
   - python3-gssapi and python3-cryptography from Ubuntu.

   Everything is version-pinned.
7. **Lab access for Phase 1b (2026-10-30):** Claude Code Remote Control on the lab's runner VM, run as a separate account from the CI runner's, so the spike's lab tests run from the session that owns #2021.

## Spike Results

### Phase 2: hardening probes (2026-10-31)

**Everything Phase 2 set out to prove holds through the harness, after fixing six faults it found in the transport.** `Tetron.PowerShellRemoting.Tests` passes 106 of 106 with the harness up (about 50 seconds), skipping only the 6 Exchange lab tests; CI now runs the harness tier on every pull request that touches the library or the harness (`remoting-harness-tests` in `ci.yml`), and fails rather than skips if the harness does not come up.

| Proven | How |
|---|---|
| Stopping a command (Q6) | A stopped `Start-Sleep 120` ends within seconds and the runspace carries on. A remote stop surfaces as a `RemoteException` ("The pipeline has been stopped.") with the invocation in the Stopped state, not as a local `PipelineStoppedException`; callers must judge it by the state |
| Long commands and timeouts (Q6) | A command that outlasts the server's `OperationTimeout` still returns its output; a closed port fails at once; an endpoint that never answers fails within the request timeout; cancelling an open throws `OperationCanceledException` |
| One runspace for a batch (Q6) | 200 commands on one runspace cost **1 Kerberos login and 3 connections** (from 202 of each before the fix); about 100 ms a command against the stand-in, which says nothing about Exchange's own cost |
| Idle connections | When the server closes an idle connection (HTTP.sys does after 120 seconds; the harness after 5), the next request authenticates a new one and is sent again, with no new login |
| Secrets | The library logs nothing, and no exception it raises carries the password, a token or a message body. `RemoteSessionOptions` cannot give its password back, so logging or serialising the options cannot leak it. A `SecureString` parameter, as `New-Mailbox -Password` needs, arrives intact through PowerShell's own session-key encryption |
| Failures for reporting and retry (Q5, transport side) | `RemoteFailure` classifies each failure as Command, Stopped, Authentication, Certificate, Connection, Timeout or Protocol, with `IsTransient` for the two a retry may cure, and keeps the error ID, reason, target and Exchange's own `FailureCategory`. Each kind is checked against the real exception chain |

**Faults found and fixed, all in the client:**
- Every command's Receive opened a new connection, and every connection logged in to the KDC again with the password. A session now shares one ticket-granting ticket (a reference-counted `SafeHandle`, so closing mid-handshake cannot free it in use), renews it once if it expires, and hands finished connections to the next command.
- A 401 after authenticating, which is what an idle connection the server has closed produces, failed the command as access denied. It now re-authenticates and sends again, once.
- An exception while the session closed escaped the transport's own thread, **which ends the host process**: in JIM, the Worker. Nothing escapes that thread now.
- A request timing out inside a Receive was swallowed, so the command waited for output for ever. It is now reported.
- Per-command cancellation sources were never disposed, and every connection ever opened was kept until the session ended.
- The harness script failed silently in a new shell and then waited 20 minutes for a KDC that had not started.

**Still open:** Exchange's own error records (object not found, already enabled, insufficient RBAC, throttling) and their mapping to JIM's activity error types are Phase 3's, captured from the lab. Renewal of an expired ticket-granting ticket is classified and handled, but untested end to end; the lab's long runs will exercise it.

### Phase 1a: sandbox harness (2026-10-30)

**Q1 and Q2 pass against the harness: PowerShell remoting runs from Linux through our transport, Kerberos-authenticated and Kerberos-encrypted.** `Tetron.PowerShellRemoting.Tests` passes 72 of 72 with the harness up (about 6 seconds), and skips its 15 harness tests without it.

| Proven | How |
|---|---|
| Kerberos from a username and password, no keytab, with a generated `krb5.conf` and an in-memory credential cache (Q1) | `KerberosClientContext` against the Samba KDC; a wrong password fails with GSSAPI's own explanation and never echoes the password |
| WinRM message encryption both ways (Q2) | Our `gss_wrap_iov` / `gss_unwrap_iov` against a test acceptor, and then against **pyspnego** in the stand-in server, which decrypts our requests and whose encrypted replies we decrypt |
| Refusal of anything unencrypted | Our decoder refuses unencrypted replies; the stand-in refuses unencrypted requests once authenticated, as `AllowUnencrypted=false` does |
| PSRP through the custom transport (Q2) | A remote runspace opens; `Get-Date` with bound parameters returns typed output; a failing `Get-Item` returns its error record; three commands run in turn on one runspace |
| HTTPS (Q3, harness side) | A command runs over TLS with the harness CA supplied; without it the connection is refused for the certificate |

The faults the harness found were all in the stand-in server, and all are fixed:
- it read pwsh's `<DataAck/>` as `<Data>`, which stalled every command 30 seconds;
- it dispatched unknown actions before looking up the shell;
- it demanded message encryption on its TLS listener.

None was in the client. That is weak evidence: the stand-in and the client share an author for everything above the Kerberos layer, which is exactly what Phase 1b exists to test.

**Inside the Worker image (2026-10-31): works, with one change the Worker needs.** A throwaway client ran a command through the harness from the Worker's `base` stage, with the Worker's own hardening: UID 1654, a read-only root filesystem, `/tmp` as the only writable path, all capabilities dropped and `no-new-privileges`.
- **What breaks without the change:** `System.Management.Automation` creates its cache folder under `$HOME/.cache` (`/home/app/.cache`) the first time it runs. On the read-only root filesystem that throws, and every type that depends on it fails to initialise, so no session can open.
- **The fix:** set `XDG_CACHE_HOME=/tmp/.cache` in the Worker image. With only that, the session opens and the command runs, and the only things written are an empty `/tmp/.cache/powershell` folder and the generated `krb5.conf` under `/tmp`. Neither `XDG_CONFIG_HOME` nor `XDG_DATA_HOME` is needed. This belongs in the #2021 implementation, in the PR where the Worker first references the library.
- **Already in place:** the image's pinned `libgssapi-krb5-2` loads, and an in-memory credential cache needs nothing written.

**Not yet done, carried forward:**
- **A CI job for the harness.** The tests skip without it today.
- **The 16 KB-chunk and large-payload cases.** Phase 1b tests them against Windows.

### Phase 0: prior-art survey (2026-10-21)

**Q0: nothing is adoptable as a dependency, but there is a much better route than writing PSRP ourselves.**

| Candidate | What it is | Verdict |
|---|---|---|
| [PSWSMan](https://github.com/jborean93/PSWSMan) 3.x (MIT, Jordan Borean, who also wrote pypsrp) | Managed .NET 8 WinRM client for PowerShell 7.4+: WSMan, Kerberos/NTLM/CredSSP/certificate authentication, and WinRM message encryption over GSSAPI. It plugs into **PowerShell's own PSRP implementation** through the public custom remoting transport API (`RunspaceConnectionInfo` and `ClientSessionTransportManagerBase`, public since PowerShell 7.3). Active (last commit 2026-10-14), but self-described as "currently an unreleased project", with no releases | **Not adoptable**: unreleased, a PowerShell module rather than a library, single maintainer, and it pulls in MonoMod and Devolutions.Sspi. **The best available reference**, and proof that the architecture below works. |
| [WinRm.NET](https://github.com/CodyBatt/WinRm.NET) (MIT) | .NET WinRM client. WinRS command execution, not PSRP | Not adoptable: "not suitable for production use", 0.0.2-pre1, 0 stars |
| [WinRMSharp](https://github.com/adstep/WinRMSharp) (MIT) | .NET WinRM client. WinRS command execution, not PSRP | Not adoptable: preview only, 1 star, no PSRP |
| Naos.WinRM | Wrapper over Windows' own WSMan client | Not applicable: .NET Framework, Windows only |
| [libpsrp](https://github.com/Hugne/libpsrp) | C implementation of MS-PSRP | Not applicable: native C |
| PowerShell's built-in WSMan remoting on Linux | Depended on OMI's `libmi`/`libpsrpclient` | Dead: Microsoft documents that the OMI-based client no longer supports PowerShell remoting |

**Finding 1: PowerShell itself can be the PSRP layer.** PowerShell 7.3 and later let a host supply its own transport under PowerShell's PSRP, runspace and serialisation code. Hosting `System.Management.Automation` 7.6.x (Microsoft, MIT, targets `net10.0`, matching JIM) would mean:

- JIM writes only the WinRM transport and authentication (MS-WSMV envelopes, Kerberos, message encryption) as a `ClientSessionTransportManagerBase`.
- Microsoft's code handles PSRP fragments, runspace pools, CLIXML serialisation, error records and Exchange's PowerShell behaviour. That removes the largest and riskiest part of the original option A. It probably also settles the serialisation payload signing question (Q5), since the client side is Microsoft's own code; Phase 3 still confirms it.
- Commands become ordinary `PowerShell.Create().AddCommand("Enable-Mailbox").AddParameter(...)` calls against a remote runspace, returning `PSObject`s.

**Finding 2: Kerberos message encryption over HTTP needs native GSSAPI interop.** WinRM's `HTTP-Kerberos-session-encrypted` framing sends the wrap header separately from the encrypted data. PSWSMan gets that from `gss_wrap_iov` (`GSS_IOV_BUFFER_TYPE_HEADER`, plus a padding buffer). .NET's `NegotiateAuthentication.Wrap` returns one combined token and has no IOV equivalent. So the plan's earlier assumption that `NegotiateAuthentication` covers message encryption was wrong. The options are:

- **HTTPS (TLS 1.2+) with Kerberos authentication through `NegotiateAuthentication`, and no message encryption.** Managed only, no interop. Exchange serves `/PowerShell` on the Default Web Site over 443 as well as 80, so this should be available on every Exchange server; Q3 confirms it. **Recommended as the default.**
- **HTTP with message encryption, through P/Invoke to `libgssapi_krb5`** (`gss_wrap_iov`, `gss_unwrap_iov`). The library is already in the Worker image, so there's no new package, but we'd own native interop code. Offer it only if HTTPS proves impractical for customers.

Two notes from PSWSMan's implementation:
- Heimdal has a `gss_unwrap_iov` bug with WinRM payloads, so PSWSMan uses plain unwrap there. The Worker image uses MIT Kerberos, which is unaffected.
- RC4 Kerberos needs the IOV path because of its padding. JIM's generated `krb5.conf` allows AES only, which avoids it.

**Finding 3: the dependency cost of hosting PowerShell.** `System.Management.Automation` 7.6.7 is an 8 MB package with direct dependencies on:
- `Newtonsoft.Json`, `Microsoft.ApplicationInsights` (PowerShell's telemetry), `Microsoft.Management.Infrastructure` and `Microsoft.PowerShell.Native` (native `libpsl-native`);
- about ten `System.*` packages at 10.0.5.

Points to weigh:
- **Its major version is tied to .NET's** (7.6 is .NET 10, 7.7 is .NET 11), so a .NET upgrade carries a PowerShell upgrade.
- **Telemetry:** `POWERSHELL_TELEMETRY_OPTOUT=1` must be set in the Worker image.
- **Trivy:** every one of these packages adds to the Worker image's scan surface.
- **Remote runspaces only:** JIM never creates a local runspace or runs local script, and the code would enforce that.

**Revised options for the library**

| | A1: own everything (original) | A2: host PowerShell, own the transport (proposed) |
|---|---|---|
| We write | WinRM, PSRP, CLIXML, authentication and encryption | WinRM and authentication only, as a PowerShell custom transport |
| Rough size | 3,000 to 5,000 lines | Roughly 1,500 to 2,500 lines (PSWSMan's comparable parts, without CredSSP, SSPI and certificate authentication) |
| Correctness risk | High: PSRP and serialisation edge cases are ours | Lower: the protocol state machine and serialisation are Microsoft's |
| New dependencies | None | `System.Management.Automation` and its tree (dependency governance approval needed) |
| Ties | None | PowerShell's major version is tied to .NET's |
| Later open-source split | Clean | Clean. The library would depend on `System.Management.Automation`, which is fine for a public package. |

**Recommendation: A2, HTTPS-first** (superseded on 2026-10-30 by HTTP with Kerberos message encryption as the default; see Decisions). The Phase 1 spike should start with HTTPS plus Kerberos through `NegotiateAuthentication`, under PowerShell's PSRP, against the lab DC. HTTP with message encryption over `gss_wrap_iov` follows only if HTTPS turns out to be impractical. **Approved 2026-10-21**, including `System.Management.Automation` under dependency governance (see Decisions).
