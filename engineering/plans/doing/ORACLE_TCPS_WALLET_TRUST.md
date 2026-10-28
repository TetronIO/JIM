# Oracle TCPS: Trust a Certificate from the JIM Certificate Store

- **Status:** Doing (Phase 0 in review as #2590; Phases 1 and 2 under way together)
- **Issue:** [#2581](https://github.com/TetronIO/JIM/issues/2581)
- **Prerequisite:** [#2589](https://github.com/TetronIO/JIM/issues/2589) (TCPS host name verification)

## Overview

Let an Oracle Database connection over TCPS trust a server certificate from the estate's private certificate authority by trusting that authority in **Admin > Certificates**, as SQL Server, LDAP and SCIM already can. Today the Oracle driver only trusts what the operating system of the JIM containers trusts, which a customer running the published images cannot change. So an Oracle estate whose TCPS listener uses a private authority must fall back to Native Network Encryption. #2583 made JIM stop offering to trust such a certificate; this plan makes the trust real.

The route is an Oracle wallet that JIM writes for the one connection that needs it. A spike against a real TCPS listener settled that it works, and in a simpler form than the issue expected: a PEM file, with no Oracle tooling and no wallet password.

## Spike findings (28 October 2026)

Setup:
- **Server:** Oracle Database Free 23ai (`container-registry.oracle.com/database/free:23.9.0.0`). A TCPS listener on port 2484, with a server wallet built by `orapki` from a certificate signed by a lab CA. The certificate's subject alternative name is `DNS:oracle-hris-b`.
- **Client:** ODP.NET 23.26.400 (JIM's pinned version), with `OracleConnection.WalletLocation` set per connection.

| Client wallet folder | Result |
|---|---|
| Empty (no wallet) | Refused: "The remote certificate was rejected by the provided RemoteCertificateValidationCallback" (under `ORA-50201`) |
| `cwallet.sso` made by `orapki`, holding the CA (control) | Connects, TLS |
| **`ewallet.pem` holding the CA certificate, written as plain PEM** | **Connects, TLS** |
| `ewallet.pem` holding only the server's own (CA-signed) certificate | Refused |
| `ewallet.p12` written by .NET, holding the CA, no password given | Refused (the driver looks for `cwallet.sso`) |

Host name verification (see #2589):

| `SSL_SERVER_DN_MATCH` | By the certificate's name | By an address the certificate does not name |
|---|---|---|
| `TRUE` | Connects | Refused |
| `FALSE`, or unset (JIM today) | Connects | **Connects** |

What follows from the spike:
- **No wallet tooling or secret.** JIM writes the trusted authority's certificate, which is public, as `ewallet.pem`, and points the connection at its folder. Nothing for the image to ship, and nothing to protect.
- **The anchor must be a root.** Unlike SQL Server, whose driver accepts the server's exact certificate, ODP.NET builds a chain to a self-signed certificate in the wallet. Trusting the server's own certificate or an intermediate, both of which the certificate card offers today, will not work for Oracle. The exception is a self-signed server certificate, which is its own root.
- **Host name verification has to be switched on.** That is #2589, a small change of its own that ships first.

Settled against the lane's own server (a root, an intermediate and a `localhost` server certificate; the server sends all three):

| Client `ewallet.pem` holds | Result |
|---|---|
| The root | Refused |
| The intermediate | Refused |
| The server's certificate | Refused |
| **The root and the intermediate** | **Connects** |
| The server's certificate, the intermediate and the root | Connects |

- **The wallet needs the whole chain above the server's certificate,** not just the root. ODP.NET builds the chain from the wallet alone, ignoring the intermediates the server sends, and still requires it to end at a self-signed certificate. So JIM writes the root it trusts *and* the intermediates the presented (or assembled) chain carries. That widens nothing: an intermediate alone is refused, so the root remains the only anchor.
- **No caching by folder path.** A wallet rewritten in place takes effect on the next connection, in both directions. A new folder per materialisation is still used, for tidy clean-up rather than correctness.
- **Concurrent connections** from one process, with different `WalletLocation` values, do not interfere (eight at once, twice over). Concurrent *refused* connections sometimes report ODP.NET's own "m_safeCertContext is an invalid handle" instead of the certificate error; they are refused either way.
- **A self-signed server certificate** is its own root, so it is the same case as a one-link chain and needs nothing special.

## Design

### The provider says what it can be told to trust

`ISqlProvider.SupportsPinnedServerCertificate` (a bool) becomes `PinnableTrustAnchor`, with these values:

- `None`;
- `ServerCertificate` (SQL Server: the exact certificate);
- `RootAuthority` (Oracle over TCPS: a self-signed certificate the chain reaches, with the intermediates between).

`SecureEndpoint` gains the same field, replacing `AcceptsJimTrustedCertificates`. That field was the yes/no answer to the same question, so there is one source of truth, not two.

### Choosing the anchor

`SqlConnector.OpenTrustingJimCertificateStore` keeps its shape. An ordinary connection goes first, so the operating system always gets the first say and trust stays additive. Only when that is refused does JIM read the presented chain. What it then hands the driver depends on the provider:
- **`ServerCertificate`:** the server's own certificate, as today.
- **`RootAuthority`:** the self-signed certificate in the JIM store that the chain reaches, together with the intermediates between it and the server's certificate. If the store vouches for the chain only through an intermediate or the server's own certificate, there is no retry. The refusal is diagnosed as "trust the root".

### Materialising the wallet

A new `SqlTrustedOracleWallet` is the sibling of `SqlTrustedServerCertificateFile`:
- a new folder per materialisation under the temporary directory, owner-only (`0700`), named with the same sweepable prefix scheme;
- `ewallet.pem` inside it, owner-only (`0600`), holding the root and the intermediates below it;
- deleted when the Connector is disposed, with abandoned folders swept after seven days.

`SqlConnectionSettings.PinnedServerCertificatePath` becomes a general `PinnedTrustAnchorPath`. `OracleProvider.ConfigureConnection` sets `OracleConnection.WalletLocation` to it for TCPS. That is a per-connection property, so one Connected System's trust never leaks into another's. `OracleConfiguration.WalletLocation` is static and is never used.

### What the administrator sees (all three surfaces)

- **The certificate card:** offers to trust what the connection can use. For Oracle TCPS that is the root (recommended, as everywhere), or the server's own certificate where it is self-signed; it does not offer the intermediate or a CA-signed leaf. The card already ranks the root first, so this is a narrowing of the choices rather than a new layout.
- **REST and `Approve-JIMConnectedSystemServerCertificate`:** trusting a certificate the connection cannot use is refused with a new outcome, `RootRequired`. It names the root to trust instead, where the presented chain or the download address supplies it. `NotUsedByThisConnection` stays for a connection that takes no anchor at all.
- **The read (`Get-JIMConnectedSystemServerCertificate`, REST):** reports `pinnableTrustAnchor` in place of `acceptsJimTrustedCertificates`. This is a breaking change, which is acceptable before v1.0, and the changelog records it.
- **Docs:**
  - `jim-sql-connector-security.md` and the troubleshooting page are rewritten for the Oracle TCPS case;
  - so is the **Oracle Encryption** setting's description;
  - `docs/powershell/certificates.md` is regenerated.

## Implementation phases

### Phase 0: Host name verification (#2589)

A separate, small pull request that ships first: `(SECURITY=(SSL_SERVER_DN_MATCH=TRUE))` for TCPS, with a descriptor unit test and a Security changelog entry. Its runtime proof arrives with the lane in Phase 1.

### Phases 1 and 2 ship together

Like the SQL Server lane, the Oracle lane's CA is trusted by nobody but the JIM store, so its "connects once trusted" case only exists once wallet trust does. A lane on its own could test refusals and nothing else, so Phases 1 and 2 are one pull request, and the lane is what proves the trust.

### Phase 1: An Oracle TCPS test lane

The `RequiresOracleTls` NUnit tier and an `oracle-tls-tests` CI job, the same shape as `sql-tls-tests`:
- **`test/scripts/Start-OracleTlsTestServer.ps1`:**
  - generates a lab root and intermediate, and a server certificate for `localhost`;
  - builds the server wallet with the image's own `orapki`;
  - adds a TCPS endpoint on port 2484, published to `localhost`.
- **Tests, through `SqlConnector`:**
  - refused with nothing trusted;
  - connects once the root is in the JIM store;
  - refused when only the intermediate or the server's certificate is trusted;
  - refused when connecting by a name the certificate does not carry (#2589).
- **The four open verifications above** become tests here.
- **Runs on:** pull requests that change the SQL Connector (path filter), and on `main`.

### Phase 2: Wallet trust

The provider capability, anchor selection, `SqlTrustedOracleWallet` and the `ConfigureConnection` change, each red first. Unit tests sit alongside `SqlConnectorCertificateTrustTests` and the file's own tests, covering:
- materialisation, permissions, clean-up on dispose, and the sweep;
- anchor selection against chains with and without a vouched-for root;
- the refusal when the store holds only an intermediate.

### Phase 3: Surfaces and docs

The card's choices, `RootRequired` through REST and PowerShell with Pester coverage, the read's new field, the regenerated PowerShell reference, the docs pages and setting description, and a changelog entry (a 🔄 for the trust behaviour, plus a note on the breaking field rename).

## Success criteria

- An Oracle TCPS connection whose certificate chains to a root trusted in **Admin > Certificates** connects, with no image rebuild and no operating system change.
- Trusting something the connection cannot use is refused with a reason on every surface, never reported as a success.
- A certificate issued for another host name is refused (#2589).
- `oracle-tls-tests` runs on every pull request that changes the SQL Connector, and passes.

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| The Oracle image is 13.6GB uncompressed, and a hosted runner may lack the disk. | The job frees disk first, as `scan-images` does, and is path-filtered to SQL Connector changes. If it still does not fit, it runs nightly and on release instead, which needs the user's agreement because the issue asks for every pull request. |
| ODP.NET's PEM wallet support changes in a future driver version. | The lane fails on the upgrade's pull request, not in a customer's estate. `DEPENDENCY_PINNING.md` already makes driver bumps explicit pull requests. |
| A wallet folder left behind if the Worker is killed. | The same seven-day sweep the certificate file uses. The contents are public certificates, so a stray folder discloses nothing. |
| An administrator trusts an intermediate for an Oracle connection and expects it to work. | The card does not offer it, REST and PowerShell refuse it with `RootRequired`, and the docs say why. |

## Dependencies

No new packages. ODP.NET 23.26.400 already supports per-connection `WalletLocation` and PEM wallets. The lane uses the Oracle image Scenario 016 already pulls.
