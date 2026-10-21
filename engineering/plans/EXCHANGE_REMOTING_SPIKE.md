# Exchange Remote PowerShell Transport Spike

- **Status:** Planned
- **Created:** 2026-10-21
- **Issue:** [#2021](https://github.com/TetronIO/JIM/issues/2021)

## Overview

Exchange mailbox provisioning (#2021) depends on JIM calling Exchange Server cmdlets (`Enable-Mailbox`, `Enable-DistributionGroup`, `Get-Recipient` and so on) from the Linux Worker container. On-premises Exchange (2016, 2019 and Subscription Edition) exposes recipient management only through Remote PowerShell: WinRM (MS-WSMV) carrying the PowerShell Remoting Protocol (MS-PSRP) to the `Microsoft.Exchange` endpoint at `/PowerShell`. There is no REST admin API on-premises: EWS manages mailbox content, not recipients, and the Exchange Online Admin API is cloud-only. PowerShell 7 on Linux has no supported WSMan client.

This spike decides whether JIM can own a native, managed .NET WinRM/PSRP client (option A in #2021), and what it costs. It is time-boxed to **two weeks of effort**. It ends with a go / no-go decision recorded in this document, not with a shippable feature.

## Business Value

- **Removes the single largest risk in #2021 before any feature work starts.** If the transport is not viable, the design changes (to a Windows-side relay or SSH to a management host), and every later phase depends on that answer.
- **Keeps JIM a single set of Linux containers.** No Windows component is needed, which matters to air-gapped buyers and follows the self-contained design principle.
- **Produces a reusable building block.** The same client could later reach other Windows-only management surfaces without touching the LDAP connector.

## Questions the spike must answer

| # | Question | Evidence that answers it |
|---|---|---|
| Q0 | Is there already a mature, maintained, managed .NET WinRM/PSRP client that works on Linux and that we could adopt instead? | Survey of NuGet and GitHub. For any candidate: licence, maintainer, last release, Linux support, Kerberos message encryption support, known CVEs. Adoption goes through dependency governance. |
| Q1 | Can the Worker container authenticate to an `HTTP/<host>` SPN with Kerberos using .NET `NegotiateAuthentication`, from a username and password (no keytab), with the `libgssapi-krb5-2` already in the image? | A passing authentication from the Worker image against the lab DC. Records what Kerberos configuration (realm, KDC, `krb5.conf`) JIM has to provide and how: generated at runtime from Connected System settings, or a mounted file. |
| Q2 | Can we produce and consume WinRM Kerberos message encryption over HTTP (`multipart/encrypted`, `application/HTTP-Kerberos-session-encrypted`) with `NegotiateAuthentication.Wrap`/`Unwrap`? | A round-tripped WinRM `Identify` and `Create` exchange over plain HTTP on port 5985, with message encryption on. |
| Q3 | Does HTTPS with Kerberos authentication (no message encryption) work, with TLS 1.2 minimum and normal certificate validation? | The same exchange over 5986 or HTTPS `/PowerShell`. |
| Q4 | Can the minimal PSRP subset open a runspace pool, run one command with bound parameters, and return typed output and error records? | `Get-Date`, `Get-Service -Name WinRM`, and a deliberately failing command against the lab DC's default `Microsoft.PowerShell` endpoint, all parsed into property bags. |
| Q5 | Does the same client work against Exchange's `Microsoft.Exchange` endpoint, including Exchange's restricted (no-language) runspace and its serialisation payload signing (on by default since the November 2023 SU)? | `Get-Recipient`, `Get-MailboxDatabase`, `Enable-Mailbox` with and without `-Database`, and `Disable-Mailbox`, each with `-DomainController`, against a real Exchange server. |
| Q6 | What do the failures look like? | Captured error records for: object not found (replication lag), already mailbox-enabled, non-universal group passed to `Enable-DistributionGroup`, insufficient RBAC, bad credentials, and Exchange PowerShell throttling (`PowerShellMaxConcurrency`). Each mapped to a proposed RPEI error type and to retry or no-retry. |
| Q7 | What does it cost at run time? | Session set-up time, per-cmdlet latency, whether one runspace can serve a whole export batch, and the safe concurrency against Exchange's default throttling policy. |

## Technical Architecture

### Current state

- There is no WinRM, PSRP or Exchange code in JIM.
- The Worker image ships `libgssapi-krb5-2` (`src/JIM.Worker/Dockerfile`). Today that's only for PostgreSQL and LDAP.
- The LDAP connector does not use Kerberos or SASL. The AD lab PRD (`engineering/prd/doing/PRD_ACTIVE_DIRECTORY_LAB.md`) lists that as a non-goal, so Kerberos from the container is new ground for this spike.
- The AD lab provides Windows Server 2025 domain controllers with Exchange schema preparation only. There is no Exchange server (#2036).

### Spike layout

The prototype is written where the real library would live, so that a "go" keeps the code rather than rewriting it:

```
src/Tetron.PowerShellRemoting/            # standalone library, no JIM.* references
  Authentication/                         # NegotiateAuthentication wrapper, Kerberos config
  WsMan/                                  # SOAP envelopes, Create/Command/Send/Receive/Signal/Delete
  Encryption/                             # HTTP-Kerberos-session-encrypted framing
  Psrp/                                   # fragments, messages, runspace pool, pipeline
  Serialisation/                          # minimal CLIXML reader/writer, property bags
  RemoteSession.cs                        # public entry point: open, invoke command, close
test/Tetron.PowerShellRemoting.Tests/     # unit tests on recorded fixtures, boundary test
```

Its public surface is deliberately small:

```csharp
await using var session = await RemoteSession.OpenAsync(new RemoteSessionOptions
{
    ConnectionUri = new Uri("http://ex01.lab.local/PowerShell/"),
    ConfigurationName = "Microsoft.Exchange",
    Credential = credential,                      // Kerberos via NegotiateAuthentication
    MessageEncryption = MessageEncryption.Required
}, cancellationToken);

var result = await session.InvokeAsync(
    new RemoteCommand("Enable-Mailbox")
        .WithParameter("Identity", objectGuid)
        .WithParameter("DomainController", "dc01.lab.local"),
    cancellationToken);
// result.Output: IReadOnlyList<PropertyBag>; result.Errors: IReadOnlyList<RemoteError>
```

Commands are always a command name plus bound parameters, never script text. This matches Exchange's restricted endpoint and rules out injection by construction.

### Out of scope for the spike

- Any LDAP connector, JIM model, UI, REST API or PowerShell module changes. The Exchange cmdlet façade and the staged Exchange step are #2021's first-release phase.
- NTLM, CredSSP, Basic and certificate authentication.
- PowerShell host calls (`Read-Host` and so on). The client rejects them.
- Disconnected or reconnected sessions, and file copy.
- A complete CLIXML type system. Only the primitives, lists, hashtables and property bags that Exchange output needs.

## Implementation Phases

### Phase 0: Prior-art survey (day 1)

- Answer Q0. If a candidate passes, present the comparison and stop for the user's dependency decision before writing protocol code.
- Read MS-WSMV, MS-PSRP and the WinRM message encryption details. Note which sections the minimal subset needs.

### Phase 1: Authentication and transport against the lab DC (days 2 to 4)

- Kerberos authentication from the Worker image to `HTTP/dc01` (Q1). Document the Kerberos configuration JIM will need to own.
- WinRM `Identify`, then `Create` and `Delete` of a shell, over HTTP with message encryption (Q2) and over HTTPS (Q3).
- Refuse plain HTTP without message encryption. That's a security requirement, not a preference.
- Unit tests for the encryption framing against fixtures captured from a real session.

### Phase 2: PSRP subset against the lab DC (days 5 to 7)

- Session capability and runspace pool initialisation, create pipeline, receive output and state, close (Q4).
- Fragment assembly and disassembly, and the minimal CLIXML reader and writer.
- Error records surfaced as structured `RemoteError` values.
- Unit tests on recorded message sequences, so the protocol layer is tested offline in CI without a Windows host.

### Phase 3: Exchange (days 8 to 9)

**Prerequisite:** an Exchange Server (2019 CU latest or Subscription Edition) joined to the lab forest. See open question 1.

- Run Q5's cmdlets against `Microsoft.Exchange`, including with serialisation payload signing on.
- Collect Q6's failure catalogue and Q7's timings.

### Phase 4: Decision and write-up (day 10)

- Record the result of every question under "Spike Results" below.
- Make a go / no-go call against the criteria below, and update #2021: the chosen transport, revised estimates, and the Kerberos configuration the Connected System settings must expose.
- On "go", the prototype stays in place, gains the boundary test, and becomes the basis of #2021's first-release phase. On "no-go", it is removed in the same PR that records the decision.

## Success Criteria

The spike is a **go** for option A when all of these hold:

1. Q1 to Q4 pass from the Worker container image (not just the devcontainer) against the lab DC.
2. Q5 passes against a real Exchange server for at least `Get-Recipient` and `Enable-Mailbox`, including with serialisation payload signing on.
3. The estimated effort to harden the prototype into a production library (tests, error mapping, logging without secrets, cancellation, session reuse) is no more than **four further weeks**.
4. No finding requires weakening JIM's security baseline: Kerberos only, message encryption or TLS 1.2+ always, certificate validation on, and no secrets in logs.

It is a **no-go** when Kerberos message encryption cannot be made to work from the container, when serialisation payload signing blocks non-Microsoft clients, or when the hardening estimate exceeds the threshold. In that case, the write-up compares option B (Windows-side relay) and option C (SSH to a Windows management host) with the evidence gathered.

## Benefits

- A firm transport decision before UI, model or connector work is committed.
- A failure catalogue (Q6) that directly shapes #2021's RPEI types and retry rules.
- Known Kerberos configuration requirements, which the Connected System settings page and documentation will need either way.

## Dependencies

- **Lab:** Windows Server 2025 DC WinRM listeners (enabled by default on domain controllers). An Exchange server in the lab forest for Phase 3.
- **Runtime:** `libgssapi-krb5-2` in the Worker image (already present). No new apt or NuGet packages are expected. Any that the spike finds necessary go through dependency governance first.
- **Network:** the spike runs where the lab is reachable (the devcontainer on the lab network). Cloud sandbox sessions cannot reach the lab, so they are limited to offline unit tests against recorded fixtures.
- **Specifications:** MS-WSMV and MS-PSRP (Microsoft Open Specifications). pypsrp (MIT) is used only to compare behaviour. No code is copied from it; anything derived would be attributed in `third-party-notices`.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Kerberos configuration inside the container is fragile (realm, KDC discovery, clock skew, SPN) | Make it a first-class finding of Q1. Plan a "Test connection" action with plain-language diagnostics for #2021. |
| Exchange serialisation payload signing rejects non-Microsoft clients | Test it explicitly in Phase 3. If it blocks, it is a no-go trigger, not something to work around. |
| No Exchange server available for Phase 3 | Phases 0 to 2 run against the lab DC alone and still answer most of the risk. Phase 3 slips until Exchange is in the lab; the decision waits for it. |
| The protocol subset grows beyond "minimal" | The out-of-scope list above is binding for the spike. Anything else needed is recorded as hardening effort, not built. |
| A security-sensitive protocol stack becomes ours to maintain | Keep the scope to what Exchange needs, with no general-purpose remoting. Keep the library behind a no-`JIM.*` boundary so it could be split out and opened later. |
| Microsoft changes or retires on-premises Remote PowerShell | The cmdlet façade (command plus parameters) has the same shape as the Exchange Online Admin API, so a future REST transport could sit behind the same interface. |

## Open Questions

1. **Exchange in the lab:** is there an existing Exchange server we can use, or does the AD lab gain an Exchange Subscription Edition VM? The latter would be a sub-task of #2036 and needs sizing on the lab host.
2. **Kerberos configuration ownership:** should JIM generate `krb5.conf` from Connected System settings (realm, KDCs), or should operators mount one? Q1 should inform this, but it is a product decision.

## Spike Results

_To be completed at the end of Phase 4._
